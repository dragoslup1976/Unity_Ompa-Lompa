using UnityEngine;

namespace OmpaLompa.Audio
{
    /// <summary>
    /// Stand-in for a creature in the AudioTest scene. Jump() = jump and flash red (shout),
    /// Slide() = glide sideways and back (blow). Wire them to VoiceEventDetector.OnShout / OnBlow
    /// in the Inspector: this also shows colleagues how to hook their own creatures.
    /// </summary>
    public class AudioTestCubeReaction : MonoBehaviour
    {
        [Header("Jump (shout)")]
        [SerializeField] float jumpHeight = 1.2f;
        [SerializeField] float jumpDuration = 0.5f;
        [SerializeField] float redDuration = 0.9f;

        [Header("Slide (blow)")]
        [SerializeField] Vector3 slideOffset = new Vector3(2.5f, 0f, 0f);
        [SerializeField] float slideDuration = 2.5f;

        Vector3 basePosition;
        Renderer cubeRenderer;
        Color baseColor;
        float jumpT = 1f, slideT = 1f, redT = 1f; // normalised progress, 1 = finished

        void Awake()
        {
            basePosition = transform.position;
            cubeRenderer = GetComponent<Renderer>();
            baseColor = cubeRenderer.material.color; // material instance, doesn't touch the shared asset
        }

        /// <summary>Hook to OnShout.</summary>
        public void Jump()
        {
            jumpT = 0f;
            redT = 0f;
        }

        /// <summary>Hook to OnBlow.</summary>
        public void Slide() => slideT = 0f;

        void Update()
        {
            Vector3 pos = basePosition;

            if (jumpT < 1f)
            {
                jumpT = Mathf.Min(1f, jumpT + Time.deltaTime / jumpDuration);
                pos.y += Mathf.Sin(jumpT * Mathf.PI) * jumpHeight; // simple arc, no physics needed
            }

            if (slideT < 1f)
            {
                slideT = Mathf.Min(1f, slideT + Time.deltaTime / slideDuration);
                // 0 -> 1 -> 0 with smooth ends: slides away, then returns.
                pos += slideOffset * Mathf.SmoothStep(0f, 1f, Mathf.Sin(slideT * Mathf.PI));
            }

            transform.position = pos;

            if (redT < 1f)
                redT = Mathf.Min(1f, redT + Time.deltaTime / redDuration);
            cubeRenderer.material.color = Color.Lerp(Color.red, baseColor, redT);
        }
    }
}
