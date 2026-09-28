using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OsuStoryBoardsEditor.Models
{
    public enum SpriteLayer { Background, Fail, Pass, Foreground }
    public enum SpriteOrigin
    {
        TopLeft, Centre, CentreLeft, TopCentre,
        TopRight, BottomCentre, BottomLeft, BottomRight, CentreRight
    }

    // ── Loop de comandos ──────────────────────────────────
    public class OsuSpriteLoop
    {
        public int StartTime { get; set; }
        public int LoopCount { get; set; }
        public List<OsuCommand> Commands { get; set; } = new();

        // Duración de una iteración (el comando interno con mayor EndTime)
        public int IterationDuration
        {
            get
            {
                if (Commands.Count == 0) return 0;
                int max = Commands[0].EndTime;
                for (int i = 1; i < Commands.Count; i++)
                    if (Commands[i].EndTime > max) max = Commands[i].EndTime;
                return max;
            }
        }

        // EndTime absoluto del loop completo
        public int AbsoluteEndTime =>
            StartTime + IterationDuration * LoopCount;
    }

    public class OsuSpriteTrigger
    {
        public string TriggerName { get; set; } = "";
        public int StartTime { get; set; }
        public int EndTime { get; set; }
        public int? Group { get; set; } // opcional, extensión de osu!stable
        public List<OsuCommand> Commands { get; set; } = new();
    }

    public class OsuSprite : INotifyPropertyChanged
    {
        private string _name = "sprite";
        private double _x = 320, _y = 240;
        private double _scale = 1.0;
        private double _rotation = 0;
        private double _opacity = 1.0;
        private bool _visible = true;

        public string FilePath { get; set; } = "";
        public SpriteLayer Layer { get; set; } = SpriteLayer.Background;
        public SpriteOrigin Origin { get; set; } = SpriteOrigin.Centre;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }
        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }
        public double Scale
        {
            get => _scale;
            set { _scale = value; OnPropertyChanged(); }
        }
        public double Rotation
        {
            get => _rotation;
            set { _rotation = value; OnPropertyChanged(); }
        }
        public double Opacity
        {
            get => _opacity;
            set { _opacity = value; OnPropertyChanged(); }
        }
        public bool Visible
        {
            get => _visible;
            set { _visible = value; OnPropertyChanged(); }
        }

        public int StartTime { get; set; } = 0;
        public int EndTime { get; set; } = 5000;

        public ObservableCollection<OsuCommand> Commands { get; set; } = new();

        // ── Loops ─────────────────────────────────────────
        public List<OsuSpriteLoop> Loops { get; set; } = new();

        public List<OsuSpriteTrigger> Triggers { get; set; } = new();

        public OsuSprite Clone(int timeOffset = 0, string? newName = null)
        {
            var c = new OsuSprite
            {
                FilePath = FilePath,
                Layer = Layer,
                Origin = Origin,
                Name = newName ?? Name,
                X = X,
                Y = Y,
                Scale = Scale,
                Rotation = Rotation,
                Opacity = Opacity,
                Visible = Visible,
                StartTime = StartTime + timeOffset,
                EndTime = EndTime + timeOffset
            };

            foreach (var cmd in Commands)
            {
                var cc = cmd.Clone();
                cc.StartTime += timeOffset;
                cc.EndTime += timeOffset;
                c.Commands.Add(cc);
            }

            foreach (var l in Loops)
            {
                var lc = new OsuSpriteLoop { StartTime = l.StartTime + timeOffset, LoopCount = l.LoopCount };
                foreach (var cmd in l.Commands) lc.Commands.Add(cmd.Clone()); // relativos al loop: NO se desplazan
                c.Loops.Add(lc);
            }

            foreach (var t in Triggers)
            {
                var tc = new OsuSpriteTrigger
                {
                    TriggerName = t.TriggerName,
                    Group = t.Group,
                    StartTime = t.StartTime + timeOffset,
                    EndTime = t.EndTime + timeOffset
                };
                foreach (var cmd in t.Commands) tc.Commands.Add(cmd.Clone()); // igual: relativos
                c.Triggers.Add(tc);
            }

            return c;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}