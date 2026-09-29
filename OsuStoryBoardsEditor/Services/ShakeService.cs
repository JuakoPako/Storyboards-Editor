using OsuStoryBoardsEditor.Models;

namespace OsuStoryBoardsEditor
{
    public enum ShakeMode { Smooth, Harsh }

    public class ShakeOptions
    {
        public int RangeStart { get; set; }
        public int RangeEnd { get; set; }
        public ShakeMode Mode { get; set; } = ShakeMode.Smooth;

        public double AmpX { get; set; } = 6;          // px
        public double AmpY { get; set; } = 6;          // px
        public double Frequency { get; set; } = 5;     // sacudidas por segundo
        public bool Rotate { get; set; } = false;
        public double RotDeg { get; set; } = 1.5;      // grados
        public double EndPercent { get; set; } = 100;  // intensidad al final: 100 = constante, 0 = se apaga
        public int Seed { get; set; } = 1;             // misma semilla = mismo temblor
    }

    // Temblor continuo por rango de tiempo (no depende del ritmo).
    // Se guarda como UN loop de 1 iteración: sus comandos M (y R) son relativos al inicio del rango.
    public class ShakeService
    {
        public const int MaxCommands = 6000;

        private static int StepsFor(ShakeOptions o)
        {
            int len = o.RangeEnd - o.RangeStart;
            if (len < 20) return 0;
            double interval = 1000.0 / Math.Clamp(o.Frequency, 0.5, 60);
            return Math.Max(2, (int)Math.Round(len / interval));
        }

        public int EstimateCommands(ShakeOptions o) => StepsFor(o) * (o.Rotate ? 2 : 1);

        public OsuSpriteLoop? Generate(double baseX, double baseY, double baseRot, ShakeOptions o)
        {
            int steps = StepsFor(o);
            if (steps == 0) return null;

            int len = o.RangeEnd - o.RangeStart;
            var rng = new Random(o.Seed);
            double rotAmp = o.RotDeg * Math.PI / 180.0;   // radianes, como el resto del editor
            double end = Math.Clamp(o.EndPercent, 0, 100) / 100.0;

            // Desplazamientos por paso. El primero y el último quedan en la base:
            // no hay tirón al empezar y el sprite termina exactamente donde estaba.
            var dx = new double[steps + 1];
            var dy = new double[steps + 1];
            var dr = new double[steps + 1];
            double sx = 1, sy = 1, sr = 1;
            bool smooth = o.Mode == ShakeMode.Smooth;
            for (int k = 1; k < steps; k++)
            {
                double f = 1 + (end - 1) * k / steps;
                dx[k] = NextOffset(rng, ref sx, o.AmpX * f, smooth);
                dy[k] = NextOffset(rng, ref sy, o.AmpY * f, smooth);
                dr[k] = o.Rotate ? NextOffset(rng, ref sr, rotAmp * f, smooth) : 0;
            }

            var loop = new OsuSpriteLoop { StartTime = o.RangeStart, LoopCount = 1 };
            for (int k = 0; k < steps; k++)
            {
                int t0 = (int)Math.Round((double)k * len / steps);
                int t1 = (int)Math.Round((double)(k + 1) * len / steps);
                if (t1 <= t0) t1 = t0 + 1;

                int b = t1;
                int easing = 17;                           // Sine In/Out: vaivén suave
                if (o.Mode == ShakeMode.Harsh)
                {
                    // golpe seco: salta rápido y se queda quieto hasta el próximo
                    b = Math.Min(t1, t0 + Math.Clamp((int)Math.Round((t1 - t0) * 0.25), 6, 40));
                    easing = 4;                            // Quad Out
                }

                loop.Commands.Add(new OsuCommand
                {
                    Type = CommandType.M, Easing = easing, StartTime = t0, EndTime = b,
                    StartValues = new[] { baseX + dx[k], baseY + dy[k] },
                    EndValues = new[] { baseX + dx[k + 1], baseY + dy[k + 1] }
                });

                if (o.Rotate)
                    loop.Commands.Add(new OsuCommand
                    {
                        Type = CommandType.R, Easing = easing, StartTime = t0, EndTime = b,
                        StartValues = new[] { baseRot + dr[k] },
                        EndValues = new[] { baseRot + dr[k + 1] }
                    });
            }
            return loop;
        }

        // Casi siempre cambia de lado (va y viene); a veces repite lado para que no se vea mecánico.
        private static double NextOffset(Random rng, ref double sign, double amp, bool smooth)
        {
            if (smooth)
            {
                // balanceo: siempre cambia de lado y el recorrido casi no varía
                sign = -sign;
                return sign * (0.75 + 0.25 * rng.NextDouble()) * amp;
            }

            // brusco: casi siempre cambia de lado, a veces repite
            if (rng.NextDouble() < 0.75) sign = -sign;
            return sign * (0.4 + 0.6 * rng.NextDouble()) * amp;
        }
    }
}
