using UnityEngine;

namespace CPR
{
    /// <summary>
    /// The compression target: a translucent cylinder whose top sits on the chest (or, in practice
    /// mode, on a cushion / in the air) and whose height is the target compression depth (5 cm
    /// adult/child, 4 cm infant). While the rescuer's tracked hands push down inside it, the color
    /// blends toward the "depth reached" color; on release it fades back, and reaching the base
    /// color again means "full recoil - do the next one".
    ///
    /// Depth is measured from where the hands come back up to between pushes (the recoil height),
    /// so it doesn't depend on exactly where the hand tracker puts the palm.
    /// Log tags: "CPRCompress" (one line per compression), "CPRHands" (tracking diagnostics).
    /// </summary>
    public class CompressionDisc : MonoBehaviour
    {
        [Header("References")]
        public PoseOverlayTest pose;
        public KneelIndicator kneel;
        [Tooltip("Hand/controller anchors. Defaults to the OVRCameraRig's hand anchors.")]
        public Transform leftHand, rightHand;

        [Header("Target depth (m)")]
        public float adultDepth = 0.05f;   // 2 in (AHA: 5-6 cm)
        public float childDepth = 0.05f;   // ~2 in (about 1/3 chest depth)
        public float infantDepth = 0.04f;  // ~1.5 in

        [Header("Disc size")]
        [Tooltip("Disc diameter as a fraction of the patient's measured height (Auto mode).")]
        public float diameterFraction = 0.07f;
        [Tooltip("Fixed radius (m) used in demo modes and before height is known.")]
        public float adultRadius = 0.06f;
        public float childRadius = 0.04f;
        public float infantRadius = 0.015f;

        [Header("Hand detection")]
        [Tooltip("Hands count as 'on the disc' within this horizontal distance of its center (m).")]
        public float zoneRadius = 0.15f;
        [Tooltip("...and no higher than this above the disc top (m). Wrists rest ~5 cm above it.")]
        public float zoneHeight = 0.25f;
        [Header("Stroke detection")]
        [Tooltip("Hands must move this far (m) to start/end a stroke - filters tracking jitter.")]
        public float strokeStart = 0.012f;
        [Tooltip("Strokes shallower than this (m) aren't counted as compressions.")]
        public float minStroke = 0.025f;
        [Tooltip("Deeper than target + this (m) = too deep (adults: about 6 cm).")]
        public float tooDeepMargin = 0.012f;
        [Tooltip("Next stroke must start within this (m) of the previous peak = full chest recoil.")]
        public float recoilTolerance = 0.015f;
        [Tooltip("Hand travel under this counts as released / fully recoiled (m).")]
        public float releaseThreshold = 0.01f;

        [Header("Practice placement")]
        [Tooltip("Wrist height above the surface the hands rest on (m). The disc top goes this far below the wrists.")]
        public float wristAboveSurface = 0.05f;
        [Tooltip("Hands must be held still this long (s) to place the practice disc.")]
        public float placeHoldSeconds = 1.0f;

        [Header("Look")]
        public Material discMaterial;
        public Color baseColor = new Color(1f, 0.45f, 0f, 0.55f);    // orange
        public Color reachedColor = new Color(0.1f, 1f, 0.35f, 0.8f); // green
        [Tooltip("Base ring color (under the button cap).")]
        public Color ringColor = new Color(0.15f, 0.15f, 0.15f, 0.6f);
        [Tooltip("Button: base ring thickness, cap thickness, and how far the cap visibly sinks (m).")]
        public float ringThickness = 0.004f, capThickness = 0.008f, capTravel = 0.007f;
        [Tooltip("Click when depth is reached; softer tick on full release.")]
        public bool clickSounds = true;
        [Tooltip("Seconds to fade from 'reached' back to the base color after release.")]
        public float recoilFadeSeconds = 0.35f;

        [Header("Posture coaching (straight arms, shoulders over hands)")]
        [Tooltip("Head must drop at least this fraction of the hand travel per push (locked elbows push with the body).")]
        public float minHeadFollow = 0.45f;
        [Tooltip("Head more than this far (m) behind the hands, horizontally = shoulders not over hands.")]
        public float maxShoulderLag = 0.15f;

