using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Temporary demo/test controls until the voice trigger and real UI exist:
    ///   A button, or hold an index-finger pinch  -> cycle patient mode (Auto/Adult/Child/Infant)
    ///   B button, or hold a middle-finger pinch -> reset (forget the patient, scan again)
    ///   Menu (hamburger) button on the left controller -> hand-pose edit mode on/off (record / clear poses)
    ///   X button, or hold a ring-finger pinch   -> practice target on/off: placed where you look
    ///                                              (a cushion within reach), or in the air in front of you
    /// Pinches must be held briefly, and are ignored while the hands are on the compression disc,
    /// so compressions can't trigger them by accident.
    /// </summary>
    public class CPRDemoControls : MonoBehaviour
    {
        public PoseOverlayTest pose;
        public KneelIndicator kneel;
        public CompressionDisc disc;
        public HandPoseRecorder recorder;
        [Tooltip("Seconds a pinch must be held to count.")]
        public float pinchHoldSeconds = 0.6f;
        [Tooltip("Pinches only count when the other hand is at least this far away (m) - interlocked CPR hands look like pinches.")]
        public float handsApartDistance = 0.25f;
        [Tooltip("Minimum pinch strength (0-1).")]
        public float minPinchStrength = 0.7f;

        OVRHand[] m_Hands;
        float m_IndexHeld, m_MiddleHeld, m_RingHeld;
        bool m_IndexFired, m_MiddleFired, m_RingFired;

        void Start()
        {
            m_Hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        }

        void Update()
        {
            var blocked = disc != null && disc.HandsInZone;

            if (OVRInput.GetDown(OVRInput.RawButton.A) || HeldPinch(OVRHand.HandFinger.Index, ref m_IndexHeld, ref m_IndexFired, blocked))
                CycleMode();
            if (OVRInput.GetDown(OVRInput.RawButton.B) || HeldPinch(OVRHand.HandFinger.Middle, ref m_MiddleHeld, ref m_MiddleFired, blocked))
                ResetAll();
            if (OVRInput.GetDown(OVRInput.RawButton.X) || HeldPinch(OVRHand.HandFinger.Ring, ref m_RingHeld, ref m_RingFired, blocked))
                TogglePractice();
            // Left controller menu (hamburger) button - also the left-hand palm-up menu pinch.
            if (OVRInput.GetDown(OVRInput.Button.Start) && recorder != null)
                recorder.ToggleEditMode();
        }

        void TogglePractice()
        {
            if (disc == null) return;
            if (disc.IsPractice || disc.IsPlacingPractice) { disc.StopPractice(); return; }

            var head = Camera.main != null ? Camera.main.transform : null;
            var forward = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized : Vector3.forward;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            // The disc appears under the rescuer's hands once they hold them still (cushion or air).
            disc.BeginPracticePlacement(forward);
            Debug.Log("CPRDemo: practice - waiting for hands to be placed");
        }

        bool HeldPinch(OVRHand.HandFinger finger, ref float held, ref bool fired, bool blocked)
        {
            var pinching = false;
            if (!blocked && m_Hands != null)
                foreach (var h in m_Hands)
                    if (IsDeliberatePinch(h, finger)) { pinching = true; break; }

            if (!pinching) { if (held > 0f) CPRHud.Set("1-gesture", null); held = 0f; fired = false; return false; }
            held += Time.deltaTime;
            if (!fired)
            {
                var what = finger == OVRHand.HandFinger.Index ? "change patient mode" : finger == OVRHand.HandFinger.Middle ? "reset" : "practice on/off";
                CPRHud.Set("1-gesture", $"{finger} pinch: {what} [{new string('#', Mathf.RoundToInt(Mathf.Clamp01(held / pinchHoldSeconds) * 10))}]", 1f);
            }
            if (fired || held < pinchHoldSeconds) return false;
            fired = true; // once per pinch
            return true;
        }

        /// <summary>
        /// A real gesture, not fingers touching while the hands are interlocked for CPR:
        /// firm pinch, valid pointer pose, other hand far away (or untracked), and not on the disc.
        /// </summary>
        bool IsDeliberatePinch(OVRHand h, OVRHand.HandFinger finger)
        {
            if (h == null || !h.IsTracked) return false; // (pointer pose isn't valid with the palm down, so don't require it)
            if (!h.GetFingerIsPinching(finger) || h.GetFingerPinchStrength(finger) < minPinchStrength) return false;
            foreach (var other in m_Hands)
            {
                if (other == null || other == h || !other.IsTracked) continue;
                if (Vector3.Distance(other.transform.position, h.transform.position) < handsApartDistance) return false;
            }
            if (disc != null && disc.TryGetDiscFrame(out var top, out _) && Vector3.Distance(h.transform.position, top) < 0.3f) return false;
            return true;
        }

        void CycleMode()
        {
            if (pose != null) pose.CycleMode();
        }

        void ResetAll()
        {
            if (disc != null && disc.IsPractice && recorder != null && recorder.EditMode) { recorder.ClearAndRedo(); return; } // editing: redo the pose
            Debug.Log("CPRDemo: reset requested");
            if (disc != null) disc.ResetCount();
            if (kneel != null) kneel.ResetIndicator(); // also resets pose tracking
            else if (pose != null) pose.ResetTracking();
        }
    }
}
