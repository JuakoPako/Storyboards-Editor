namespace OsuStoryBoardsEditor.Models
{
    public class CommandSaveData
    {
        public string Type { get; set; } = "";
        public int Easing { get; set; }
        public int StartTime { get; set; }
        public int EndTime { get; set; }
        public double[] StartValues { get; set; } = Array.Empty<double>();
        public double[] EndValues { get; set; } = Array.Empty<double>();

        public string? Parameter { get; set; }

    }

    public class SpriteSaveData
    {
        public string FilePath { get; set; } = "";
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Scale { get; set; } = 1.0;
        public double Rotation { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool Visible { get; set; } = true;
        public int StartTime { get; set; }
        public int EndTime { get; set; } = 5000;

        public List<TriggerSaveData> Triggers { get; set; } = new();
        public List<CommandSaveData> Commands { get; set; } = new();
    }

    public class ProjectSaveData
    {
        public string AudioPath { get; set; } = "";
        public int TotalDuration { get; set; }
        public List<SpriteSaveData> Sprites { get; set; } = new();
    }

    public class TriggerSaveData
    {
        public string TriggerName { get; set; } = "";
        public int StartTime { get; set; }
        public int EndTime { get; set; }
        public int? Group { get; set; }
        public List<CommandSaveData> Commands { get; set; } = new();
    }
}