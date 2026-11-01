using System;
using UnityEngine;
using UnityEngine.Events;

namespace OmpaLompa.Audio
{
    /// <summary>What the detector currently "hears". Used by the debug UI and by other scripts.</summary>
    public enum VoiceState { Quiet, Shout, Blow }

    /// <summary>
    /// Turns the raw numbers from <see cref="MicrophoneInput"/> into two game events:
    ///  - OnShout : loud and short sound that is NOT dominated by low frequencies.
    ///  - OnBlow  : sound above the blow threshold, dominated by low frequencies, lasting longer than ~1 s.
    /// Thresholds are the max of a manual minimum and (background noise x multiplier), where the
    /// background noise is measured automatically for a couple of seconds at startup.
    /// Hook up the UnityEvents in the Inspector, or subscribe to the C# events from code.
    /// </summary>
    [DisallowMultipleComponent]
    public class VoiceEventDetector : MonoBehaviour
    {
        [SerializeField] MicrophoneInput microphone;

        [Header("Events")]
        public UnityEvent OnShout;
        public UnityEvent OnBlow;
        /// <summary>Raised when a sustained blow stops (useful to stop the wind).</summary>
        public UnityEvent OnBlowEnd;

        /// <summary>C# equivalents of the UnityEvents.</summary>
        public event Action ShoutDetected;
        public event Action BlowDetected;
        public event Action BlowEnded;

        [Header("Calibration")]
        [SerializeField] bool calibrateOnStart = true;
        [Tooltip("Seconds of background noise (music, nature sounds, room) measured at startup. Stay quiet!")]
        [SerializeField, Min(0.5f)] float calibrationSeconds = 2f;
        [Tooltip("Seconds ignored at the very beginning (mic start-up clicks).")]
        [SerializeField, Min(0f)] float calibrationSettleSeconds = 0.3f;

        [Header("Shout (loud + short)")]
        [Tooltip("Minimum RMS for a shout, even in a silent room.")]
        [SerializeField, Range(0.001f, 1f)] float shoutMinRms = 0.12f;
        [Tooltip("Shout threshold is also at least this many times the measured background noise.")]
        [SerializeField, Min(1f)] float shoutNoiseMultiplier = 4f;
        [Tooltip("The sound must stay above the threshold at least this long (rejects clicks and knocks).")]
        [SerializeField, Min(0.01f)] float shoutMinDuration = 0.06f;
        [Tooltip("Longer than this and it is no longer a 'short' shout (only used when Fire On Onset is off).")]
        [SerializeField, Min(0.1f)] float shoutMaxDuration = 0.5f;
        [Tooltip("ON: fire as soon as Min Duration has passed (lowest latency, a long scream still counts). " +
                 "OFF: fire when the sound ends, only if it lasted between Min and Max Duration.")]
        [SerializeField] bool fireShoutOnOnset = true;
        [Tooltip("A shout ends when the level drops below threshold x this ratio (hysteresis).")]
        [SerializeField, Range(0.3f, 1f)] float shoutReleaseRatio = 0.7f;

        [Header("Blow (continuous + low frequencies)")]
        [Tooltip("Minimum RMS for a blow, even in a silent room.")]
        [SerializeField, Range(0.001f, 1f)] float blowMinRms = 0.04f;
        [Tooltip("Blow threshold is also at least this many times the measured background noise.")]
        [SerializeField, Min(1f)] float blowNoiseMultiplier = 2.5f;
        [Tooltip("Share of the power under the low-band limit needed to call it a blow. " +
                 "Voice is typically 0.2-0.6, a blow 0.8+.")]
        [SerializeField, Range(0f, 1f)] float lowFreqRatioThreshold = 0.7f;
        [Tooltip("How long the blow must last before OnBlow fires.")]
        [SerializeField, Min(0.1f)] float blowMinDuration = 1f;
        [Tooltip("A blow may dip below the conditions for this long without being cancelled.")]
        [SerializeField, Min(0f)] float blowDropoutGrace = 0.2f;

        [Header("Cooldown")]
        [Tooltip("Minimum seconds between two events (of any kind).")]
        [SerializeField, Min(0f)] float cooldownSeconds = 0.75f;

        [Header("Creature sounds (avoid false triggers)")]
        [Tooltip("Sources playing project sounds (creatures, effects). While one plays, thresholds are raised.")]
        [SerializeField] AudioSource[] suppressWhilePlaying;
        [Tooltip("Thresholds are multiplied by this while a creature sound is playing.")]
        [SerializeField, Min(1f)] float playbackThresholdMultiplier = 2f;
        [Tooltip("Keep the raised thresholds this long after the sound ends (room echo).")]
        [SerializeField, Min(0f)] float playbackTailSeconds = 0.2f;

