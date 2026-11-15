using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Guided CPR steps for both modes:
    ///   find person -> kneel -> "Check for responsiveness" (4 s) -> ghost hands -> compressions
    ///   (count, depth in inches, hands/arms form, 120 bpm metronome) -> after the test minute:
    ///   "Please move the victim into the recovery position." + recovery-position ghost.
    /// SOS shows text in the small top-left HUD (SosHud); Practice uses the world panel (CPRHud).
    /// Log tag: "CPRSteps".
    /// </summary>
    public class CprStepFlow : MonoBehaviour
    {
        public enum Step { Off, FindPerson, Kneel, Responsiveness, Hands, Compressions, Recovery, Complete }

        public CPRModeManager modes;
        public PoseOverlayTest pose;
        public KneelIndicator kneel;
        public GhostHands ghostHands;
        public CompressionDisc disc;
        public PracticeMannequin mannequin;
        public Metronome metronome;
        public RecoveryGhost recoveryGhost;

        [Tooltip("Seconds for 'Check for responsiveness' before hand placement.")]
        public float responsivenessSeconds = 4f;
        [Tooltip("Prototype test: compressions run this long (s) before the recovery-position step.")]
        public float compressionTestSeconds = 30f;
        [Tooltip("Practice: the dummy can't be rolled over, so the recovery step completes after this long (s).")]
        public float practiceRecoverySeconds = 10f;
        [Tooltip("Real patient: how long (s) the recovery shape must be seen before the protocol completes.")]
        public float recoveryHoldSeconds = 0.8f;
        [Tooltip("Real patient: complete anyway after this long (s) if tracking never sees the shape.")]
        public float recoveryTimeoutSeconds = 60f;
        [Tooltip("Deeper than target + this (m) = 'Shallower' (adult: 2.0-2.4 in).")]
        public float tooDeepMargin = 0.01f;

        public Step Current { get; private set; } = Step.Off;
        float m_StepStart, m_FirstCompressionAt = -1f, m_RecoverySeen;
        Vector3 m_PatientChest, m_PatientHeadDir; float m_PatientLength; bool m_HavePatient;
        int m_LoggedCount = -1;
        bool m_CheckedResponsiveness;
        CPRModeManager.Mode m_Mode;

        bool Sos => m_Mode == CPRModeManager.Mode.SOS;

        void Update()
        {
            var mode = modes != null ? modes.CurrentMode : CPRModeManager.Mode.Idle;
            if (mode != m_Mode) { m_Mode = mode; Restart(); }
            if (mode == CPRModeManager.Mode.Idle || mode == CPRModeManager.Mode.Pose) { if (Current != Step.Off) Restart(); return; }

            if (disc != null) disc.postHud = false;       // this flow shows the compression status
            if (ghostHands != null) ghostHands.postHud = false; // hints go through this flow's HUD text

            // Protocol order: kneel -> check responsiveness -> hands -> compressions (30 s) -> recovery -> done.
            // The patient's size is locked with the figure so hand placement can't switch mid-run.
            if (pose != null && kneel != null && pose.LockedClass == PoseOverlayTest.BodyClass.Unknown &&
                (kneel.CurrentState == KneelIndicator.State.Locked || kneel.CurrentState == KneelIndicator.State.Kneeling))
                pose.LockClass();

            var kneeling = kneel != null && kneel.CurrentState == KneelIndicator.State.Kneeling;
            var afterKneel = m_CheckedResponsiveness ? Step.Hands : Step.Responsiveness;
            switch (Current)
            {
                case Step.Off:
                case Step.FindPerson:
                    Go(kneel != null && kneel.CurrentState != KneelIndicator.State.Searching ? Step.Kneel : Step.FindPerson);
                    if (kneeling) Go(afterKneel);
                    break;
                case Step.Kneel:
                    if (kneeling) Go(afterKneel);
                    else if (kneel != null && kneel.CurrentState == KneelIndicator.State.Searching) Go(Step.FindPerson);
                    break;
                case Step.Responsiveness:
                    if (Time.time - m_StepStart >= responsivenessSeconds) { m_CheckedResponsiveness = true; Go(Step.Hands); }
                    break;
                case Step.Hands:
                    if (ghostHands != null && ghostHands.FormMatched) Go(Step.Compressions);
                    break;
                case Step.Compressions:
                    // Hidden timer: starts with the metronome, not shown.
                    if (m_FirstCompressionAt < 0f && disc != null && disc.CompressionCount > 0) m_FirstCompressionAt = Time.time;
                    if (Time.time - m_StepStart >= compressionTestSeconds) Go(Step.Recovery);
                    break;
                case Step.Recovery:
                    if (!Sos) { if (Time.time - m_StepStart >= practiceRecoverySeconds) Go(Step.Complete); break; }
                    m_RecoverySeen = RecoveryShapeSeen() ? m_RecoverySeen + Time.deltaTime : Mathf.Max(0f, m_RecoverySeen - 0.5f * Time.deltaTime);
                    if (m_RecoverySeen >= recoveryHoldSeconds) { Debug.Log("CPRSteps: recovery position recognised"); Go(Step.Complete); }
                    else if (Time.time - m_StepStart >= recoveryTimeoutSeconds) { Debug.Log("CPRSteps: recovery step timed out - completing"); Go(Step.Complete); }
                    break;
            }
            ShowText();
        }

        void Go(Step s)
        {
            if (s == Current) return;
            Current = s;
            m_StepStart = Time.time;
            if (ghostHands != null) ghostHands.allowShow = s == Step.Hands || s == Step.Compressions;
            if (s == Step.Compressions) { if (disc != null) disc.ResetCount(); m_FirstCompressionAt = -1f; if (metronome != null) metronome.Play(); RememberPatient(); }
            if (s == Step.Recovery)
            {
                if (metronome != null) metronome.Stop();
                m_RecoverySeen = 0f;
                if (Sos && pose != null) pose.SetFrozen(false); // watch the person being rolled over
                ShowRecoveryGhost();
            }
            if (s == Step.Complete && recoveryGhost != null) recoveryGhost.Hide();
            // Recovery onward: no disc, no compression dummy.
            if (disc != null) disc.hidden = s >= Step.Recovery;
            if (mannequin != null) mannequin.SetBodyVisible(s < Step.Recovery);
            if (s == Step.Responsiveness) ResponsivenessDemo.Show(); else ResponsivenessDemo.Hide();
            Debug.Log($"CPRSteps: {s}");
        }

        void Restart()
        {
            Current = Step.Off;
            m_FirstCompressionAt = -1f;
            m_CheckedResponsiveness = false;
            m_HavePatient = false;
            ResponsivenessDemo.Hide();
            if (disc != null) disc.hidden = false;
            if (mannequin != null) mannequin.SetBodyVisible(true);
            if (metronome != null) metronome.Stop();
            if (recoveryGhost != null) recoveryGhost.Hide();
            if (ghostHands != null) ghostHands.allowShow = false;
            SosHud.Set("3-step", null);
            CPRHud.Set("3-steps", null);
        }

        // ---------- text ----------

        void ShowText()
        {
            string text = null;
            switch (Current)
            {
                case Step.FindPerson:
                    text = Sos || (mannequin != null && !mannequin.IsSpawned) ? null : "Look at the figure on the floor";
                    break;
                case Step.Kneel:
                    text = Sos ? null : "Kneel at the green box";
                    break;
                case Step.Responsiveness:
                    var left = Mathf.Max(1, Mathf.CeilToInt(responsivenessSeconds - (Time.time - m_StepStart)));
                    text = $"Check for responsiveness\nTap their shoulders and shout \"Are you okay?\" ({left})";
                    break;
                case Step.Hands:
                    text = ghostHands != null && !string.IsNullOrEmpty(ghostHands.CurrentHint) ? ghostHands.CurrentHint
                                                                                              : "Place your hands like the ghost hands";
                    break;
                case Step.Compressions:
                    text = CompressionStatus();
                    if (disc != null && disc.CompressionCount != m_LoggedCount)
                    {
                        m_LoggedCount = disc.CompressionCount; // what the rescuer sees, once per compression
                        Debug.Log($"CPRSteps: HUD [{(Sos ? "SOS" : "Practice")}] {text.Replace('\n', '|')}");
                    }
                    break;
                case Step.Recovery:
                    text = "Please move the victim into the recovery position.";
                    break;
                case Step.Complete:
                    text = "Keep a close eye on the victim while waiting for first responders to arrive.";
                    break;
            }
            SosHud.Set("3-step", text); // same head-locked HUD in SOS and practice
        }

        string CompressionStatus()
        {
            var count = disc != null ? disc.CompressionCount : 0;
            string depth = "--";
            if (disc != null && count > 0)
            {
                var inches = disc.LastDepth / 0.0254f;
                var target = disc.CurrentTargetDepth;
                var verdict = disc.LastDepth < target ? "Deeper" : disc.LastDepth > target + tooDeepMargin ? "Shallower" : "Perfect";
                depth = $"{inches:F1} in ({verdict})";
            }
            string form;
            if (ghostHands != null && !ghostHands.FormMatched) form = "Slipping - put your hands back on the button";
            else
            {
                var p = disc != null ? disc.LastPostureFeedback ?? "" : "";
                form = p.Contains("elbow") ? "Lock your elbows" : p.Contains("shoulders") ? "Shoulders over your hands" : p.Contains("chest come") ? "Let the chest come all the way up" : "Good";
            }
            return $"Compressions: {count}\nDepth: {depth}\nHands and arms: {form}"; // the 30 s timer is not shown
        }

        // ---------- recovery ghost placement ----------

        /// <summary>Remember where the patient lies (locked disc = chest) when compressions start.</summary>
        void RememberPatient()
        {
            m_HavePatient = false;
            if (disc == null || !disc.TryGetDiscFrame(out var chest, out var rot)) return;
            m_PatientChest = chest;
            var axis = pose != null ? -pose.BodyAxis : Vector3.zero;               // hips -> head
            if (axis.sqrMagnitude < 1e-4f) axis = rot * Vector3.right;              // disc frame fallback
            m_PatientHeadDir = Vector3.ProjectOnPlane(axis, Vector3.up).normalized;
            m_PatientLength = mannequin != null && mannequin.IsSpawned ? mannequin.BodyLength
                            : pose != null && pose.EstimatedLength > 0.5f ? pose.EstimatedLength : 1.75f;
            m_HavePatient = m_PatientHeadDir.sqrMagnitude > 0.5f;
            Debug.Log($"CPRSteps: patient remembered (chest {m_PatientChest}, length {m_PatientLength:F2} m)");
        }

        /// <summary>Generous check that the real patient has been rolled into a recovery-like shape.</summary>
        bool RecoveryShapeSeen()
        {
            if (pose == null) return false;
            bool J(int i, out Vector3 p) => pose.TryGetJoint(i, out p);
            if (J(11, out var ls) && J(12, out var rs) && Mathf.Abs(ls.y - rs.y) > 0.12f) return true;   // shoulders stacked: on their side
            if (J(23, out var lh) && J(24, out var rh) && Mathf.Abs(lh.y - rh.y) > 0.10f) return true;   // hips stacked
            foreach (var (h, k, a) in new[] { (23, 25, 27), (24, 26, 28) })                            // a knee bent
                if (J(h, out var hp) && J(k, out var kp) && J(a, out var ap) && Vector3.Angle(hp - kp, ap - kp) < 140f) return true;
            return false;
        }

        void ShowRecoveryGhost()
        {
            if (recoveryGhost == null) return;
            recoveryGhost.solidModel = mannequin != null && mannequin.IsSpawned; // practice: the dummy "rolled over"
            if (ghostHands != null) ghostHands.allowShow = false;
            var cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            if (m_Mode == CPRModeManager.Mode.Practice && mannequin != null && mannequin.Root != null)
            {
                var r = mannequin.Root;
                recoveryGhost.Show(r.position, r.forward, cam - r.position, mannequin.BodyLength);
                return;
            }
            if (m_HavePatient)
            {
                // Exactly where the person lies: hips ~22% of body length from the chest toward the feet.
                var hips = m_PatientChest - m_PatientHeadDir * (0.22f * m_PatientLength);
                hips.y = pose != null ? pose.FloorY : m_PatientChest.y - 0.2f;
                recoveryGhost.Show(hips, m_PatientHeadDir, cam - hips, m_PatientLength);
                return;
            }
            if (pose != null && pose.TryGetJoint(23, out var lh) && pose.TryGetJoint(24, out var rh))
            {
                var hips = 0.5f * (lh + rh); hips.y = pose.FloorY;
                recoveryGhost.Show(hips, -pose.BodyAxis, cam - hips, pose.EstimatedLength > 0.5f ? pose.EstimatedLength : 1.75f);
            }
            else
            {
                // No patient data: in front of the user.
                var fwd = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
                var spot = cam + fwd * 0.9f; spot.y = pose != null ? pose.FloorY : 0f;
                recoveryGhost.Show(spot, Vector3.Cross(fwd, Vector3.up), -fwd);
            }
        }
    }
}
