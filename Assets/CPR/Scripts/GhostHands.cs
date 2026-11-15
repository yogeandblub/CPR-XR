using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Shows the correct CPR hand position on the button as translucent "ghost hands" (Meta's own hand
    /// models held in a pose recorded with <see cref="HandPoseRecorder"/>), ONE STEP AT A TIME:
    ///   Adult  - step 1: the bottom hand (heel on the button); once the rescuer's hand matches it, it
    ///            disappears and step 2 appears: the top hand, interlocked. When both are matched, both
    ///            are gone. If a hand drifts off, the ghost for the first unmet step comes back.
    ///   Child / Infant - a single step (one hand / two fingers).
    /// "Matched" = a tracked wrist within <see cref="matchTolerance"/> of the ghost's wrist.
    /// In Default & Practice the ghosts only appear after the rescuer kneels. Log tag: "CPRForm".
    /// </summary>
    public class GhostHands : MonoBehaviour
    {
        public PoseOverlayTest pose;
        public CompressionDisc disc;
        [Tooltip("Translucent material for the ghost hands (e.g. the aura glow).")]
        public Material ghostMaterial;

        [Header("Guidance flow")]
        public KneelIndicator kneel;
        [Tooltip("Only show the ghost hands once the rescuer has knelt (Default & Practice). Off in Pose mode.")]
        public bool requireKneel = true;
        [Tooltip("A hand matches its ghost when its wrist is within this distance (m).")]
        public float matchTolerance = 0.05f;
        [Tooltip("Extra distance before a matched hand counts as off again (stops flicker).")]
        public float breakMargin = 0.02f;
        [Tooltip("Seconds a step must stay matched (or unmatched) before the ghosts change.")]
        public float formHoldSeconds = 0.3f;
        [Tooltip("Fade time for each ghost (s).")]
        public float fadeSeconds = 0.25f;

        [Header("Interlocked hands (occlusion-tolerant)")]
        [Tooltip("Matching is horizontal (across the chest). Wrist height only has to be within this band of the ghost (m): pushing down during compressions isn't 'off'.")]
        public Vector2 heightBand = new Vector2(-0.14f, 0.10f);
        [Tooltip("Once both hands are in place, it stays complete until the wrists separate by more than this (m).")]
        public float separateToReset = 0.25f;
        [Tooltip("Two-hand step also counts as done when both wrists are over the button and this close together (m).")]
        public float interlockWristDistance = 0.12f;
        [Tooltip("Keep a hand's last position this long (s) after it loses tracking (interlocked fingers hide each other).")]
        public float trackingGraceSeconds = 1.5f;

        /// <summary>Both hands in place and held (sticky until the hands separate).</summary>
        bool m_Locked;
        Vector3 m_LastL, m_LastR; float m_LostL = -1f, m_LostR = -1f;

        /// <summary>True once every step is matched (both hands in place for adults).</summary>
        public bool FormMatched => m_Steps.Count > 0 && (m_Locked || m_Step >= m_Steps.Count);

        class Step { public HandPoseEntry entry; public GameObject hand; public Material mat; public float visibility; }
        readonly List<Step> m_Steps = new List<Step>();
        int m_Step, m_PendingStep; float m_PendingTimer;
        OVRHand m_RealL, m_RealR;
        PoseOverlayTest.BodyClass m_ShownClass = PoseOverlayTest.BodyClass.Unknown;
        HandPoseFile m_File;
        Color m_BaseColor = Color.white;
        readonly HashSet<PoseOverlayTest.BodyClass> m_Hidden = new HashSet<PoseOverlayTest.BodyClass>();

        void Start()
        {
            if (ghostMaterial != null && ghostMaterial.HasProperty("_Color")) m_BaseColor = ghostMaterial.color;
            foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var t = ((OVRSkeleton.IOVRSkeletonDataProvider)h).GetSkeletonType();
                if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) m_RealL = h; else m_RealR = h;
            }
        }

        void Update()
        {
            // Default/Practice: ghosts appear only after the rescuer kneels in the kneel spot.
            var kneeling = kneel == null || kneel.CurrentState == KneelIndicator.State.Kneeling;
            if (disc == null || !disc.TryGetDiscFrame(out var origin, out var rotation) || (requireKneel && !kneeling))
            {
                ShowNone();
                return;
            }

            var cls = disc.CurrentClass;
            if (cls != m_ShownClass) Load(cls);
            if (m_Steps.Count == 0) { ShowNone(); return; }

            foreach (var s in m_Steps)
                s.hand.transform.SetPositionAndRotation(origin + rotation * s.entry.wristPosition, rotation * s.entry.wristRotation);

            UpdateStep(origin, rotation);

            // Only the ghost for the current (first unmet) step is visible; the others fade out.
            for (var i = 0; i < m_Steps.Count; i++)
            {
                var s = m_Steps[i];
                s.visibility = Mathf.MoveTowards(s.visibility, i == m_Step ? 1f : 0f, Time.deltaTime / Mathf.Max(0.01f, fadeSeconds));
                var c = m_BaseColor; c.a *= s.visibility;
                if (s.mat != null) s.mat.color = c;
                var on = s.visibility > 0.02f;
                if (s.hand.activeSelf != on) s.hand.SetActive(on);
            }
        }

        // ---------- Step logic ----------

        void UpdateStep(Vector3 top, Quaternion rot)
        {
            // Hand positions with an occlusion grace period: a hand that just lost tracking keeps its last spot.
            var reals = new List<Vector3>(2);
            var trackedCount = 0;
            if (GraceHand(m_RealL, ref m_LastL, ref m_LostL, out var pl, ref trackedCount)) reals.Add(pl);
            if (GraceHand(m_RealR, ref m_LastR, ref m_LostR, out var pr, ref trackedCount)) reals.Add(pr);

            // ---- Sticky "all hands in place": only released when the hands clearly separate. ----
            if (m_Locked)
            {
                var bothTracked = m_RealL != null && m_RealR != null && m_RealL.IsTracked && m_RealR.IsTracked;
                var apart = bothTracked ? Vector3.Distance(m_RealL.transform.position, m_RealR.transform.position) : 0f;
                var singleHandOff = m_Steps.Count == 1 && reals.Count > 0 && HorizontalOff(top, rot, m_Steps[0], reals, out _) > separateToReset;
                if (apart > separateToReset || singleHandOff)
                {
                    m_Locked = false; m_Step = 0; m_PendingStep = 0; m_PendingTimer = 0f;
                    Debug.Log($"CPRForm: hands separated ({(singleHandOff ? "hand moved off" : $"{apart * 100f:F0} cm apart")}) - showing the first ghost again");
                }
                else
                {
                    CPRHud.Set("4-form", "GOOD HAND POSITION - keep going");
                    return;
                }
            }

            var met = CountMet(top, rot, reals, out var offBy);
            // Two-hand fallback: both wrists over the button and close together = interlocked, even if the
            // top hand isn't exactly where the ghost is (fingers occlude each other when clasped).
            if (m_Steps.Count == 2 && met == 1 && reals.Count == 2 && Vector3.Distance(reals[0], reals[1]) <= interlockWristDistance &&
                HorizontalOff(top, rot, m_Steps[0], reals, out _) <= matchTolerance + breakMargin)
                met = 2;

            // Never step backwards while a hand is only being held by the grace period (it's probably just hidden).
            var target = met;
            if (target < m_Step && trackedCount < reals.Count) target = m_Step;

            // Debounce: the step only changes after the new value has held for formHoldSeconds.
            if (target == m_Step) { m_PendingTimer = 0f; }
            else
            {
                if (target != m_PendingStep) { m_PendingStep = target; m_PendingTimer = 0f; }
                m_PendingTimer += Time.deltaTime;
                if (m_PendingTimer >= formHoldSeconds)
                {
                    Debug.Log($"CPRForm: step {m_Step} -> {target} of {m_Steps.Count}" + (target >= m_Steps.Count ? " (all hands in place - locked until hands separate)" : ""));
                    m_Step = target;
                    m_PendingTimer = 0f;
                    if (m_Step >= m_Steps.Count) m_Locked = true;
                }
            }

            string hint;
            var two = m_Steps.Count >= 2;
            if (m_Step >= m_Steps.Count) hint = "GOOD HAND POSITION - keep going";
            else if (reals.Count == 0) hint = "Hands not tracked - keep them in view";
            else if (m_Step == 0) hint = (two ? "Place your FIRST hand like the ghost (heel of the hand)" : "Place your hand like the ghost")
                                        + (float.IsInfinity(offBy) ? "" : $"\n{offBy * 100f:F0} cm off");
            else hint = "Now lay your OTHER hand on top, fingers interlocked" + (float.IsInfinity(offBy) ? "" : $"\n{offBy * 100f:F0} cm off");
            CPRHud.Set("4-form", hint);
        }

        /// <summary>Tracked position, or the last tracked position for trackingGraceSeconds after tracking drops.</summary>
        bool GraceHand(OVRHand h, ref Vector3 last, ref float lostAt, out Vector3 pos, ref int trackedCount)
        {
            pos = default;
            if (h == null) return false;
            if (h.IsTracked) { last = h.transform.position; lostAt = -1f; pos = last; trackedCount++; return true; }
            if (lostAt < 0f) lostAt = Time.time;
            if (Time.time - lostAt > trackingGraceSeconds || last == Vector3.zero) return false;
            pos = last;
            return true;
        }

        /// <summary>Horizontal distance (across the chest) from the best hand to a ghost's wrist; inf if height is way off.</summary>
        float HorizontalOff(Vector3 top, Quaternion rot, Step step, List<Vector3> reals, out int bestIndex)
        {
            bestIndex = -1;
            var best = float.PositiveInfinity;
            for (var r = 0; r < reals.Count; r++)
            {
                var d = HorizontalDistance(top, rot, step, reals[r]);
                if (d < best) { best = d; bestIndex = r; }
            }
            return best;
        }

        float HorizontalDistance(Vector3 top, Quaternion rot, Step step, Vector3 real)
        {
            var local = Quaternion.Inverse(rot) * (real - top) - step.entry.wristPosition; // in the button's frame
            if (local.y < heightBand.x || local.y > heightBand.y) return float.PositiveInfinity;
            return new Vector2(local.x, local.z).magnitude;
        }

        /// <summary>
        /// Number of leading steps matched. Step i needs a hand (not already used by an earlier step)
        /// within tolerance of ghost i, measured across the chest. offBy = distance to the first unmet ghost.
        /// </summary>
        int CountMet(Vector3 top, Quaternion rot, List<Vector3> reals, out float offBy)
        {
            offBy = float.PositiveInfinity;
            var used = new bool[reals.Count];
            for (var i = 0; i < m_Steps.Count; i++)
            {
                var tol = i < m_Step ? matchTolerance + breakMargin : matchTolerance;
                var best = -1; var bestD = float.PositiveInfinity;
                for (var r = 0; r < reals.Count; r++)
                {
                    if (used[r]) continue;
                    var d = HorizontalDistance(top, rot, m_Steps[i], reals[r]);
                    if (d < bestD) { bestD = d; best = r; }
                }
                if (best < 0 || bestD > tol) { offBy = bestD; return i; }
                used[best] = true;
            }
            return m_Steps.Count;
        }

        // ---------- Loading / building ----------

        /// <summary>Reload the pose for the current class (call after recording a new one). Un-hides it.</summary>
        public void Reload()
        {
            m_Hidden.Clear();
            m_ShownClass = PoseOverlayTest.BodyClass.Unknown;
        }

        /// <summary>Hide a class's pose (it was cleared for re-recording) until the next Reload.</summary>
        public void Hide(PoseOverlayTest.BodyClass cls)
        {
            m_Hidden.Add(cls);
            m_ShownClass = PoseOverlayTest.BodyClass.Unknown;
        }

        void Load(PoseOverlayTest.BodyClass cls)
        {
            m_ShownClass = cls;
            foreach (var s in m_Steps) { if (s.hand != null) Destroy(s.hand); if (s.mat != null) Destroy(s.mat); }
            m_Steps.Clear();
            m_Step = 0; m_PendingStep = 0; m_PendingTimer = 0f; m_Locked = false;

            var key = cls == PoseOverlayTest.BodyClass.Unknown ? PoseOverlayTest.BodyClass.Adult : cls;
            m_File = m_Hidden.Contains(key) ? null : HandPoseFile.Load(key);
            if (m_File == null) { Debug.Log($"CPRHands: no recorded pose for {cls} yet"); return; }

            // File order = step order (the recorder saves the bottom hand first, then the top hand).
            foreach (var e in m_File.hands)
            {
                var mat = ghostMaterial != null ? new Material(ghostMaterial) : null;
                m_Steps.Add(new Step { entry = e, mat = mat, hand = BuildHand(e, $"GhostHand_{(e.isLeft ? "L" : "R")}", mat) });
            }
            Debug.Log($"CPRHands: {cls} pose, {m_Steps.Count} step(s), recorded {m_File.recordedAt}");
        }

        GameObject BuildHand(HandPoseEntry entry, string name, Material mat)
        {
            // Built inactive so the skeleton type is set before OVRSkeleton.Awake looks for its provider.
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(transform, false);

            var provider = go.AddComponent<GhostHandProvider>();
            provider.SetPose(entry);

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMaterial = mat;
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var skeleton = go.AddComponent<OVRSkeleton>();
            // OVRSkeleton's setter is internal; its serialized field is what the Inspector sets.
            typeof(OVRSkeleton).GetField("_skeletonType", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(skeleton, (OVRSkeleton.SkeletonType)entry.skeletonType);

            go.AddComponent<OVRMesh>();          // reads the mesh type from the provider
            go.AddComponent<OVRMeshRenderer>();  // skins Meta's hand mesh to the skeleton
            go.SetActive(true);
            return go;
        }

        void ShowNone()
        {
            foreach (var s in m_Steps) { s.visibility = 0f; if (s.hand != null && s.hand.activeSelf) s.hand.SetActive(false); }
            m_Step = 0; m_PendingStep = 0; m_PendingTimer = 0f; m_Locked = false;
            CPRHud.Set("4-form", null);
        }
    }
}
