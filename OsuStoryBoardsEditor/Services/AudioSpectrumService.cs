using NAudio.Dsp;
using NAudio.Vorbis;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OsuStoryBoardsEditor.Services
{
    // Resultado del análisis: Frames[i][b] = energía 0..1 de la banda b en el instante TimeMs(i)
    public sealed class SpectrumData
    {
        public int StartMs { get; init; }
        public int FrameMs { get; init; }
        public int BandCount { get; init; }
        public float[][] Frames { get; init; } = Array.Empty<float[]>();

        public int TimeMs(int frame) => StartMs + frame * FrameMs;
    }

    public class AudioSpectrumService
    {
        private const int FftLog2 = 11;              // 2^11 = 2048 muestras por ventana
        private const int FftSize = 1 << FftLog2;

        // Analiza [startMs, endMs] del audio y devuelve `bands` bandas en escala logarítmica.
        // Es síncrono y pesado: llamalo desde Task.Run para no congelar la UI.
        public SpectrumData Analyze(string audioPath, int startMs, int endMs, int bands = 32,
                                    int frameMs = 50, double minHz = 40, double maxHz = 16000)
        {
            if (!File.Exists(audioPath)) throw new FileNotFoundException("No existe el audio.", audioPath);
            if (endMs <= startMs) throw new ArgumentException("Rango de tiempo inválido.");
            bands = Math.Clamp(bands, 2, 256);
            frameMs = Math.Clamp(frameMs, 10, 500);

            // 1) Audio a mono, solo el tramo pedido (+ media ventana a cada lado para los bordes)
            var (mono, sampleRate) = ReadMono(audioPath, startMs, endMs);
            maxHz = Math.Min(maxHz, sampleRate / 2.0 - 1);

            // 2) Bordes de banda en escala logarítmica -> rango de bins de la FFT
            double binHz = sampleRate / (double)FftSize;
            var lo = new int[bands];
            var hi = new int[bands];
            for (int b = 0; b < bands; b++)
            {
                double f0 = minHz * Math.Pow(maxHz / minHz, b / (double)bands);
                double f1 = minHz * Math.Pow(maxHz / minHz, (b + 1) / (double)bands);
                lo[b] = Math.Max(1, (int)Math.Floor(f0 / binHz));
                hi[b] = Math.Max(lo[b], (int)Math.Ceiling(f1 / binHz) - 1);   // al menos 1 bin por banda
                hi[b] = Math.Min(hi[b], FftSize / 2 - 1);
            }

            // 3) Ventana de Hann y buffers reutilizables
            var window = new float[FftSize];
            double winSum = 0;
            for (int i = 0; i < FftSize; i++)
            {
                window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                winSum += window[i];
            }
            var fft = new Complex[FftSize];

            // 4) Un frame de espectro cada frameMs
            int frameCount = Math.Max(1, (endMs - startMs) / frameMs + 1);
            var frames = new float[frameCount][];
            int padSamples = FftSize / 2;   // mono[0] corresponde a (startMs - media ventana)

            for (int f = 0; f < frameCount; f++)
            {
                int center = padSamples + (int)((long)f * frameMs * sampleRate / 1000);
                int from = center - FftSize / 2;

                for (int i = 0; i < FftSize; i++)
                {
                    int idx = from + i;
                    float v = (idx >= 0 && idx < mono.Length) ? mono[idx] : 0f;
                    fft[i].X = v * window[i];
                    fft[i].Y = 0;
                }
                FastFourierTransform.FFT(true, FftLog2, fft);

                var row = new float[bands];
                for (int b = 0; b < bands; b++)
                {
                    // máximo de los bins de la banda (más "vivo" que el promedio en bandas anchas)
                    float peak = 0;
                    for (int k = lo[b]; k <= hi[b]; k++)
                    {
                        float mag = MathF.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
                        if (mag > peak) peak = mag;
                    }
                    double amp = peak * 2.0 / winSum;           // amplitud ~ 0..1 (senoidal a fondo de escala = 1)
                    double db = 20 * Math.Log10(amp + 1e-9);
                    // Compensación de inclinación: los agudos suenan con menos energía; +3 dB por octava
                    double octaves = Math.Log2(Math.Max(1.0, (lo[b] + hi[b]) / 2.0 * binHz) / minHz);
                    row[b] = (float)(db + 3.0 * octaves);       // todavía en dB, se normaliza abajo
                }
                frames[f] = row;
            }

            // 5) dB -> 0..1: el techo es el percentil 99 de todo el tramo (un pico suelto no aplasta al resto)
            //    y el piso está 55 dB más abajo.
            var all = new List<float>(frameCount * bands);
            foreach (var row in frames) all.AddRange(row);
            all.Sort();
            float ceil = all[Math.Min(all.Count - 1, (int)(all.Count * 0.99))];
            const float range = 55f;
            foreach (var row in frames)
                for (int b = 0; b < bands; b++)
                    row[b] = Math.Clamp((row[b] - (ceil - range)) / range, 0f, 1f);

            return new SpectrumData { StartMs = startMs, FrameMs = frameMs, BandCount = bands, Frames = frames };
        }

        // Suavizado tipo "barra de ecualizador": sube rápido y baja lento (attack/release en 0..1;
        // más cerca de 1 = más rápido). Devuelve una copia, no toca el original.
        public static SpectrumData Smooth(SpectrumData src, float attack = 0.8f, float release = 0.25f)
        {
            var frames = new float[src.Frames.Length][];
            var prev = new float[src.BandCount];
            for (int f = 0; f < frames.Length; f++)
            {
                var row = new float[src.BandCount];
                for (int b = 0; b < src.BandCount; b++)
                {
                    float target = src.Frames[f][b];
                    float k = target > prev[b] ? attack : release;
                    row[b] = prev[b] + (target - prev[b]) * k;
                }
                frames[f] = row;
                prev = row;
            }
            return new SpectrumData { StartMs = src.StartMs, FrameMs = src.FrameMs, BandCount = src.BandCount, Frames = frames };
        }

        // ── Lectura de audio (mp3/wav vía NAudio, ogg vía NAudio.Vorbis) ──
        private static (float[] mono, int sampleRate) ReadMono(string path, int startMs, int endMs)
        {
            using var reader = OpenReader(path);
            int sr = reader.WaveFormat.SampleRate;
            int ch = reader.WaveFormat.Channels;

            // arrancamos media ventana antes para que el primer frame tenga contexto
            long skipFrames = Math.Max(0, (long)(startMs / 1000.0 * sr) - FftSize / 2);
            long padFront = Math.Max(0, FftSize / 2 - (long)(startMs / 1000.0 * sr));   // si startMs≈0, faltó audio: ceros
            long wantFrames = (long)((endMs - startMs) / 1000.0 * sr) + FftSize;

            var mono = new List<float>((int)Math.Min(wantFrames + padFront, int.MaxValue / 4));
            for (long i = 0; i < padFront; i++) mono.Add(0f);

            var buf = new float[sr * ch];   // ~1 s por lectura
            long framesSkipped = 0;
            long framesTaken = 0;

            while (framesTaken < wantFrames)
            {
                int read = reader.Read(buf, 0, buf.Length);
                if (read <= 0) break;
                int framesRead = read / ch;

                for (int i = 0; i < framesRead; i++)
                {
                    if (framesSkipped < skipFrames) { framesSkipped++; continue; }
                    if (framesTaken >= wantFrames) break;
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += buf[i * ch + c];
                    mono.Add(sum / ch);
                    framesTaken++;
                }
            }
            return (mono.ToArray(), sr);
        }

        private static DisposableSampleProvider OpenReader(string path)
        {
            // Se devuelve el envoltorio (y no ISampleProvider) para poder usarlo con `using`
            return Path.GetExtension(path).ToLowerInvariant() == ".ogg"
                ? new DisposableSampleProvider(new VorbisWaveReader(path))
                : new DisposableSampleProvider(new AudioFileReader(path));
        }

        private sealed class DisposableSampleProvider : ISampleProvider, IDisposable
        {
            private readonly ISampleProvider _inner;
            private readonly IDisposable _disposable;
            public DisposableSampleProvider(ISampleProvider inner) { _inner = inner; _disposable = (IDisposable)inner; }
            public WaveFormat WaveFormat => _inner.WaveFormat;
            public int Read(float[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public void Dispose() => _disposable.Dispose();
        }
    }
}