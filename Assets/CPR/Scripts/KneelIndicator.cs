using Meta.XR;
using UnityEngine;
using UnityEngine.Events;

namespace CPR
{
    /// <summary>
    /// Shows a highlighted box on the floor beside the patient's chest, on the rescuer's side,
    /// and hides it once the rescuer kneels inside it.
    ///
    /// Flow: Searching (needs a lying torso) -> Placing (smooths toward the target)
    ///       -> Locked (stays put even if pose tracking drops) -> Kneeling (box hidden, event fired).
    /// </summary>
    public class KneelIndicator : MonoBehaviour
    {
        public enum State { Searching, Placing, Locked, Kneeling }

        [Header("References")]
        public PoseOverlayTest pose;
        public EnvironmentRaycastManager raycastManager;
        [Tooltip("The user's head. Defaults to Camera.main (CenterEyeAnchor).")]
        public Transform head;
        [Tooltip("Editor only: a stand-in head you can drag around in Play mode to test kneeling.")]
        public Transform editorHead;

        [Header("Power")]
        [Tooltip("Stop pose inference once the box locks (the patient isn't moving). Saves battery.")]
        public bool freezePoseOnLock = true;

        [Header("Placement")]
        [Tooltip("Distance from the body midline to the center of the kneel box (m).")]
        public float sideOffset = 0.45f;
        [Tooltip("Box size on the floor: x = across the body, y = along the body (m).")]
        public Vector2 boxSize = new Vector2(0.4f, 0.6f);
        [Tooltip("Torso must be within this many degrees of horizontal to count as lying down.")]
        public float maxTorsoTiltDegrees = 35f;
        [Tooltip("Seconds the target must stay steady before the box locks in place.")]
        public float lockAfterSeconds = 1.0f;
        [Tooltip("Movement under this (m) counts as steady.")]
        public float steadyTolerance = 0.06f;

        [Header("Kneel detection")]
        [Tooltip("Extra margin around the box when checking if the user is in it (m).")]
        public float zoneMargin = 0.2f;
        [Tooltip("Head must be lower than this above the floor to count as kneeling (m).")]
        public float kneelHeadHeight = 1.2f;
        [Tooltip("Seconds the kneel must hold before the box disappears (prevents flicker).")]
        public float kneelHoldSeconds = 0.5f;

        [Header("Look")]
        public Material boxMaterial;
        public Color color = new Color(0.1f, 1f, 0.4f, 1f);
        public float pulseSpeed = 2.5f;

        [Header("Kneel here text")]
        public string kneelText = "Kneel here";
        [Tooltip("Height of the text above the box (m).")]
        public float textHeight = 0.35f;
        public Color textColor = Color.white;

        [Header("Events")]
        public UnityEvent onUserKneeled;

        public State CurrentState { get; private set; } = State.Searching;

        const int L_SHOULDER = 11, R_SHOULDER = 12, L_HIP = 23, R_HIP = 24;

        Transform m_Box;
        TextMesh m_KneelText;
        LineRenderer m_Outline;
        Material m_FillMat;
        Vector3 m_TargetPos, m_SmoothedPos;
        Quaternion m_TargetRot;
        float m_SteadyTime, m_KneelTime, m_FloorY, m_ShownAt, m_LockedAt;

        Transform Head => Application.isEditor && editorHead != null ? editorHead : head;

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            BuildBox();
            SetBoxVisible(false);
        }

        void Update()
        {
            switch (CurrentState)
            {
                case State.Searching:
                case State.Placing:
                    UpdatePlacement();
                    break;
                case State.Locked:
                    UpdateKneelCheck();
                    break;
            }
            if (m_Box.gameObject.activeSelf) { Pulse(); UpdateKneelText(); }
        }

        // ---------- Placement ----------

