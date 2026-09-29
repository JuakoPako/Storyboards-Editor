using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Models/OsuTimingPoint.cs
namespace OsuStoryBoardsEditor.Models
{
    public class OsuTimingPoint
    {
        public int Time { get; set; }
        public double BeatLength { get; set; } // ms por beat (solo válido si Uninherited)
        public int Meter { get; set; }
        public bool Uninherited { get; set; }   // true = línea roja (define BPM), false = verde (SV)

        public bool Kiai { get; set; }

    }
}
