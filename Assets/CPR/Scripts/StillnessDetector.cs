using UnityEngine;

namespace CPR
{
    /// <summary>
    /// "Is this point being held still?" that tolerates hand-tracking jitter.
    /// Comparing frame-to-frame motion fails because tracking jitters a few mm every frame
    /// (which reads as 10+ cm/s at 72 fps). Instead: the point must stay within <see cref="radius"/>
    /// of where the hold started. Leaving that radius restarts the hold from the new spot.
    /// </summary>
    public class StillnessDetector
    {
        public float radius;
        public float holdSeconds;

        Vector3 m_Anchor;
        bool m_HasAnchor;

        public float HeldFor { get; private set; }
        public float Progress => holdSeconds <= 0f ? 1f : Mathf.Clamp01(HeldFor / holdSeconds);
        public bool IsDone => HeldFor >= holdSeconds;
        /// <summary>How far the point is from where the hold started (m).</summary>
        public float Drift { get; private set; }

        public StillnessDetector(float radius, float holdSeconds)
        {
            this.radius = radius;
            this.holdSeconds = holdSeconds;
        }

        /// <returns>true once the point has stayed put for holdSeconds.</returns>
        public bool Update(Vector3 p, float dt)
        {
            if (!m_HasAnchor) { m_Anchor = p; m_HasAnchor = true; HeldFor = 0f; }
            Drift = Vector3.Distance(p, m_Anchor);
            if (Drift > radius) { m_Anchor = p; HeldFor = 0f; Drift = 0f; return false; }
            // Let the anchor follow very slowly, so a slow sag doesn't fail an otherwise steady hold.
            m_Anchor = Vector3.Lerp(m_Anchor, p, 0.02f);
            HeldFor += dt;
            return IsDone;
        }

        public void Reset()
        {
            m_HasAnchor = false;
            HeldFor = 0f;
            Drift = 0f;
        }
    }
}
