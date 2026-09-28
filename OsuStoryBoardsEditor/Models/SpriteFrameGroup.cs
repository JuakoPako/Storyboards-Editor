using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsuStoryBoardsEditor.Models
{
    internal class SpriteFrameGroup
    {

        public string Name { get; set; } = "";
        public List<OsuSprite> Frames { get; set; } = new();

        public int StartTime => Frames.Count > 0 ? Frames.Min(f => f.StartTime) : 0;
        public int EndTime => Frames.Count > 0 ? Frames.Max(f => f.EndTime) : 0;

    }
}
