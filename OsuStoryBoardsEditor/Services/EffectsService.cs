using OsuStoryBoardsEditor.Models;
using SkiaSharp;
using System.Globalization;
using System.IO;

namespace OsuStoryBoardsEditor.Services
{
    public class ParticleBurstOptions
    {
        public int StartTime;
        public int Duration = 1500;
        public int Count = 40;
        public double MinSize = 6, MaxSize = 40;
        public double MinDist = 80, MaxDist = 420;
        public double SpinDeg = 180;
        public double Alpha = 0.6;
        public double CenterX = 320, CenterY = 240;
        public byte R = 255, G = 255, B = 255;
        public int Seed = 1;
    }

    public class EffectsService
    {
        public static bool ParseHex(string s, out byte r, out byte g, out byte b)
        {
            r = g = b = 255;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            return byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        private static OsuCommand Cmd(CommandType type, int easing, int t0, int t1, double[] a, double[] b) => new()
        {
            Type = type,
            Easing = easing,
            StartTime = t0,
            EndTime = t1,
            StartValues = a,
            EndValues = b
        };

        private static void AddTint(OsuSprite s, byte r, byte g, byte b, int t0, int t1)
        {
            if (r == 255 && g == 255 && b == 255) return;
            var c = new double[] { r, g, b };
            s.Commands.Add(Cmd(CommandType.C, 0, t0, t1, c, c));
        }

        // ── Explosión de partículas (rectángulos que salen del centro) ──
        public List<OsuSprite> GenerateBurst(ParticleBurstOptions o)
        {
            string png = new SpectrumBarsService().EnsureBarPng();   // PNG blanco 1x1, no suma peso
            var rnd = new Random(o.Seed);
            int end = o.StartTime + o.Duration;
            var list = new List<OsuSprite>(o.Count);

            for (int i = 0; i < o.Count; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2;
                double dist = o.MinDist + rnd.NextDouble() * (o.MaxDist - o.MinDist);
                double w = Math.Round(o.MinSize + rnd.NextDouble() * (o.MaxSize - o.MinSize));
                double h = Math.Max(1, Math.Round(w * (0.5 + rnd.NextDouble() * 1.5)));
                double spin = (rnd.NextDouble() * 2 - 1) * o.SpinDeg * Math.PI / 180.0;

                int s = o.StartTime + (int)(rnd.NextDouble() * o.Duration * 0.15);   // no salen todas juntas
                int mid = s + (end - s) / 2;

                var sp = new OsuSprite
                {
                    FilePath = png,
                    Name = $"particula {i + 1:00}",
                    X = o.CenterX,
                    Y = o.CenterY,
                    Origin = SpriteOrigin.Centre,
                    Layer = SpriteLayer.Foreground,
                    StartTime = s,
                    EndTime = end
                };

                sp.Commands.Add(Cmd(CommandType.F, 0, s, s + 100, new[] { 0.0 }, new[] { o.Alpha }));
                sp.Commands.Add(Cmd(CommandType.F, 0, mid, end, new[] { o.Alpha }, new[] { 0.0 }));
                sp.Commands.Add(Cmd(CommandType.M, 19, s, end,   // 19 = Expo Out
                    new[] { o.CenterX, o.CenterY },
                    new[] { Math.Round(o.CenterX + Math.Cos(ang) * dist), Math.Round(o.CenterY + Math.Sin(ang) * dist) }));
                sp.Commands.Add(Cmd(CommandType.V, 1, s, end, new[] { w, h }, new[] { Math.Round(w * 0.4), Math.Round(h * 0.4) }));
                sp.Commands.Add(Cmd(CommandType.R, 1, s, end, new[] { 0.0 }, new[] { spin }));
                AddTint(sp, o.R, o.G, o.B, s, end);
                list.Add(sp);
            }
            return list;
        }

        // ── Rayos radiales ──
        // PNG de 512 px (pesa poco); el sprite se escala x2 con el comando S.
        public string EnsureRaysPng(int rays, double alpha)
        {
            Directory.CreateDirectory(TextSpriteService.CacheFolder);
            string path = Path.Combine(TextSpriteService.CacheFolder, $"rays_{rays}_{(int)(alpha * 100)}.png");
            if (File.Exists(path)) return path;

            const int size = 512;
            float c = size / 2f, radius = size * 0.72f;   // llega a las esquinas
            using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);

            byte a = (byte)Math.Clamp(alpha * 255, 1, 255);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(c, c), radius,
                    new[] { SKColors.White.WithAlpha(a), SKColors.White.WithAlpha(0) },
                    null, SKShaderTileMode.Clamp)
            };

            double step = Math.PI * 2 / rays;
            for (int i = 0; i < rays; i++)
            {
                double a0 = i * step, a1 = a0 + step * 0.5;
                using var p = new SKPath();
                p.MoveTo(c, c);
                p.LineTo(c + (float)(Math.Cos(a0) * radius), c + (float)(Math.Sin(a0) * radius));
                p.LineTo(c + (float)(Math.Cos(a1) * radius), c + (float)(Math.Sin(a1) * radius));
                p.Close();
                canvas.DrawPath(p, paint);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        public OsuSprite GenerateRays(int start, int end, int rays, double alpha, double spinDeg,
                                      byte r, byte g, byte b, double cx, double cy)
        {
            int fade = Math.Min(500, (end - start) / 3);
            var s = new OsuSprite
            {
                FilePath = EnsureRaysPng(rays, alpha),
                Name = "rayos",
                X = cx,
                Y = cy,
                Origin = SpriteOrigin.Centre,
                Layer = SpriteLayer.Foreground,
                StartTime = start,
                EndTime = end
            };
            s.Commands.Add(Cmd(CommandType.F, 0, start, start + fade, new[] { 0.0 }, new[] { 1.0 }));
            s.Commands.Add(Cmd(CommandType.F, 0, end - fade, end, new[] { 1.0 }, new[] { 0.0 }));
            s.Commands.Add(Cmd(CommandType.S, 0, start, end, new[] { 2.0 }, new[] { 2.2 }));
            s.Commands.Add(Cmd(CommandType.R, 0, start, end, new[] { 0.0 }, new[] { spinDeg * Math.PI / 180.0 }));
            AddTint(s, r, g, b, start, end);
            return s;
        }
    }
}