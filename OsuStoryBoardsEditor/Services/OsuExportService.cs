using OsuStoryBoardsEditor.Models;
using System.IO;
using System.Globalization;

namespace OsuStoryBoardsEditor.Services
{
    public class OsuExportService
    {
        // Carpeta estándar (dentro de la carpeta del mapa) donde van todas las imágenes del storyboard
        public const string AssetsFolder = "sb";

        public void Export(StoryboardProject project, string outputFolder, string osbFileName = "storyboard.osb")
        {
            // El formato .osb exige punto decimal: forzamos InvariantCulture durante la exportación
            var prev = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                ExportInternal(project, outputFolder, osbFileName);
            }
            finally
            {
                CultureInfo.CurrentCulture = prev;
            }
        }

        private void ExportInternal(StoryboardProject project, string outputFolder, string osbFileName)
        {
            Directory.CreateDirectory(outputFolder);
            var assetsDir = Path.Combine(outputFolder, AssetsFolder);
            Directory.CreateDirectory(assetsDir);

            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sprite in project.Sprites)
            {
                if (!File.Exists(sprite.FilePath)) continue;
                var destFile = Path.Combine(assetsDir, Path.GetFileName(sprite.FilePath));

                // Si la imagen ya está en la carpeta de salida, no hay nada que copiar
                if (string.Equals(Path.GetFullPath(sprite.FilePath), Path.GetFullPath(destFile),
                                  StringComparison.OrdinalIgnoreCase)) continue;

                // Varios sprites pueden usar la misma imagen: se copia una sola vez
                if (!copied.Add(destFile)) continue;

                File.Copy(sprite.FilePath, destFile, overwrite: true);
            }

            var osbPath = Path.Combine(outputFolder, osbFileName);
            using var writer = new StreamWriter(osbPath);

            writer.WriteLine("[Events]");
            writer.WriteLine("//Background and Video events");
            writer.WriteLine("//Storyboard Layer 0 (Background)");

            foreach (var sprite in project.Sprites)
            {
                if (!sprite.Visible) continue;

                var fileName = $"{AssetsFolder}\\{Path.GetFileName(sprite.FilePath)}";
                writer.WriteLine($"Sprite,{sprite.Layer},{sprite.Origin},\"{fileName}\",{(int)sprite.X},{(int)sprite.Y}");

                if (sprite.Commands.Count == 0 && sprite.Loops.Count == 0 && sprite.Triggers.Count == 0)
                {
                    writer.WriteLine($" F,0,{sprite.StartTime},{sprite.EndTime},1,1");
                    if (sprite.Opacity < 1.0)
                        writer.WriteLine($" F,0,{sprite.StartTime},{sprite.EndTime},{sprite.Opacity:F2},{sprite.Opacity:F2}");
                    if (sprite.Scale != 1.0)
                        writer.WriteLine($" S,0,{sprite.StartTime},{sprite.EndTime},{sprite.Scale:F2},{sprite.Scale:F2}");
                    if (sprite.Rotation != 0.0)
                    {
                        double rad = sprite.Rotation;
                        writer.WriteLine($" R,0,{sprite.StartTime},{sprite.EndTime},{rad:F4},{rad:F4}");
                    }
                }
                else
                {
                    bool hasF = sprite.Commands.Any(c => c.Type == CommandType.F)
         || sprite.Loops.Any(l => l.Commands.Any(c => c.Type == CommandType.F));
                    if (!hasF)
                        writer.WriteLine($" F,0,{sprite.StartTime},{sprite.EndTime},1,1");

                    // Comandos sueltos del sprite (indent nivel 1 = un espacio)
                    foreach (var cmd in sprite.Commands
                        .Where(c => c.StartTime != c.EndTime)
                        .OrderBy(c => c.StartTime))
                    {
                        WriteCommand(writer, cmd, " ");
                    }

                    // Loops (L,... a nivel 1; sus comandos internos a nivel 2 = dos espacios)
                    foreach (var loop in sprite.Loops)
                    {
                        writer.WriteLine($" L,{loop.StartTime},{loop.LoopCount}");
                        foreach (var cmd in loop.Commands.OrderBy(c => c.StartTime))
                            WriteCommand(writer, cmd, "  ");
                    }

                    foreach (var trig in sprite.Triggers)
                    {
                        var groupPart = trig.Group.HasValue ? $",{trig.Group.Value}" : "";
                        writer.WriteLine($" T,{trig.TriggerName},{trig.StartTime},{trig.EndTime}{groupPart}");
                        foreach (var cmd in trig.Commands.OrderBy(c => c.StartTime))
                            WriteCommand(writer, cmd, "  ");
                    }
                }
            }

            writer.WriteLine("//Storyboard Layer 1 (Fail)");
            writer.WriteLine("//Storyboard Layer 2 (Pass)");
            writer.WriteLine("//Storyboard Layer 3 (Foreground)");
            writer.WriteLine("//Storyboard Sound Samples");
        }

        // Escribe un comando individual con el indent dado (" " para nivel de sprite, "  " dentro de un loop)
        private void WriteCommand(StreamWriter writer, OsuCommand cmd, string indent)
        {
            switch (cmd.Type)
            {
                case CommandType.F:
                    writer.WriteLine($"{indent}F,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F2},{cmd.EndValues[0]:F2}");
                    break;

                case CommandType.M:
                    writer.WriteLine($"{indent}M,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F0},{cmd.StartValues[1]:F0},{cmd.EndValues[0]:F0},{cmd.EndValues[1]:F0}");
                    break;

                case CommandType.S:
                    writer.WriteLine($"{indent}S,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F2},{cmd.EndValues[0]:F2}");
                    break;

                case CommandType.V:
                    writer.WriteLine($"{indent}V,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F2},{cmd.StartValues[1]:F2},{cmd.EndValues[0]:F2},{cmd.EndValues[1]:F2}");
                    break;

                case CommandType.R:
                    writer.WriteLine($"{indent}R,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F4},{cmd.EndValues[0]:F4}");
                    break;

                case CommandType.MX:
                    writer.WriteLine($"{indent}MX,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F0},{cmd.EndValues[0]:F0}");
                    break;

                case CommandType.MY:
                    writer.WriteLine($"{indent}MY,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.StartValues[0]:F0},{cmd.EndValues[0]:F0}");
                    break;

                case CommandType.C:
                    writer.WriteLine($"{indent}C,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{(int)cmd.StartValues[0]},{(int)cmd.StartValues[1]},{(int)cmd.StartValues[2]},{(int)cmd.EndValues[0]},{(int)cmd.EndValues[1]},{(int)cmd.EndValues[2]}");
                    break;

                case CommandType.P:
                    writer.WriteLine($"{indent}P,{cmd.Easing},{cmd.StartTime},{cmd.EndTime},{cmd.Parameter ?? "A"}");
                    break;


            }
        }
    }
}