        [Header("Display")]
        [Tooltip("How long the state stays on SCREAM after a shout (for UI / debugging).")]
        [SerializeField, Min(0f)] float shoutDisplaySeconds = 0.6f;

        // ---- Public read-only data -------------------------------------------------------

        public VoiceState State { get; private set; } = VoiceState.Quiet;
        public bool IsCalibrating { get; private set; }
        public float CalibrationProgress { get; private set; }
        /// <summary>Background noise RMS measured during calibration.</summary>
        public float NoiseFloor { get; private set; }
        public bool IsBlowing { get; private set; }
        public bool IsSuppressed => suppressed;
        public MicrophoneInput MicInput => microphone;

        /// <summary>Current effective thresholds (include noise floor and playback suppression).</summary>
        public float ShoutThreshold { get; private set; }
        public float BlowThreshold { get; private set; }
        public float LowFreqRatioThreshold => lowFreqRatioThreshold;

        // ---- Internal state --------------------------------------------------------------

        // Calibration accumulators.
        bool calibStarted;
        float calibStartTime;
        double calibSum, calibSumSq;
        int calibCount;

        // Shout segment tracking.
        bool shoutActive;
        bool shoutHandled;
        float shoutStart, shoutLastLoud;
        float shoutLowSum;
        int shoutSamples;

        // Blow tracking.
        float smoothLowRatio;
        bool blowCandidate;
        float blowStart, blowLastOk;

        float lastEventTime = -999f;
        float stateHoldUntil;
        float suppressUntil;
        bool suppressed;
        bool wasCapturing;

        void Reset() => microphone = GetComponent<MicrophoneInput>();

        void Awake()
        {
            if (microphone == null) microphone = GetComponent<MicrophoneInput>();
            if (microphone == null) microphone = FindFirstObjectByType<MicrophoneInput>();
            NoiseFloor = 0f;
            RefreshThresholds();
        }

        void Update()
        {
            if (microphone == null || !microphone.IsCapturing)
            {
                wasCapturing = false;
                return;
            }

            // Start calibration the first time the mic delivers data.
            if (!wasCapturing)
            {
                wasCapturing = true;
                if (calibrateOnStart && !calibStarted) Recalibrate();
            }

            Process(microphone.Rms, microphone.LowFreqRatio, Time.unscaledDeltaTime, Time.unscaledTime);
        }

        // ---- Public API ------------------------------------------------------------------

        /// <summary>Re-measures the background noise. Keep the room "normal" (music on) and quiet for the duration.</summary>
        public void Recalibrate()
        {
            IsCalibrating = true;
            calibStarted = false; // start time is latched on the next Process call
            calibSum = calibSumSq = 0;
            calibCount = 0;
            CalibrationProgress = 0f;
            CancelActivity();
        }

        /// <summary>
        /// Call when a project sound (creature voice, effect) starts and is not covered by
        /// 'Suppress While Playing', to raise the thresholds for 'seconds'.
        /// </summary>
        public void SuppressFor(float seconds)
        {
            suppressUntil = Mathf.Max(suppressUntil, Time.unscaledTime + seconds);
        }

        /// <summary>
        /// Feeds one measurement into the state machine. Normally called from Update;
        /// public so the logic can be driven with synthetic values (tests, simulations).
        /// </summary>
        public void Process(float rms, float lowRatio, float dt, float now)
        {
            if (IsCalibrating)
            {
                UpdateCalibration(rms, now);
                return;
            }

            UpdateSuppression(now);
            RefreshThresholds();

            // Smooth the ratio so single odd frames don't flip the blow/shout decision.
            float alpha = 1f - Mathf.Exp(-dt / 0.08f);
            smoothLowRatio += (lowRatio - smoothLowRatio) * alpha;

            UpdateBlow(rms, now);
            UpdateShout(rms, lowRatio, now);
            UpdateState(now);
        }

        // ---- Calibration -----------------------------------------------------------------

