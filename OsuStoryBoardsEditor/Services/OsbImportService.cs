using OsuStoryBoardsEditor.Models;
using System.IO;

namespace OsuStoryBoardsEditor.Services
{
    public class OsbImportService
    {
        public List<OsuSprite> Import(string osbPath, string baseFolder)
        {
            var sprites = new List<OsuSprite>();
            var lines = File.ReadAllLines(osbPath);

            OsuSprite? currentSprite = null;
            OsuSpriteLoop? currentLoop = null;
            OsuSpriteTrigger? currentTrigger = null;
            bool inEvents = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();

                if (line == "[Events]") { inEvents = true; continue; }
                if (!inEvents) continue;
                if (line.StartsWith("[") && line != "[Events]") break;

                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("//")) continue;

                var indent = line.Length - line.TrimStart().Length;
                var trimmed = line.TrimStart();
                var parts = trimmed.Split(',');

                // ── Sprite / Animation ────────────────────
                if (indent == 0 && (trimmed.StartsWith("Sprite") || trimmed.StartsWith("Animation")))
                {
                    currentLoop = null;
                    currentTrigger = null;
                    currentSprite = ParseSpriteHeader(parts, baseFolder);
                    if (currentSprite != null)
                        sprites.Add(currentSprite);
                    continue;
                }

                if (currentSprite == null) continue;

                // ── Indent 1: comando normal, loop o trigger ──
                if (indent == 1)
                {
                    currentLoop = null;
                    currentTrigger = null;

                    if (parts[0] == "T")
                    {
                        // T,triggerName,startTime,endTime[,group]
                        if (parts.Length >= 4
                            && int.TryParse(parts[2].Trim(), out int trigStart)
                            && int.TryParse(parts[3].Trim(), out int trigEnd))
                        {
                            int? group = null;
                            if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out int g)) group = g;

                            currentTrigger = new OsuSpriteTrigger
                            {
                                TriggerName = parts[1].Trim(),
                                StartTime = trigStart,
                                EndTime = trigEnd,
                                Group = group
                            };
                            currentSprite.Triggers.Add(currentTrigger);
                        }
                        continue;
                    }

                    if (parts[0] == "L")
                    {
                        if (parts.Length >= 3
                            && int.TryParse(parts[1].Trim(), out int loopStart)
                            && int.TryParse(parts[2].Trim(), out int loopCount))
                        {
                            currentLoop = new OsuSpriteLoop
                            {
                                StartTime = loopStart,
                                LoopCount = loopCount
                            };
                            currentSprite.Loops.Add(currentLoop);
                        }
                        continue;
                    }

                    var cmd = ParseCommand(parts);
                    if (cmd != null)
                        currentSprite.Commands.Add(cmd);

