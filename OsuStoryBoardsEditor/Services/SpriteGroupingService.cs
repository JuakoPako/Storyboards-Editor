using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor.Services
{
    public static class SpriteGroupingService
    {
        private const int MinFramesToGroup = 3;   // menos de esto, no vale la pena agrupar
        private const int MaxGapMs = 500;         // gap máximo entre frames para considerarlos la misma animación

        // Devuelve una lista mezclada de filas: OsuSprite (sprite suelto) o SpriteFrameGroup (animación colapsada)
        public static List<object> BuildTimelineRows(IEnumerable<OsuSprite> sprites)
        {
            var result = new List<object>();

            var byPrefix = sprites
                .Select(s => new { Sprite = s, Prefix = GetPrefix(s.Name) })
                .GroupBy(x => x.Prefix);

            foreach (var group in byPrefix)
            {
                var ordered = group.Select(x => x.Sprite).OrderBy(s => s.StartTime).ToList();
                if (ordered.Count == 0) continue;

                var currentRun = new List<OsuSprite> { ordered[0] };
                for (int i = 1; i < ordered.Count; i++)
                {
                    var prev = currentRun[^1];
                    var curr = ordered[i];
                    int gap = curr.StartTime - prev.EndTime;

                    if (gap <= MaxGapMs)
                        currentRun.Add(curr);
                    else
                    {
                        FlushRun(currentRun, group.Key, result);
                        currentRun = new List<OsuSprite> { curr };
                    }
                }
                FlushRun(currentRun, group.Key, result);
            }

            return result.OrderBy(r => r switch
            {
                OsuSprite s => s.StartTime,
                SpriteFrameGroup g => g.StartTime,
                _ => 0
            }).ToList();
        }

        private static void FlushRun(List<OsuSprite> run, string prefix, List<object> result)
        {
            if (run.Count >= MinFramesToGroup)
                result.Add(new SpriteFrameGroup { Name = prefix, Frames = new List<OsuSprite>(run) });
            else
                result.AddRange(run); // muy pocos frames, no vale agruparlos
        }

        // "f12" -> "f", "sprite" -> "sprite" (saca los dígitos finales del nombre)
        private static string GetPrefix(string name)
        {
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            return i == name.Length ? name : name.Substring(0, i);
        }
    }
}