using OsuStoryBoardsEditor.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace OsuStoryBoardsEditor.Services
{
    public class SpectrumBarsOptions
    {
        public int RangeStart { get; set; }
        public int RangeEnd { get; set; }

        public bool KiaiOnly { get; set; }

        public int Bars { get; set; } = 32;
        public int Fps { get; set; } = 20;              // cuadros de espectro por segundo
        public double TotalWidth { get; set; } = 854;   // ancho que ocupa toda la fila de barras
        public double MaxHeight { get; set; } = 120;
        public double Gap { get; set; } = 2;            // separación entre barras (px)
        public double BaseY { get; set; } = 470;        // línea base (las barras crecen hacia arriba)
        public double Contrast { get; set; } = 2.0;     // >1 = más contraste entre suave y fuerte
        public double MinDeltaPx { get; set; } = 2;     // cambios menores a esto no generan comando
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 255;
        public byte B { get; set; } = 255;
    }

    public class SpectrumBarsService
    {
        public static List<(int Start, int End)> GetKiaiRanges(IEnumerable<OsuTimingPoint> points, int totalMs)
        {
            var res = new List<(int Start, int End)>();
            int? open = null;
            foreach (var tp in points.OrderBy(t => t.Time))
            {
                if (tp.Kiai && open == null) open = tp.Time;
                else if (!tp.Kiai && open != null) { res.Add((open.Value, tp.Time)); open = null; }
            }
            if (open != null && totalMs > open.Value) res.Add((open.Value, totalMs));   // kiai hasta el final
            return res;
        }
        // PNG blanco de 1x1: con el comando V (escala X/Y) cada barra mide exactamente w x h píxeles.
        public string EnsureBarPng()
        {
            Directory.CreateDirectory(TextSpriteService.CacheFolder);
            string path = Path.Combine(TextSpriteService.CacheFolder, "bar_px.png");
            if (File.Exists(path)) return path;

            using var bmp = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
            bmp.SetPixel(0, 0, SKColors.White);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        // Una barra (sprite) por banda. Solo se escribe un comando cuando la altura cambia lo
        // suficiente; mientras no cambie, osu! mantiene el último valor.
        public List<OsuSprite> Generate(SpectrumData data, SpectrumBarsOptions o)
        {
            string png = EnsureBarPng();
            int n = data.BandCount;
            int frames = data.Frames.Length;
            int fMs = data.FrameMs;

            int gap = (int)Math.Max(0, Math.Round(o.Gap));
            int barW = Math.Max(1, (int)Math.Floor((o.TotalWidth - gap * (n - 1)) / n));
            int pitch = barW + gap;
            double total = pitch * n - gap;
            double left = 320 - total / 2.0;   // 320 = centro X del storyboard

            int tStart = data.TimeMs(0);
            int tEnd = data.TimeMs(frames - 1);
            bool tinted = !(o.R == 255 && o.G == 255 && o.B == 255);

            var result = new List<OsuSprite>(n);
            for (int b = 0; b < n; b++)
            {
                var s = new OsuSprite
                {
                    FilePath = png,
                    Name = $"espectro {b + 1:00}",   // los dígitos finales hacen que el timeline las agrupe en una sola fila
                    X = Math.Round(left + barW / 2.0 + b * pitch),
                    Y = o.BaseY,
                    Origin = SpriteOrigin.BottomCentre,
                    Layer = SpriteLayer.Foreground,
                    StartTime = tStart,
                    EndTime = tEnd
                };

                double H(int f) => Math.Max(1, Math.Round(o.MaxHeight * Math.Pow(data.Frames[f][b], o.Contrast)));

                OsuCommand Seg(int t0, int t1, double h0, double h1) => new()
                {
                    Type = CommandType.V,
                    Easing = 0,
                    StartTime = t0,
                    EndTime = t1,
                    StartValues = new[] { (double)barW, h0 },
                    EndValues = new[] { (double)barW, h1 }
                };

                double hPrev = H(0);
                int lastEnd = tStart;
                bool any = false;

                for (int f = 1; f < frames; f++)
                {
                    double h = H(f);
                    if (h == hPrev) continue;
                    if (Math.Abs(h - hPrev) < o.MinDeltaPx && f != frames - 1) continue;

                    int t1 = data.TimeMs(f);
                    int t0 = Math.Max(t1 - fMs, lastEnd);   // nunca se pisa con el tramo anterior

                    if (!any && t0 > tStart)   // altura inicial hasta el primer cambio
                        s.Commands.Add(Seg(tStart, t0, hPrev, hPrev));

                    s.Commands.Add(Seg(t0, t1, hPrev, h));
                    hPrev = h;
                    lastEnd = t1;
                    any = true;
                }

                if (lastEnd < tEnd)   // mantiene la barra viva hasta el final del rango
                    s.Commands.Add(Seg(lastEnd, tEnd, hPrev, hPrev));

                if (tinted)
                    s.Commands.Add(new OsuCommand
                    {
                        Type = CommandType.C,
                        StartTime = tStart,
                        EndTime = tEnd,
                        StartValues = new double[] { o.R, o.G, o.B },
                        EndValues = new double[] { o.R, o.G, o.B }
                    });
                if (o.KiaiOnly)
                {
                    int fd = Math.Min(300, (tEnd - tStart) / 4);
                    s.Commands.Add(new OsuCommand
                    {
                        Type = CommandType.F,
                        StartTime = tStart,
                        EndTime = tStart + fd,
                        StartValues = new[] { 0.0 },
                        EndValues = new[] { 1.0 }
                    });
                    s.Commands.Add(new OsuCommand
                    {
                        Type = CommandType.F,
                        StartTime = tEnd - fd,
                        EndTime = tEnd,
                        StartValues = new[] { 1.0 },
                        EndValues = new[] { 0.0 }
                    });
                }

                result.Add(s);
            }
            return result;
        }
        // Interpolación lineal (los segmentos del espectro usan easing 0)
        private static double[] ValuesAt(OsuCommand c, int t)
        {
            double k = (double)(t - c.StartTime) / (c.EndTime - c.StartTime);
            var r = new double[c.StartValues.Length];
            for (int i = 0; i < r.Length; i++)
                r[i] = Math.Round(c.StartValues[i] + (c.EndValues[i] - c.StartValues[i]) * k);
            return r;
        }

        // Deja solo lo que cae dentro de [from, to] (ms). Devuelve true si cambió algo.
        public static bool Trim(IEnumerable<OsuSprite> bars, int from, int to, List<OsuSprite> toRemove)
        {
            var list = bars.ToList();
            if (list.Count == 0 || from >= to) return false;
            // si el rango no toca el espectro, no hace nada (evita dejarlo vacío)
            if (from >= list.Max(s => s.EndTime) || to <= list.Min(s => s.StartTime)) return false;

            bool changed = false;
            foreach (var s in list)
            {
                if (s.EndTime <= from || s.StartTime >= to) { toRemove.Add(s); changed = true; continue; }
                var kept = new List<OsuCommand>();
                foreach (var c in s.Commands)
                {
                    if (c.EndTime <= from || c.StartTime >= to) { changed = true; continue; }   // fuera del rango

                    if (c.StartTime < from || c.EndTime > to)   // cruza un borde: se corta interpolando
                    {
                        changed = true;
                        int a = Math.Max(c.StartTime, from), b = Math.Min(c.EndTime, to);
                        var n = c.Clone();
                        n.StartValues = ValuesAt(c, a);
                        n.EndValues = ValuesAt(c, b);
                        n.StartTime = a;
                        n.EndTime = b;
                        kept.Add(n);
                    }
                    else kept.Add(c);
                }

                s.Commands.Clear();
                foreach (var c in kept) s.Commands.Add(c);
                s.StartTime = Math.Max(s.StartTime, from);
                s.EndTime = Math.Min(s.EndTime, to);
            }
            return changed;
        }
    }
}