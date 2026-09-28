using OsuStoryBoardsEditor.Models;
using System.Globalization;
using System.IO;

namespace OsuStoryBoardsEditor.Services
{
    public class BeatmapLibraryService
    {
        // Lee [TimingPoints] de un .osu cualquiera (para importar BPM/timing a un proyecto de SB)
        public List<OsuTimingPoint> ParseTimingPoints(string osuPath)
        {
            var result = new List<OsuTimingPoint>();
            string section = "";
            foreach (var raw in File.ReadAllLines(osuPath))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[")) { section = line; continue; }
                if (section != "[TimingPoints]") continue;

                var p = line.Split(',');
                if (p.Length < 7) continue;

                result.Add(new OsuTimingPoint
                {
                    Time = (int)double.Parse(p[0], CultureInfo.InvariantCulture),
                    BeatLength = double.Parse(p[1], CultureInfo.InvariantCulture),
                    Meter = int.Parse(p[2]),
                    Uninherited = p[6] == "1"
                });
            }
            return result;
        }

        // Ruta default de osu! stable. Si no existe, quien llame debe pedir la carpeta manualmente.
        public static string? GetDefaultSongsFolder()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "osu!", "Songs");
            return Directory.Exists(path) ? path : null;
        }

        // Escanea todas las subcarpetas de Songs y devuelve solo los sets que tienen .osb
        public List<BeatmapSetInfo> ScanSongsFolder(string songsFolder)
        {
            var result = new List<BeatmapSetInfo>();
            if (!Directory.Exists(songsFolder)) return result;

            foreach (var folder in Directory.GetDirectories(songsFolder))
            {
                var osbFiles = Directory.GetFiles(folder, "*.osb");
                if (osbFiles.Length == 0) continue; // sin storyboard, no nos interesa

                var osuFiles = Directory.GetFiles(folder, "*.osu");
                if (osuFiles.Length == 0) continue; // no debería pasar, pero por las dudas

                var info = ParseOsuMetadata(osuFiles[0], folder);
                if (info == null) continue;

                info.FolderPath = folder;
                info.OsbPath = osbFiles[0];
                info.OsuPath = osuFiles[0];
                result.Add(info);
            }

            return result;
        }

        // Escanea Songs y devuelve los sets que tienen .osu pero NO tienen .osb todavía
        // (para arrancar un storyboard nuevo sobre un mapa que ya tiene timing/audio listos)
        public List<BeatmapSetInfo> ScanSongsFolderWithoutSb(string songsFolder)
        {
            var result = new List<BeatmapSetInfo>();
            if (!Directory.Exists(songsFolder)) return result;

            foreach (var folder in Directory.GetDirectories(songsFolder))
            {
                var osbFiles = Directory.GetFiles(folder, "*.osb");
                if (osbFiles.Length > 0) continue; // ya tiene SB, no nos sirve para este flujo

                var osuFiles = Directory.GetFiles(folder, "*.osu");
                if (osuFiles.Length == 0) continue; // ni timing tiene, no hay nada que importar

                var info = ParseOsuMetadata(osuFiles[0], folder);
                if (info == null) continue;

                info.FolderPath = folder;
                info.OsbPath = ""; // todavía no existe
                info.OsuPath = osuFiles[0];
                result.Add(info);
            }

            return result;
        }

        // Lee Title/Artist/Creator/AudioFilename/Background de un .osu
        private BeatmapSetInfo? ParseOsuMetadata(string osuPath, string baseFolder)
        {
            string title = "", artist = "", creator = "", audioFile = "";
            string? background = null;

            string section = "";
            foreach (var rawLine in File.ReadAllLines(osuPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line;
                    continue;
                }

                if (section == "[General]" && line.StartsWith("AudioFilename:"))
                    audioFile = line.Substring("AudioFilename:".Length).Trim();

                else if (section == "[Metadata]")
                {
                    if (line.StartsWith("Title:")) title = line.Substring("Title:".Length).Trim();
                    else if (line.StartsWith("Artist:")) artist = line.Substring("Artist:".Length).Trim();
                    else if (line.StartsWith("Creator:")) creator = line.Substring("Creator:".Length).Trim();
                }
                else if (section == "[Events]" && background == null && line.StartsWith("0,0,"))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 3)
                    {
                        var bgPath = Path.Combine(baseFolder, parts[2].Trim().Trim('"'));
                        if (File.Exists(bgPath)) background = bgPath;
                    }
                }
            }

            if (string.IsNullOrEmpty(title)) return null; // .osu sin metadata válida, lo saltamos

            var audioPath = !string.IsNullOrEmpty(audioFile)
                ? Path.Combine(baseFolder, audioFile)
                : null;

            return new BeatmapSetInfo
            {
                Title = title,
                Artist = artist,
                Creator = creator,
                BackgroundPath = background,
                AudioPath = File.Exists(audioPath) ? audioPath : null
            };
        }
    }
}