        /// <summary>Posture feedback from the last compression, e.g. "lock your elbows".</summary>
        public string LastPostureFeedback { get; private set; } = "";
        public bool HandsInZone { get; private set; }
        public int CompressionCount { get; private set; }
        public bool IsPractice { get; private set; }
        /// <summary>Practice requested; waiting for the hands to be held still where the disc should go.</summary>
        public bool IsPlacingPractice { get; private set; }
        /// <summary>0..1 progress of holding the hands still during placement (for UI).</summary>
        public float PlacementProgress => m_PlaceStill?.Progress ?? 0f;
        /// <summary>What the rescuer should do right now to place the practice disc (for the HUD).</summary>
        public string PlacementHint { get; private set; } = "";
        /// <summary>Short feedback on the last compression, e.g. "last 3.1 cm - push deeper".</summary>
        public string LastCompressionFeedback { get; private set; } = "";
        /// <summary>Class the disc is sized for (forced demo mode, else the estimate; Adult if unknown).</summary>
        public PoseOverlayTest.BodyClass CurrentClass =>
            pose == null || pose.EffectiveClass == PoseOverlayTest.BodyClass.Unknown ? PoseOverlayTest.BodyClass.Adult : pose.EffectiveClass;

        Transform m_Disc;            // root of the button (ring + cap)
        Transform m_Ring, m_Cap;
        Material m_Mat, m_RingMat;
        AudioSource m_Audio;
        AudioClip m_Click, m_Tick;
        float m_Pressed01;           // 0 = released, 1 = at target depth (drives the cap)
        OVRHand m_LeftHand, m_RightHand;
        Vector3 m_PracticePoint, m_PracticeForward;
        StillnessDetector m_PlaceStill;
        Vector3 m_Top;                // top center of the disc (world)
        Quaternion m_FrameRotation;   // +Z = rescuer -> patient
        bool m_Visible;

        // Stroke detection: phase 0 = rising/resting (tracking the peak), 1 = pushing down (tracking the trough).
        int m_Phase;
        float m_PeakY = float.NaN, m_RepPeakY, m_TroughY, m_SmoothY = float.NaN, m_PrevPeakY = float.NaN;
        bool m_LastRecoilOk = true;
        float m_Intensity;          // 0 = base color, 1 = reached color
        bool m_ReachedThisRep;
        float m_RepHeadStartY, m_RepHeadMinY, m_RepHeadLagSum; int m_RepFrames;
        float m_LastCoachTime = -10f;
        float m_LastRepTime = -1f, m_LastDiag;
        int m_DiagFrames, m_DiagTrackedL, m_DiagTrackedR, m_DiagInZone;