        void UpdatePlacement()
        {
            if (!TryComputeTarget(out var pos, out var rot))
            {
                // No confirmed lying person (or lost before locking): hide and start over.
                m_SteadyTime = 0;
                if (CurrentState == State.Placing) { CurrentState = State.Searching; SetBoxVisible(false); Debug.Log("CPRKneel: lost before lock, box hidden"); }
                return;
            }

            if (CurrentState == State.Searching)
            {
                m_SmoothedPos = pos;
                CurrentState = State.Placing;
                SetBoxVisible(true);
                m_ShownAt = Time.realtimeSinceStartup;
                Debug.Log("CPRKneel: box shown, placing");
            }

            var moved = Vector3.Distance(pos, m_SmoothedPos);
            m_SmoothedPos = Vector3.Lerp(m_SmoothedPos, pos, 1f - Mathf.Exp(-6f * Time.deltaTime));
            m_TargetRot = rot;
            m_Box.SetPositionAndRotation(m_SmoothedPos, m_TargetRot);

            m_SteadyTime = moved < steadyTolerance ? m_SteadyTime + Time.deltaTime : 0f;
            if (m_SteadyTime >= lockAfterSeconds)
            {
                CurrentState = State.Locked;
                m_LockedAt = Time.realtimeSinceStartup;
                Debug.Log($"CPRKneel: locked after {m_LockedAt - m_ShownAt:F1}s at {m_SmoothedPos} (floor y={m_FloorY:F2}), patient {pose.EffectiveClass} est. {pose.EstimatedLength:F2} m");
                if (freezePoseOnLock) pose.RequestFreeze(); // patient isn't moving: stop inference once their size is measured
            }
        }

        /// <summary>Box pose on the floor beside the chest, on the side of the body nearest the user.</summary>
        bool TryComputeTarget(out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (pose == null || Head == null || !pose.IsLyingConfirmed) return false;
            if (!pose.TryGetJoint(L_SHOULDER, out var ls) || !pose.TryGetJoint(R_SHOULDER, out var rs) ||
                !pose.TryGetJoint(L_HIP, out var lh) || !pose.TryGetJoint(R_HIP, out var rh))
                return false;

            var shoulderMid = 0.5f * (ls + rs);
            var hipMid = 0.5f * (lh + rh);
            var torso = hipMid - shoulderMid;

            // Only a person lying down gets a kneel box.
            var tilt = Vector3.Angle(torso, Vector3.ProjectOnPlane(torso, Vector3.up));
            if (torso.sqrMagnitude < 0.01f || tilt > maxTorsoTiltDegrees) return false;

            var chest = pose.TryGetCompressionPoint(out var cp) ? cp : Vector3.Lerp(shoulderMid, hipMid, 0.3f);

            // Body axis and "across the body" direction, flattened onto the floor.
            var along = Vector3.ProjectOnPlane(torso, Vector3.up).normalized;
            var across = Vector3.Cross(Vector3.up, along).normalized;

            // Kneel on the side the rescuer is already on.
            var toUser = Vector3.ProjectOnPlane(Head.position - chest, Vector3.up);
            if (Vector3.Dot(toUser, across) < 0) across = -across;

            var guess = chest + across * sideOffset;
            m_FloorY = FindFloorY(guess, Mathf.Min(ls.y, rs.y, lh.y, rh.y));
            pos = new Vector3(guess.x, m_FloorY + 0.005f, guess.z); // just above the floor to avoid z-fighting
            rot = Quaternion.LookRotation(along, Vector3.up);         // box's local z runs along the body
            return true;
        }

        float FindFloorY(Vector3 above, float lowestJointY)
        {
            if (Application.isEditor && pose.editorTestImage != null) return pose.floorY;
            // Prefer a real depth hit straight down; fall back to the tracking-space floor / lowest joint.
            var ray = new Ray(new Vector3(above.x, Mathf.Max(above.y, 0.5f) + 0.5f, above.z), Vector3.down);
            if (raycastManager != null && raycastManager.Raycast(ray, out var hit, 3f) && Mathf.Abs(hit.point.y - lowestJointY) < 0.5f)
                return hit.point.y;
            return Mathf.Min(pose.FloorY, lowestJointY - 0.1f);
        }

        // ---------- Kneel detection ----------

        void UpdateKneelCheck()
        {
            var inZone = IsUserKneelingInZone(Head.position, m_Box.position, m_Box.rotation, boxSize, m_FloorY);
            m_KneelTime = inZone ? m_KneelTime + Time.deltaTime : 0f;
            if (m_KneelTime < kneelHoldSeconds) return;

            CurrentState = State.Kneeling;
            SetBoxVisible(false);
            Debug.Log($"CPRKneel: user kneeling in position, {Time.realtimeSinceStartup - m_LockedAt:F1}s after lock (head {Head.position.y - m_FloorY:F2} m above floor)");
            onUserKneeled?.Invoke();
        }

