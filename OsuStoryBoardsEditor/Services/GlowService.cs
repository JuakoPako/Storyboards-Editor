using SkiaSharp;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OsuStoryBoardsEditor.Services
{
    public class GlowService
    {
        // Genera una copia desenfocada (con margen para que el halo no se corte) y devuelve su ruta.
        // Misma imagen + mismo blur = mismo archivo (se reutiliza).
        public string EnsureGlowPng(string srcPath, float sigma)
        {
            Directory.CreateDirectory(TextSpriteService.CacheFolder);

            var key = $"{Path.GetFullPath(srcPath)}|{new FileInfo(srcPath).LastWriteTimeUtc.Ticks}|{sigma}";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
            var path = Path.Combine(TextSpriteService.CacheFolder, $"glow_{hash}.png");
            if (File.Exists(path)) return path;

            using var src = SKBitmap.Decode(srcPath) ?? throw new IOException("No se pudo leer la imagen.");
            int pad = (int)Math.Ceiling(sigma * 3);

            using var bmp = new SKBitmap(src.Width + pad * 2, src.Height + pad * 2,
                                         SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
                    IsAntialias = true
                };
                canvas.DrawBitmap(src, pad, pad, paint);
            }

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }
    }
}