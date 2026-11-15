using System.Text;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Records the correct CPR hand position on a PRACTICE disc (cushion or air - never a person),
    /// for the current patient mode, and saves it for <see cref="GhostHands"/>.
    /// While armed it tells the rescuer exactly what is stopping the recording (on the CPR HUD),
    /// e.g. "Left hand not tracked", "Right hand 9 cm too far left", "Hands moving 3 cm".
    ///
    /// Flow: start practice (X / pinch with ring finger) -> hands on the cushion or in the air ->
    /// hold still: the disc appears under them -> keep holding: pose saved.
    /// Adult needs both hands; Child and Infant need at least one.
    /// Files: /sdcard/Android/data/com.cprxr.prototype/files/hand-poses/{Adult,Child,Infant}.json
    /// Log tag: "CPRRecord".
    /// </summary>
    public class HandPoseRecorder : MonoBehaviour
    {
        public CompressionDisc disc;
        public GhostHands ghostHands;
        [Tooltip("Hands must stay within this distance (m) of where the hold started...")]
        public float stillRadius = 0.02f;
        [Tooltip("...for this long (s) to record.")]
        public float holdSeconds = 1.5f;
        [Tooltip("How close (m, horizontally) a wrist must be to the disc center to count.")]
        public float nearRadius = 0.18f;
        [Tooltip("Wrist must be within this height band above the disc top (m): min..max.")]
        public Vector2 wristHeightBand = new Vector2(-0.03f, 0.15f);

        const string Hud = "2-recorder";

        /// <summary>Pose editing (recording / clearing) is off unless switched on with the left menu button.</summary>
        public bool EditMode { get; private set; }

        /// <summary>Left controller menu (hamburger) button / left-hand menu gesture.</summary>
        public void ToggleEditMode()
        {
            EditMode = !EditMode;
            m_Armed = false;
            m_WasPractice = false; // re-arm on the next frame if practice is running
            Debug.Log($"CPRRecord: pose edit mode {(EditMode ? "ON" : "OFF")}");
            if (EditMode && disc != null && !disc.IsPractice && !disc.IsPlacingPractice)
            {
                var cam = Camera.main;
                disc.BeginPracticePlacement(cam != null ? cam.transform.forward : Vector3.forward);
            }
            CPRHud.Set("0-mode", EditMode ? "POSE EDIT MODE  (menu button: exit)" : null);
            CPRHud.Set(Hud, EditMode ? null : "Pose edit mode off - saved poses kept", 2f);
        }

        OVRHand m_Left, m_Right;
        bool m_Armed, m_WasPractice;
        PoseOverlayTest.BodyClass m_ArmedClass;
        StillnessDetector m_Still;
        float m_SavedMessageUntil;
        // Adult is recorded one hand at a time (interlocked fingers hide each other from the cameras):
        // step 0 = bottom hand alone, step 1 = top hand laid over the ghost of the bottom hand.
        int m_AdultStep;
        HandPoseEntry m_BottomHand;

        void Start()
        {
            m_Still = new StillnessDetector(stillRadius, holdSeconds);
            foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var t = ((OVRSkeleton.IOVRSkeletonDataProvider)h).GetSkeletonType();
                if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) m_Left = h; else m_Right = h;
            }
        }

        void Update()
        {
            if (disc == null) return;

            if (disc.IsPlacingPractice) { CPRHud.Set(Hud, disc.PlacementHint); m_WasPractice = false; return; }

            // Arm (edit mode only) when practice starts, or when the patient mode changes during practice.
            if (EditMode && disc.IsPractice && (!m_WasPractice || disc.CurrentClass != m_ArmedClass)) Arm(disc.CurrentClass);
            if (!EditMode) m_Armed = false;
            if (!disc.IsPractice) { m_Armed = false; m_WasPractice = false; CPRHud.Set(Hud, null); return; }
            m_WasPractice = true;

            if (Time.time < m_SavedMessageUntil) return; // keep "Saved" on screen for a moment
            if (!disc.TryGetDiscFrame(out var top, out var rot)) return;

            if (!m_Armed)
            {
                CPRHud.Set(Hud, $"PRACTICE - {disc.CurrentClass}\n" +
                                "Hands apart, then pinch:  index = mode,  ring = exit" +
                                (EditMode ? "\nmiddle pinch / B: clear & re-record this pose" : "\nmenu button: edit hand poses"));
                return;
            }

            // ---- What's stopping the recording? ----
            var lIssue = HandIssue(m_Left, "Left", top, rot);
            var rIssue = HandIssue(m_Right, "Right", top, rot);
            var lOk = lIssue == null;
            var rOk = rIssue == null;
            var adult = m_ArmedClass == PoseOverlayTest.BodyClass.Adult;
            var sb = new StringBuilder();
            bool useL, useR;

            if (adult && m_AdultStep == 0)
            {
                sb.Append("RECORD ADULT POSE - step 1 of 2: BOTTOM hand\nHeel of ONE hand on the disc, fingers lifted");
                if (lOk && rOk) { Block(sb, "Use one hand only - lift the other hand away"); return; }
                if (!lOk && !rOk) { Block(sb, NearerIssue(lIssue, rIssue)); return; }
                useL = lOk; useR = rOk;
            }
            else if (adult)
            {
                var topIsLeft = !m_BottomHand.isLeft;
                var issue = topIsLeft ? lIssue : rIssue;
                var bottomReal = m_BottomHand.isLeft ? m_Left : m_Right;
                sb.Append($"RECORD ADULT POSE - step 2 of 2: TOP hand ({(topIsLeft ? "left" : "right")})\n" +
                          "Lay it over the GHOST hand, fingers curled down between its fingers");
                if (bottomReal != null && bottomReal.IsTracked && Near(bottomReal, top)) { Block(sb, "Lift your real bottom hand away - the ghost stands in for it"); return; }
                if (issue != null) { Block(sb, issue); return; }
                useL = topIsLeft; useR = !topIsLeft;
            }
            else
            {
                sb.Append($"RECORD {m_ArmedClass.ToString().ToUpper()} POSE\n" +
                          (m_ArmedClass == PoseOverlayTest.BodyClass.Infant ? "Two fingers on the disc" : "Heel of one hand on the disc"));
                if (!lOk && !rOk) { Block(sb, NearerIssue(lIssue, rIssue)); return; }
                useL = lOk && !rOk; useR = rOk; // one hand: prefer the right if both are there
            }

            // Hold still.
            var p = (useL ? m_Left : m_Right).transform.position;
            if (m_Still.Update(p, Time.deltaTime)) { Record(useL, useR, top, rot); return; }
            sb.Append($"\nHold still... {Bar(m_Still.Progress)}");
            if (m_Still.Drift > stillRadius * 0.6f) sb.Append($"\n- moving {m_Still.Drift * 100f:F1} cm: relax and keep steady");
            CPRHud.Set(Hud, sb.ToString() + "\n\n(middle pinch / B: clear & redo)");
        }

        void Block(StringBuilder sb, string reason)
        {
            m_Still.Reset();
            CPRHud.Set(Hud, sb.Append("\n- ").Append(reason).Append("\n\n(middle pinch / B: clear & redo)").ToString());
        }

        /// <summary>Is this (tracked) hand anywhere over the disc?</summary>
        bool Near(OVRHand h, Vector3 top)
        {
            var p = h.transform.position;
            return new Vector2(p.x - top.x, p.z - top.z).magnitude <= nearRadius && p.y - top.y < wristHeightBand.y + 0.05f;
        }

        /// <summary>When neither hand is usable, report the one that's closest to being right.</summary>
        static string NearerIssue(string l, string r) =>
            l.Contains("not tracked") && !r.Contains("not tracked") ? r :
            r.Contains("not tracked") && !l.Contains("not tracked") ? l : l + "\n- " + r;

        /// <summary>Clear the saved pose for the current mode and record it again.</summary>
        public void ClearAndRedo()
        {
            if (disc == null || !disc.IsPractice || !EditMode) return;
            var cls = disc.CurrentClass;
            HandPoseFile.DeleteFromDevice(cls);
            if (ghostHands != null) ghostHands.Hide(cls);
            Arm(cls);
            CPRHud.Set(Hud, $"CLEARED {cls.ToString().ToUpper()} POSE - record it again", 2f);
            m_SavedMessageUntil = Time.time + 1.5f;
            Debug.Log($"CPRRecord: cleared {cls} pose, re-armed");
        }

        /// <summary>null if this hand is usable; otherwise a plain-language reason and fix.</summary>
        string HandIssue(OVRHand h, string name, Vector3 top, Quaternion rot)
        {
            if (h == null) return $"{name} hand: not found";
            if (!h.IsTracked) return $"{name} hand not tracked - keep it in view, don't cover it";
            if (h.HandConfidence != OVRHand.TrackingConfidence.High) return $"{name} hand tracking is weak - more light, less overlap";

            // Offset in the disc frame: x = sideways, z = toward the patient, y = up.
            var local = Quaternion.Inverse(rot) * (h.transform.position - top);
            var flat = new Vector2(local.x, local.z).magnitude;
            if (flat > nearRadius)
            {
                var side = Mathf.Abs(local.x) > Mathf.Abs(local.z)
                    ? (local.x > 0 ? "right" : "left")
                    : (local.z > 0 ? "far (toward patient)" : "close (toward you)");
                return $"{name} hand {(flat - nearRadius) * 100f:F0} cm too far {side} - move onto the disc";
            }
            if (local.y > wristHeightBand.y) return $"{name} hand {(local.y - wristHeightBand.y) * 100f:F0} cm too high - rest it on the disc";
            if (local.y < wristHeightBand.x) return $"{name} hand {(wristHeightBand.x - local.y) * 100f:F0} cm too low - lift slightly";
            return null;
        }

        void Arm(PoseOverlayTest.BodyClass cls)
        {
            m_Armed = true;
            m_ArmedClass = cls;
            m_AdultStep = 0;
            m_BottomHand = null;
            m_Still.Reset();
            Debug.Log($"CPRRecord: armed for {cls}");
        }

        void Record(bool useLeft, bool useRight, Vector3 top, Quaternion rot)
        {
            var worldToDisc = Matrix4x4.TRS(top, rot, Vector3.one).inverse;
            var file = new HandPoseFile { patientClass = m_ArmedClass.ToString(), recordedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            if (useLeft) { var e = HandPoseEntry.Capture(m_Left, m_Left.transform, worldToDisc, rot); if (e != null) file.hands.Add(e); }
            if (useRight) { var e = HandPoseEntry.Capture(m_Right, m_Right.transform, worldToDisc, rot); if (e != null) file.hands.Add(e); }
            if (file.hands.Count == 0)
            {
                m_Still.Reset();
                CPRHud.Set(Hud, "Recording failed: hand data invalid this frame - hold again", 2f);
                Debug.Log("CPRRecord: capture failed (invalid skeleton data)");
                return;
            }

            if (m_ArmedClass == PoseOverlayTest.BodyClass.Adult && m_AdultStep == 0)
            {
                // Step 1 done: keep the bottom hand, show it as a ghost, and ask for the top hand.
                m_BottomHand = file.hands[0];
                file.SaveToDevice(); // partial file: lets the ghost show the bottom hand
                m_AdultStep = 1;
                m_Still.Reset();
                if (ghostHands != null) ghostHands.Reload();
                CPRHud.Set(Hud, "Bottom hand saved.\nNow lift it away and lay the other hand over the ghost.", 2.5f);
                m_SavedMessageUntil = Time.time + 2.5f;
                Debug.Log($"CPRRecord: adult step 1 saved (bottom = {(m_BottomHand.isLeft ? "left" : "right")} hand)");
                return;
            }
            if (m_ArmedClass == PoseOverlayTest.BodyClass.Adult && m_BottomHand != null) file.hands.Insert(0, m_BottomHand);

            file.SaveToDevice();
            m_Armed = false;
            m_SavedMessageUntil = Time.time + 3f;
            CPRHud.Set(Hud, $"SAVED {m_ArmedClass.ToString().ToUpper()} POSE\nGhost hands now show it.\nHands apart + index pinch: next mode");
            Debug.Log($"CPRRecord: saved {m_ArmedClass} pose ({file.hands.Count} hand(s): " +
                      $"{(useLeft ? "L " : "")}{(useRight ? "R" : "")}) to {HandPoseFile.DeviceFolder}");
            if (ghostHands != null) ghostHands.Reload();
        }

        static string Bar(float t) => "[" + new string('#', Mathf.RoundToInt(t * 10)) + new string('.', 10 - Mathf.RoundToInt(t * 10)) + "]";

        void OnDisable() => CPRHud.Set(Hud, null);
    }
}
