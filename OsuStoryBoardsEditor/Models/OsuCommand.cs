namespace OsuStoryBoardsEditor.Models
{
    public enum CommandType { F, M, S, R, C, MX, MY, V, P }  // Fade, Move, Scale, Rotate, Color, MoveX, MoveY, Vector

    public class OsuCommand
    {
        public CommandType Type { get; set; }
        public int Easing { get; set; } = 0;
        public int StartTime { get; set; }
        public int EndTime { get; set; }
        public double[] StartValues { get; set; } = Array.Empty<double>();
        public double[] EndValues { get; set; } = Array.Empty<double>();

        public string? Parameter { get; set; }

        public static readonly string[] EasingNames =
        {
            "Linear", "Out", "In",
            "Quad In", "Quad Out", "Quad In/Out",
            "Cubic In", "Cubic Out", "Cubic In/Out",
            "Quart In", "Quart Out", "Quart In/Out",
            "Quint In", "Quint Out", "Quint In/Out",
            "Sine In", "Sine Out", "Sine In/Out",
            "Expo In", "Expo Out", "Expo In/Out",
            "Circ In", "Circ Out", "Circ In/Out",
            "Elastic In", "Elastic Out", "ElasticHalf Out", "ElasticQuarter Out", "Elastic In/Out",
            "Back In", "Back Out", "Back In/Out",
            "Bounce In", "Bounce Out", "Bounce In/Out"
        };

        public OsuCommand Clone() => new()
        {
            Type = Type,
            Easing = Easing,
            StartTime = StartTime,
            EndTime = EndTime,
            StartValues = (double[])StartValues.Clone(),
            EndValues = (double[])EndValues.Clone(),
            Parameter = Parameter
        };

    }
}