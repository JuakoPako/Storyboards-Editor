using OsuStoryBoardsEditor.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OsuStoryBoardsEditor.Services
{
    public class TextSpriteService
    {
        public static string CacheFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuStoryBoardsEditor", "textcache");

        public static IReadOnlyList<string> GetInstalledFonts()
            => SKFontManager.Default.GetFontFamilies().OrderBy(f => f).ToList();

        private static readonly Lazy<HashSet<string>> InstalledSet = new(() =>
            new HashSet<string>(SKFontManager.Default.GetFontFamilies(), StringComparer.OrdinalIgnoreCase));

        public static bool IsFontInstalled(string family) => InstalledSet.Value.Contains(family);

        private static SKTypeface CreateTypeface(TextSpec s) => SKTypeface.FromFamilyName(
            s.FontFamily,
            s.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            s.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        private static string Hash(TextSpec s)
        {
            var key = string.Create(CultureInfo.InvariantCulture,
                $"{s.Text}|{s.FontFamily}|{s.FontSize}|{s.Bold}|{s.Italic}|{s.R},{s.G},{s.B}");
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        }

        private static float Measure(SKFont font, SKPaint paint, string text)
        {
            int n = font.CountGlyphs(text);
            if (n == 0) return 0f;
            var glyphs = new ushort[n];
            font.GetGlyphs(text, glyphs);
            return font.MeasureText(glyphs, paint);
        }

        // Genera el PNG si no existe y devuelve su ruta. Mismo texto+estilo = mismo archivo.
        public string EnsurePng(TextSpec s)
        {
            Directory.CreateDirectory(CacheFolder);
            string suffix = IsFontInstalled(s.FontFamily) ? "" : "_fb";   // fallback: no se mezcla con el real
            string path = Path.Combine(CacheFolder, $"t_{Hash(s)}{suffix}.png");

            if (File.Exists(path)) return path;

            using var typeface = CreateTypeface(s);
            using var font = new SKFont(typeface, s.FontSize) { Edging = SKFontEdging.Antialias };
            using var paint = new SKPaint { Color = new SKColor(s.R, s.G, s.B), IsAntialias = true };
            font.GetFontMetrics(out var m);

            // La celda mide "avance del texto + margen" x "alto de línea + margen".
            // Así el centro del PNG es el centro del texto, y todas las letras de una
            // palabra comparten alto y línea base: al ponerlas lado a lado encajan solas.
            int pad = (int)Math.Ceiling(s.FontSize * 0.15f);
            float adv = Measure(font, paint, s.Text);
            int w = (int)Math.Ceiling(adv) + pad * 2;
            int h = (int)Math.Ceiling(m.Descent - m.Ascent) + pad * 2;

            using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawText(s.Text, pad, pad - m.Ascent, font, paint);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        // Para el modo "una letra por sprite": cada letra con su spec y su centro X
        // relativo al centro del texto completo. Los espacios no generan sprite.
        public List<(TextSpec Spec, float CenterX)> LayoutLetters(TextSpec s, out float totalWidth)
        {
            using var typeface = CreateTypeface(s);
            using var font = new SKFont(typeface, s.FontSize);
            using var paint = new SKPaint();

            totalWidth = Measure(font, paint, s.Text);
            var result = new List<(TextSpec, float)>();
            for (int i = 0; i < s.Text.Length; i++)
            {
                if (char.IsWhiteSpace(s.Text[i])) continue;
                float left = Measure(font, paint, s.Text.Substring(0, i));
                float adv = Measure(font, paint, s.Text[i].ToString());
                result.Add((s with { Text = s.Text[i].ToString() }, left + adv / 2f - totalWidth / 2f));
            }
            return result;
        }
    }
}