        void Start()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                if (leftHand == null) leftHand = rig.leftHandAnchor;
                if (rightHand == null) rightHand = rig.rightHandAnchor;
            }
            foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var t = ((OVRSkeleton.IOVRSkeletonDataProvider)h).GetSkeletonType();
                if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) m_LeftHand = h; else m_RightHand = h;
            }

            // A thin push-button: dark base ring + colored cap that sinks as the hands push.
            m_Disc = new GameObject("CompressionButton").transform;
            m_Disc.SetParent(transform, false);
            m_Mat = new Material(discMaterial);
            m_RingMat = new Material(discMaterial) { color = ringColor };
            m_Ring = MakeCylinder("Ring", m_RingMat);
            m_Cap = MakeCylinder("Cap", m_Mat);

            m_Audio = m_Disc.gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Audio.spatialBlend = 1f;
            m_Audio.minDistance = 0.3f;
            m_Click = MakeClick("Click", 1800f, 0.03f, 0.9f);
            m_Tick = MakeClick("Tick", 2600f, 0.012f, 0.35f);
            m_Disc.gameObject.SetActive(false);
        }

        // ---------- Practice target (cushion / air) ----------

        /// <summary>Put a practice disc at a point (e.g. on a cushion), facing away from the user.</summary>
        public void StartPractice(Vector3 top, Vector3 userForward)
        {
            IsPractice = true;
            m_PracticePoint = top;
            m_PracticeForward = Vector3.ProjectOnPlane(userForward, Vector3.up).normalized;
            ResetCount();
            Debug.Log($"CPRCompress: practice target placed at {top} ({CurrentClass} depth {TargetDepth * 100f:F0} cm)");
        }

        /// <summary>Start practice: the disc appears under the hands once they're held still.</summary>
        public void BeginPracticePlacement(Vector3 userForward)
        {
            IsPractice = false;
            IsPlacingPractice = true;
            m_PracticeForward = Vector3.ProjectOnPlane(userForward, Vector3.up).normalized;
            m_PlaceStill ??= new StillnessDetector(0.02f, placeHoldSeconds);
            m_PlaceStill.holdSeconds = placeHoldSeconds;
            m_PlaceStill.Reset();
        }

        void UpdatePracticePlacement()
        {
            // Both hands together (adult) or one hand (child/infant); anchors are wrists/controllers.
            var lOk = leftHand != null && (m_LeftHand == null || m_LeftHand.IsTracked);
            var rOk = rightHand != null && (m_RightHand == null || m_RightHand.IsTracked);
            var adult = CurrentClass == PoseOverlayTest.BodyClass.Adult;
            var title = $"PRACTICE ({CurrentClass}) - place the disc\n" +
                        (adult ? "Both hands interlocked on the cushion (or in the air)" : "One hand on the cushion (or in the air)");
            Vector3 p;
            var apart = lOk && rOk ? Vector3.Distance(leftHand.position, rightHand.position) : 0f;
            if (lOk && rOk && apart < 0.2f) p = 0.5f * (leftHand.position + rightHand.position);
            else if (!adult && (lOk || rOk)) p = rOk ? rightHand.position : leftHand.position;
            else
            {
                m_PlaceStill.Reset();
                PlacementHint = title + "\n- " + (!lOk && !rOk ? "Hands not tracked - hold them in view"
                    : !lOk ? "Left hand not tracked - keep it in view"
                    : !rOk ? "Right hand not tracked - keep it in view"
                    : $"Bring hands together ({apart * 100f:F0} cm apart)");
                return;
            }

            if (!m_PlaceStill.Update(p, Time.deltaTime))
            {
                PlacementHint = title + $"\nHold still... [{new string('#', Mathf.RoundToInt(m_PlaceStill.Progress * 10))}{new string('.', 10 - Mathf.RoundToInt(m_PlaceStill.Progress * 10))}]" +
                                (m_PlaceStill.Drift > 0.012f ? $"\n- moving {m_PlaceStill.Drift * 100f:F1} cm: keep steady" : "");
                return;
            }
            PlacementHint = "";

            IsPlacingPractice = false;
            StartPractice(p - Vector3.up * wristAboveSurface, m_PracticeForward);
        }

        public void StopPractice()
        {
            IsPlacingPractice = false;
            if (!IsPractice) return;
            IsPractice = false;
            Debug.Log($"CPRCompress: practice ended after {CompressionCount} compressions");
        }

        // ---------- Frame used by the ghost hands and the recorder ----------

        /// <summary>Top center of the disc and its orientation (+Z = from the rescuer toward the patient).</summary>
        public bool TryGetDiscFrame(out Vector3 top, out Quaternion rotation)
        {
            top = m_Top; rotation = m_FrameRotation;
            return m_Visible;
        }

        float TargetDepth => CurrentClass switch
        {
            PoseOverlayTest.BodyClass.Infant => infantDepth,
            PoseOverlayTest.BodyClass.Child => childDepth,
            _ => adultDepth,
        };

        float TargetRadius
        {
            get
            {
                // Real patient in Auto mode with a measured height: scale with the body.
                if (!IsPractice && pose != null && pose.mode == PoseOverlayTest.PatientMode.Auto && pose.EstimatedLength > 0.3f)
                    return 0.5f * diameterFraction * pose.EstimatedLength;
                return CurrentClass switch
                {
                    PoseOverlayTest.BodyClass.Infant => infantRadius,
                    PoseOverlayTest.BodyClass.Child => childRadius,
                    _ => adultRadius,
                };
            }
        }

        void Update()
        {
            if (IsPlacingPractice) UpdatePracticePlacement();
            var forward = Vector3.forward;
            m_Visible = !IsPlacingPractice && TryGetTop(out m_Top, out forward);
            if (!m_Visible)
            {
                m_Disc.gameObject.SetActive(false);
                HandsInZone = false;
                CPRHud.Set("3-coach", null);
                return;
            }
            m_FrameRotation = Quaternion.LookRotation(forward, Vector3.up);

            var depth = TargetDepth;
            var radius = TargetRadius;
            // Unity cylinder: height 2, radius 0.5. Top face at the surface, extending `depth` down into the body.
            m_Disc.gameObject.SetActive(true);
            m_Disc.SetPositionAndRotation(m_Top, m_FrameRotation);
            // Unity cylinder: height 2, radius 0.5 -> scale y = thickness / 2. Ring sits on the surface,
            // cap sits on the ring and sinks by capTravel as the push approaches the target depth.
            m_Ring.localScale = new Vector3(2f * radius, 0.5f * ringThickness, 2f * radius);
            m_Ring.localPosition = Vector3.up * (0.5f * ringThickness);
            var capRadius = radius * 0.8f;
            m_Cap.localScale = new Vector3(2f * capRadius, 0.5f * capThickness, 2f * capRadius);
            m_Cap.localPosition = Vector3.up * (ringThickness + 0.5f * capThickness - capTravel * m_Pressed01);

            UpdateCompression(depth);
            m_Mat.color = Color.Lerp(baseColor, reachedColor, m_Intensity);
            Diagnostics();
            UpdateCoachHud();
        }

        bool TryGetTop(out Vector3 top, out Vector3 forward)
        {
            forward = Vector3.forward;
            if (IsPractice)
            {
                top = m_PracticePoint;
                forward = m_PracticeForward;
                return true;
            }
            if (pose == null || !pose.TryGetCompressionPoint(out top)) { top = default; return false; }

            // Rescuer kneels in the box beside the patient and faces across the body.
            Vector3 from;
            if (kneel != null && kneel.TryGetBoxPosition(out var box)) from = box;
            else from = Camera.main != null ? Camera.main.transform.position : top - Vector3.forward;
            var f = Vector3.ProjectOnPlane(top - from, Vector3.up);
            if (f.sqrMagnitude > 1e-4f) forward = f.normalized;
            return true;
        }

        // ---------- Compression measurement ----------

        void UpdateCompression(float targetDepth)
        {
            HandsInZone = TryLowestHandInZone(out var rawY);
            if (!HandsInZone)
            {
                m_Phase = 0; m_PeakY = float.NaN; m_SmoothY = float.NaN;
                m_Pressed01 = Mathf.MoveTowards(m_Pressed01, 0f, Time.deltaTime / Mathf.Max(0.05f, recoilFadeSeconds));
                FadeBack();
                return;
            }

            // Light smoothing so tracking jitter can't start or end a stroke on its own.
            m_SmoothY = float.IsNaN(m_SmoothY) ? rawY : Mathf.Lerp(m_SmoothY, rawY, 0.5f);
            var y = m_SmoothY;

            if (m_Phase == 0)
            {
                // Rising / resting: follow the peak (the recoil height) until the hands start down.
                m_PeakY = float.IsNaN(m_PeakY) ? y : Mathf.Max(m_PeakY, y);
                if (m_PeakY - y > strokeStart)
                {
                    m_Phase = 1;
                    m_RepPeakY = m_PeakY; m_TroughY = y; m_ReachedThisRep = false;
                    m_RepHeadStartY = m_RepHeadMinY = HeadY; m_RepHeadLagSum = 0f; m_RepFrames = 0;
                    // Recoil check: did the hands come all the way back up since the last stroke?
                    m_LastRecoilOk = float.IsNaN(m_PrevPeakY) || m_RepPeakY >= m_PrevPeakY - recoilTolerance;
                }
                m_Pressed01 = Mathf.MoveTowards(m_Pressed01, 0f, Time.deltaTime / Mathf.Max(0.05f, recoilFadeSeconds));
                FadeBack();
                return;
            }

            // Pushing down: follow the trough; the stroke ends once the hands come back up a little.
            m_TroughY = Mathf.Min(m_TroughY, y);
            var pushed = m_RepPeakY - y;
            m_RepHeadMinY = Mathf.Min(m_RepHeadMinY, HeadY);
            m_RepHeadLagSum += HeadLag; m_RepFrames++;
            if (pushed >= targetDepth && !m_ReachedThisRep) { m_ReachedThisRep = true; if (clickSounds) m_Audio.PlayOneShot(m_Click); }
            m_Pressed01 = Mathf.Clamp01(pushed / targetDepth);
            m_Intensity = Mathf.Max(m_Intensity, m_ReachedThisRep ? 1f : 0.6f * Mathf.Clamp01(pushed / targetDepth));

            if (y - m_TroughY > strokeStart)
            {
                var depth = m_RepPeakY - m_TroughY;
                if (depth >= minStroke) EndRep(depth, targetDepth);
                if (clickSounds && m_ReachedThisRep) m_Audio.PlayOneShot(m_Tick);
                m_PrevPeakY = m_RepPeakY;
                m_Phase = 0;
                m_PeakY = y; // start looking for the next peak from here
            }
        }

        void EndRep(float depth, float targetDepth)
        {
            // Posture: with straight, locked arms the upper body (head) drops along with the hands,
            // and the head is roughly above the hands. Bent elbows = hands move, head stays put.
            var headFollow = depth > 0.005f ? (m_RepHeadStartY - m_RepHeadMinY) / depth : 1f;
            var lag = m_RepFrames > 0 ? m_RepHeadLagSum / m_RepFrames : 0f;
            LastPostureFeedback = lag > maxShoulderLag ? $"move shoulders over your hands ({lag * 100f:F0} cm behind)"
                                : headFollow < minHeadFollow ? "lock your elbows - push with your body"
                                : !m_LastRecoilOk ? "let the chest come all the way up"
                                : "arms straight - good";
            m_LastCoachTime = Time.time;

            var tooDeep = depth > targetDepth + tooDeepMargin;
            LastCompressionFeedback = depth < targetDepth ? $"last {depth * 100f:F1} cm - push deeper"
                                    : tooDeep ? $"last {depth * 100f:F1} cm - a little less deep"
                                    : $"last {depth * 100f:F1} cm - good depth";
            CompressionCount++;
            var now = Time.realtimeSinceStartup;
            var rate = m_LastRepTime > 0f ? 60f / (now - m_LastRepTime) : 0f;
            m_LastRepTime = now;
            if (rate > 0f && rate < 100f) LastCompressionFeedback += " - faster";
            else if (rate > 120f) LastCompressionFeedback += " - slower";
            Debug.Log($"CPRCompress: #{CompressionCount}{(IsPractice ? " (practice)" : "")} depth {depth * 100f:F1} cm " +
                      $"(target {targetDepth * 100f:F0}, {(depth < targetDepth ? "too shallow" : tooDeep ? "too deep" : "good")})" +
                      (rate > 0f ? $", rate {rate:F0}/min" : "") +
                      $", recoil {(m_LastRecoilOk ? "full" : "incomplete")}, head follow {headFollow:F2}, shoulder lag {lag * 100f:F0} cm -> {LastPostureFeedback}");
        }

        float HeadY => Camera.main != null ? Camera.main.transform.position.y : 0f;

        /// <summary>How far the head is BEHIND the hands (toward the rescuer), horizontally, in m.</summary>
        float HeadLag
        {
            get
            {
                if (Camera.main == null) return 0f;
                var local = Quaternion.Inverse(m_FrameRotation) * (Camera.main.transform.position - m_Top);
                return -local.z; // +z points from the rescuer toward the patient
            }
        }

        void UpdateCoachHud()
        {
            var recent = Time.time - m_LastCoachTime < 4f;
            if (!m_Visible || (!HandsInZone && !recent)) { CPRHud.Set("3-coach", null); return; }
            CPRHud.Set("3-coach", $"Compressions: {CompressionCount}\n{(LastCompressionFeedback == "" ? "push 5 cm, 100-120 per minute" : LastCompressionFeedback)}" +
                                  (LastPostureFeedback == "" ? "" : $"\n{LastPostureFeedback}"));
        }

        void FadeBack()
        {
            m_Intensity = recoilFadeSeconds <= 0f ? 0f : Mathf.MoveTowards(m_Intensity, 0f, Time.deltaTime / recoilFadeSeconds);
        }

        /// <summary>Lowest TRACKED hand inside the zone above the disc. Untracked hands are frozen in place, so they're ignored.</summary>
        bool TryLowestHandInZone(out float y)
        {
            y = float.PositiveInfinity;
            var found = false;
            Check(leftHand, m_LeftHand, ref y, ref found);
            Check(rightHand, m_RightHand, ref y, ref found);
            return found;
        }

        void Check(Transform anchor, OVRHand hand, ref float y, ref bool found)
        {
            if (anchor == null || !anchor.gameObject.activeInHierarchy) return;
            // With hand tracking, only trust tracked hands. With controllers (no OVRHand tracking), use the anchor.
            if (hand != null && OVRInput.GetActiveController() != OVRInput.Controller.Touch && !hand.IsTracked) return;
            if (!InZone(anchor.position)) return;
            y = Mathf.Min(y, anchor.position.y);
            found = true;
        }

        bool InZone(Vector3 p)
        {
            var flat = new Vector2(p.x - m_Top.x, p.z - m_Top.z);
            return flat.magnitude <= zoneRadius && p.y <= m_Top.y + zoneHeight && p.y >= m_Top.y - 0.15f;
        }

        /// <summary>Once a second while hands are near the disc: is tracking holding up?</summary>
        void Diagnostics()
        {
            var nearL = leftHand != null && InZone(leftHand.position);
            var nearR = rightHand != null && InZone(rightHand.position);
            if (!nearL && !nearR && !HandsInZone) return;
            m_DiagFrames++;
            if (m_LeftHand != null && m_LeftHand.IsTracked) m_DiagTrackedL++;
            if (m_RightHand != null && m_RightHand.IsTracked) m_DiagTrackedR++;
            if (HandsInZone) m_DiagInZone++;
            if (Time.realtimeSinceStartup - m_LastDiag < 1f) return;
            m_LastDiag = Time.realtimeSinceStartup;
            Debug.Log($"CPRHands: tracked L {100 * m_DiagTrackedL / m_DiagFrames}% (conf {m_LeftHand?.HandConfidence}), " +
                      $"R {100 * m_DiagTrackedR / m_DiagFrames}% (conf {m_RightHand?.HandConfidence}), usable on disc {100 * m_DiagInZone / m_DiagFrames}%, " +
                      $"stroke {(m_Phase == 1 ? (m_RepPeakY - m_SmoothY) * 100f : 0f):F1} cm");
            m_DiagFrames = m_DiagTrackedL = m_DiagTrackedR = m_DiagInZone = 0;
        }

        Transform MakeCylinder(string name, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(m_Disc, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        /// <summary>A short decaying sine "click" - generated, so no audio asset is needed.</summary>
        static AudioClip MakeClick(string name, float freq, float seconds, float volume)
        {
            const int rate = 44100;
            var n = Mathf.CeilToInt(rate * seconds);
            var data = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / rate;
                data[i] = volume * Mathf.Sin(2f * Mathf.PI * freq * t) * Mathf.Exp(-t / (seconds * 0.25f));
            }
            var clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        public void ResetCount()
        {
            CompressionCount = 0;
            m_LastRepTime = -1f;
        }
    }
}
