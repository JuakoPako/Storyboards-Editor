using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor
{
    
    public enum WrapAxis { Horizontal, Vertical }

    public enum BeatEffectType { ScalePulse, WrapMove, StepMove }


    public class BeatEffectOptions
    {
        public BeatEffectType Type { get; set; } = BeatEffectType.ScalePulse;
        public int RangeStart { get; set; }
        public int RangeEnd { get; set; }

        // Pulso de escala
        public double Intensity { get; set; } = 0.15;
        public int EasingIn { get; set; } = 4;
        public int EasingOut { get; set; } = 2;

        // Movimiento con wrap
        public WrapAxis Axis { get; set; } = WrapAxis.Horizontal;
        public bool Reverse { get; set; } = false;
        public int BeatsPerCross { get; set; } = 4;
        public double Margin { get; set; } = 100;
        public int MoveEasing { get; set; } = 0;

        
        // Saltos al ritmo
        public int Jumps { get; set; } = 8;          // saltos para cruzar la pantalla
        public int BeatsPerJump { get; set; } = 1;   // 1 = cada beat, 4 = solo tiempo fuerte
        public double JumpPct { get; set; } = 35;    // % del intervalo que dura el salto
        public int JumpEasing { get; set; } = 19;    // Expo Out
    }

    public class BeatLoopService
    {
        public List<OsuSpriteLoop> Generate(OsuSprite sprite, List<OsuTimingPoint> tps, BeatEffectOptions o)

            => o.Type switch

            {
                BeatEffectType.WrapMove => GenerateWrapMove(sprite, tps, o),
                BeatEffectType.StepMove => GenerateStepMove(sprite, tps, o),
                _ => GenerateScalePulse(sprite, tps, o.RangeStart, o.RangeEnd,
                                        o.Intensity, o.EasingIn, o.EasingOut)
            };

        public List<OsuSpriteLoop> GenerateScalePulse(OsuSprite sprite, List<OsuTimingPoint> tps,
            int start, int end, double intensity = 0.15, int easingIn = 4, int easingOut = 2)
        {
            double s = sprite.Scale;
            return BuildLoops(tps, start, end, 1, dur =>
            {
                int q = (int)Math.Round(dur * 0.25);
                return new List<OsuCommand>
                {
                    new() { Type = CommandType.S, Easing = easingIn, StartTime = 0, EndTime = q,
                            StartValues = new[] { s }, EndValues = new[] { s * (1 + intensity) } },
                    new() { Type = CommandType.S, Easing = easingOut, StartTime = q, EndTime = dur,
                            StartValues = new[] { s * (1 + intensity) }, EndValues = new[] { s } }
                };
            });
        }

        public List<OsuSpriteLoop> GenerateWrapMove(OsuSprite sprite, List<OsuTimingPoint> tps, BeatEffectOptions o)
        {
            bool horiz = o.Axis == WrapAxis.Horizontal;
            double size = horiz ? 854 : 480;
            double a = -o.Margin, b = size + o.Margin;
            if (o.Reverse) (a, b) = (b, a);

            return BuildLoops(tps, o.RangeStart, o.RangeEnd, Math.Max(1, o.BeatsPerCross), dur =>
                new List<OsuCommand>
                {
                    new() { Type = horiz ? CommandType.MX : CommandType.MY, Easing = o.MoveEasing,
                            StartTime = 0, EndTime = dur,
                            StartValues = new[] { a }, EndValues = new[] { b } }
                });
        }

        public List<OsuSpriteLoop> GenerateStepMove(OsuSprite sprite, List<OsuTimingPoint> tps, BeatEffectOptions o)
        {
            bool horiz = o.Axis == WrapAxis.Horizontal;
            double size = horiz ? 854 : 480;
            int jumps = Math.Max(1, o.Jumps);
            int bpj = Math.Max(1, o.BeatsPerJump);
            double pct = Math.Clamp(o.JumpPct, 5, 100);

            double step = (size + 2 * o.Margin) / jumps * (o.Reverse ? -1 : 1);
            double startPos = o.Reverse ? size + o.Margin : -o.Margin;
            var type = horiz ? CommandType.MX : CommandType.MY;

            return BuildLoops(tps, o.RangeStart, o.RangeEnd, jumps * bpj, dur =>
            {
                var cmds = new List<OsuCommand>();
                double perJump = dur / (double)jumps;
                int jumpDur = Math.Max(1, (int)Math.Round(perJump * pct / 100.0));

                for (int j = 0; j < jumps; j++)
                {
                    int t0 = (int)Math.Round(j * perJump);
                    cmds.Add(new OsuCommand
                    {
                        Type = type,
                        Easing = o.JumpEasing,
                        StartTime = t0,
                        EndTime = Math.Min(t0 + jumpDur, dur),
                        StartValues = new[] { startPos + step * j },
                        EndValues = new[] { startPos + step * (j + 1) }
                    });
                }

                // relleno: mantiene la duración del loop igual al recorrido completo
                var last = cmds[^1];
                if (last.EndTime < dur)
                    cmds.Add(new OsuCommand
                    {
                        Type = type,
                        Easing = 0,
                        StartTime = last.EndTime,
                        EndTime = dur,
                        StartValues = new[] { last.EndValues[0] },
                        EndValues = new[] { last.EndValues[0] }
                    });
                return cmds;
            });
        }

        // Recorre cada línea roja compás por compás (re-anclado, sin deriva)
        private List<OsuSpriteLoop> BuildLoops(List<OsuTimingPoint> tps, int start, int end,
            int beatsPerIter, Func<int, List<OsuCommand>> make)
        {
            var result = new List<OsuSpriteLoop>();
            var reds = tps.Where(t => t.Uninherited && t.BeatLength > 0).OrderBy(t => t.Time).ToList();

            for (int i = 0; i < reds.Count; i++)
            {
                var line = reds[i];
                double limit = i + 1 < reds.Count ? Math.Min(reds[i + 1].Time, end) : end;
                int meter = Math.Max(1, line.Meter);
                int itersPerLoop = Math.Max(1, meter / beatsPerIter);
                double groupLen = itersPerLoop * beatsPerIter * line.BeatLength;
                int iterDur = (int)Math.Round(beatsPerIter * line.BeatLength);

                for (int g = 0; ; g++)
                {
                    double gStart = line.Time + g * groupLen;
                    if (gStart + groupLen > limit + 1) break;
                    if (gStart < start - 1) continue;

                    result.Add(new OsuSpriteLoop
                    {
                        StartTime = (int)Math.Round(gStart),
                        LoopCount = itersPerLoop,
                        Commands = make(iterDur)
                    });
                }
            }
            return result;
        }
    }
}