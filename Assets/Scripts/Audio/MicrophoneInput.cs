using System.Collections;
using UnityEngine;

namespace OmpaLompa.Audio
{
    /// <summary>
    /// Captures the microphone and exposes, every frame:
    ///  - Rms           : loudness over the last ~20-50 ms (linear, 0..1)
    ///  - LowFreqLevel  : RMS-like amplitude of the low band only (rumble of a blow)
    ///  - LowFreqRatio  : share of the signal power that is in the low band (0..1)
    /// The microphone is never played back, so there is no feedback and no extra latency.
    /// Runs before other scripts so consumers always read this frame's values.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class MicrophoneInput : MonoBehaviour
    {
        [Header("Device")]
        [Tooltip("Name of the microphone to use. Pick it from the dropdown in the Inspector. " +
                 "Empty or unknown = first available device.")]
        [SerializeField] string deviceName = "";

        [Header("Capture")]
        [Tooltip("Requested sample rate. Clamped to what the device supports.")]
        [SerializeField] int requestedSampleRate = 44100;
        [Tooltip("Length of the looping capture buffer in seconds. Does not add latency: " +
                 "we always read the newest samples. Keep small.")]
        [SerializeField, Range(1, 5)] int bufferSeconds = 1;
        [Tooltip("Linear gain applied to the samples before analysis. Use it to compensate a quiet mic.")]
        [SerializeField, Min(0f)] float inputGain = 1f;
        [Tooltip("Seconds between attempts to (re)open the microphone if it fails or disappears.")]
        [SerializeField, Min(0.5f)] float retryInterval = 2f;

        [Header("Analysis")]
        [Tooltip("Length of the RMS window in milliseconds (20-50 recommended).")]
        [SerializeField, Range(10f, 100f)] float rmsWindowMs = 30f;
        [Tooltip("FFT size (power of two). 2048 @ 44.1 kHz gives ~21 Hz per bin.")]
        [SerializeField] int fftSize = 2048;
        [Tooltip("Lowest frequency counted (ignores DC offset and sub-sonic hum).")]
        [SerializeField, Min(1f)] float lowBandMinHz = 20f;
        [Tooltip("Upper edge of the 'low' band. Blowing into a mic is mostly noise under this limit.")]
        [SerializeField, Min(50f)] float lowBandMaxHz = 250f;

        // ---- Public read-only data -------------------------------------------------------

        /// <summary>RMS loudness of the latest window (linear, 0..1).</summary>
        public float Rms { get; private set; }
        /// <summary>RMS loudness in dBFS (about -80 when silent, 0 at full scale).</summary>
        public float RmsDb { get; private set; } = -80f;
        /// <summary>RMS-like amplitude of the low band.</summary>
        public float LowFreqLevel { get; private set; }
        /// <summary>Fraction (0..1) of signal power located in the low band.</summary>
        public float LowFreqRatio { get; private set; }
        /// <summary>True once the microphone is open and delivering enough samples.</summary>
        public bool IsCapturing { get; private set; }
        public string ActiveDevice { get; private set; } = "";
        public int SampleRate { get; private set; }
        public float RmsWindowMs => rmsWindowMs;
        public string DeviceName { get => deviceName; set => deviceName = value; }

        AudioClip clip;
        float[] ring;           // whole looping clip, copied once per frame
        float[] block;          // newest fftSize mono samples
        AudioBandAnalyzer analyzer;
        int channels = 1;
        int lastPos;
        long samplesCaptured;
        float nextRetryTime;
        bool permissionGranted;

        IEnumerator Start()
        {
            // Required on macOS/iOS/Android/WebGL; returns immediately where no prompt exists.
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

            permissionGranted = Application.HasUserAuthorization(UserAuthorization.Microphone);
            if (!permissionGranted)
                Debug.LogWarning("[MicrophoneInput] Microphone permission denied.");
            else
                OpenMicrophone();
        }

        void OnDisable() => CloseMicrophone();

        void OnValidate()
        {
            // fftSize must be a power of two.
            fftSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(256, fftSize)), 256, 16384);
        }

        void Update()
        {
            if (!permissionGranted) return;

            if (clip == null)
            {
                if (Time.unscaledTime >= nextRetryTime) OpenMicrophone();
                return;
            }

            // Device unplugged or taken over by another app: close and retry later.
            if (!Microphone.IsRecording(ActiveDevice))
            {
                Debug.LogWarning($"[MicrophoneInput] '{ActiveDevice}' stopped recording, retrying...");
                CloseMicrophone();
                return;
            }

            int pos = Microphone.GetPosition(ActiveDevice);
            if (pos <= 0 && samplesCaptured == 0) return; // device still warming up

            int length = clip.samples;
            int delta = pos - lastPos;
            if (delta < 0) delta += length; // the ring buffer wrapped around
            samplesCaptured += delta;
            lastPos = pos;

            if (samplesCaptured < fftSize) return; // not enough audio yet for a full block

            clip.GetData(ring, 0);
            ExtractNewestBlock(pos, length);
            Analyze();
            IsCapturing = true;
        }

        // ---- Microphone lifecycle --------------------------------------------------------

        void OpenMicrophone()
        {
            nextRetryTime = Time.unscaledTime + retryInterval;

            string[] devices = Microphone.devices;
            if (devices.Length == 0)
            {
                Debug.LogWarning("[MicrophoneInput] No microphone found.");
                return;
            }

            string chosen = System.Array.Exists(devices, d => d == deviceName) ? deviceName : devices[0];
            if (!string.IsNullOrEmpty(deviceName) && chosen != deviceName)
                Debug.LogWarning($"[MicrophoneInput] Device '{deviceName}' not found, using '{chosen}'.");

            Microphone.GetDeviceCaps(chosen, out int minFreq, out int maxFreq);
            // min = max = 0 means "any rate is supported".
            int rate = (minFreq == 0 && maxFreq == 0) ? requestedSampleRate
                                                      : Mathf.Clamp(requestedSampleRate, minFreq, maxFreq);

            clip = Microphone.Start(chosen, true, bufferSeconds, rate);
            if (clip == null)
            {
                Debug.LogWarning($"[MicrophoneInput] Could not start '{chosen}'.");
                return;
            }

            ActiveDevice = chosen;
            SampleRate = clip.frequency;
            channels = Mathf.Max(1, clip.channels);
            ring = new float[clip.samples * channels];
            block = new float[fftSize];
            analyzer = new AudioBandAnalyzer(fftSize);
            lastPos = 0;
            samplesCaptured = 0;
            Debug.Log($"[MicrophoneInput] Started '{chosen}' @ {SampleRate} Hz, {channels} ch.");
        }

        void CloseMicrophone()
        {
            if (!string.IsNullOrEmpty(ActiveDevice) && Microphone.IsRecording(ActiveDevice))
                Microphone.End(ActiveDevice);
            clip = null;
            IsCapturing = false;
            Rms = LowFreqLevel = LowFreqRatio = 0f;
            RmsDb = -80f;
            nextRetryTime = Time.unscaledTime + retryInterval;
        }

        // ---- Analysis --------------------------------------------------------------------

        /// <summary>Copies the newest fftSize samples (first channel only) ending at 'pos' into 'block'.</summary>
        void ExtractNewestBlock(int pos, int length)
        {
            int start = pos - fftSize;
            for (int i = 0; i < fftSize; i++)
            {
                int idx = (start + i) % length;
                if (idx < 0) idx += length;
                block[i] = ring[idx * channels] * inputGain;
            }
        }

        void Analyze()
        {
            // RMS over the newest window only: short window = fast reaction to a shout.
            int windowSamples = Mathf.Clamp(Mathf.RoundToInt(SampleRate * rmsWindowMs / 1000f), 16, fftSize);
            double sum = 0;
            for (int i = fftSize - windowSamples; i < fftSize; i++) sum += block[i] * block[i];
            Rms = Mathf.Sqrt((float)(sum / windowSamples));
            RmsDb = Rms > 1e-4f ? 20f * Mathf.Log10(Rms) : -80f;

            // Spectrum uses the whole (longer) block for frequency resolution.
            analyzer.Analyze(block, SampleRate, lowBandMinHz, lowBandMaxHz, out float low, out float total);
            LowFreqLevel = Mathf.Sqrt(low);
            LowFreqRatio = total > 1e-12f ? Mathf.Clamp01(low / total) : 0f;
        }
    }
}
