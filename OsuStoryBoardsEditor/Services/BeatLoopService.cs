using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor
{

    public enum WrapAxis { Horizontal, Vertical }

    public enum BeatEffectType { ScalePulse, WrapMove, StepMove, GlowPulse }


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
        public int JumpMs { get; set; } = 80;     // % del intervalo que dura el salto
        public int JumpEasing { get; set; } = 19;    // Expo Out

        public bool ShakePos { get; set; } = false;
        public double ShakePosPx { get; set; } = 3;
        public bool ShakeRot { get; set; } = false;
        public double ShakeRotDeg { get; set; } = 4;
        public int ShakeCount { get; set; } = 3;     // vibraciones
        public int ShakeMs { get; set; } = 150;      // duración total del temblor
    }

    public class BeatLoopService
    {
        public List<OsuSpriteLoop> Generate(OsuSprite sprite, List<OsuTimingPoint> tps, BeatEffectOptions o)

            => o.Type switch

            {
                BeatEffectType.WrapMove => GenerateWrapMove(sprite, tps, o),
                BeatEffectType.StepMove => GenerateStepMove(sprite, tps, o),
                BeatEffectType.GlowPulse => GenerateGlowPulse(sprite, tps, o),
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

        // Pulso de brillo: la opacidad más alta que ya tenga el sprite es el pico del flash,
        // y "Intensidad" es cuánto baja entre beats (0..1).
        public List<OsuSpriteLoop> GenerateGlowPulse(OsuSprite sprite, List<OsuTimingPoint> tps, BeatEffectOptions o)
        {
            double peak = sprite.Commands.Where(c => c.Type == CommandType.F)
                .Select(c => Math.Max(c.StartValues[0], c.EndValues[0]))
                .DefaultIfEmpty(sprite.Opacity).Max();
            double low = peak * (1 - Math.Clamp(o.Intensity, 0, 1));

            return BuildLoops(tps, o.RangeStart, o.RangeEnd, 1, dur =>
            {
                int q = (int)Math.Round(dur * 0.25);
                return new List<OsuCommand>
                {
                    new() { Type = CommandType.F, Easing = o.EasingIn, StartTime = 0, EndTime = q,
                            StartValues = new[] { low }, EndValues = new[] { peak } },
                    new() { Type = CommandType.F, Easing = o.EasingOut, StartTime = q, EndTime = dur,
                            StartValues = new[] { peak }, EndValues = new[] { low } }
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


            double step = (size + 2 * o.Margin) / jumps * (o.Reverse ? -1 : 1);
            double startPos = o.Reverse ? size + o.Margin : -o.Margin;
            var type = horiz ? CommandType.MX : CommandType.MY;

            return BuildLoops(tps, o.RangeStart, o.RangeEnd, jumps * bpj, dur =>
            {
                var cmds = new List<OsuCommand>();
                OsuCommand? lastJump = null;
                double perJump = dur / (double)jumps;
                int jumpDur = Math.Clamp(o.JumpMs, 1, (int)perJump);

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

                    lastJump = cmds[^1];
                    int room = (int)Math.Round((j + 1) * perJump) - lastJump.EndTime;   // espacio hasta el próximo salto
                    if ((o.ShakePos || o.ShakeRot) && room > 10)
                        AddShake(cmds, o, horiz, sprite, lastJump.EndTime, Math.Min(o.ShakeMs, room));
                }

                // relleno: mantiene la duración del loop igual al recorrido completo
                var last = lastJump!;
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

        private static void AddShake(List<OsuCommand> cmds, BeatEffectOptions o, bool horiz,
    OsuSprite sprite, int t0, int len)
        {
            int segs = Math.Min(Math.Max(1, o.ShakeCount) * 2, len);
            if (segs < 2) return;

            void Track(CommandType type, double baseVal, double amp)
            {
                double prev = baseVal;
                for (int k = 0; k < segs; k++)
                {
                    // va y viene alrededor de la base, cada vez más chico; el último tramo vuelve a la base
                    double target = k == segs - 1
                        ? baseVal
                        : baseVal + (k % 2 == 0 ? 1 : -1) * amp * (1.0 - (double)k / segs);

                    int a = t0 + (int)Math.Round(k * len / (double)segs);
                    int b = t0 + (int)Math.Round((k + 1) * len / (double)segs);
                    if (b <= a) b = a + 1;

                    cmds.Add(new OsuCommand
                    {
                        Type = type,
                        Easing = 17,   // Sine In/Out: temblor suave
                        StartTime = a,
                        EndTime = b,
                        StartValues = new[] { prev },
                        EndValues = new[] { target }
                    });
                    prev = target;
                }
            }

            if (o.ShakePos)   // sobre el eje perpendicular al salto
                Track(horiz ? CommandType.MY : CommandType.MX, horiz ? sprite.Y : sprite.X, o.ShakePosPx);
            if (o.ShakeRot)   // en radianes, como el resto del editor
                Track(CommandType.R, sprite.Rotation, o.ShakeRotDeg * Math.PI / 180.0);
        }

        // Recorre cada línea roja desde el primer beat del rango (sin esperar al próximo compás)
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

                // Primer beat de esta línea roja que cae en o después del inicio (1 ms de tolerancia por el redondeo)
                double firstBeat = start <= line.Time
                    ? line.Time
                    : line.Time + Math.Ceiling((start - line.Time - 1) / line.BeatLength) * line.BeatLength;

                for (int g = 0; ; g++)
                {
                    double gStart = firstBeat + g * groupLen;
                    if (gStart + groupLen > limit + 1) break;

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