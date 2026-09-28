using OsuStoryBoardsEditor.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OsuStoryBoardsEditor.Services
{
    public static class BeatSnapService
    {
        
        public static int SnapToBeat(int timeMs, List<OsuTimingPoint> allPoints, int divisor)
        {
            var redLines = allPoints.Where(t => t.Uninherited).OrderBy(t => t.Time).ToList();
            if (redLines.Count == 0) return timeMs;

            var tp = redLines.LastOrDefault(t => t.Time <= timeMs) ?? redLines[0];
            double beatLen = tp.BeatLength / divisor;
            double beatsFromTp = (timeMs - tp.Time) / beatLen;
            int nearestBeat = (int)Math.Round(beatsFromTp);

            return (int)Math.Round(tp.Time + nearestBeat * beatLen);
        }
    }
}