                    continue;
                }

                // ── Indent 2: cuerpo de loop o trigger ───
                if (indent >= 2)
                {
                    if (currentLoop != null)
                    {
                        var cmd = ParseCommand(parts);
                        if (cmd != null)
                            currentLoop.Commands.Add(cmd);
                    }
                    else if (currentTrigger != null)
                    {
                        var cmd = ParseCommand(parts);
                        if (cmd != null)
                            currentTrigger.Commands.Add(cmd);
                    }
                    continue;
                }
            }

            // Calcular StartTime / EndTime incluyendo loops
            foreach (var sprite in sprites)
            {
                var times = new List<int>();

                if (sprite.Commands.Count > 0)
                {
                    times.Add(sprite.Commands.Min(c => c.StartTime));
                    times.Add(sprite.Commands.Max(c => c.EndTime));
                }

                foreach (var loop in sprite.Loops)
                {
                    if (loop.Commands.Count > 0)
                    {
                        times.Add(loop.StartTime);
                        times.Add(loop.AbsoluteEndTime);
                    }
                }

                foreach (var trig in sprite.Triggers)
                {
                    times.Add(trig.StartTime);
                    times.Add(trig.EndTime);
                }

                if (times.Count > 0)
                {
                    sprite.StartTime = times.Min();
                    sprite.EndTime = times.Max();
                }
            }

            return sprites;
        }

        private OsuSprite? ParseSpriteHeader(string[] parts, string baseFolder)
        {
            if (parts.Length < 6) return null;

            var layerStr = parts[1].Trim();
            var originStr = parts[2].Trim();
            var filePath = parts[3].Trim().Trim('"').Replace("\\", Path.DirectorySeparatorChar.ToString());
            var xStr = parts[4].Trim();
            var yStr = parts[5].Trim();

            if (!double.TryParse(xStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double x)) x = 320;
            if (!double.TryParse(yStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double y)) y = 240;

            var fullPath = Path.Combine(baseFolder, filePath);

            return new OsuSprite
            {
                FilePath = fullPath,
                Name = Path.GetFileNameWithoutExtension(filePath),
                X = x,
                Y = y,
                Layer = ParseLayer(layerStr),
                Origin = ParseOrigin(originStr),
                StartTime = 0,
                EndTime = 5000
            };
        }

        private OsuCommand? ParseCommand(string[] parts)
        {
            if (parts.Length < 3) return null;

            var typeStr = parts[0].Trim();
            if (!TryParseCommandType(typeStr, out CommandType type)) return null;

            if (!int.TryParse(parts[1].Trim(), out int easing)) easing = 0;
            if (!int.TryParse(parts[2].Trim(), out int startTime)) return null;

            int endTime = startTime;
            if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
                int.TryParse(parts[3].Trim(), out endTime);

            var valueStrs = parts.Skip(4)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (type == CommandType.P)
            {
                string param = valueStrs.Length > 0 ? valueStrs[0].Trim().ToUpperInvariant() : "A";
                return new OsuCommand
                {
                    Type = type,
                    Easing = easing,
                    StartTime = startTime,
                    EndTime = endTime,
                    StartValues = Array.Empty<double>(),
                    EndValues = Array.Empty<double>(),
                    Parameter = param
                };
            }

            int valCount = ExpectedValueCount(type);

            var startVals = new List<double>();
            var endVals = new List<double>();

            for (int i = 0; i < valCount && i < valueStrs.Length; i++)
            {
                startVals.Add(double.TryParse(valueStrs[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0);
            }

            for (int i = valCount; i < valueStrs.Length && i < valCount * 2; i++)
            {
                endVals.Add(double.TryParse(valueStrs[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0);
            }

            if (endVals.Count == 0)
                endVals = new List<double>(startVals);

            while (startVals.Count < valCount) startVals.Add(0);
            while (endVals.Count < valCount) endVals.Add(endVals.Count > 0 ? endVals[^1] : 0);

            return new OsuCommand
            {
                Type = type,
                Easing = easing,
                StartTime = startTime,
                EndTime = endTime,
                StartValues = startVals.ToArray(),
                EndValues = endVals.ToArray()
            };
        }

        private static int ExpectedValueCount(CommandType type) => type switch
        {
            CommandType.M => 2,
            CommandType.MX => 1,
            CommandType.MY => 1,
            CommandType.F => 1,
            CommandType.S => 1,
            CommandType.R => 1,
            CommandType.C => 3,
            CommandType.V => 2,
            CommandType.P => 1,
            _ => 1
        };

        private static bool TryParseCommandType(string s, out CommandType type)
            => Enum.TryParse(s, out type);

        private static SpriteLayer ParseLayer(string s) => s switch
        {
            "Background" => SpriteLayer.Background,
            "Fail" => SpriteLayer.Fail,
            "Pass" => SpriteLayer.Pass,
            "Foreground" => SpriteLayer.Foreground,
            _ => SpriteLayer.Background
        };

        private static SpriteOrigin ParseOrigin(string s) => s switch
        {
            "TopLeft" => SpriteOrigin.TopLeft,
            "TopCentre" => SpriteOrigin.TopCentre,
            "TopRight" => SpriteOrigin.TopRight,
            "CentreLeft" => SpriteOrigin.CentreLeft,
            "Centre" => SpriteOrigin.Centre,
            "CentreRight" => SpriteOrigin.CentreRight,
            "BottomLeft" => SpriteOrigin.BottomLeft,
            "BottomCentre" => SpriteOrigin.BottomCentre,
            "BottomRight" => SpriteOrigin.BottomRight,
            _ => SpriteOrigin.Centre
        };
    }
}