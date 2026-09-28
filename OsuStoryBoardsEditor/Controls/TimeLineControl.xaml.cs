using OsuStoryBoardsEditor.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OsuStoryBoardsEditor.Services;
using OsuStoryBoardsEditor.Commands;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OsuStoryBoardsEditor.Controls
{
    public partial class TimeLineControl : UserControl
    {
        private double _zoom = 1.0;
        private double _baseWidth = 0;

        public event Action<double>? SeekRequested;

        // ── NUEVO: se dispara cuando un keyframe se mueve ──
        public event Action? KeyframeChanged;

        private StoryboardProject? _project;

        private bool _isDragging = false;
        private int _totalDuration = 5000;
        private double _currentMs = 0;

        private Popup? _kfPopup;
        private Rectangle? _draggingRect;

        private enum DragMode { None, Seek, ClipMove, ClipTrimStart, ClipTrimEnd, Diamond, MarqueePending, Marquee }
        private DragMode _dragMode = DragMode.None;
        private OsuSprite? _draggingSprite;
        private double _dragStartX;
        private int _dragOriginalStart;
        private int _dragOriginalEnd;
        private HashSet<OsuSprite> _expandedSprites = new();
        private HashSet<SpriteFrameGroup> _expandedGroups = new();
        private Polygon? _draggingDiamond;
        private int _diamondOriginalTime;      // tiempo actualmente aplicado a los comandos del keyframe
        private int _diamondDownTime;          // tiempo al hacer click (ancla fija del drag)
        private double _diamondDownX;          // posición X del mouse al hacer click (ancla fija)
        private bool _diamondMoved;            // true apenas el mouse supera el umbral de drag
        private const double DiamondDragThresholdPx = 3;
        private OsuSprite? _diamondSprite;
        private List<OsuCommand>? _diamondCmds;

        private readonly HashSet<(OsuSprite sprite, int time)> _selectedKeyframes = new();
        private HashSet<(OsuSprite sprite, int time)> _marqueeBase = new();
        private Point _marqueeStart;
        private bool _marqueeAdditive;
        private Rectangle? _marqueeRect;
        private const double MarqueeThresholdPx = 4;

        private sealed record KeyframeClip(OsuSprite Sprite, int Time, List<(CommandType Type, int Easing, double[] Values)> Props);
        private List<KeyframeClip> _kfClipboard = new();

        public bool HasKeyframeClipboard => _kfClipboard.Count > 0;
        public void ClearKeyframeClipboard() => _kfClipboard.Clear();

        // ── Undo/redo global ──
        private UndoRedoManager? _undo;
        private UndoRedoManager.Transaction? _clipTx;      // drag/trim de clip
        private UndoRedoManager.Transaction? _diamondTx;   // drag de keyframe
        public void SetUndoManager(UndoRedoManager undo) => _undo = undo;

        // ── Selección activa: el sprite que recibe keyframes / se ve en Propiedades ──
        private OsuSprite? _selectedSprite;
        public event Action<OsuSprite>? SpriteSelected;
        private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0x79, 0xF9));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9));
        private static readonly Brush LabelMutedBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70));



        private static readonly Brush[] TrackColors =
        {
            new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
            new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC)),
            new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C)),
            new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)),
        };

        private static readonly Dictionary<CommandType, Brush> CmdColors = new()
        {
            { CommandType.M,  new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)) },
            { CommandType.F,  new SolidColorBrush(Color.FromRgb(0xFB, 0xD3, 0x8D)) },
            { CommandType.S,  new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) },
            { CommandType.R,  new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C)) },
            { CommandType.C,  new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x72)) },
            { CommandType.MX, new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)) },
            { CommandType.MY, new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC)) },
            { CommandType.V,  new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)) },
        };

        private static readonly Dictionary<CommandType, double> CmdRowOffset = new()
        {
            { CommandType.M,  0  },
            { CommandType.MX, 0  },
            { CommandType.MY, 0  },
            { CommandType.F,  6  },
            { CommandType.S,  12 },
            { CommandType.R,  18 },
            { CommandType.C,  6  },
            { CommandType.V,  12 },
        };

        // ── NUEVO: preview de zoom instantáneo ──
        // Mientras arrastrás el slider, en vez de esperar al redibujo, escalamos el canvas
        // YA dibujado (barato, lo hace la GPU). El redibujo con anchos/posiciones exactos
        // recién se hace una vez, cuando soltás.
        private readonly ScaleTransform _zoomPreview = new(1, 1);
        private double _appliedZoom = 1.0;

        public TimeLineControl()
        {
            InitializeComponent();

            TimelineCanvas.RenderTransformOrigin = new Point(0, 0);
            TimelineCanvas.RenderTransform = _zoomPreview;

            PlayheadHead.MouseLeftButtonDown += (s, e) =>
            {
                _isDragging = true;
                PlayheadHead.CaptureMouse();
                e.Handled = true;
            };
            PlayheadHead.MouseMove += (s, e) =>
            {
                if (!_isDragging) return;
                Seek(e.GetPosition(TimelineCanvas).X);
                e.Handled = true;
            };
            PlayheadHead.MouseLeftButtonUp += (s, e) =>
            {
                _isDragging = false;
                PlayheadHead.ReleaseMouseCapture();
                e.Handled = true;
            };
        }

        public void SetProject(StoryboardProject project)
        {
            _project = project;
            TrackLabels.ItemsSource = project.Sprites;
            TxtTotalTime.Text = $"total: {TimeSpan.FromMilliseconds(project.TotalDuration):mm\\:ss\\.fff}";
            _totalDuration = project.TotalDuration;

            project.Sprites.CollectionChanged += (s, e) => RedrawTracks(project.Sprites);
            project.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(StoryboardProject.TotalDuration))
                {
                    _totalDuration = project.TotalDuration;
                    TxtTotalTime.Text = $"total: {TimeSpan.FromMilliseconds(_totalDuration):mm\\:ss\\.fff}";
                    RedrawTracks(project.Sprites);
                }
                // ── NUEVO: redibujar la grilla de beats cuando cambia el divisor de snap ──
                else if (e.PropertyName == nameof(StoryboardProject.BeatDivisor))
                {
                    RedrawTracks(project.Sprites);
                }
            };

            Dispatcher.InvokeAsync(() =>
            {
                _baseWidth = TimelineCanvas.ActualWidth;
                RedrawTracks(project.Sprites);
                if (project.TimingPoints.Count > 0) AutoFitZoomToBeats();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_baseWidth == 0) _baseWidth = e.NewSize.Width;
            if (_project != null) RedrawTracks(_project.Sprites);
        }

        public void UpdatePlayhead(double currentMs)
        {
            _currentMs = currentMs;
            if (_totalDuration <= 0) return;
            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            double x = (currentMs / _totalDuration) * width;
            Canvas.SetLeft(PlayheadLine, x);
            Canvas.SetLeft(PlayheadHead, x - 6);
            AutoScrollToPlayhead(x);
        }

        // ── NUEVO: si el playhead se sale del área visible, la vista lo sigue ──
        // (como cualquier DAW — al reproducir o arrastrar el playhead, no tenés que
        // ir a buscarlo vos con la scrollbar).
        private void AutoScrollToPlayhead(double playheadX)
        {
            double viewportW = TracksScrollViewer.ViewportWidth;
            if (viewportW <= 0) return;

            double offset = TracksScrollViewer.HorizontalOffset;
            const double margin = 40;

            if (playheadX < offset + margin)
                TracksScrollViewer.ScrollToHorizontalOffset(Math.Max(0, playheadX - margin));
            else if (playheadX > offset + viewportW - margin)
                TracksScrollViewer.ScrollToHorizontalOffset(playheadX - viewportW + margin);
        }

        // ── NUEVO: helper de snap, no-op si el proyecto no tiene timing importado ──
        private int SnappedTime(int rawMs)
        {
            if (_project == null || _project.TimingPoints.Count == 0) return rawMs;
            return BeatSnapService.SnapToBeat(rawMs, _project.TimingPoints, Math.Max(1, _project.BeatDivisor));
        }

        public void RedrawTracks(IEnumerable<OsuSprite> sprites)
        {
            TracksCanvas.Children.Clear();
            RulerCanvas.Children.Clear();

            _zoomPreview.ScaleX = 1.0; // por si este redibujo no vino del debounce del slider
            _appliedZoom = _zoom;

            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            TimelineCanvas.Width = width;
            TracksCanvas.Width = width;
            RulerCanvas.Width = width;

            // Agrupar sprites tipo "frames de animación" (f1,f2,f3...) en filas colapsables.
            // displayRows es la lista final que ven tanto los labels de la izquierda como los tracks de la derecha:
            // si un grupo está expandido, sus sprites individuales se insertan justo debajo del header del grupo.
            var groupedRows = SpriteGroupingService.BuildTimelineRows(sprites);
            var displayRows = new List<object>();
            foreach (var row in groupedRows)
            {
                displayRows.Add(row);
                if (row is SpriteFrameGroup group && _expandedGroups.Contains(group))
                    displayRows.AddRange(group.Frames);
            }
            TrackLabels.ItemsSource = displayRows;

            if (width <= 0 || _totalDuration <= 0) return;

            DrawRuler(width);

            // Altura total estimada de las filas, para que la grilla de beats
            // atraviese TODAS las pistas (como en un DAW) en vez de vivir apretada en el ruler.
            double estimatedHeight = 0;
            foreach (var row in displayRows)
            {
                estimatedHeight += row switch
                {
                    SpriteFrameGroup => 36,
                    OsuSprite s => _expandedSprites.Contains(s) ? 60 : 36,
                    _ => 0
                };
            }
            DrawBeatGrid(width, Math.Max(estimatedHeight, 100)); // ← se agrega ANTES que los clips → queda atrás

            int i = 0;
            double trackY = 0;
            foreach (var row in displayRows)
            {
                trackY = row switch
                {
                    SpriteFrameGroup group => DrawGroupRow(group, i, trackY, width),
                    OsuSprite sprite => DrawSpriteRow(sprite, i, trackY, width),
                    _ => trackY
                };
                i++;
            }

            TracksCanvas.Height = Math.Max(trackY, 100);
            Dispatcher.InvokeAsync(RefreshTrackLabels, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Dibuja una fila de sprite normal (clip + diamantes de keyframe si está expandido).
        // Es lo que antes vivía inline dentro de RedrawTracks; devuelve el trackY siguiente.
        private double DrawSpriteRow(OsuSprite sprite, int index, double trackY, double width)
        {
            bool expanded = _expandedSprites.Contains(sprite);
            double trackHeight = expanded ? 60 : 36;

            if (index % 2 == 1)
            {
                var bg = new Rectangle
                {
                    Width = width,
                    Height = trackHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0x1E, 0x14, 0x28))
                };
                Canvas.SetLeft(bg, 0);
                Canvas.SetTop(bg, trackY);
                TracksCanvas.Children.Add(bg);
            }

            var selBg = new Rectangle
            {
                Width = width,
                Height = trackHeight,
                Fill = SelectedRowBrush,
                IsHitTestVisible = false,
                Tag = ("selbg", sprite),
                Visibility = sprite == _selectedSprite ? Visibility.Visible : Visibility.Collapsed
            };
            Canvas.SetLeft(selBg, 0);
            Canvas.SetTop(selBg, trackY);
            TracksCanvas.Children.Add(selBg);

            var baseLine = new Rectangle
            {
                Width = width,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0x88, 0x88, 0x99))
            };
            Canvas.SetLeft(baseLine, 0);
            Canvas.SetTop(baseLine, trackY + trackHeight - 1);
            TracksCanvas.Children.Add(baseLine);

            DrawClip(sprite, index, trackY, width);

            if (expanded)
            {
                var times = sprite.Commands
                    .SelectMany(c => new[] { c.StartTime, c.EndTime })
                    .Distinct()
                    .OrderBy(t => t);

                foreach (var time in times)
                {
                    var cmdsAtTime = sprite.Commands
                        .Where(c => c.StartTime == time || c.EndTime == time)
                        .ToList();
                    if (cmdsAtTime.Count > 0)
                        DrawGroupedDiamond(time, sprite, cmdsAtTime, trackY, trackHeight, width);
                }
            }

            return trackY + trackHeight;
        }

        // Dibuja la fila colapsada de un SpriteFrameGroup: una barra rayada (no arrastrable/trimeable,
        // representa N sprites individuales) que abarca desde el primer hasta el último frame del grupo.
        private double DrawGroupRow(SpriteFrameGroup group, int index, double trackY, double width)
        {
            const double trackHeight = 36;

            if (index % 2 == 1)
            {
                var bg = new Rectangle
                {
                    Width = width,
                    Height = trackHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0x1E, 0x14, 0x28))
                };
                Canvas.SetLeft(bg, 0);
                Canvas.SetTop(bg, trackY);
                TracksCanvas.Children.Add(bg);
            }

            var baseLine = new Rectangle
            {
                Width = width,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0x88, 0x88, 0x99))
            };
            Canvas.SetLeft(baseLine, 0);
            Canvas.SetTop(baseLine, trackY + trackHeight - 1);
            TracksCanvas.Children.Add(baseLine);

            double x1 = (group.StartTime / (double)_totalDuration) * width;
            double x2 = (group.EndTime / (double)_totalDuration) * width;
            double clipWidth = Math.Max(16, x2 - x1);
            Brush trackColor = TrackColors[index % TrackColors.Length];

            var body = new Rectangle
            {
                Width = Math.Max(4, clipWidth),
                Height = 20,
                Fill = trackColor,
                Opacity = 0.25,
                Stroke = trackColor,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(body, x1);
            Canvas.SetTop(body, trackY + 4);
            TracksCanvas.Children.Add(body);

            var label = new TextBlock
            {
                Text = $"{group.Name} ({group.Frames.Count} frames)",
                Foreground = Brushes.White,
                FontSize = 9,
                Opacity = 0.8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, x1 + 6);
            Canvas.SetTop(label, trackY + 7);
            TracksCanvas.Children.Add(label);

            return trackY + trackHeight;
        }

        // ── FIX: trackHeight pasado como parámetro, diamante centrado correctamente ──
        private void DrawGroupedDiamond(int timeMs, OsuSprite sprite, List<OsuCommand> cmdsAtTime,
            double trackY, double trackHeight, double width)
        {
            double rawX = (timeMs / (double)_totalDuration) * width;
            const double half = 6;
            // Evita que el rombo quede medio cortado (visual y clickeable) en los bordes
            // de la timeline, típicamente el keyframe inicial en t=0.
            // Si el ancho todavía es menor que el propio rombo (ej. justo durante el layout
            // inicial), width-half < half y Math.Clamp tiraría — en ese caso no clampeamos.
            double x = width > half * 2 ? Math.Clamp(rawX, half, width - half) : rawX;
            // Centro vertical del track, debajo del clip (clip está en trackY+4, h=20 → centro en trackY+14)
            double cy = trackY + trackHeight * 0.72;

            var diamond = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(0,    -half),
                    new Point(half,  0),
                    new Point(0,     half),
                    new Point(-half, 0),
                },
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.2,
                Cursor = Cursors.Hand,
                Tag = (timeMs, sprite, cmdsAtTime),
                ToolTip = $"{timeMs}ms — {cmdsAtTime.Count} props"
            };

            Canvas.SetLeft(diamond, x);
            Canvas.SetTop(diamond, cy);

            ApplyDiamondStyle(diamond, _selectedKeyframes.Contains((sprite, timeMs)));

            diamond.MouseLeftButtonDown += GroupedDiamond_MouseDown;
            TracksCanvas.Children.Add(diamond);
        }

        private void ShowGroupedKeyframePopup(Polygon? diamond, OsuSprite sprite,
            List<OsuCommand> cmds, int timeMs)
        {
            var panel = new StackPanel
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x14, 0x28)),
                MinWidth = 200
            };

            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x10)),
                Padding = new Thickness(10, 6, 10, 6)
            };
            header.Child = new TextBlock
            {
                Text = $"Keyframe  •  {timeMs}ms",
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            panel.Children.Add(header);

            var fields = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };

            TextBox MakeField(string label, string value, Brush color)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                var lbl = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var tb = new TextBox
                {
                    Text = value,
                    Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x12)),
                    Foreground = color,
                    BorderThickness = new Thickness(0),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Padding = new Thickness(6, 2, 6, 2),
                    TextAlignment = TextAlignment.Right
                };
                Grid.SetColumn(lbl, 0);
                Grid.SetColumn(tb, 1);
                row.Children.Add(lbl);
                row.Children.Add(tb);
                fields.Children.Add(row);
                return tb;
            }

            var cmdM = cmds.FirstOrDefault(c => c.Type == CommandType.M);
            var cmdS = cmds.FirstOrDefault(c => c.Type == CommandType.S);
            var cmdR = cmds.FirstOrDefault(c => c.Type == CommandType.R);
            var cmdF = cmds.FirstOrDefault(c => c.Type == CommandType.F);
            var cmdMX = cmds.FirstOrDefault(c => c.Type == CommandType.MX);
            var cmdMY = cmds.FirstOrDefault(c => c.Type == CommandType.MY);

            bool isStartM = cmdM != null && cmdM.StartTime == timeMs;
            bool isStartS = cmdS != null && cmdS.StartTime == timeMs;
            bool isStartR = cmdR != null && cmdR.StartTime == timeMs;
            bool isStartF = cmdF != null && cmdF.StartTime == timeMs;
            bool isStartMX = cmdMX != null && cmdMX.StartTime == timeMs;
            bool isStartMY = cmdMY != null && cmdMY.StartTime == timeMs;

            var blue = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
            var green = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            var orange = new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));
            var yellow = new SolidColorBrush(Color.FromRgb(0xFB, 0xD3, 0x8D));

            TextBox? tbX = null, tbY = null, tbScale = null, tbRot = null, tbOpacity = null;

            if (cmdM != null)
            {
                var vals = isStartM ? cmdM.StartValues : cmdM.EndValues;
                tbX = MakeField("X", vals.Length > 0 ? vals[0].ToString("F0") : "0", blue);
                tbY = MakeField("Y", vals.Length > 1 ? vals[1].ToString("F0") : "0", blue);
            }
            if (cmdMX != null)
                tbX = MakeField("X", (isStartMX ? cmdMX.StartValues : cmdMX.EndValues)[0].ToString("F0"), blue);
            if (cmdMY != null)
                tbY = MakeField("Y", (isStartMY ? cmdMY.StartValues : cmdMY.EndValues)[0].ToString("F0"), blue);
            if (cmdS != null)
                tbScale = MakeField("Escala", (isStartS ? cmdS.StartValues : cmdS.EndValues)[0].ToString("F2"), green);
            if (cmdR != null)
                tbRot = MakeField("Rotación (°)", (isStartR ? cmdR.StartValues : cmdR.EndValues)[0].ToString("F2"), orange);
            if (cmdF != null)
                tbOpacity = MakeField("Opacidad", (isStartF ? cmdF.StartValues : cmdF.EndValues)[0].ToString("F2"), yellow);

            panel.Children.Add(fields);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 4, 10, 10)
            };

            var btnDelete = new Button
            {
                Content = "Eliminar",
                Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x10, 0x10)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x72)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 10,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0)
            };
            btnDelete.Click += (s, e) =>
            {
                using var tx = _undo?.BeginTransaction("Eliminar keyframe", sprite);
                foreach (var cmd in cmds.ToList())
                {
                    if (cmd.StartTime == timeMs && cmd.EndTime == timeMs)
                        sprite.Commands.Remove(cmd);
                    else if (cmd.StartTime == timeMs)
                        cmd.StartTime = cmd.EndTime;
                    else if (cmd.EndTime == timeMs)
                        cmd.EndTime = cmd.StartTime;
                }
                if (sprite.Commands.Count > 0)
                {
                    // Borrar un keyframe no debería acortar el clip — solo agrandarlo si hiciera
                    // falta. Si no, borrar el último keyframe "come" la duración del sprite.
                    sprite.StartTime = Math.Min(sprite.StartTime, sprite.Commands.Min(c => c.StartTime));
                    sprite.EndTime = Math.Max(sprite.EndTime, sprite.Commands.Max(c => c.EndTime));
                }
                _kfPopup!.IsOpen = false;
                if (_project != null) RedrawTracks(_project.Sprites);
                KeyframeChanged?.Invoke(); // ← re-render canvas
            };

            var btnApply = new Button
            {
                Content = "Aplicar",
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x14, 0x28)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)),
                BorderThickness = new Thickness(0.5),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x20, 0x50)),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 10,
                Cursor = Cursors.Hand
            };
            btnApply.Click += (s, e) =>
            {
                using var tx = _undo?.BeginTransaction("Editar keyframe", sprite);
                // Aplicar X / Y
                if (cmdM != null)
                {
                    var vals = isStartM ? cmdM.StartValues : cmdM.EndValues;
                    if (tbX != null && double.TryParse(tbX.Text, out double nx)) vals[0] = nx;
                    if (tbY != null && double.TryParse(tbY.Text, out double ny)) vals[1] = ny;
                }
                if (cmdMX != null && tbX != null && double.TryParse(tbX.Text, out double nMX))
                    (isStartMX ? cmdMX.StartValues : cmdMX.EndValues)[0] = nMX;
                if (cmdMY != null && tbY != null && double.TryParse(tbY.Text, out double nMY))
                    (isStartMY ? cmdMY.StartValues : cmdMY.EndValues)[0] = nMY;
                if (cmdS != null && tbScale != null && double.TryParse(tbScale.Text, out double ns))
                    (isStartS ? cmdS.StartValues : cmdS.EndValues)[0] = ns;
                if (cmdR != null && tbRot != null && double.TryParse(tbRot.Text, out double nr))
                    (isStartR ? cmdR.StartValues : cmdR.EndValues)[0] = nr;
                if (cmdF != null && tbOpacity != null && double.TryParse(tbOpacity.Text, out double nf))
                    (isStartF ? cmdF.StartValues : cmdF.EndValues)[0] = Math.Clamp(nf, 0, 1);

                _kfPopup!.IsOpen = false;
                if (_project != null) RedrawTracks(_project.Sprites);
                KeyframeChanged?.Invoke(); // ← re-render canvas
            };

            btnRow.Children.Add(btnDelete);
            btnRow.Children.Add(btnApply);
            panel.Children.Add(btnRow);

            _kfPopup = new Popup
            {
                Child = new Border
                {
                    Child = panel,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x20, 0x50)),
                    BorderThickness = new Thickness(0.5),
                    CornerRadius = new CornerRadius(4)
                },
                PlacementTarget = diamond,
                Placement = PlacementMode.Top,
                StaysOpen = false,
                AllowsTransparency = true,
                IsOpen = true
            };
        }

        private static string[] GetValueLabels(CommandType type) => type switch
        {
            CommandType.M => new[] { "X", "Y" },
            CommandType.MX => new[] { "X" },
            CommandType.MY => new[] { "Y" },
            CommandType.F => new[] { "opacidad" },
            CommandType.S => new[] { "escala" },
            CommandType.R => new[] { "rotación (°)" },
            CommandType.C => new[] { "R", "G", "B" },
            CommandType.V => new[] { "escala X", "escala Y" },
            _ => new[] { "valor" }
        };

        private void DrawRuler(double width)
        {
            // Antes: paso fijo de 1000ms, mostrando el número crudo en ms ("24000") — feo e
            // ilegible de un vistazo. Ahora: formato mm:ss (con décimas si estás bien zoomeado),
            // y el paso entre marcas se adapta al zoom para que las etiquetas nunca se amontonen.
            double pxPerMs = width / _totalDuration;
            int[] niceSteps = { 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000 };
            int step = niceSteps[^1];
            foreach (var s in niceSteps)
            {
                if (s * pxPerMs >= 55) { step = s; break; }
            }

            for (int ms = 0; ms <= _totalDuration; ms += step)
            {
                double x = (ms / (double)_totalDuration) * width;
                var tick = new Rectangle
                {
                    Width = 0.5,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99))
                };
                var label = new TextBlock
                {
                    Text = FormatRulerTime(ms, step),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)),
                    FontSize = 8
                };
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, 8);
                RulerCanvas.Children.Add(tick);
                Canvas.SetLeft(label, x + 2);
                Canvas.SetTop(label, 0);
                RulerCanvas.Children.Add(label);
            }
        }

        // mm:ss normalmente; mm:ss.d (con décimas) cuando el paso es lo bastante fino como para
        // que la diferencia importe (si no, todas las etiquetas dirían lo mismo).
        private static string FormatRulerTime(int ms, int step)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            int totalMinutes = (int)ts.TotalMinutes;
            return step < 1000
                ? $"{totalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}"
                : $"{totalMinutes}:{ts.Seconds:D2}";
        }

        // ── Rayitas de subdivisión de beat (como el editor de osu!), REDISEÑADO ──
        // Antes vivía apretada en la franja de 14px del ruler junto a los números → se amontonaba
        // y quedaba ilegible con BPMs altos. Ahora:
        //   1) atraviesa toda la altura de las pistas (detrás de los clips), estilo DAW
        //   2) tiene culling adaptativo: si a este zoom las subdivisiones quedarían a menos de
        //      4px entre sí, directamente no las dibuja (solo muestra downbeats 1/1); y si ni
        //      los downbeats entran, no dibuja nada — hay que hacer zoom para verla.
        private void DrawBeatGrid(double width, double trackAreaHeight)
        {
            if (_project == null || _project.TimingPoints.Count == 0) return;

            var redLines = _project.TimingPoints
                .Where(t => t.Uninherited)
                .OrderBy(t => t.Time)
                .ToList();
            if (redLines.Count == 0) return;

            const double minPixelSpacing = 4.0;
            double pxPerMs = width / _totalDuration;
            int divisor = Math.Max(1, _project.BeatDivisor);

            var downbeatBrush = new SolidColorBrush(Color.FromArgb(55, 0xE8, 0x79, 0xF9));
            var subdivBrush = new SolidColorBrush(Color.FromArgb(20, 0x88, 0x88, 0x99));

            for (int i = 0; i < redLines.Count; i++)
            {
                var tp = redLines[i];
                double downbeatSpacingPx = tp.BeatLength * pxPerMs;
                if (downbeatSpacingPx < minPixelSpacing) continue; // muy zoomeado afuera, ni los downbeats entran

                bool showSubdivisions = divisor > 1 && (downbeatSpacingPx / divisor) >= minPixelSpacing;
                int effectiveDivisor = showSubdivisions ? divisor : 1;
                double beatLen = tp.BeatLength / effectiveDivisor;

                double segmentEnd = (i + 1 < redLines.Count)
                    ? Math.Min(redLines[i + 1].Time, _totalDuration)
                    : _totalDuration;

                int beatIndex = 0;
                for (double t = tp.Time; t < segmentEnd; t += beatLen, beatIndex++)
                {
                    if (t < 0) continue;
                    double x = (t / (double)_totalDuration) * width;
                    bool isDownbeat = beatIndex % effectiveDivisor == 0;

                    var line = new Rectangle
                    {
                        Width = isDownbeat ? 1 : 0.5,
                        Height = trackAreaHeight,
                        Fill = isDownbeat ? downbeatBrush : subdivBrush,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(line, x);
                    Canvas.SetTop(line, 0);
                    TracksCanvas.Children.Add(line);
                }
            }
        }

        private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;

            // Zona de pistas (debajo de la regla): arrastrar dibuja el rectángulo de selección;
            // un click simple sigue moviendo el playhead (se resuelve al soltar).
            var pTracks = e.GetPosition(TracksCanvas);
            if (pTracks.Y >= 0)
            {
                TakeFocus();
                _isDragging = true;
                _dragMode = DragMode.MarqueePending;
                _marqueeStart = pTracks;
                _marqueeAdditive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                TimelineCanvas.CaptureMouse();
                return;
            }

            _isDragging = true;
            _dragMode = DragMode.Seek;
            Seek(e.GetPosition(TimelineCanvas).X);
            TimelineCanvas.CaptureMouse();
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {

            if (_dragMode == DragMode.MarqueePending || _dragMode == DragMode.Marquee)
            {
                UpdateMarquee(e);
                return;
            }

            if (_dragMode == DragMode.Seek)
            {
                Seek(e.GetPosition(TimelineCanvas).X);
                return;
            }

            if (_draggingSprite == null) return;
            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            double dx = e.GetPosition(TimelineCanvas).X - _dragStartX;
            int deltams = (int)((dx / width) * _totalDuration);

            switch (_dragMode)
            {
                case DragMode.ClipMove:
                    _draggingSprite.StartTime = SnappedTime(Math.Max(0, _dragOriginalStart + deltams));
                    _draggingSprite.EndTime = SnappedTime(Math.Min(_totalDuration, _dragOriginalEnd + deltams));
                    break;
                case DragMode.ClipTrimStart:
                    _draggingSprite.StartTime = SnappedTime(Math.Clamp(
                        _dragOriginalStart + deltams, 0, _draggingSprite.EndTime - 100));
                    break;
                case DragMode.ClipTrimEnd:
                    _draggingSprite.EndTime = SnappedTime(Math.Clamp(
                        _dragOriginalEnd + deltams, _draggingSprite.StartTime + 100, _totalDuration));
                    break;
            }

            UpdateClipPosition(_draggingSprite);
        }

        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {

            if (_dragMode == DragMode.MarqueePending || _dragMode == DragMode.Marquee)
            {
                EndMarquee(e);
                return;
            }

            if (_dragMode == DragMode.ClipMove ||
                _dragMode == DragMode.ClipTrimStart ||
                _dragMode == DragMode.ClipTrimEnd)
            {
                _clipTx?.Dispose();   // registra el paso solo si el clip realmente cambió
                _clipTx = null;
                _draggingRect?.ReleaseMouseCapture();
                _draggingRect = null;
                _draggingSprite = null;
                _dragMode = DragMode.None;
                if (_project != null) RedrawTracks(_project.Sprites);
                KeyframeChanged?.Invoke();
                return;
            }

            _isDragging = false;
            _dragMode = DragMode.None;
            TimelineCanvas.ReleaseMouseCapture();
        }

        // El foco tiene que estar en el timeline, no en un cuadro de texto del panel,
        // o Ctrl+C / Ctrl+V terminarían copiando texto en vez de keyframes.
        private void TakeFocus()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Keyboard.Focus(this);
        }

        private static void ApplyDiamondStyle(Polygon d, bool selected)
        {
            d.Fill = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9));
            d.Stroke = selected ? AccentBrush : new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF));
            d.StrokeThickness = selected ? 2.2 : 1.2;
        }

        private void RestyleDiamonds()
        {
            foreach (var d in TracksCanvas.Children.OfType<Polygon>())
                if (d.Tag is (int time, OsuSprite sprite, List<OsuCommand> _))
                    ApplyDiamondStyle(d, _selectedKeyframes.Contains((sprite, time)));
        }

        private void UpdateMarquee(MouseEventArgs e)
        {
            var p = e.GetPosition(TracksCanvas);

            if (_dragMode == DragMode.MarqueePending)
            {
                if (Math.Abs(p.X - _marqueeStart.X) < MarqueeThresholdPx &&
                    Math.Abs(p.Y - _marqueeStart.Y) < MarqueeThresholdPx) return;

                _dragMode = DragMode.Marquee;
                _marqueeBase = _marqueeAdditive
                    ? new HashSet<(OsuSprite sprite, int time)>(_selectedKeyframes)
                    : new HashSet<(OsuSprite sprite, int time)>();
                _marqueeRect = new Rectangle
                {
                    Stroke = AccentBrush,
                    StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 0xE8, 0x79, 0xF9)),
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(_marqueeRect, 1000);
                TracksCanvas.Children.Add(_marqueeRect);
            }

            var area = new Rect(Math.Min(p.X, _marqueeStart.X), Math.Min(p.Y, _marqueeStart.Y),
                                Math.Abs(p.X - _marqueeStart.X), Math.Abs(p.Y - _marqueeStart.Y));
            if (_marqueeRect != null)
            {
                Canvas.SetLeft(_marqueeRect, area.X);
                Canvas.SetTop(_marqueeRect, area.Y);
                _marqueeRect.Width = area.Width;
                _marqueeRect.Height = area.Height;
            }

            _selectedKeyframes.Clear();
            foreach (var k in _marqueeBase) _selectedKeyframes.Add(k);

            foreach (var d in TracksCanvas.Children.OfType<Polygon>())
            {
                if (d.Tag is not (int time, OsuSprite sprite, List<OsuCommand> _)) continue;
                var box = new Rect(Canvas.GetLeft(d) - 6, Canvas.GetTop(d) - 6, 12, 12);
                if (area.IntersectsWith(box)) _selectedKeyframes.Add((sprite, time));
            }
            RestyleDiamonds();
        }

        private void EndMarquee(MouseButtonEventArgs e)
        {
            bool wasClick = _dragMode == DragMode.MarqueePending;   // no llegó a arrastrar: era un click simple

            if (_marqueeRect != null) { TracksCanvas.Children.Remove(_marqueeRect); _marqueeRect = null; }
            _isDragging = false;
            _dragMode = DragMode.None;
            TimelineCanvas.ReleaseMouseCapture();

            if (wasClick)
            {
                if (!_marqueeAdditive) { _selectedKeyframes.Clear(); RestyleDiamonds(); }
                Seek(e.GetPosition(TimelineCanvas).X);
            }
        }

        // ── Copiar / pegar keyframes ──────────────────────────
        // Devuelve cuántos keyframes se copiaron (0 = no había selección: no toca el portapapeles).
        public int CopySelectedKeyframes()
        {
            var clips = new List<KeyframeClip>();
            foreach (var (sprite, time) in _selectedKeyframes.OrderBy(k => k.time))
            {
                var props = new List<(CommandType Type, int Easing, double[] Values)>();
                var groups = sprite.Commands
                    .Where(c => c.Type != CommandType.P && (c.StartTime == time || c.EndTime == time))
                    .GroupBy(c => c.Type);

                foreach (var g in groups)
                {
                    var outgoing = g.FirstOrDefault(c => c.StartTime == time);   // el tramo que sale de este keyframe
                    var src = outgoing ?? g.First();
                    var vals = src.StartTime == time ? src.StartValues : src.EndValues;
                    if (vals.Length == 0) continue;
                    props.Add((g.Key, outgoing?.Easing ?? 0, (double[])vals.Clone()));
                }
                if (props.Count > 0) clips.Add(new KeyframeClip(sprite, time, props));
            }

            if (clips.Count > 0) _kfClipboard = clips;
            return clips.Count;
        }

        // Pega con el primer keyframe copiado en atMs, respetando las distancias entre ellos.
        public int PasteKeyframes(int atMs)
        {
            var project = _project;
            if (project == null || _kfClipboard.Count == 0) return 0;

            int delta = atMs - _kfClipboard.Min(k => k.Time);
            bool singleSource = _kfClipboard.Select(k => k.Sprite).Distinct().Count() == 1;

            var items = _kfClipboard
                .Select(k => (Target: (singleSource && _selectedSprite != null) ? _selectedSprite : k.Sprite, Clip: k))
                .Where(x => project.Sprites.Contains(x.Target))   // el sprite pudo haberse borrado
                .OrderBy(x => x.Clip.Time)
                .ToList();
            if (items.Count == 0) return 0;

            var targets = items.Select(i => i.Target).Distinct().ToArray();

            using (_undo?.BeginTransaction("Pegar keyframes", targets))
            {
                _selectedKeyframes.Clear();
                foreach (var (target, clip) in items)
                {
                    int t = Math.Max(0, clip.Time + delta);
                    foreach (var (type, easing, values) in clip.Props)
                        AddPointKeyframe(target, type, t, values, easing);
                    _selectedKeyframes.Add((target, t));   // lo pegado queda seleccionado
                }

                foreach (var s in targets)
                {
                    s.StartTime = Math.Min(s.StartTime, s.Commands.Min(c => c.StartTime));
                    s.EndTime = Math.Max(s.EndTime, s.Commands.Max(c => c.EndTime));
                }
            }

            RedrawTracks(project.Sprites);
            KeyframeChanged?.Invoke();
            return items.Count;
        }

        // Misma mecánica que "Agregar keyframe": cierra el keyframe puntual anterior hasta t y deja uno puntual en t.
        // Si ya había un tramo que salía de t, lo conserva (solo cambia el valor inicial).
        private static void AddPointKeyframe(OsuSprite sprite, CommandType type, int t, double[] values, int easing)
        {
            var prev = sprite.Commands.Where(c => c.Type == type && c.StartTime < t)
                .OrderByDescending(c => c.StartTime).FirstOrDefault();
            if (prev != null && prev.EndTime == prev.StartTime)
            {
                prev.EndTime = t;
                prev.EndValues = (double[])values.Clone();
            }

            var ex = sprite.Commands.FirstOrDefault(c => c.Type == type && c.StartTime == t);
            if (ex != null) sprite.Commands.Remove(ex);

            var cmd = new OsuCommand
            {
                Type = type,
                Easing = easing,
                StartTime = t,
                EndTime = t,
                StartValues = (double[])values.Clone(),
                EndValues = (double[])values.Clone()
            };
            if (ex != null && ex.EndTime > ex.StartTime)
            {
                cmd.EndTime = ex.EndTime;
                cmd.EndValues = (double[])ex.EndValues.Clone();
                cmd.Easing = ex.Easing;
            }
            sprite.Commands.Add(cmd);
        }

        private void Seek(double mouseX)
        {
            if (_totalDuration <= 0) return;
            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            mouseX = Math.Max(0, Math.Min(mouseX, width));
            double ms = (mouseX / width) * _totalDuration;
            int snapped = SnappedTime((int)Math.Round(ms));
            UpdatePlayhead(snapped);
            SeekRequested?.Invoke(snapped);
        }

        private System.Windows.Threading.DispatcherTimer? _zoomDebounceTimer;

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _zoom = e.NewValue;

            // Feedback instantáneo: mientras arrastrás, escalamos el canvas YA dibujado con el
            // último zoom aplicado (_appliedZoom) — no recalcula nada, lo hace la GPU. Se ve
            // "en vivo" aunque no sea pixel-perfect durante el arrastre.
            if (_appliedZoom > 0)
                _zoomPreview.ScaleX = _zoom / _appliedZoom;

            // El redibujo real (anchos/posiciones exactos, nítido) se hace una sola vez,
            // ~80ms después de que dejás de mover el slider — así no se traba en el medio.
            if (_zoomDebounceTimer == null)
            {
                _zoomDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(80)
                };
                _zoomDebounceTimer.Tick += (s, args) =>
                {
                    _zoomDebounceTimer!.Stop();
                    _zoomPreview.ScaleX = 1.0;
                    if (_project != null) RedrawTracks(_project.Sprites);
                };
            }
            _zoomDebounceTimer.Stop();
            _zoomDebounceTimer.Start();
        }

        // ── NUEVO: al importar timing, saltar directo a un zoom donde el beat grid sea legible,
        // en vez de arrancar siempre con toda la canción aplastada en pantalla. Apunta a ~24px
        // por beat (1/1) — suficiente para ver y clickear subdivisiones sin adivinar con el slider.
        public void AutoFitZoomToBeats()
        {
            if (_project == null || _project.TimingPoints.Count == 0 || _totalDuration <= 0) return;
            var redLine = _project.TimingPoints.FirstOrDefault(t => t.Uninherited);
            if (redLine == null) return;

            double baseW = _baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth;
            if (baseW <= 0) return;

            const double targetPxPerBeat = 24.0;
            double neededWidth = (_totalDuration / redLine.BeatLength) * targetPxPerBeat;
            double zoom = Math.Clamp(neededWidth / baseW, ZoomSlider.Minimum, ZoomSlider.Maximum);

            ZoomSlider.Value = zoom; // dispara ZoomSlider_ValueChanged → RedrawTracks
        }

        private void DrawClip(OsuSprite sprite, int index, double trackY, double width)
        {
            double x1 = (sprite.StartTime / (double)_totalDuration) * width;
            double x2 = (sprite.EndTime / (double)_totalDuration) * width;
            double clipWidth = Math.Max(16, x2 - x1);
            Brush trackColor = TrackColors[index % TrackColors.Length];

            var body = new Rectangle
            {
                Width = Math.Max(4, clipWidth - 16),
                Height = 20,
                Fill = trackColor,
                Opacity = 0.5,
                Cursor = Cursors.SizeAll,
                Tag = ("move", sprite)
            };
            Canvas.SetLeft(body, x1 + 8);
            Canvas.SetTop(body, trackY + 4);
            body.MouseLeftButtonDown += Clip_MouseDown;
            TracksCanvas.Children.Add(body);

            var left = new Rectangle
            {
                Width = 8,
                Height = 20,
                Fill = trackColor,
                Opacity = 0.9,
                Cursor = Cursors.SizeWE,
                Tag = ("trimstart", sprite)
            };
            Canvas.SetLeft(left, x1);
            Canvas.SetTop(left, trackY + 4);
            left.MouseLeftButtonDown += Clip_MouseDown;
            TracksCanvas.Children.Add(left);

            var right = new Rectangle
            {
                Width = 8,
                Height = 20,
                Fill = trackColor,
                Opacity = 0.9,
                Cursor = Cursors.SizeWE,
                Tag = ("trimend", sprite)
            };
            Canvas.SetLeft(right, x1 + clipWidth - 8);
            Canvas.SetTop(right, trackY + 4);
            right.MouseLeftButtonDown += Clip_MouseDown;
            TracksCanvas.Children.Add(right);

            var label = new TextBlock
            {
                Text = sprite.Name,
                Foreground = Brushes.White,
                FontSize = 9,
                Opacity = 0.8,
                IsHitTestVisible = false,
                Tag = sprite
            };
            Canvas.SetLeft(label, x1 + 10);
            Canvas.SetTop(label, trackY + 7);
            TracksCanvas.Children.Add(label);

            var outline = new Rectangle
            {
                Width = clipWidth,
                Height = 20,
                Stroke = AccentBrush,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Tag = ("sel", sprite),
                Visibility = sprite == _selectedSprite ? Visibility.Visible : Visibility.Collapsed
            };
            Canvas.SetLeft(outline, x1);
            Canvas.SetTop(outline, trackY + 4);
            TracksCanvas.Children.Add(outline);
        }

        private void Clip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Rectangle rect) return;
            if (rect.Tag is not (string mode, OsuSprite sprite)) return;

            SelectFromTimeline(sprite);

            _clipTx?.Dispose();
            _clipTx = _undo?.BeginTransaction(mode switch
            {
                "move" => "Mover clip",
                "trimstart" => "Recortar inicio del clip",
                "trimend" => "Recortar fin del clip",
                _ => "Editar clip"
            }, sprite);

            _draggingSprite = sprite;
            _draggingRect = rect;
            _dragStartX = e.GetPosition(TimelineCanvas).X;
            _dragOriginalStart = sprite.StartTime;
            _dragOriginalEnd = sprite.EndTime;

            _dragMode = mode switch
            {
                "move" => DragMode.ClipMove,
                "trimstart" => DragMode.ClipTrimStart,
                "trimend" => DragMode.ClipTrimEnd,
                _ => DragMode.None
            };

            rect.CaptureMouse();
            e.Handled = true;
        }

        private void UpdateClipPosition(OsuSprite sprite)
        {
            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            double x1 = (sprite.StartTime / (double)_totalDuration) * width;
            double x2 = (sprite.EndTime / (double)_totalDuration) * width;
            double clipWidth = Math.Max(16, x2 - x1);

            foreach (var child in TracksCanvas.Children)
            {
                if (child is Rectangle rect && rect.Tag is (string mode, OsuSprite s) && s == sprite)
                {
                    switch (mode)
                    {
                        case "move":
                            Canvas.SetLeft(rect, x1 + 8);
                            rect.Width = Math.Max(0, clipWidth - 16);
                            break;
                        case "trimstart":
                            Canvas.SetLeft(rect, x1);
                            break;
                        case "trimend":
                            Canvas.SetLeft(rect, x1 + clipWidth - 8);
                            break;
                    }
                }
                else if (child is TextBlock tb && tb.Tag == sprite)
                {
                    Canvas.SetLeft(tb, x1 + 10);
                }
            }
        }

        private void ArrowExpand_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;

            if (fe.DataContext is OsuSprite sprite)
            {
                if (_expandedSprites.Contains(sprite)) _expandedSprites.Remove(sprite);
                else _expandedSprites.Add(sprite);
            }
            else if (fe.DataContext is SpriteFrameGroup group)
            {
                if (_expandedGroups.Contains(group)) _expandedGroups.Remove(group);
                else _expandedGroups.Add(group);
            }
            else return;

            if (_project != null) RedrawTracks(_project.Sprites);
            e.Handled = true;
        }

        private void GroupedDiamond_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Polygon diamond) return;
            if (diamond.Tag is not (int timeMs, OsuSprite sprite, List<OsuCommand> cmds)) return;

            SelectFromTimeline(sprite);

            e.Handled = true;
            _draggingDiamond = diamond;
            _diamondOriginalTime = timeMs;   // tiempo aplicado actualmente a los comandos
            _diamondDownTime = timeMs;       // ancla fija: tiempo al momento del click
            _diamondDownX = e.GetPosition(TracksCanvas).X; // ancla fija: X al momento del click
            _diamondMoved = false;
            _diamondSprite = sprite;
            _diamondCmds = cmds;

            diamond.CaptureMouse();
            diamond.MouseMove += Diamond_MouseMove;
            diamond.MouseLeftButtonUp += Diamond_MouseUp;

            TakeFocus();
            var key = (sprite, timeMs);
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                // con modificador solo cambia la selección: no arrastra ni abre el popup
                if (!_selectedKeyframes.Remove(key)) _selectedKeyframes.Add(key);
                RestyleDiamonds();
                e.Handled = true;
                return;
            }
            if (!_selectedKeyframes.Contains(key))
            {
                _selectedKeyframes.Clear();
                _selectedKeyframes.Add(key);
                RestyleDiamonds();
            }
        }



        private void Diamond_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingDiamond == null || _diamondCmds == null) return;

            double width = (_baseWidth > 0 ? _baseWidth : TimelineCanvas.ActualWidth) * _zoom;
            double dx = e.GetPosition(TracksCanvas).X - _diamondDownX;

            // Umbral: un click con temblor de mouse no cuenta como drag (así el click sigue abriendo el popup)
            if (!_diamondMoved && Math.Abs(dx) < DiamondDragThresholdPx) return;
            _diamondMoved = true;
            _diamondTx ??= _undo?.BeginTransaction("Mover keyframe", _diamondSprite!);

            // Delta ABSOLUTO desde el punto de click (antes era incremental y se reseteaba en cada
            // evento: con el snap, cada movimiento chico se redondeaba al mismo beat y se perdía,
            // por eso el rombo se quedaba atrás del cursor y avanzaba a saltos).
            int rawTime = Math.Clamp(
                _diamondDownTime + (int)Math.Round((dx / width) * _totalDuration), 0, _totalDuration);
            int newTime = SnappedTime(rawTime);
            if (newTime == _diamondOriginalTime) return; // sigue en el mismo beat, nada que actualizar

            double newX = (newTime / (double)_totalDuration) * width;
            double visX = width > 12 ? Math.Clamp(newX, 6, width - 6) : newX; // mismo clamp que DrawGroupedDiamond
            Canvas.SetLeft(_draggingDiamond, visX);
            _draggingDiamond.ToolTip = $"{newTime}ms";

            // Actualizar los datos en tiempo real y disparar re-render
            foreach (var cmd in _diamondCmds)
            {
                if (cmd.StartTime == _diamondOriginalTime) cmd.StartTime = newTime;
                if (cmd.EndTime == _diamondOriginalTime) cmd.EndTime = newTime;
            }
            _diamondOriginalTime = newTime;

            KeyframeChanged?.Invoke(); // re-render canvas en tiempo real
        }

        private void Diamond_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingDiamond == null || _diamondCmds == null || _diamondSprite == null) return;

            var diamond = _draggingDiamond;
            var sprite = _diamondSprite;
            var cmds = _diamondCmds;
            bool wasDrag = _diamondMoved; // antes se comparaba contra _diamondOriginalTime (que ya se
                                          // había actualizado en cada move) → siempre daba "click" y
                                          // abría el popup después de cada drag.

            diamond.ReleaseMouseCapture();
            diamond.MouseMove -= Diamond_MouseMove;
            diamond.MouseLeftButtonUp -= Diamond_MouseUp;

            if (!wasDrag)
            {
                // Click sin movimiento → abrir popup
                var capturedTime = _diamondOriginalTime;
                _draggingDiamond = null;
                ShowGroupedKeyframePopup(diamond, sprite, cmds, capturedTime);
                return;
            }

            _selectedKeyframes.Clear();

            // Recalcular StartTime/EndTime del sprite: puede crecer si arrastraste más allá,
            // pero no se achica (mismo criterio que al borrar un keyframe).
            if (sprite.Commands.Count > 0)
            {
                sprite.StartTime = Math.Min(sprite.StartTime, sprite.Commands.Min(c => c.StartTime));
                sprite.EndTime = Math.Max(sprite.EndTime, sprite.Commands.Max(c => c.EndTime));
            }

            _diamondTx?.Dispose();   // cierra el paso DESPUÉS de recalcular Start/EndTime del sprite
            _diamondTx = null;

            _draggingDiamond = null;
            if (_project != null) RedrawTracks(_project.Sprites);
            KeyframeChanged?.Invoke();
        }

        private void RefreshTrackLabels()
        {
            for (int i = 0; i < TrackLabels.Items.Count; i++)
            {
                var container = TrackLabels.ItemContainerGenerator
                    .ContainerFromIndex(i) as ContentPresenter;
                if (container == null) continue;
                var border = FindVisualChild<Border>(container);
                if (border == null) continue;
                var sprite = TrackLabels.Items[i] as OsuSprite;
                if (sprite == null) continue;
                border.Height = _expandedSprites.Contains(sprite) ? 60 : 36;

                bool selected = sprite == _selectedSprite;
                border.Background = selected ? SelectedRowBrush : Brushes.Transparent;
                if (border.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock nameTb)
                {
                    nameTb.Foreground = selected ? AccentBrush : LabelMutedBrush;
                    nameTb.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
        }

        // MainWindow lo llama cuando el sprite activo cambia desde afuera (canvas / panel de capas).
        public void SetSelectedSprite(OsuSprite? sprite)
        {
            if (_selectedSprite == sprite) return;
            _selectedSprite = sprite;
            ApplySelectionHighlight();
            RefreshTrackLabels();
        }

        // Selección iniciada desde el propio timeline (label, clip o diamante).
        private void SelectFromTimeline(OsuSprite sprite)
        {
            if (_selectedSprite == sprite) return;
            SetSelectedSprite(sprite);
            SpriteSelected?.Invoke(sprite);
        }

        // Muestra/oculta fondo y contorno SIN redibujar (redibujar en un MouseDown destruiría
        // el clip/diamante que se está por arrastrar).
        private void ApplySelectionHighlight()
        {
            foreach (var child in TracksCanvas.Children)
            {
                if (child is not FrameworkElement fe) continue;
                if (fe.Tag is not (string kind, OsuSprite s)) continue;
                if (kind != "sel" && kind != "selbg") continue;
                fe.Visibility = s == _selectedSprite ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void TrackLabel_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is OsuSprite sprite)
                SelectFromTimeline(sprite);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void TracksScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0)
                LabelsScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
        }

        private void LabelsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // El scroll sobre los labels se reenvía al ScrollViewer de tracks,
            // que a su vez sincroniza labels vía ScrollChanged — así quedan pegados siempre.
            TracksScrollViewer.ScrollToVerticalOffset(TracksScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
}