        /// <summary>
        /// Decide whether the rescuer is kneeling in the box.
        /// headPos: user's head (world). boxCenter/boxRotation: the box on the floor (local z = along the body).
        /// boxSize: x = across the body, y = along the body. floorY: floor height under the box.
        /// </summary>
        bool IsUserKneelingInZone(Vector3 headPos, Vector3 boxCenter, Quaternion boxRotation, Vector2 size, float floorY)
        {
            // TODO(you): return true when the user is kneeling in the box.
            // Fields you can use: zoneMargin, kneelHeadHeight.
            // Tip: Quaternion.Inverse(boxRotation) * (headPos - boxCenter) gives the head position in box space.
            var headInBoxSpace = Quaternion.Inverse(boxRotation) * (headPos - boxCenter);
            if (Mathf.Abs(headInBoxSpace.x) > size.x * 0.5f + zoneMargin || Mathf.Abs(headInBoxSpace.z) > size.y * 0.5f + zoneMargin)
                return false;
            if (headInBoxSpace.y > kneelHeadHeight) // head still high = standing / bending, not kneeling
                return false;
            return true;
        }

        // ---------- Visuals ----------

        void BuildBox()
        {
            m_Box = new GameObject("KneelBox").transform;
            m_Box.SetParent(transform, false);

            // Translucent fill (a flat quad lying on the floor).
            var fill = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(fill.GetComponent<Collider>());
            fill.name = "Fill";
            fill.transform.SetParent(m_Box, false);
            fill.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            fill.transform.localScale = new Vector3(boxSize.x, boxSize.y, 1f);
            m_FillMat = new Material(boxMaterial);
            fill.GetComponent<Renderer>().sharedMaterial = m_FillMat;

            // Bright outline.
            m_Outline = new GameObject("Outline").AddComponent<LineRenderer>();
            m_Outline.transform.SetParent(m_Box, false);
            m_Outline.useWorldSpace = false;
            m_Outline.loop = true;
            m_Outline.positionCount = 4;
            m_Outline.startWidth = m_Outline.endWidth = 0.02f;
            float hx = boxSize.x * 0.5f, hz = boxSize.y * 0.5f;
            m_Outline.SetPositions(new[]
            {
                new Vector3(-hx, 0.002f, -hz), new Vector3(hx, 0.002f, -hz),
                new Vector3(hx, 0.002f, hz), new Vector3(-hx, 0.002f, hz),
            });
            m_Outline.sharedMaterial = boxMaterial;
            m_Outline.startColor = m_Outline.endColor = color;

            // "Kneel here" floating above the box (hides with it); positioned and turned to face the user each frame.
            var textGo = new GameObject("KneelHereText");
            textGo.transform.SetParent(m_Box, false);
            m_KneelText = textGo.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_KneelText.font = font;
            textGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            m_KneelText.text = kneelText;
            m_KneelText.fontSize = 64;
            m_KneelText.characterSize = 0.0138f;
            m_KneelText.anchor = TextAnchor.MiddleCenter;
            m_KneelText.fontStyle = FontStyle.Bold;
            m_KneelText.color = textColor;
        }

        void UpdateKneelText()
        {
            var bob = 0.02f * Mathf.Sin(Time.time * 2f);
            m_KneelText.transform.position = m_Box.position + Vector3.up * (textHeight + bob);
            var h = Head;
            if (h == null) return;
            // Face the user, staying upright.
            var away = Vector3.ProjectOnPlane(m_KneelText.transform.position - h.position, Vector3.up);
            if (away.sqrMagnitude > 1e-4f) m_KneelText.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        void Pulse()
        {
            var a = Mathf.Lerp(0.15f, 0.45f, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));
            m_FillMat.color = new Color(color.r, color.g, color.b, a);
        }

        void SetBoxVisible(bool on) { if (m_Box != null) m_Box.gameObject.SetActive(on); } // may be called before Start

        /// <summary>Where the rescuer should kneel (box center on the floor), once placed.</summary>
        public bool TryGetBoxPosition(out Vector3 position)
        {
            position = m_Box != null ? m_Box.position : default;
            return CurrentState != State.Searching && m_Box != null;
        }

        /// <summary>Start over (e.g. a new scan). Hooked up later to the voice trigger.</summary>
        public void ResetIndicator()
        {
            CurrentState = State.Searching;
            m_SteadyTime = m_KneelTime = 0;
            SetBoxVisible(false);
            if (pose != null) pose.ResetTracking();
        }
    }
}
