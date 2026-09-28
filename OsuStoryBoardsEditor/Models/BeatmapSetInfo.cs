using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsuStoryBoardsEditor.Models
{
    public class BeatmapSetInfo
    {
        public string FolderPath { get; set; } = "";
        public string OsbPath { get; set; } = "";
        public string OsuPath { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Creator { get; set; } = "";
        public string? BackgroundPath { get; set; }
        public string? AudioPath { get; set; }
    }
}