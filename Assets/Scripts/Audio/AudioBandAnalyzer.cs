using System;
using UnityEngine;

namespace OmpaLompa.Audio
{
    /// <summary>
    /// Small allocation-free FFT helper. Takes a block of mono samples and returns how much
    /// signal power lies in a "low" band (e.g. 20-250 Hz) compared to the whole spectrum.
    /// We do our own FFT because the microphone is never routed through an AudioSource
    /// (which would add latency and risk feedback), so AudioSource.GetSpectrumData is not usable.
    /// </summary>
    public sealed class AudioBandAnalyzer
    {
        readonly int size;
        readonly float[] window;
        readonly float[] re;
        readonly float[] im;
        readonly float[] cosTable;
        readonly float[] sinTable;
        readonly int[] bitReverse;
        readonly float windowPowerGain; // mean(w^2), used to normalise power

        public int Size => size;

        /// <param name="fftSize">Power of two (e.g. 1024, 2048).</param>
        public AudioBandAnalyzer(int fftSize)
        {
            if (fftSize < 8 || (fftSize & (fftSize - 1)) != 0)
                throw new ArgumentException("fftSize must be a power of two >= 8", nameof(fftSize));

            size = fftSize;
            window = new float[size];
            re = new float[size];
            im = new float[size];
            cosTable = new float[size / 2];
            sinTable = new float[size / 2];
            bitReverse = new int[size];

            // Hann window reduces spectral leakage so loud mid frequencies don't smear into the low band.
            double powerSum = 0;
            for (int i = 0; i < size; i++)
            {
                window[i] = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * i / (size - 1));
                powerSum += window[i] * window[i];
            }
            windowPowerGain = (float)(powerSum / size);

            for (int i = 0; i < size / 2; i++)
            {
                float a = -2f * Mathf.PI * i / size;
                cosTable[i] = Mathf.Cos(a);
                sinTable[i] = Mathf.Sin(a);
            }

            int bits = 0;
            while ((1 << bits) < size) bits++;
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                    if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                bitReverse[i] = r;
            }
        }

        /// <summary>
        /// Computes power (mean-square units, so sqrt gives an RMS-like amplitude) in the low band
        /// and in the whole audible spectrum above <paramref name="lowMinHz"/>.
        /// </summary>
        /// <param name="samples">Exactly <see cref="Size"/> mono samples.</param>
        public void Analyze(float[] samples, int sampleRate, float lowMinHz, float lowMaxHz,
                            out float lowPower, out float totalPower)
        {
            for (int i = 0; i < size; i++)
            {
                re[bitReverse[i]] = samples[i] * window[i];
                im[bitReverse[i]] = 0f;
            }

            // Iterative radix-2 Cooley-Tukey FFT.
            for (int len = 2; len <= size; len <<= 1)
            {
                int half = len >> 1;
                int step = size / len;
                for (int start = 0; start < size; start += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = cosTable[k * step];
                        float wi = sinTable[k * step];
                        int a = start + k;
                        int b = a + half;
                        float tr = re[b] * wr - im[b] * wi;
                        float ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }

            float binHz = (float)sampleRate / size;
            // One-sided spectrum scaling so that sum(power) ~= mean(x^2) (Parseval).
            float scale = 2f / ((float)size * size * windowPowerGain);

            lowPower = 0f;
            totalPower = 0f;
            int firstBin = Mathf.Max(1, Mathf.CeilToInt(lowMinHz / binHz)); // skip DC / sub-sonic rumble
            for (int k = firstBin; k < size / 2; k++)
            {
                float p = (re[k] * re[k] + im[k] * im[k]) * scale;
                totalPower += p;
                if (k * binHz <= lowMaxHz) lowPower += p;
            }
        }
    }
}
