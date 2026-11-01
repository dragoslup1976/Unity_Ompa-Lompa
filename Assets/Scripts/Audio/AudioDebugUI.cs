using UnityEngine;

namespace OmpaLompa.Audio
{
    /// <summary>
    /// On-screen test panel (IMGUI, no packages needed): volume bar with the shout/blow thresholds,
    /// low-frequency bar, state text (quiet / SCREAM / BLOW) and the raw RMS and low-freq values.
    /// Put it in any scene; it finds the other audio components by itself if not assigned.
    /// </summary>
    public class AudioDebugUI : MonoBehaviour
    {
        [SerializeField] MicrophoneInput microphone;
        [SerializeField] VoiceEventDetector detector;
        [SerializeField] bool visible = true;
        [Tooltip("Lowest dB shown on the volume bar.")]
        [SerializeField, Range(-80f, -20f)] float minDb = -60f;

        GUIStyle label, big;
        Texture2D white;

        void Reset()
        {
            microphone = GetComponent<MicrophoneInput>();
            detector = GetComponent<VoiceEventDetector>();
        }

        void Awake()
        {
            if (microphone == null) microphone = FindFirstObjectByType<MicrophoneInput>();
            if (detector == null) detector = FindFirstObjectByType<VoiceEventDetector>();
            white = Texture2D.whiteTexture;
        }

        void OnGUI()
        {
            if (!visible || microphone == null) return;

            // Scale so the panel looks the same on any resolution.
            float s = Screen.height / 720f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            EnsureStyles();

            const float x = 20, w = 420;
            float y = 20;

            string status = "quiet";
            Color statusColor = new Color(0.7f, 0.7f, 0.7f);
            if (!microphone.IsCapturing)
            {
                status = "NO MIC";
                statusColor = Color.yellow;
            }
            else if (detector != null)
            {
                if (detector.IsCalibrating)
                {
                    status = $"CALIBRATING {detector.CalibrationProgress * 100f:F0}%";
                    statusColor = Color.yellow;
                }
                else if (detector.State == VoiceState.Shout) { status = "SCREAM"; statusColor = Color.red; }
                else if (detector.State == VoiceState.Blow) { status = "BLOW"; statusColor = Color.cyan; }
            }

            DrawRect(new Rect(x - 10, y - 10, w + 20, 330), new Color(0, 0, 0, 0.6f));

            big.normal.textColor = statusColor;
            GUI.Label(new Rect(x, y, w, 40), status, big);
            y += 46;

            // Volume bar (dB scale so quiet sounds are visible) with threshold markers.
            GUI.Label(new Rect(x, y, w, 20), "Volume (RMS)", label);
            y += 20;
            Rect bar = new Rect(x, y, w, 22);
            DrawRect(bar, new Color(1, 1, 1, 0.15f));
            float level = DbToBar(microphone.RmsDb);
            DrawRect(new Rect(bar.x, bar.y, bar.width * level, bar.height), statusColor);
            if (detector != null && !detector.IsCalibrating)
            {
                DrawMarker(bar, detector.BlowThreshold, Color.cyan);
                DrawMarker(bar, detector.ShoutThreshold, Color.red);
            }
            y += 30;

            // Low-frequency share bar (0..1) with the blow threshold.
            GUI.Label(new Rect(x, y, w, 20), "Low-freq share", label);
            y += 20;
            Rect lowBar = new Rect(x, y, w, 14);
            DrawRect(lowBar, new Color(1, 1, 1, 0.15f));
            DrawRect(new Rect(lowBar.x, lowBar.y, lowBar.width * microphone.LowFreqRatio, lowBar.height),
                     new Color(0.3f, 0.7f, 1f));
            if (detector != null)
                DrawRect(new Rect(lowBar.x + lowBar.width * detector.LowFreqRatioThreshold - 1, lowBar.y - 2, 2, lowBar.height + 4),
                         Color.cyan);
            y += 26;

            GUI.Label(new Rect(x, y, w, 20), $"RMS: {microphone.Rms:F4}  ({microphone.RmsDb:F1} dB)", label); y += 20;
            GUI.Label(new Rect(x, y, w, 20), $"Low-freq level: {microphone.LowFreqLevel:F4}   ratio: {microphone.LowFreqRatio:F2}", label); y += 20;
            if (detector != null)
            {
                GUI.Label(new Rect(x, y, w, 20), $"Noise floor: {detector.NoiseFloor:F4}", label); y += 20;
                GUI.Label(new Rect(x, y, w, 20),
                    $"Shout thr: {detector.ShoutThreshold:F3}   Blow thr: {detector.BlowThreshold:F3}" +
                    (detector.IsSuppressed ? "  (raised)" : ""), label); y += 20;
            }
            GUI.Label(new Rect(x, y, w, 20), $"Mic: {(string.IsNullOrEmpty(microphone.ActiveDevice) ? "-" : microphone.ActiveDevice)} @ {microphone.SampleRate} Hz", label); y += 26;

            if (detector != null && GUI.Button(new Rect(x, y, 160, 28), "Recalibrate"))
                detector.Recalibrate();
        }

        float DbToBar(float db) => Mathf.InverseLerp(minDb, 0f, db);

        void DrawMarker(Rect bar, float linearRms, Color c)
        {
            float db = linearRms > 1e-5f ? 20f * Mathf.Log10(linearRms) : -80f;
            float mx = bar.x + bar.width * Mathf.Clamp01(DbToBar(db));
            DrawRect(new Rect(mx - 1, bar.y - 3, 2, bar.height + 6), c);
        }

        void DrawRect(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, white);
            GUI.color = old;
        }

        void EnsureStyles()
        {
            if (label != null) return;
            label = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            label.normal.textColor = Color.white;
            big = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold };
        }
    }
}