        void UpdateCalibration(float rms, float now)
        {
            if (!calibStarted)
            {
                calibStarted = true;
                calibStartTime = now;
            }

            float elapsed = now - calibStartTime;
            CalibrationProgress = Mathf.Clamp01(elapsed / (calibrationSettleSeconds + calibrationSeconds));

            if (elapsed >= calibrationSettleSeconds)
            {
                calibSum += rms;
                calibSumSq += (double)rms * rms;
                calibCount++;
            }

            if (elapsed >= calibrationSettleSeconds + calibrationSeconds)
            {
                // mean + 2 sigma covers music dynamics without chasing single peaks.
                if (calibCount > 0)
                {
                    double mean = calibSum / calibCount;
                    double var = Math.Max(0, calibSumSq / calibCount - mean * mean);
                    NoiseFloor = (float)(mean + 2.0 * Math.Sqrt(var));
                }
                IsCalibrating = false;
                CalibrationProgress = 1f;
                RefreshThresholds();
                Debug.Log($"[VoiceEventDetector] Calibrated: noise floor {NoiseFloor:F4} -> " +
                          $"shout threshold {ShoutThreshold:F3}, blow threshold {BlowThreshold:F3}");
            }
        }

        void UpdateSuppression(float now)
        {
            bool playing = false;
            if (suppressWhilePlaying != null)
            {
                foreach (var src in suppressWhilePlaying)
                    if (src != null && src.isPlaying) { playing = true; break; }
            }
            if (playing) suppressUntil = Mathf.Max(suppressUntil, now + playbackTailSeconds);
            suppressed = now < suppressUntil;
        }

        void RefreshThresholds()
        {
            float boost = suppressed ? playbackThresholdMultiplier : 1f;
            ShoutThreshold = Mathf.Max(shoutMinRms, NoiseFloor * shoutNoiseMultiplier) * boost;
            BlowThreshold = Mathf.Max(blowMinRms, NoiseFloor * blowNoiseMultiplier) * boost;
        }

        // ---- Blow ------------------------------------------------------------------------

        void UpdateBlow(float rms, float now)
        {
            bool blowLike = rms >= BlowThreshold && smoothLowRatio >= lowFreqRatioThreshold;

            if (blowLike)
            {
                blowLastOk = now;
                if (!blowCandidate)
                {
                    blowCandidate = true;
                    blowStart = now;
                }
            }
            else if (blowCandidate && now - blowLastOk > blowDropoutGrace)
            {
                blowCandidate = false;
                if (IsBlowing) EndBlow();
            }

            if (blowCandidate && !IsBlowing && now - blowStart >= blowMinDuration && CooldownReady(now))
            {
                IsBlowing = true;
                lastEventTime = now;
                OnBlow?.Invoke();
                BlowDetected?.Invoke();
            }
        }

        void EndBlow()
        {
            IsBlowing = false;
            OnBlowEnd?.Invoke();
            BlowEnded?.Invoke();
        }

        // ---- Shout -----------------------------------------------------------------------

        void UpdateShout(float rms, float lowRatio, float now)
        {
            if (!shoutActive)
            {
                if (rms >= ShoutThreshold)
                {
                    shoutActive = true;
                    shoutHandled = false;
                    shoutStart = shoutLastLoud = now;
                    shoutLowSum = 0f;
                    shoutSamples = 0;
                }
                else return;
            }

            shoutLowSum += lowRatio;
            shoutSamples++;
            if (rms >= ShoutThreshold * shoutReleaseRatio) shoutLastLoud = now;

            float duration = shoutLastLoud - shoutStart;
            bool ended = now - shoutLastLoud > 0.08f; // short grace so one quiet frame doesn't split a shout
            float meanLow = shoutLowSum / Mathf.Max(1, shoutSamples);
            bool voiceLike = meanLow < lowFreqRatioThreshold && !IsBlowing;

            if (!shoutHandled)
            {
                if (fireShoutOnOnset)
                {
                    if (duration >= shoutMinDuration)
                    {
                        shoutHandled = true;
                        if (voiceLike) TryFireShout(now);
                    }
                }
                else if (ended)
                {
                    shoutHandled = true;
                    if (voiceLike && duration >= shoutMinDuration && duration <= shoutMaxDuration)
                        TryFireShout(now);
                }
            }

            if (ended) shoutActive = false;
        }

        void TryFireShout(float now)
        {
            if (!CooldownReady(now)) return;
            lastEventTime = now;
            stateHoldUntil = now + shoutDisplaySeconds;
            State = VoiceState.Shout;
            OnShout?.Invoke();
            ShoutDetected?.Invoke();
        }

        // ---- Helpers ---------------------------------------------------------------------

        bool CooldownReady(float now) => now - lastEventTime >= cooldownSeconds;

        void UpdateState(float now)
        {
            if (IsBlowing) State = VoiceState.Blow;
            else if (State == VoiceState.Blow || (State == VoiceState.Shout && now >= stateHoldUntil))
                State = VoiceState.Quiet;
        }

        void CancelActivity()
        {
            shoutActive = false;
            blowCandidate = false;
            if (IsBlowing) EndBlow();
            State = VoiceState.Quiet;
        }
    }
}
