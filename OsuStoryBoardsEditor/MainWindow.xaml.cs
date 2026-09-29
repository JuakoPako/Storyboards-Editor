using Microsoft.Win32;
using OsuStoryBoardsEditor.Commands;
using OsuStoryBoardsEditor.Models;
using OsuStoryBoardsEditor.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OsuStoryBoardsEditor
{
    public partial class MainWindow : Window
    {
        // ── Servicios y proyecto ──────────────────────────
        private readonly OsbImportService _osbImport = new();
        private readonly TextSpriteService _textService = new();
        private readonly OsuExportService _exportService = new();
        private readonly UndoRedoManager _undoRedo = new();
        private string _baseTitle = "";

        private readonly BeatmapLibraryService _beatmapLibrary = new();
        private StoryboardProject _project = new();
        private System.Windows.Media.MediaPlayer _audioPlayer = new();
        private DispatcherTimer _timer;
        private int _fps = 30;
        private string? _bgPath = null;

        // Mapa de osu! con el que se está trabajando (para exportar directo a su carpeta)
        private BeatmapSetInfo? _currentMap = null;

        private DispatcherTimer? _audioOpenWatchdog;

        // ── Cache de bitmaps Skia ─────────────────────────
        private readonly SKPaint _spritePaint = new SKPaint { IsAntialias = true };

        // ── campos ──
        private static readonly int CmdTypeCount = Enum.GetValues(typeof(CommandType)).Length;
        private readonly OsuCommand?[] _resolved = new OsuCommand?[CmdTypeCount];
        private readonly OsuCommand?[] _scrActive = new OsuCommand?[CmdTypeCount];
        private readonly OsuCommand?[] _scrPast = new OsuCommand?[CmdTypeCount];
        private readonly OsuCommand?[] _scrFuture = new OsuCommand?[CmdTypeCount];

        private readonly Dictionary<string, SKBitmap> _bitmapCache = new();
        private OsuSprite? _spriteClipboard;   // copia congelada: editar el original después no la altera

        private enum TransformHandle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate }
        private TransformHandle _activeHandle = TransformHandle.None;
        private double _handleDragStartScale;
        private double _handleDragStartRot;
        private SKPoint _handleDragStartPt;
        private double _handleDragStartAngle;
        private OsuCommand? _handleDragCmdS;
        private double _handleDragCmdSStart, _handleDragCmdSEnd;
        private OsuCommand? _handleDragCmdR;
        private double _handleDragCmdRStart, _handleDragCmdREnd;

        private const float StoryboardXOffset = 107f;

        // ── Drag ──────────────────────────────────────────
        private OsuSprite? _draggingSprite = null;
        private SKPoint _dragOffset;
        private double _dragStartSpriteX, _dragStartSpriteY;
        private UndoRedoManager.Transaction? _dragTx;   // paso de undo abierto mientras dura el drag

        private OsuSprite? _pendingSprite;
        private int _pendingMs;
        private readonly Dictionary<string, double> _pending = new();

        public MainWindow()
        {
            InitializeComponent();

            LayerPanel.DataContext = _project;
            LayerPanel.SpriteSelected += OnSpriteSelected;
            LayerPanel.AddLayerRequested += OpenImageDialog;
            LayerPanel.DuplicateRequested += sprite =>
            {
                OnSpriteSelected(sprite);
                PasteSprite(sprite, atPlayhead: false);
            };
            LayerPanel.DeleteRequested += sprite =>
            {
                OnSpriteSelected(sprite);
                DeleteSelectedSprite();
            };
            LayerPanel.BeatLoopRequested += sprite =>
            {
                OnSpriteSelected(sprite);
                OpenBeatLoopDialog(sprite);
            };
            Timeline.SpriteSelected += OnSpriteSelected;

            // ── Undo/redo global ──
            _baseTitle = Title;
            Timeline.SetUndoManager(_undoRedo);
            LayerPanel.SetUndoManager(_undoRedo);
            _undoRedo.StateChanged += UpdateTitle;
            _undoRedo.Replayed += OnHistoryReplayed;

            Timeline.SetProject(_project);
            Timeline.SeekRequested += ms => SetCurrentTime(ms, updatePlayhead: false);

            Timeline.KeyframeChanged += () => OsuCanvas.InvalidateVisual();

            _audioPlayer.MediaOpened += (s, e) =>
            {
                _audioOpenWatchdog?.Stop();
                if (_audioPlayer.NaturalDuration.HasTimeSpan)
                {
                    _project.TotalDuration = (int)_audioPlayer.NaturalDuration.TimeSpan.TotalMilliseconds;
                    Timeline.SetProject(_project);
                }
            };

            _audioPlayer.MediaFailed += (s, e) =>
            {
                MessageBox.Show(
                    $"No se pudo cargar el audio:\n{e.ErrorException.Message}\n\n" +
                    "Esto suele pasar con archivos .ogg si Windows no tiene el códec de Vorbis instalado.",
                    "Error de audio");
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / _fps) };
            _timer.Tick += (s, e) =>
            {
                if (!_audioPlayer.NaturalDuration.HasTimeSpan) return;
                double ms = _audioPlayer.Position.TotalMilliseconds;
                _currentMs = ms;
                TxtCurrentTime.Text = $"{_audioPlayer.Position:mm\\:ss\\.fff}";
                Timeline.UpdatePlayhead(ms);
                if (_project.SelectedSprite != null)
                    RefreshPropertiesPanel(_project.SelectedSprite);
                OsuCanvas.InvalidateVisual();
            };

            TxtCurrentTime.Text = "00:00.000";
        }

        private static bool SetValueAtKeyframe(OsuSprite sprite, CommandType type, int index, int t, double typed)
        {
            bool any = false;
            foreach (var c in sprite.Commands)
            {
                if (c.Type != type) continue;
                if (c.StartTime == t && index < c.StartValues.Length) { c.StartValues[index] = typed; any = true; }
                if (c.EndTime == t && index < c.EndValues.Length) { c.EndValues[index] = typed; any = true; }
            }
            return any;
        }

        private bool ApplyPanelValue(OsuSprite sprite, CommandType type, int index, string key, double typed)
        {
            if (!sprite.Commands.Any(c => c.Type == type)) return false;   // sin keyframes: propiedad base, como antes
            int t = (int)Math.Round(CurrentMs);
            if (SetValueAtKeyframe(sprite, type, index, t, typed)) return true;   // justo sobre un keyframe: edita ese
            if (_pendingSprite != sprite || _pendingMs != t) { _pending.Clear(); _pendingSprite = sprite; _pendingMs = t; }
            _pending[key] = typed;   // entre keyframes: no toca nada existente
            return true;
        }

        // ── RENDER PRINCIPAL ──────────────────────────────
        private void OsuCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Black);

            double ms = CurrentMs;

            // ── Fondo (cover, igual que osu!) ──
            if (_bgPath != null && _bitmapCache.TryGetValue(_bgPath, out var bgBmp))
            {
                using var bgPaint = new SKPaint { IsAntialias = false };
                const float canvasW = 854f, canvasH = 480f;
                float imgW = bgBmp.Width, imgH = bgBmp.Height;
                float scale = Math.Max(canvasW / imgW, canvasH / imgH);
                float drawW = imgW * scale;
                float drawH = imgH * scale;
                float offX = (canvasW - drawW) / 2f;
                float offY = (canvasH - drawH) / 2f;
                canvas.DrawBitmap(bgBmp, new SKRect(offX, offY, offX + drawW, offY + drawH), bgPaint);
            }

            // ── Sprites ──
            foreach (var sprite in _project.Sprites)
            {
                if (!_bitmapCache.TryGetValue(sprite.FilePath, out var bmp)) continue;
                if (!sprite.Visible) continue;
                if (ms < sprite.StartTime || ms > sprite.EndTime) continue;

                var state = GetSpriteStateAt(sprite, ms);
                if (state.opacity <= 0) continue;

                double absW = bmp.Width * Math.Abs(state.scaleX);
                double absH = bmp.Height * Math.Abs(state.scaleY);
                var (ox, oy) = GetOriginOffset(sprite.Origin, absW, absH);

                var cmdP = _resolved[(int)CommandType.P];
                bool additive = cmdP?.Parameter == "A";

                canvas.Save();
                canvas.Translate((float)state.x + StoryboardXOffset, (float)state.y);

                canvas.RotateRadians((float)state.rot);
                // Antes: "using var paint = new SKPaint {...}" alocaba un SKPaint por sprite por frame.
                // Ahora reusamos _spritePaint y solo actualizamos sus propiedades.
                _spritePaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(state.opacity * 255, 0, 255));
                _spritePaint.BlendMode = additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
                canvas.DrawBitmap(bmp,
                    new SKRect((float)-ox, (float)-oy,
                               (float)(absW - ox), (float)(absH - oy)),
                    _spritePaint);
                canvas.Restore();
            }

            // ── Guías ──
            using var guidePaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 25),
                StrokeWidth = 0.5f,
                IsStroke = true
            };
            canvas.DrawLine(0, 240, 854, 240, guidePaint);
            canvas.DrawLine(427, 0, 427, 480, guidePaint);

            // ── Selección ──
            if (_project.SelectedSprite != null &&
    _bitmapCache.TryGetValue(_project.SelectedSprite.FilePath, out var selBmp))
            {
                var sprite = _project.SelectedSprite;
                var state = GetSpriteStateAt(sprite, ms);

                double absW = selBmp.Width * Math.Abs(state.scaleX);
                double absH = selBmp.Height * Math.Abs(state.scaleY);
                var (ox, oy) = GetOriginOffset(sprite.Origin, absW, absH);

                canvas.Save();
                canvas.Translate((float)state.x + StoryboardXOffset, (float)state.y);
                canvas.RotateRadians((float)state.rot);

                using var selPaint = new SKPaint
                {
                    Color = new SKColor(232, 121, 249, 180),
                    StrokeWidth = 1f,
                    IsStroke = true
                };
                canvas.DrawRect((float)-ox, (float)-oy, (float)absW, (float)absH, selPaint);

                float hw = 6f;
                var handlePositions = new (float x, float y)[]
                {
        ((float)-ox,           (float)-oy),
        ((float)(-ox+absW/2),  (float)-oy),
        ((float)(-ox+absW),    (float)-oy),
        ((float)(-ox+absW),    (float)(-oy+absH/2)),
        ((float)(-ox+absW),    (float)(-oy+absH)),
        ((float)(-ox+absW/2),  (float)(-oy+absH)),
        ((float)-ox,           (float)(-oy+absH)),
        ((float)-ox,           (float)(-oy+absH/2)),
                };

                using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true };
                using var handleBorder = new SKPaint { Color = new SKColor(232, 121, 249), StrokeWidth = 1.5f, IsStroke = true, IsAntialias = true };

                foreach (var (hx, hy) in handlePositions)
                {
                    canvas.DrawRect(hx - hw, hy - hw, hw * 2, hw * 2, handleFill);
                    canvas.DrawRect(hx - hw, hy - hw, hw * 2, hw * 2, handleBorder);
                }

                float rotHx = (float)(-ox + absW / 2);
                float rotHy = (float)-oy - 24f;
                canvas.DrawLine(rotHx, (float)-oy, rotHx, rotHy + 6, selPaint);
                canvas.DrawCircle(rotHx, rotHy, 5f, handleFill);
                canvas.DrawCircle(rotHx, rotHy, 5f, handleBorder);

                canvas.Restore();
            }
        }

        // ── CARGAR BITMAP AL CACHE ────────────────────────
        private SKBitmap? LoadBitmap(string path)
        {
            if (_bitmapCache.TryGetValue(path, out var cached)) return cached;
            if (!File.Exists(path)) return null;
            try
            {
                var bmp = SKBitmap.Decode(path);
                if (bmp != null) _bitmapCache[path] = bmp;
                return bmp;
            }
            catch { return null; }
        }

        // ── FPS ───────────────────────────────────────────
        private void CmbFps_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_timer == null) return;
            _fps = CmbFps.SelectedIndex == 1 ? 60 : 30;
            _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        }

        private void CmbBeatDivisor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // índice 0 → 1/1, índice 4 → 1/5
            _project.BeatDivisor = CmbBeatDivisor.SelectedIndex + 1;
        }

        // ── Selección de sprite ───────────────────────────
        private void OnSpriteSelected(OsuSprite sprite)
        {
            _project.SelectedSprite = sprite;
            LayerPanel.SetSelectedSprite(sprite);
            Timeline.SetSelectedSprite(sprite);
            RefreshPropertiesPanel(sprite);
            OsuCanvas.InvalidateVisual();
        }

        private void RefreshPropertiesPanel(OsuSprite sprite)
        {
            // Usar el estado interpolado en el tiempo actual (igual que el render),
            // no los campos crudos del sprite — si no, el panel queda desincronizado
            // apenas hay un keyframe de por medio.
            double ms = CurrentMs;
            var state = GetSpriteStateAt(sprite, ms);

            PropX.Text = state.x.ToString("F0");
            PropY.Text = state.y.ToString("F0");
            PropScale.Text = state.scaleX.ToString("F2");
            PropRot.Text = (state.rot * 180 / Math.PI).ToString("F1") + "°";
            PropOpacity.Text = state.opacity.ToString("F2");
            PropStart.Text = sprite.StartTime.ToString();
            PropEnd.Text = sprite.EndTime.ToString();
        }

        // ── Mouse — Hit test manual ───────────────────────
        private OsuSprite? HitTest(SKPoint pt)
        {
            double ms = CurrentMs;
            foreach (var sprite in _project.Sprites.Reverse())
            {
                if (!sprite.Visible) continue;
                if (!_bitmapCache.TryGetValue(sprite.FilePath, out var bmp)) continue;

                // Igual que el render: si no se ve en este instante, no captura clicks
                // (antes un sprite en opacidad 0 tapaba a los de abajo y "robaba" la selección).
                if (ms < sprite.StartTime || ms > sprite.EndTime) continue;

                // Usar GetSpriteStateAt igual que el render
                var state = GetSpriteStateAt(sprite, ms);
                if (state.opacity <= 0) continue;

                double absW = bmp.Width * Math.Abs(state.scaleX);
                double absH = bmp.Height * Math.Abs(state.scaleY);
                var (ox, oy) = GetOriginOffset(sprite.Origin, absW, absH);

                var rect = new SKRect(
                    (float)(state.x + StoryboardXOffset - ox),
                    (float)(state.y - oy),
                    (float)(state.x + StoryboardXOffset - ox + absW),
                    (float)(state.y - oy + absH));

                if (rect.Contains(pt)) return sprite;
            }
            return null;
        }

        private SKPoint WpfToCanvas(System.Windows.Point pt)
        {
            return new SKPoint((float)pt.X, (float)pt.Y);
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pt = WpfToCanvas(e.GetPosition(OsuCanvas));
            double ms = CurrentMs;

            // ── Handle de transform activo ──
            if (_activeHandle != TransformHandle.None && _project.SelectedSprite != null)
            {
                var sprite = _project.SelectedSprite;
                var state = GetSpriteStateAt(sprite, ms);

                if (_activeHandle == TransformHandle.Rotate)
                {
                    double angle = Math.Atan2(pt.Y - state.y, pt.X - state.x);
                    double delta = angle - _handleDragStartAngle;

                    if (_handleDragCmdR != null)
                    {
                        if (ms <= _handleDragCmdR.StartTime)
                            _handleDragCmdR.StartValues[0] = _handleDragCmdRStart + delta;
                        else if (ms >= _handleDragCmdR.EndTime)
                            _handleDragCmdR.EndValues[0] = _handleDragCmdREnd + delta;
                        else
                        {
                            _handleDragCmdR.StartValues[0] = _handleDragCmdRStart + delta;
                            _handleDragCmdR.EndValues[0] = _handleDragCmdREnd + delta;
                        }
                        PropRot.Text = (GetSpriteStateAt(sprite, ms).rot * 180 / Math.PI).ToString("F2") + "°";
                    }
                    else
                    {
                        sprite.Rotation = _handleDragStartRot + delta;
                        PropRot.Text = (sprite.Rotation * 180 / Math.PI).ToString("F2") + "°";
                    }
                }
                else
                {
                    double dist = Math.Sqrt(Math.Pow(pt.X - state.x, 2) + Math.Pow(pt.Y - state.y, 2));
                    double distOrig = Math.Sqrt(Math.Pow(_handleDragStartPt.X - state.x, 2) + Math.Pow(_handleDragStartPt.Y - state.y, 2));
                    if (distOrig > 0)
                    {
                        double ratio = dist / distOrig;

                        if (_handleDragCmdS != null)
                        {
                            if (ms <= _handleDragCmdS.StartTime)
                                _handleDragCmdS.StartValues[0] = Math.Max(0.01, _handleDragCmdSStart * ratio);
                            else if (ms >= _handleDragCmdS.EndTime)
                                _handleDragCmdS.EndValues[0] = Math.Max(0.01, _handleDragCmdSEnd * ratio);
                            else
                            {
                                _handleDragCmdS.StartValues[0] = Math.Max(0.01, _handleDragCmdSStart * ratio);
                                _handleDragCmdS.EndValues[0] = Math.Max(0.01, _handleDragCmdSEnd * ratio);
                            }
                            PropScale.Text = GetSpriteStateAt(sprite, ms).scaleX.ToString("F2");
                        }
                        else
                        {
                            sprite.Scale = Math.Max(0.01, _handleDragStartScale * ratio);
                            PropScale.Text = sprite.Scale.ToString("F2");
                        }
                    }
                }

                OsuCanvas.InvalidateVisual();
                return;
            }

            // ── Drag normal ──
            if (_draggingSprite == null) return;

            double newX = pt.X - _dragOffset.X - StoryboardXOffset;
            double newY = pt.Y - _dragOffset.Y;

            var cmdM = GetActiveOrLastCommand(_draggingSprite, CommandType.M, ms);
            if (cmdM != null)
            {
                double dx = newX - _draggingSprite.X;
                double dy = newY - _draggingSprite.Y;

                if (ms <= cmdM.StartTime)
                {
                    cmdM.StartValues[0] += dx;
                    cmdM.StartValues[1] += dy;
                }
                else if (ms >= cmdM.EndTime)
                {
                    cmdM.EndValues[0] += dx;
                    cmdM.EndValues[1] += dy;
                }
                else
                {
                    cmdM.StartValues[0] += dx;
                    cmdM.StartValues[1] += dy;
                    cmdM.EndValues[0] += dx;
                    cmdM.EndValues[1] += dy;
                }
            }

            _draggingSprite.X = newX;
            _draggingSprite.Y = newY;
            PropX.Text = newX.ToString("F0");
            PropY.Text = newY.ToString("F0");
            OsuCanvas.InvalidateVisual();
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var pt = WpfToCanvas(e.GetPosition(OsuCanvas));
            double ms = CurrentMs;

            if (_project.SelectedSprite != null &&
                _bitmapCache.TryGetValue(_project.SelectedSprite.FilePath, out var selBmp))
            {
                var handle = GetHandleAt(pt, _project.SelectedSprite, selBmp, ms);
                if (handle != TransformHandle.None)
                {
                    _dragTx?.Dispose();
                    _dragTx = _undoRedo.BeginTransaction(
                        handle == TransformHandle.Rotate ? "Rotar sprite" : "Escalar sprite",
                        _project.SelectedSprite);
                    _activeHandle = handle;
                    _handleDragStartPt = pt;

                    var state = GetSpriteStateAt(_project.SelectedSprite, ms);
                    _handleDragStartAngle = Math.Atan2(pt.Y - state.y, pt.X - state.x);
                    _handleDragStartScale = state.scaleX;
                    _handleDragStartRot = state.rot;

                    _handleDragCmdS = GetActiveOrLastCommand(_project.SelectedSprite, CommandType.S, ms);
                    if (_handleDragCmdS != null)
                    {
                        _handleDragCmdSStart = _handleDragCmdS.StartValues[0];
                        _handleDragCmdSEnd = _handleDragCmdS.EndValues[0];
                    }

                    _handleDragCmdR = GetActiveOrLastCommand(_project.SelectedSprite, CommandType.R, ms);
                    if (_handleDragCmdR != null)
                    {
                        _handleDragCmdRStart = _handleDragCmdR.StartValues[0];
                        _handleDragCmdREnd = _handleDragCmdR.EndValues[0];
                    }

                    OsuCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            var sprite = HitTest(pt);
            if (sprite != null)
            {
                _dragTx?.Dispose();
                _dragTx = _undoRedo.BeginTransaction("Mover sprite", sprite);
                _draggingSprite = sprite;
                _dragStartSpriteX = sprite.X;
                _dragStartSpriteY = sprite.Y;
                var state = GetSpriteStateAt(sprite, ms);
                _dragOffset = new SKPoint(pt.X - ((float)state.x + StoryboardXOffset), pt.Y - (float)state.y);
                OnSpriteSelected(sprite);
                TxtProjectName.Text = $"[{sprite.Name}]  X:{sprite.X:F0} Y:{sprite.Y:F0}  cmds:{sprite.Commands.Count}";
            }

            OsuCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeHandle != TransformHandle.None)
            {
                _dragTx?.Dispose();   // cierra el paso: registra solo si hubo cambios
                _dragTx = null;
                _activeHandle = TransformHandle.None;
                OsuCanvas.ReleaseMouseCapture();
                if (_project.SelectedSprite != null)
                    RefreshPropertiesPanel(_project.SelectedSprite);
                return;
            }

            if (_draggingSprite != null)
            {
                _dragTx?.Dispose();   // incluye los cambios a cmdM.StartValues/EndValues hechos durante el drag
                _dragTx = null;
                Timeline.RedrawTracks(_project.Sprites);
            }

            _draggingSprite = null;
            OsuCanvas.ReleaseMouseCapture();
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pt = WpfToCanvas(e.GetPosition(OsuCanvas));
            var sprite = HitTest(pt);
            if (sprite == null)
            {
                var bgMenu = new ContextMenu();
                var miSpec = new MenuItem { Header = "Espectro de audio..." };
                miSpec.Click += (_, __) => OpenSpectrumDialog();
                bgMenu.Items.Add(miSpec);
                bgMenu.PlacementTarget = OsuCanvas;
                bgMenu.IsOpen = true;
                e.Handled = true;
                return;
            }

            OnSpriteSelected(sprite);
            // ... el resto del método queda igual

            OnSpriteSelected(sprite);

            var menu = new ContextMenu();

            var miDup = new MenuItem { Header = "Duplicar" };
            miDup.Click += (_, __) => PasteSprite(sprite, atPlayhead: false);
            menu.Items.Add(miDup);

            var miDel = new MenuItem { Header = "Eliminar" };
            miDel.Click += (_, __) => DeleteSelectedSprite();
            menu.Items.Add(miDel);

            menu.Items.Add(new Separator());

            var miEfectos = new MenuItem { Header = "Efectos" };
            var miLoop = new MenuItem { Header = "Loop al ritmo..." };
            miLoop.Click += (_, __) => OpenBeatLoopDialog(sprite);
            miEfectos.Items.Add(miLoop);
            var miGlow = new MenuItem { Header = "Glow" };
            foreach (var (label, sigma) in new[] { ("Suave", 6f), ("Medio", 12f), ("Fuerte", 24f) })
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, __) => AddGlow(sprite, sigma);
                miGlow.Items.Add(mi);
            }
            miEfectos.Items.Add(miGlow);
            menu.Items.Add(miEfectos);

            menu.PlacementTarget = OsuCanvas;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private TransformHandle GetHandleAt(SKPoint pt, OsuSprite sprite, SKBitmap bmp, double ms)
        {
            var state = GetSpriteStateAt(sprite, ms);
            double absW = bmp.Width * Math.Abs(state.scaleX);
            double absH = bmp.Height * Math.Abs(state.scaleY);
            var (ox, oy) = GetOriginOffset(sprite.Origin, absW, absH);

            float dx = pt.X - ((float)state.x + StoryboardXOffset);
            float dy = pt.Y - (float)state.y;
            float cos = (float)Math.Cos(-state.rot);
            float sin = (float)Math.Sin(-state.rot);
            float lx = dx * cos - dy * sin;
            float ly = dx * sin + dy * cos;

            float hw = 10f;

            var positions = new (float x, float y, TransformHandle h)[]
            {
        ((float)-ox,           (float)-oy,          TransformHandle.TopLeft),
        ((float)(-ox+absW/2),  (float)-oy,          TransformHandle.Top),
        ((float)(-ox+absW),    (float)-oy,          TransformHandle.TopRight),
        ((float)(-ox+absW),    (float)(-oy+absH/2), TransformHandle.Right),
        ((float)(-ox+absW),    (float)(-oy+absH),   TransformHandle.BottomRight),
        ((float)(-ox+absW/2),  (float)(-oy+absH),   TransformHandle.Bottom),
        ((float)-ox,           (float)(-oy+absH),   TransformHandle.BottomLeft),
        ((float)-ox,           (float)(-oy+absH/2), TransformHandle.Left),
        ((float)(-ox+absW/2),  (float)(-oy-24),     TransformHandle.Rotate),
            };

            foreach (var (hx, hy, handle) in positions)
                if (Math.Abs(lx - hx) <= hw && Math.Abs(ly - hy) <= hw)
                    return handle;

            return TransformHandle.None;
        }

        // ── ProcessFile ───────────────────────────────────
        private void ProcessFile(string file)
        {
            var ext = Path.GetExtension(file).ToLower();

            if (ext == ".mp3" || ext == ".ogg")
            {
                var audioBefore = CaptureProjectState();
                _audioPlayer.Open(new Uri(file));
                _project.AudioPath = file;
                TxtProjectName.Text = $"beatmap: {Path.GetFileName(file)}";
                _undoRedo.Record(new ProjectStateCommand("Cargar audio", audioBefore, CaptureProjectState(), ApplyProjectState));
                return;
            }

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                var sprite = _project.AddSprite(file);
                var bmp = LoadBitmap(file);

                System.Diagnostics.Debug.WriteLine($"Sprite creado: X={sprite.X} Y={sprite.Y} Scale={sprite.Scale} Origin={sprite.Origin} bmp={bmp?.Width}x{bmp?.Height}");

                if (bmp != null)
                {
                    float scaleToFit = Math.Min(854f / bmp.Width, 480f / bmp.Height);
                    if (scaleToFit < 1f)
                        sprite.Scale = Math.Round(scaleToFit, 3);
                }

                System.Diagnostics.Debug.WriteLine($"Después: X={sprite.X} Y={sprite.Y} Scale={sprite.Scale}");

                // AddSprite ya lo agregó al proyecto: lo registramos como paso deshacible
                _undoRedo.Record(new AddSpritesCommand(_project, new[] { sprite }));
                OnSpriteSelected(sprite);
                OsuCanvas.InvalidateVisual();
            }
        }

        private void AddTextSprites(TextSpec spec, bool perLetter)
        {
            var created = new List<OsuSprite>();

            if (!perLetter)
            {
                var sp = _project.AddSprite(_textService.EnsurePng(spec));
                sp.Text = spec;
                sp.Name = "texto: " + spec.Text;
                created.Add(sp);
            }
            else
            {
                foreach (var (letter, cx) in _textService.LayoutLetters(spec, out _))
                {
                    var sp = _project.AddSprite(_textService.EnsurePng(letter));
                    sp.Text = letter;
                    sp.Name = $"letra '{letter.Text}'";
                    sp.X = 320 + cx;          // 320 = centro X que usa AddSprite
                    created.Add(sp);
                }
            }

            if (created.Count == 0) return;
            foreach (var sp in created) LoadBitmap(sp.FilePath);
            _undoRedo.Record(new AddSpritesCommand(_project, created.ToArray(), $"Agregar texto \"{spec.Text}\""));
            OnSpriteSelected(created[^1]);
            OsuCanvas.InvalidateVisual();
        }

        private void BtnAddText_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new TextSpriteWindow { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;
            AddTextSprites(dlg.Result, dlg.PerLetter);
        }

        // ── Playback ──────────────────────────────────────
        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_audioPlayer.Source == null)
            {
                MessageBox.Show("Arrastrá un archivo de audio (.mp3 / .ogg) primero.");
                return;
            }
            if (_timer.IsEnabled)
            {
                _currentMs = _audioPlayer.Position.TotalMilliseconds;   // deja el reloj propio donde quedó el audio
                _audioPlayer.Pause();
                _timer.Stop();
                BtnPlay.Content = "▶";
            }
            else
            {
                _audioPlayer.Position = TimeSpan.FromMilliseconds(_currentMs);   // arranca desde el playhead
                _audioPlayer.Play();
                _timer.Start();
                BtnPlay.Content = "⏸";
            }
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentTime(0);
        }

        private void BtnPrevFrame_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentTime(_currentMs - 1000.0 / _fps);
        }

        private void BtnNextFrame_Click(object sender, RoutedEventArgs e)
        {
            SetCurrentTime(_currentMs + 1000.0 / _fps);
        }

        private void BtnEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_audioPlayer.NaturalDuration.HasTimeSpan)
            {
                SetCurrentTime(_audioPlayer.NaturalDuration.TimeSpan.TotalMilliseconds);
            }
        }

        // ── Import OSB ────────────────────────────────────
        private void BtnImportOsb_Click(object sender, RoutedEventArgs e)
        {
            var songsFolder = BeatmapLibraryService.GetDefaultSongsFolder();
            if (songsFolder == null)
            {
                var folderDlg = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccioná tu carpeta Songs de osu!" };
                if (folderDlg.ShowDialog() != true) return;
                songsFolder = folderDlg.FolderName;
            }

            var beatmaps = _beatmapLibrary.ScanSongsFolder(songsFolder);
            if (beatmaps.Count == 0)
            {
                MessageBox.Show("No se encontraron beatmaps con storyboard (.osb) en esa carpeta.", "Sin resultados");
                return;
            }

            var picker = new BeatmapPickerWindow(beatmaps) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedBeatmap == null) return;

            var chosen = picker.SelectedBeatmap;
            _currentMap = chosen;

            // Foto del estado anterior para poder deshacer el import completo.
            // (Ya no se liberan los bitmaps ni se limpia el historial: el undo los necesita.)
            var stateBefore = CaptureProjectState();
            _project.Sprites.Clear();

            // Timing (BPM real) — buscamos el .osu de esa carpeta y parseamos sus timing points
            var osuFiles = Directory.GetFiles(chosen.FolderPath, "*.osu");
            _project.TimingPoints = osuFiles.Length > 0
                ? _beatmapLibrary.ParseTimingPoints(osuFiles[0])
                : new();

            var redLine = _project.TimingPoints.FirstOrDefault(t => t.Uninherited);
            if (redLine != null)
                TxtProjectName.Text = $"BPM detectado: {60000.0 / redLine.BeatLength:F1}";

            // Fondo (ya resuelto por BeatmapLibraryService, sin volver a parsear el .osu)
            _bgPath = chosen.BackgroundPath;
            if (_bgPath != null) LoadBitmap(_bgPath);

            // Audio automático
            _project.AudioPath = chosen.AudioPath ?? "";
            if (chosen.AudioPath != null)
                _audioPlayer.Open(new Uri(chosen.AudioPath));

            // Sprites
            var imported = _osbImport.Import(chosen.OsbPath, chosen.FolderPath);
            int totalEnd = 0;

            foreach (var sprite in imported)
            {
                if (_bgPath != null && Path.GetFileName(sprite.FilePath)
                    .Equals(Path.GetFileName(_bgPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                _project.Sprites.Add(sprite);
                totalEnd = Math.Max(totalEnd, sprite.EndTime);
                LoadBitmap(sprite.FilePath);
            }

            if (totalEnd > 0) _project.TotalDuration = totalEnd;
            Timeline.SetProject(_project);
            TxtProjectName.Text = $"{chosen.Artist} - {chosen.Title}";
            OsuCanvas.InvalidateVisual();
            _undoRedo.Record(new ProjectStateCommand("Importar storyboard", stateBefore, CaptureProjectState(), ApplyProjectState));
            MessageBox.Show($"Importados {imported.Count} sprites de \"{chosen.Title}\".", "Import OK");
        }

        // ── Nuevo SB desde un mapa que todavía NO tiene .osb ──
        // Trae timing, audio y bg del mapa elegido, pero arranca con el lienzo de sprites vacío.
        private void BtnNewSbFromMap_Click(object sender, RoutedEventArgs e)
        {
            var songsFolder = BeatmapLibraryService.GetDefaultSongsFolder();
            if (songsFolder == null)
            {
                var folderDlg = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccioná tu carpeta Songs de osu!" };
                if (folderDlg.ShowDialog() != true) return;
                songsFolder = folderDlg.FolderName;
            }

            var beatmaps = _beatmapLibrary.ScanSongsFolderWithoutSb(songsFolder);
            if (beatmaps.Count == 0)
            {
                MessageBox.Show("No se encontraron mapas SIN storyboard en esa carpeta (todos ya tienen .osb, o no hay .osu con metadata válida).", "Sin resultados");
                return;
            }

            var picker = new BeatmapPickerWindow(beatmaps,
                "Elegí el mapa sobre el que querés armar un storyboard nuevo (doble click)")
            { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedBeatmap == null) return;

            var chosen = picker.SelectedBeatmap;
            _currentMap = chosen;

            // Proyecto en blanco: nada de sprites, arrancás de cero como pediste.
            // Foto del estado anterior para poder deshacerlo (los bitmaps se conservan en el cache).
            var stateBefore = CaptureProjectState();
            _project.Sprites.Clear();

            // Timing (BPM real) del .osu elegido
            _project.TimingPoints = !string.IsNullOrEmpty(chosen.OsuPath)
                ? _beatmapLibrary.ParseTimingPoints(chosen.OsuPath)
                : new();

            // Fondo y audio del mapa, listos para dibujar encima
            _bgPath = chosen.BackgroundPath;
            if (_bgPath != null) LoadBitmap(_bgPath);

            if (chosen.AudioPath != null)
                _audioPlayer.Open(new Uri(chosen.AudioPath));

            _project.AudioPath = chosen.AudioPath ?? "";
            // TotalDuration se termina de fijar solo al abrir el audio (evento MediaOpened, ya wireado en el constructor)

            Timeline.SetProject(_project);
            OsuCanvas.InvalidateVisual();
            _undoRedo.Record(new ProjectStateCommand("Nuevo SB desde mapa", stateBefore, CaptureProjectState(), ApplyProjectState));

            var redLine = _project.TimingPoints.FirstOrDefault(t => t.Uninherited);
            var bpmMsg = redLine != null ? $"\nBPM detectado: {60000.0 / redLine.BeatLength:F1}" : "\n(sin timing points detectados)";
            TxtProjectName.Text = $"[Nuevo SB] {chosen.Artist} - {chosen.Title}";
            MessageBox.Show($"Proyecto nuevo listo sobre \"{chosen.Title}\".{bpmMsg}\nAhora arrastrá tus sprites al canvas.", "Nuevo SB");
        }

        private void LoadProject(string path)
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<ProjectSaveData>(File.ReadAllText(path));
            if (data == null) return;

            var stateBefore = CaptureProjectState();
            _project.Sprites.Clear();
            _currentMap = null;   // un proyecto cargado no debe exportar al mapa anterior

            _project.AudioPath = data.AudioPath;
            _project.TotalDuration = data.TotalDuration;

            if (!string.IsNullOrEmpty(data.AudioPath) && File.Exists(data.AudioPath))
                _audioPlayer.Open(new Uri(data.AudioPath));

            foreach (var sd in data.Sprites)
            {
                var sprite = new OsuSprite
                {
                    FilePath = sd.FilePath,
                    Name = sd.Name,
                    X = sd.X,
                    Y = sd.Y,
                    Scale = sd.Scale,
                    Rotation = sd.Rotation,
                    Opacity = sd.Opacity,
                    Visible = sd.Visible,
                    Layer = sd.Layer,
                    Origin = sd.Origin,
                    Text = sd.Text,
                    StartTime = sd.StartTime,
                    EndTime = sd.EndTime
                };
                foreach (var cd in sd.Commands)
                    if (Enum.TryParse<CommandType>(cd.Type, out var ct))
                        sprite.Commands.Add(new OsuCommand
                        {
                            Type = ct,
                            Easing = cd.Easing,
                            StartTime = cd.StartTime,
                            EndTime = cd.EndTime,
                            StartValues = cd.StartValues,
                            EndValues = cd.EndValues,
                            Parameter = cd.Parameter,
                        });
                foreach (var td in sd.Triggers)
                {
                    var trigger = new OsuSpriteTrigger
                    {
                        TriggerName = td.TriggerName,
                        StartTime = td.StartTime,
                        EndTime = td.EndTime,
                        Group = td.Group
                    };
                    foreach (var cd in td.Commands)
                        if (Enum.TryParse<CommandType>(cd.Type, out var ct))
                            trigger.Commands.Add(new OsuCommand
                            {
                                Type = ct,
                                Easing = cd.Easing,
                                StartTime = cd.StartTime,
                                EndTime = cd.EndTime,
                                StartValues = cd.StartValues,
                                EndValues = cd.EndValues,
                                Parameter = cd.Parameter
                            });
                    sprite.Triggers.Add(trigger);
                }

                if (sprite.Text != null)
                    sprite.FilePath = _textService.EnsurePng(sprite.Text);   // regenera si falta (otra PC, caché borrada)
                _project.Sprites.Add(sprite);
                LoadBitmap(sprite.FilePath);
            }

            Timeline.SetProject(_project);
            OsuCanvas.InvalidateVisual();
            _undoRedo.Record(new ProjectStateCommand("Cargar proyecto", stateBefore, CaptureProjectState(), ApplyProjectState));
            _undoRedo.MarkSaved();

            var missing = _project.Sprites
                .Where(s => s.Text != null && !TextSpriteService.IsFontInstalled(s.Text.FontFamily))
                .Select(s => s.Text!.FontFamily).Distinct().ToList();
            if (missing.Count > 0)
                MessageBox.Show("Estas fuentes no están instaladas y se usó otra en su lugar:\n" +
                                string.Join("\n", missing), "Fuentes faltantes");

            MessageBox.Show("Proyecto cargado.", "Cargar");
        }

        // ── Keyframe ──────────────────────────────────────
        private void BtnAddKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (_project.SelectedSprite == null) { MessageBox.Show("Seleccioná un sprite primero."); return; }
            var sprite = _project.SelectedSprite;
            int t = (int)Math.Round(CurrentMs);
            var st = GetSpriteStateAt(sprite, t);   // lo que se ve en el canvas en este instante
            using var tx = _undoRedo.BeginTransaction("Agregar keyframe", sprite);

            void CloseOpen(CommandType type, double[] vals)
            {
                var prev = sprite.Commands.Where(c => c.Type == type && c.StartTime < t)
                    .OrderByDescending(c => c.StartTime).FirstOrDefault();
                if (prev != null && prev.EndTime == prev.StartTime) { prev.EndTime = t; prev.EndValues = vals; }
                var ex = sprite.Commands.FirstOrDefault(c => c.Type == type && c.StartTime == t);
                if (ex != null) sprite.Commands.Remove(ex);
            }

            CloseOpen(CommandType.M, new[] { st.x, st.y });
            CloseOpen(CommandType.S, new[] { st.scaleX });
            CloseOpen(CommandType.R, new[] { st.rot });
            CloseOpen(CommandType.F, new[] { st.opacity });

            sprite.Commands.Add(new OsuCommand { Type = CommandType.M, StartTime = t, EndTime = t, StartValues = new[] { st.x, st.y }, EndValues = new[] { st.x, st.y } });
            sprite.Commands.Add(new OsuCommand { Type = CommandType.S, StartTime = t, EndTime = t, StartValues = new[] { st.scaleX }, EndValues = new[] { st.scaleX } });
            sprite.Commands.Add(new OsuCommand { Type = CommandType.R, StartTime = t, EndTime = t, StartValues = new[] { st.rot }, EndValues = new[] { st.rot } });
            sprite.Commands.Add(new OsuCommand { Type = CommandType.F, StartTime = t, EndTime = t, StartValues = new[] { st.opacity }, EndValues = new[] { st.opacity } });

            sprite.StartTime = Math.Min(sprite.StartTime, sprite.Commands.Min(c => c.StartTime));
            if (sprite.EndTime <= sprite.StartTime) sprite.EndTime = sprite.StartTime + 1000;

            _pending.Clear(); _pendingSprite = null;

            Timeline.RedrawTracks(_project.Sprites);
            RefreshPropertiesPanel(sprite);
            TxtProjectName.Text = $"keyframe → [{sprite.Name}] @ {t} ms";
        }

        // ── Undo/Redo ─────────────────────────────────────
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;

            if (ctrl && e.Key == Key.Z && !shift)
            {
                _undoRedo.Undo();   // el refresco de toda la UI lo hace OnHistoryReplayed
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
            {
                _undoRedo.Redo();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _project.SelectedSprite != null &&
                     Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                DeleteSelectedSprite();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.C && !typing)
            {
                int n = Timeline.CopySelectedKeyframes();
                if (n > 0)
                {
                    _spriteClipboard = null;   // lo último que copiaste es lo que se pega
                    TxtProjectName.Text = $"Copiados {n} keyframe{(n == 1 ? "" : "s")}";
                    e.Handled = true;
                }
                else if (_project.SelectedSprite != null)
                {
                    CopySelectedSprite();
                    Timeline.ClearKeyframeClipboard();
                    e.Handled = true;
                }
            }
            else if (ctrl && e.Key == Key.V && !typing)
            {
                if (Timeline.HasKeyframeClipboard)
                {
                    int n = Timeline.PasteKeyframes((int)Math.Round(CurrentMs));
                    if (n > 0)
                    {
                        if (_project.SelectedSprite != null) RefreshPropertiesPanel(_project.SelectedSprite);
                        TxtProjectName.Text = $"Pegados {n} keyframe{(n == 1 ? "" : "s")}  (Ctrl+Z para deshacer)";
                    }
                    e.Handled = true;
                }
                else if (_spriteClipboard != null)
                {
                    PasteSprite(_spriteClipboard, atPlayhead: true);
                    e.Handled = true;
                }
            }
            else if (ctrl && e.Key == Key.D && !typing && _project.SelectedSprite != null)
            {
                PasteSprite(_project.SelectedSprite, atPlayhead: false);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.L && !typing && _project.SelectedSprite != null)
            {
                TestBeatLoop();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.E && !typing)      // ← NUEVO
            {
                TestSpectrum();
                e.Handled = true;
            }
        }

        // ── Loop al ritmo (prueba) ─────────────────────────
        private void TestBeatLoop()
        {
            var sprite = _project.SelectedSprite;
            if (sprite == null) return;

            var service = new BeatLoopService();
            var loops = service.GenerateScalePulse(sprite, _project.TimingPoints, sprite.StartTime, sprite.EndTime);

            if (loops.Count == 0)
            {
                TxtProjectName.Text = "Sin timing points: no se generó ningún loop";
                return;
            }

            using (_undoRedo.BeginTransaction("Loop al ritmo (prueba)", sprite))

                sprite.Loops.AddRange(loops);

            RefreshPropertiesPanel(sprite);
            OsuCanvas.InvalidateVisual();
            TxtProjectName.Text = $"Generados {loops.Count} loops al ritmo  (Ctrl+Z para deshacer)";
        }

        // ── Espectro (prueba) ──────────────────────────────
        private async void TestSpectrum()
        {
            if (string.IsNullOrEmpty(_project.AudioPath)) { TxtProjectName.Text = "Cargá un audio primero"; return; }

            string path = _project.AudioPath;
            int end = _project.TotalDuration > 0 ? _project.TotalDuration : 30000;
            TxtProjectName.Text = "Analizando audio...";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var data = await Task.Run(() => new AudioSpectrumService().Analyze(path, 0, end, bands: 32, frameMs: 50));
                var smooth = AudioSpectrumService.Smooth(data);

                // promedio por banda: graves a la izquierda, agudos a la derecha
                var avg = Enumerable.Range(0, data.BandCount)
                    .Select(b => data.Frames.Average(fr => fr[b]))
                    .Select(v => v.ToString("0.00"));
                System.Diagnostics.Debug.WriteLine("Promedio por banda: " + string.Join(" ", avg));

                TxtProjectName.Text = $"Espectro OK: {data.Frames.Length} frames x {data.BandCount} bandas en {sw.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                TxtProjectName.Text = "Error de espectro: " + ex.Message;
            }
        }

        // ── Espectro de audio ──────────────────────────────
        private async void OpenSpectrumDialog()
        {
            if (string.IsNullOrEmpty(_project.AudioPath) || !File.Exists(_project.AudioPath))
            { MessageBox.Show("Arrastrá un archivo de audio (.mp3 / .ogg) primero."); return; }

            var dlg = new SpectrumWindow(_project.TotalDuration) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var o = dlg.Options;
            string audio = _project.AudioPath;
            TxtProjectName.Text = "Analizando audio...";

            try
            {
                var sprites = await Task.Run(() =>
                {
                    var raw = new AudioSpectrumService().Analyze(audio, o.RangeStart, o.RangeEnd, o.Bars, 1000 / o.Fps);
                    var smooth = AudioSpectrumService.Smooth(raw);
                    return new SpectrumBarsService().Generate(smooth, o);
                });

                foreach (var sp in sprites) _project.Sprites.Add(sp);
                LoadBitmap(sprites[0].FilePath);
                _undoRedo.Record(new AddSpritesCommand(_project, sprites.ToArray(), "Agregar espectro de audio"));
                OsuCanvas.InvalidateVisual();

                int cmds = sprites.Sum(s => s.Commands.Count);
                TxtProjectName.Text = $"Espectro: {sprites.Count} barras, {cmds:N0} comandos  (Ctrl+Z para deshacer)";
            }
            catch (Exception ex)
            {
                TxtProjectName.Text = "Error de espectro: " + ex.Message;
            }
        }

        private void OpenBeatLoopDialog(OsuSprite sprite)
        {
            var dlg = new BeatLoopWindow(sprite) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var service = new BeatLoopService();
            var loops = service.Generate(sprite, _project.TimingPoints, dlg.Options);

            if (loops.Count == 0)
            {
                TxtProjectName.Text = "Sin timing points en ese rango: no se generó ningún loop";
                return;
            }

            using (_undoRedo.BeginTransaction("Loop al ritmo", sprite))
            {
                // reemplaza loops previos que toquen los mismos comandos (MX, S, etc.)
                var types = loops.SelectMany(l => l.Commands.Select(c => c.Type)).ToHashSet();
                sprite.Loops.RemoveAll(l => l.Commands.Any(c => types.Contains(c.Type)));
                // el pulso de brillo reemplaza a la opacidad fija (si no, los dos F se pisarían)
                if (dlg.Options.Type == BeatEffectType.GlowPulse)
                    foreach (var f in sprite.Commands.Where(c => c.Type == CommandType.F).ToList())
                        sprite.Commands.Remove(f);
                sprite.Loops.AddRange(loops);
            }

            RefreshPropertiesPanel(sprite);
            OsuCanvas.InvalidateVisual();
            TxtProjectName.Text = $"Generados {loops.Count} loops al ritmo  (Ctrl+Z para deshacer)";
        }

        // ── Drag & Drop de archivos ───────────────────────
        private void Window_DragEnter(object sender, DragEventArgs e)
            => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            using (_undoRedo.BeginGroup("Soltar archivos"))
                foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
                    ProcessFile(f);
        }

        private void BtnAddSprite_Click(object sender, RoutedEventArgs e) => OpenImageDialog();
        private void OpenImageDialog()
        {
            var dlg = new OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg|Todos|*.*" };
            if (dlg.ShowDialog() == true) ProcessFile(dlg.FileName);
        }

        // ── Propiedades ───────────────────────────────────
        private void PropX_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPropX(); }
        private void PropX_LostFocus(object sender, RoutedEventArgs e) => ApplyPropX();
        private void ApplyPropX()
        {
            if (_project.SelectedSprite == null || !double.TryParse(PropX.Text, out double v)) return;
            var sprite = _project.SelectedSprite;
            double current = GetSpriteStateAt(sprite, CurrentMs).x;
            if (PanelValueUnchanged(v, current, 0.5)) return;
            using (_undoRedo.BeginTransaction("Editar X", sprite))
            {
                if (!ApplyPanelValue(sprite, CommandType.M, 0, "x", v))
                    sprite.X = v;
            }
            OsuCanvas.InvalidateVisual();
        }

        private void PropY_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPropY(); }
        private void PropY_LostFocus(object sender, RoutedEventArgs e) => ApplyPropY();
        private void ApplyPropY()
        {
            if (_project.SelectedSprite == null || !double.TryParse(PropY.Text, out double v)) return;
            var sprite = _project.SelectedSprite;
            double current = GetSpriteStateAt(sprite, CurrentMs).y;
            if (PanelValueUnchanged(v, current, 0.5)) return;
            using (_undoRedo.BeginTransaction("Editar Y", sprite))
            {
                if (!ApplyPanelValue(sprite, CommandType.M, 1, "y", v))
                    sprite.Y = v;
            }
            OsuCanvas.InvalidateVisual();
        }

        private void PropScale_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPropScale(); }
        private void PropScale_LostFocus(object sender, RoutedEventArgs e) => ApplyPropScale();
        private void ApplyPropScale()
        {
            if (_project.SelectedSprite == null || !double.TryParse(PropScale.Text, out double v)) return;
            var sprite = _project.SelectedSprite;
            v = Math.Max(0.01, v);
            double current = GetSpriteStateAt(sprite, CurrentMs).scaleX;
            if (PanelValueUnchanged(v, current, 0.005)) return;
            using (_undoRedo.BeginTransaction("Editar escala", sprite))
            {
                if (!ApplyPanelValue(sprite, CommandType.S, 0, "scale", v))
                    sprite.Scale = v;
            }
            OsuCanvas.InvalidateVisual();
        }

        private void PropRot_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPropRot(); }
        private void PropRot_LostFocus(object sender, RoutedEventArgs e) => ApplyPropRot();
        private void ApplyPropRot()
        {
            if (_project.SelectedSprite == null || !double.TryParse(PropRot.Text.Replace("°", "").Trim(), out double deg)) return;
            var sprite = _project.SelectedSprite;
            double current = GetSpriteStateAt(sprite, CurrentMs).rot;          // radianes
            if (PanelValueUnchanged(deg, current * 180 / Math.PI, 0.06)) return; // el panel muestra 1 decimal
            double rad = deg * Math.PI / 180;
            using (_undoRedo.BeginTransaction("Editar rotación", sprite))
            {
                if (!ApplyPanelValue(sprite, CommandType.R, 0, "rot", rad))
                    sprite.Rotation = rad;
            }
            OsuCanvas.InvalidateVisual();
        }

        private void PropOpacity_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyPropOpacity(); }
        private void PropOpacity_LostFocus(object sender, RoutedEventArgs e) => ApplyPropOpacity();
        private void ApplyPropOpacity()
        {
            if (_project.SelectedSprite == null || !double.TryParse(PropOpacity.Text, out double v)) return;
            var sprite = _project.SelectedSprite;
            v = Math.Clamp(v, 0, 1);
            double current = GetSpriteStateAt(sprite, CurrentMs).opacity;
            if (PanelValueUnchanged(v, current, 0.005)) return;
            using (_undoRedo.BeginTransaction("Editar opacidad", sprite))
            {
                if (!ApplyPanelValue(sprite, CommandType.F, 0, "opacity", v))
                    sprite.Opacity = v;
            }
            OsuCanvas.InvalidateVisual();
        }

        // ── Helpers ───────────────────────────────────────
        // Aplica un valor tipeado en el panel donde el render realmente lo lee.
        // Si la propiedad tiene keyframes, edita el comando vigente en el playhead.
        // Devuelve false si no hay comandos: en ese caso hay que escribir la propiedad base.
        private bool TryApplyToCommand(OsuSprite sprite, CommandType type, int index, double typed, double current)
        {
            double ms = CurrentMs;
            var cmd = GetActiveOrLastCommand(sprite, type, ms);
            if (cmd == null || index >= cmd.StartValues.Length || index >= cmd.EndValues.Length) return false;

            double d = typed - current;
            if (cmd.StartTime == cmd.EndTime || (ms > cmd.StartTime && ms < cmd.EndTime))
            {
                cmd.StartValues[index] += d;   // keyframe puntual o playhead dentro del tramo: desplaza ambos extremos
                cmd.EndValues[index] += d;
            }
            else if (ms <= cmd.StartTime) cmd.StartValues[index] += d;
            else cmd.EndValues[index] += d;
            return true;
        }

        // ── método nuevo: resuelve el comando vigente de TODOS los tipos en una sola pasada ──
        private void ResolveAllCommands(OsuSprite sprite, double ms)
        {
            Array.Clear(_scrActive, 0, CmdTypeCount);
            Array.Clear(_scrPast, 0, CmdTypeCount);
            Array.Clear(_scrFuture, 0, CmdTypeCount);

            foreach (var c in sprite.Commands)
            {
                int k = (int)c.Type;
                if (c.StartTime <= ms && c.EndTime >= ms)
                {
                    var a = _scrActive[k];
                    if (a == null || c.StartTime > a.StartTime) _scrActive[k] = c;
                }
                else if (c.EndTime < ms)
                {
                    var p = _scrPast[k];
                    if (p == null || c.EndTime > p.EndTime) _scrPast[k] = c;
                }
                else if (c.StartTime > ms)
                {
                    var f = _scrFuture[k];
                    if (f == null || c.StartTime < f.StartTime) _scrFuture[k] = c;
                }
            }

            bool hasLoops = sprite.Loops.Count > 0;
            for (int k = 0; k < CmdTypeCount; k++)
            {
                var cmd = _scrActive[k] ?? _scrPast[k] ?? _scrFuture[k];
                if (cmd == null && hasLoops)
                    cmd = ResolveLoopCommand(sprite, (CommandType)k, ms);
                _resolved[k] = cmd;
            }
        }
        private static OsuCommand? GetActiveOrLastCommand(OsuSprite sprite, CommandType type, double ms)
        {
            OsuCommand? active = null;
            OsuCommand? past = null;
            OsuCommand? future = null;

            foreach (var c in sprite.Commands)
            {
                if (c.Type != type) continue;

                if (c.StartTime <= ms && c.EndTime >= ms)
                {
                    if (active == null || c.StartTime > active.StartTime)
                        active = c;
                }
                else if (c.EndTime < ms)
                {
                    if (past == null || c.EndTime > past.EndTime)
                        past = c;
                }
                else if (c.StartTime > ms)
                {
                    if (future == null || c.StartTime < future.StartTime)
                        future = c;
                }
            }

            return active ?? past ?? future;
        }

        private (double x, double y, double scaleX, double scaleY, double rot, double opacity)

     GetSpriteStateAt(OsuSprite sprite, double ms)
        {
            ResolveAllCommands(sprite, ms);

            // ── Escala ──
            double scaleX = sprite.Scale, scaleY = sprite.Scale;
            var cmdV = _resolved[(int)CommandType.V];
            var cmdS = _resolved[(int)CommandType.S];
            if (cmdV != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdV.StartTime, cmdV.EndTime), cmdV.Easing);
                scaleX = ms < cmdV.StartTime ? cmdV.StartValues[0] : Lerp(cmdV.StartValues[0], cmdV.EndValues[0], t);
                scaleY = ms < cmdV.StartTime ? cmdV.StartValues[1] : Lerp(cmdV.StartValues[1], cmdV.EndValues[1], t);
            }
            else if (cmdS != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdS.StartTime, cmdS.EndTime), cmdS.Easing);
                double s = ms < cmdS.StartTime ? cmdS.StartValues[0] : Lerp(cmdS.StartValues[0], cmdS.EndValues[0], t);
                scaleX = scaleY = s;
            }

            // ── Posición ──
            double x = sprite.X, y = sprite.Y;
            var cmdM = _resolved[(int)CommandType.M];
            if (cmdM != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdM.StartTime, cmdM.EndTime), cmdM.Easing);
                x = ms < cmdM.StartTime ? cmdM.StartValues[0] : Lerp(cmdM.StartValues[0], cmdM.EndValues[0], t);
                y = ms < cmdM.StartTime ? cmdM.StartValues[1] : Lerp(cmdM.StartValues[1], cmdM.EndValues[1], t);
            }
            var cmdMX = _resolved[(int)CommandType.MX];
            if (cmdMX != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdMX.StartTime, cmdMX.EndTime), cmdMX.Easing);
                x = ms < cmdMX.StartTime ? cmdMX.StartValues[0] : Lerp(cmdMX.StartValues[0], cmdMX.EndValues[0], t);
            }
            var cmdMY = _resolved[(int)CommandType.MY];
            if (cmdMY != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdMY.StartTime, cmdMY.EndTime), cmdMY.Easing);
                y = ms < cmdMY.StartTime ? cmdMY.StartValues[0] : Lerp(cmdMY.StartValues[0], cmdMY.EndValues[0], t);
            }

            // ── Rotación ──
            double rot = sprite.Rotation;
            var cmdR = _resolved[(int)CommandType.R];
            if (cmdR != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdR.StartTime, cmdR.EndTime), cmdR.Easing);
                rot = ms < cmdR.StartTime ? cmdR.StartValues[0] : Lerp(cmdR.StartValues[0], cmdR.EndValues[0], t);
            }

            // ── Opacidad ──
            double opacity = sprite.Opacity;
            var cmdF = _resolved[(int)CommandType.F];
            if (cmdF != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdF.StartTime, cmdF.EndTime), cmdF.Easing);
                opacity = ms < cmdF.StartTime ? cmdF.StartValues[0] : Lerp(cmdF.StartValues[0], cmdF.EndValues[0], t);
            }

            // ── Color ──
            var cmdC = _resolved[(int)CommandType.C];
            if (cmdC != null)
            {
                double t = ApplyEasing(Lerp01(ms, cmdC.StartTime, cmdC.EndTime), cmdC.Easing);
                double r, g, b;
                if (ms < cmdC.StartTime)
                {
                    r = cmdC.StartValues[0]; g = cmdC.StartValues[1]; b = cmdC.StartValues[2];
                }
                else
                {
                    r = Lerp(cmdC.StartValues[0], cmdC.EndValues[0], t);
                    g = Lerp(cmdC.StartValues[1], cmdC.EndValues[1], t);
                    b = Lerp(cmdC.StartValues[2], cmdC.EndValues[2], t);
                }
                opacity *= (r * 0.299 + g * 0.587 + b * 0.114) / 255.0;
            }

            if (_pendingSprite == sprite && Math.Abs(ms - _pendingMs) < 1)
            {
                if (_pending.TryGetValue("x", out var px)) x = px;
                if (_pending.TryGetValue("y", out var py)) y = py;
                if (_pending.TryGetValue("scale", out var ps)) { scaleX = ps; scaleY = ps; }
                if (_pending.TryGetValue("rot", out var pr)) rot = pr;
                if (_pending.TryGetValue("opacity", out var po)) opacity = po;
            }

            return (x, y, scaleX, scaleY, rot, opacity);
        }

        private static double Lerp01(double ms, int start, int end)
            => end <= start ? 1.0 : Math.Clamp((ms - start) / (double)(end - start), 0.0, 1.0);

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static (double ox, double oy) GetOriginOffset(SpriteOrigin origin, double w, double h) => origin switch
        {
            SpriteOrigin.TopLeft => (0, 0),
            SpriteOrigin.TopCentre => (w / 2, 0),
            SpriteOrigin.TopRight => (w, 0),
            SpriteOrigin.CentreLeft => (0, h / 2),
            SpriteOrigin.Centre => (w / 2, h / 2),
            SpriteOrigin.CentreRight => (w, h / 2),
            SpriteOrigin.BottomLeft => (0, h),
            SpriteOrigin.BottomCentre => (w / 2, h),
            SpriteOrigin.BottomRight => (w, h),
            _ => (w / 2, h / 2)
        };

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { FileName = "proyecto", DefaultExt = ".osbs", Filter = "Osu Storyboard Save|*.osbs" };
            if (dlg.ShowDialog() == true) SaveProject(dlg.FileName);
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Osu Storyboard Save|*.osbs" };
            if (dlg.ShowDialog() == true) LoadProject(dlg.FileName);
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_project.Sprites.Count == 0) { MessageBox.Show("No hay sprites.", "Export"); return; }

            // Caso 1: hay un mapa asociado → exporta directo a su carpeta, sin diálogo
            if (_currentMap != null && Directory.Exists(_currentMap.FolderPath))
            {
                string osbName = !string.IsNullOrEmpty(_currentMap.OsbPath)
                    ? Path.GetFileName(_currentMap.OsbPath)   // mapa que ya tenía .osb: mismo nombre
                    : SafeFileName($"{_currentMap.Artist} - {_currentMap.Title} ({_currentMap.Creator}).osb");

                string target = Path.Combine(_currentMap.FolderPath, osbName);
                if (File.Exists(target))
                {
                    var r = MessageBox.Show($"Ya existe \"{osbName}\" en la carpeta del mapa.\n¿Sobrescribirlo?",
                        "Export", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }

                _exportService.Export(_project, _currentMap.FolderPath, osbName);
                _currentMap.OsbPath = target;
                MessageBox.Show($"Exportado a:\n{target}", "Export OK");
                return;
            }

            // Caso 2: sin mapa asociado → diálogo como antes
            var dlg = new SaveFileDialog { FileName = "storyboard", DefaultExt = ".osb", Filter = "OSB|*.osb" };
            if (dlg.ShowDialog() == true)
            {
                _exportService.Export(_project, Path.GetDirectoryName(dlg.FileName)!, Path.GetFileName(dlg.FileName));
                MessageBox.Show("Exportado.", "Export OK");
            }
        }

        // Quita caracteres que Windows no permite en nombres de archivo
        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "");
            return name;
        }

        private void SaveProject(string path)
        {
            var data = new ProjectSaveData
            {
                AudioPath = _project.AudioPath,
                TotalDuration = _project.TotalDuration,
                Sprites = _project.Sprites.Select(s => new SpriteSaveData
                {
                    FilePath = s.FilePath,
                    Name = s.Name,
                    X = s.X,
                    Y = s.Y,
                    Scale = s.Scale,
                    Rotation = s.Rotation,
                    Opacity = s.Opacity,
                    Visible = s.Visible,
                    Layer = s.Layer,
                    Origin = s.Origin,
                    Text = s.Text,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    Commands = s.Commands.Select(c => new CommandSaveData
                    {
                        Type = c.Type.ToString(),
                        Easing = c.Easing,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        StartValues = c.StartValues,
                        EndValues = c.EndValues,
                        Parameter = c.Parameter
                    }).ToList(),
                    Triggers = s.Triggers.Select(t => new TriggerSaveData
                    {
                        TriggerName = t.TriggerName,
                        StartTime = t.StartTime,
                        EndTime = t.EndTime,
                        Group = t.Group,
                        Commands = t.Commands.Select(c => new CommandSaveData
                        {
                            Type = c.Type.ToString(),
                            Easing = c.Easing,
                            StartTime = c.StartTime,
                            EndTime = c.EndTime,
                            StartValues = c.StartValues,
                            EndValues = c.EndValues,
                            Parameter = c.Parameter
                        }).ToList()
                    }).ToList()
                }).ToList()
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            _undoRedo.MarkSaved();
            MessageBox.Show("Proyecto guardado.", "Guardar");
        }

        // ── Undo/redo global: helpers ─────────────────────
        private double _currentMs;                 // reloj propio del editor: fuente de verdad del tiempo actual
        private double CurrentMs => _currentMs;

        // Mueve el "ahora" del editor sin depender de que el audio esté sonando.
        private void SetCurrentTime(double ms, bool updatePlayhead = true)
        {
            double max = _project.TotalDuration > 0 ? _project.TotalDuration : double.MaxValue;
            ms = Math.Clamp(ms, 0, max);
            _currentMs = ms;
            _pending.Clear(); _pendingSprite = null;
            _audioPlayer.Position = TimeSpan.FromMilliseconds(ms);
            TxtCurrentTime.Text = TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss\.fff");
            if (updatePlayhead) Timeline.UpdatePlayhead(ms);
            if (_project.SelectedSprite != null)
                RefreshPropertiesPanel(_project.SelectedSprite);
            OsuCanvas.InvalidateVisual();
        }

        // RefreshPropertiesPanel escribe valores interpolados y redondeados en los campos, y LostFocus los
        // re-aplicaba al sprite (pisando el valor real y ensuciando el historial). Si lo que hay en el campo
        // coincide con lo que se está mostrando, no hay nada que aplicar.
        private static bool PanelValueUnchanged(double typed, double current, double tolerance)
            => Math.Abs(typed - current) < tolerance;

        private ProjectState CaptureProjectState() => new(
            _project.Sprites.ToList(), _project.TimingPoints, _project.AudioPath,
            _project.TotalDuration, _bgPath);

        // Aplica un estado de proyecto guardado (lo usan los pasos de undo/redo de import, nuevo SB, cargar y audio)
        private void ApplyProjectState(ProjectState s)
        {
            if (!_project.Sprites.SequenceEqual(s.Sprites))
            {
                _project.Sprites.Clear();
                foreach (var sp in s.Sprites) _project.Sprites.Add(sp);
            }

            _project.TimingPoints = s.TimingPoints;
            _bgPath = s.BgPath;
            _project.TotalDuration = s.TotalDuration;

            if (!string.Equals(_project.AudioPath, s.AudioPath, StringComparison.OrdinalIgnoreCase))
            {
                _project.AudioPath = s.AudioPath;
                if (!string.IsNullOrEmpty(s.AudioPath) && File.Exists(s.AudioPath))
                    OpenAudioSafe(s.AudioPath);   // MediaOpened vuelve a fijar TotalDuration con la duración real
                else
                    _audioPlayer.Close();
            }

            _project.SelectedSprite = null;
        }

        // Tras un import/undo puede haber sprites cuyo bitmap no está en el cache (p.ej. después de rehacer)
        private void EnsureBitmaps()
        {
            if (_bgPath != null) LoadBitmap(_bgPath);
            foreach (var s in _project.Sprites) LoadBitmap(s.FilePath);
        }

        // Sincroniza TODA la UI con el modelo después de un undo/redo (o de una acción sin evento propio)
        private void RefreshAfterHistory()
        {
            EnsureBitmaps();

            var sel = _project.SelectedSprite;
            if (sel != null && !_project.Sprites.Contains(sel))
            {
                sel = null;
                _project.SelectedSprite = null;
            }

            LayerPanel.SetSelectedSprite(sel);
            Timeline.SetSelectedSprite(sel);
            Timeline.RedrawTracks(_project.Sprites);
            if (sel != null) RefreshPropertiesPanel(sel);
            OsuCanvas.InvalidateVisual();
        }

        private void OnHistoryReplayed(string description, bool wasUndo)
        {
            RefreshAfterHistory();
            TxtProjectName.Text = $"{(wasUndo ? "Deshacer" : "Rehacer")}: {description}";
        }

        private void UpdateTitle()
            => Title = _undoRedo.IsDirty ? _baseTitle + " •" : _baseTitle;

        private void DeleteSelectedSprite()
        {
            var sprite = _project.SelectedSprite;
            if (sprite == null) return;
            _undoRedo.Execute(new RemoveSpritesCommand(_project, new[] { sprite }));
            RefreshAfterHistory();
            TxtProjectName.Text = $"Eliminado: {sprite.Name}  (Ctrl+Z para deshacer)";
        }

        private void CopySelectedSprite()
        {
            if (_project.SelectedSprite == null) return;
            _spriteClipboard = _project.SelectedSprite.Clone();
            TxtProjectName.Text = $"Copiado: {_spriteClipboard.Name}";
        }

        // atPlayhead=true → Ctrl+V (arranca en el playhead) · false → Ctrl+D (mismo tiempo)
        private void PasteSprite(OsuSprite source, bool atPlayhead)
        {
            int offset = atPlayhead ? (int)Math.Round(CurrentMs) - source.StartTime : 0;
            var copy = source.Clone(offset, source.Name + " copia");

            _undoRedo.Execute(new AddSpritesCommand(_project, new[] { copy }, $"Pegar \"{copy.Name}\""));
            RefreshAfterHistory();
            OnSpriteSelected(copy);
            TxtProjectName.Text = $"Pegado: {copy.Name}  (Ctrl+Z para deshacer)";
        }

        private static readonly OsuCommand[] _loopCmdScratch = BuildLoopCmdScratch();
        private static OsuCommand[] BuildLoopCmdScratch()
        {
            var values = Enum.GetValues<CommandType>();
            var arr = new OsuCommand[values.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = new OsuCommand();
            return arr;
        }


        private static OsuCommand? ResolveLoopCommand(OsuSprite sprite, CommandType type, double ms)
        {
            foreach (var loop in sprite.Loops)
            {
                if (loop.Commands.Count == 0) continue;
                int iterDur = loop.IterationDuration;
                if (iterDur <= 0) continue;

                double absoluteEnd = loop.StartTime + (double)iterDur * loop.LoopCount;
                if (ms < loop.StartTime || ms > absoluteEnd) continue;

                double relMs = (ms - loop.StartTime) % iterDur;

                OsuCommand? active = null;
                OsuCommand? past = null;

                foreach (var c in loop.Commands)
                {
                    if (c.Type != type) continue;

                    if (c.StartTime <= relMs && c.EndTime >= relMs)
                    {
                        if (active == null || c.StartTime > active.StartTime)
                            active = c;
                    }
                    else if (c.EndTime <= relMs)
                    {
                        if (past == null || c.EndTime > past.EndTime)
                            past = c;
                    }
                }

                var cmd = active ?? past;
                if (cmd == null) continue;

                double iterStart = loop.StartTime + Math.Floor((ms - loop.StartTime) / iterDur) * iterDur;

                var result = _loopCmdScratch[(int)type];
                result.Type = cmd.Type;
                result.Easing = cmd.Easing;
                result.StartTime = (int)(iterStart + cmd.StartTime);
                result.EndTime = (int)(iterStart + cmd.EndTime);
                result.StartValues = cmd.StartValues;
                result.EndValues = cmd.EndValues;
                result.Parameter = cmd.Parameter; // ← faltaba: se perdía A/H/V si el P estaba dentro de un loop
                return result;
            }
            return null;
        }

        // Curvas de easing reales de osu! (mismas fórmulas que usa el juego, 0-34)
        private static double ApplyEasing(double t, int easing)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            const double c1 = 1.70158;
            const double c2 = c1 * 1.525;
            const double c3 = c1 + 1;
            const double c4 = (2 * Math.PI) / 3;
            const double c5 = (2 * Math.PI) / 4.5;

            switch (easing)
            {
                case 0: return t;                              // Linear
                case 1: return 1 - (1 - t) * (1 - t);           // Out (legado, = QuadOut)
                case 2: return t * t;                           // In (legado, = QuadIn)

                case 3: return t * t;                                                  // InQuad
                case 4: return 1 - (1 - t) * (1 - t);                                  // OutQuad
                case 5: return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;  // InOutQuad

                case 6: return t * t * t;                                              // InCubic
                case 7: return 1 - Math.Pow(1 - t, 3);                                 // OutCubic
                case 8: return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // InOutCubic

                case 9: return t * t * t * t;                                          // InQuart
                case 10: return 1 - Math.Pow(1 - t, 4);                                // OutQuart
                case 11: return t < 0.5 ? 8 * t * t * t * t : 1 - Math.Pow(-2 * t + 2, 4) / 2; // InOutQuart

                case 12: return t * t * t * t * t;                                     // InQuint
                case 13: return 1 - Math.Pow(1 - t, 5);                                // OutQuint
                case 14: return t < 0.5 ? 16 * t * t * t * t * t : 1 - Math.Pow(-2 * t + 2, 5) / 2; // InOutQuint

                case 15: return 1 - Math.Cos((t * Math.PI) / 2);                       // InSine
                case 16: return Math.Sin((t * Math.PI) / 2);                           // OutSine
                case 17: return -(Math.Cos(Math.PI * t) - 1) / 2;                      // InOutSine

                case 18: return t == 0 ? 0 : Math.Pow(2, 10 * t - 10);                 // InExpo
                case 19: return t == 1 ? 1 : 1 - Math.Pow(2, -10 * t);                 // OutExpo
                case 20:                                                              // InOutExpo
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return t < 0.5 ? Math.Pow(2, 20 * t - 10) / 2 : (2 - Math.Pow(2, -20 * t + 10)) / 2;

                case 21: return 1 - Math.Sqrt(1 - Math.Pow(t, 2));                     // InCirc
                case 22: return Math.Sqrt(1 - Math.Pow(t - 1, 2));                     // OutCirc
                case 23:                                                              // InOutCirc
                    return t < 0.5
                        ? (1 - Math.Sqrt(1 - Math.Pow(2 * t, 2))) / 2
                        : (Math.Sqrt(1 - Math.Pow(-2 * t + 2, 2)) + 1) / 2;

                case 24:                                                              // InElastic
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return -Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * c4);
                case 25:                                                              // OutElastic
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1;
                case 26:                                                              // OutElasticHalf
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4 * 0.5) + 1;
                case 27:                                                              // OutElasticQuarter
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4 * 0.25) + 1;

                case 28:                                                            // InOutElastic
                    if (t == 0) return 0;
                    if (t == 1) return 1;
                    return t < 0.5
                        ? -(Math.Pow(2, 20 * t - 10) * Math.Sin((20 * t - 11.125) * c5)) / 2
                        : (Math.Pow(2, -20 * t + 10) * Math.Sin((20 * t - 11.125) * c5)) / 2 + 1;


                case 29: return c3 * t * t * t - c1 * t * t;                           // InBack
                case 30: return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2); // OutBack
                case 31:                                                              // InOutBack
                    return t < 0.5
                        ? (Math.Pow(2 * t, 2) * ((c2 + 1) * 2 * t - c2)) / 2
                        : (Math.Pow(2 * t - 2, 2) * ((c2 + 1) * (t * 2 - 2) + c2) + 2) / 2;

                case 32: return 1 - OutBounce(1 - t);                                  // InBounce
                case 33: return OutBounce(t);                                          // OutBounce
                case 34:                                                              // InOutBounce
                    return t < 0.5
                        ? (1 - OutBounce(1 - 2 * t)) / 2
                        : (1 + OutBounce(2 * t - 1)) / 2;

                // OutPow10 (aproximado)

                default: return t; // easing desconocido → fallback a linear
            }
        }

        private static double OutBounce(double t)
        {
            const double n1 = 7.5625;
            const double d1 = 2.75;

            if (t < 1 / d1) return n1 * t * t;
            if (t < 2 / d1) return n1 * (t -= 1.5 / d1) * t + 0.75;
            if (t < 2.5 / d1) return n1 * (t -= 2.25 / d1) * t + 0.9375;
            return n1 * (t -= 2.625 / d1) * t + 0.984375;
        }

        private void OpenAudioSafe(string path)
        {
            _audioOpenWatchdog?.Stop();
            _audioOpenWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _audioOpenWatchdog.Tick += (s, e) =>
            {
                _audioOpenWatchdog!.Stop();
                MessageBox.Show(
                    $"El audio \"{Path.GetFileName(path)}\" está tardando demasiado en cargar " +
                    "(probablemente un códec no soportado, típico con .ogg). Se canceló la carga.",
                    "Audio no soportado");
                _audioPlayer.Close();
            };
            _audioOpenWatchdog.Start();

            _audioPlayer.Open(new Uri(path));
        }

        private void AddGlow(OsuSprite src, float sigma)
        {
            if (src.Origin != SpriteOrigin.Centre)
            { MessageBox.Show("Por ahora el glow solo funciona con sprites de origen Centre."); return; }
            if (!File.Exists(src.FilePath)) return;

            string glowPath;
            try { glowPath = new GlowService().EnsureGlowPng(src.FilePath, sigma); }
            catch (Exception ex) { MessageBox.Show("No se pudo generar el glow: " + ex.Message); return; }

            const double intensity = 0.8;
            var glow = new OsuSprite
            {
                FilePath = glowPath,
                Name = "glow: " + src.Name,
                X = src.X,
                Y = src.Y,
                Scale = src.Scale,
                Rotation = src.Rotation,
                Opacity = intensity,
                Layer = src.Layer,
                Origin = SpriteOrigin.Centre,
                StartTime = src.StartTime,
                EndTime = src.EndTime
            };

            // El glow acompaña el movimiento del original (la opacidad es propia)
            foreach (var c in src.Commands.Where(c => c.Type is CommandType.M or CommandType.MX
                     or CommandType.MY or CommandType.S or CommandType.V or CommandType.R))
                glow.Commands.Add(c.Clone());

            // El exportador solo escribe escala/rotación base cuando el sprite no tiene comandos: se las damos explícitas
            bool hasScale = glow.Commands.Any(c => c.Type is CommandType.S or CommandType.V);
            if (!hasScale && Math.Abs(src.Scale - 1) > 1e-6)
                glow.Commands.Add(new OsuCommand
                {
                    Type = CommandType.S,
                    StartTime = glow.StartTime,
                    EndTime = glow.EndTime,
                    StartValues = new[] { src.Scale },
                    EndValues = new[] { src.Scale }
                });
            if (!glow.Commands.Any(c => c.Type == CommandType.R) && src.Rotation != 0)
                glow.Commands.Add(new OsuCommand
                {
                    Type = CommandType.R,
                    StartTime = glow.StartTime,
                    EndTime = glow.EndTime,
                    StartValues = new[] { src.Rotation },
                    EndValues = new[] { src.Rotation }
                });

            glow.Commands.Add(new OsuCommand
            {
                Type = CommandType.F,
                StartTime = glow.StartTime,
                EndTime = glow.EndTime,
                StartValues = new[] { intensity },
                EndValues = new[] { intensity }
            });
            glow.Commands.Add(new OsuCommand
            {
                Type = CommandType.P,
                StartTime = glow.StartTime,
                EndTime = glow.EndTime,
                Parameter = "A"
            });

            // Justo detrás del original (el orden de la lista es el orden de dibujo)
            _project.Sprites.Insert(_project.Sprites.IndexOf(src), glow);
            LoadBitmap(glow.FilePath);
            _undoRedo.Record(new AddSpritesCommand(_project, new[] { glow }, "Agregar glow"));
            OnSpriteSelected(glow);
            OsuCanvas.InvalidateVisual();
        }

    }
}