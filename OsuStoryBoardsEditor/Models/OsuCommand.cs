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