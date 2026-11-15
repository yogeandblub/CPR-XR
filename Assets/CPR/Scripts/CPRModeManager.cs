using UnityEngine;

namespace CPR
{
    /// <summary>
    /// The app's three modes, chosen from the hamburger menu (left controller menu button, or the
    /// left-hand palm-up menu pinch):
    ///   Default  - scan for a real person lying down: button disc on the chest, kneel spot, then
    ///              ghost arms/hands after kneeling that fade once the rescuer's form matches.
    ///   Practice - same experience on a grabbable glowing mannequin (Adult / Child / Infant);
    ///              camera inference is off.
    ///   Pose     - TEMPORARY: re-record the ghost hand poses on a practice disc.
    /// Menu buttons call the public methods below (wired in the Inspector by CPR/Configure Modes).
    /// Log tag: "CPRMode".
    /// </summary>
    public class CPRModeManager : MonoBehaviour
    {
        public enum Mode { Default, Practice, Pose }

        [Header("References")]
        public PoseOverlayTest pose;
        public KneelIndicator kneel;
        public CompressionDisc disc;
        public GhostHands ghostHands;
        public HandPoseRecorder recorder;
        public PracticeMannequin mannequin;
        [Tooltip("Old pinch / A-B-X test shortcuts. Disabled: everything is in the menu now.")]
        public CPRDemoControls legacyControls;
        [Tooltip("Root of the menu panel (world-space canvas).")]
        public GameObject menu;

        [Header("Button colors")]
        [Tooltip("Default mode: blue button turning red at full depth (high visibility on a real person).")]
        public Color defaultBaseColor = new Color(0.15f, 0.45f, 1f, 0.75f);
        public Color defaultReachedColor = new Color(1f, 0.15f, 0.15f, 0.9f);
        Color m_PracticeBase, m_PracticeReached;
        bool m_ColorsSaved;

        [Header("Menu placement")]
        public float menuDistance = 0.45f;
        public float menuBelowEye = 0.12f;

        public Mode CurrentMode { get; private set; } = Mode.Default;

        void Start()
        {
            if (disc != null) { m_PracticeBase = disc.baseColor; m_PracticeReached = disc.reachedColor; m_ColorsSaved = true; }
            if (legacyControls != null) legacyControls.enabled = false;
            if (menu != null) menu.SetActive(false);
            SetDefault();
        }

        void Update()
        {
            // Left controller menu (hamburger) button - also fired by the left-hand menu pinch.
            if (OVRInput.GetDown(OVRInput.Button.Start)) ToggleMenu();
        }

        // ---------- Menu ----------

        public void ToggleMenu()
        {
            if (menu == null) return;
            var open = !menu.activeSelf;
            menu.SetActive(open);
            if (open) PlaceMenu();
        }

        public void CloseMenu() { if (menu != null) menu.SetActive(false); }

        void PlaceMenu()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.up;
            fwd.Normalize();
            menu.transform.position = cam.transform.position + fwd * menuDistance - Vector3.up * menuBelowEye;
            // Canvas faces the user; tilted back slightly so it's easy to poke.
            menu.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(15f, 0f, 0f);
        }

        // ---------- Modes (menu buttons) ----------

        public void SetDefault()
        {
            LeavePose();
            if (mannequin != null) mannequin.Despawn();
            if (pose != null) pose.mode = PoseOverlayTest.PatientMode.Auto;
            if (disc != null) disc.StopPractice();
            if (ghostHands != null) ghostHands.requireKneel = true;
            if (kneel != null) kneel.ResetIndicator();
            Enter(Mode.Default, "DEFAULT - looking for a person lying down");
        }

        public void SetPracticeAdult() => SetPractice(PoseOverlayTest.BodyClass.Adult);
        public void SetPracticeChild() => SetPractice(PoseOverlayTest.BodyClass.Child);
        public void SetPracticeInfant() => SetPractice(PoseOverlayTest.BodyClass.Infant);

        public void SetPractice(PoseOverlayTest.BodyClass cls)
        {
            LeavePose();
            if (disc != null) disc.StopPractice();
            if (ghostHands != null) ghostHands.requireKneel = true;
            if (pose != null) pose.mode = cls == PoseOverlayTest.BodyClass.Infant ? PoseOverlayTest.PatientMode.Infant
                                         : cls == PoseOverlayTest.BodyClass.Child ? PoseOverlayTest.PatientMode.Child
                                         : PoseOverlayTest.PatientMode.Adult;
            if (kneel != null) kneel.ResetIndicator();
            var head = Camera.main != null ? Camera.main.transform : null;
            if (mannequin != null && head != null)
            {
                mannequin.Spawn(cls, head, FindFloor(head));
                // Prefer the surface the user is looking at (couch, bed, table...), if there is one in reach.
                if (TryLookSurface(head, out var surface)) mannequin.PlaceOnSurface(surface, Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized);
            }
            Enter(Mode.Practice, $"PRACTICE - {cls}\nPoint at the figure + pinch (or trigger) to move it");
        }

        public void SetPose()
        {
            if (mannequin != null) mannequin.Despawn();
            if (ghostHands != null) ghostHands.requireKneel = false;
            if (pose != null && pose.mode == PoseOverlayTest.PatientMode.Auto) pose.mode = PoseOverlayTest.PatientMode.Adult;
            if (recorder != null && !recorder.EditMode) recorder.ToggleEditMode(); // starts the practice disc
            Enter(Mode.Pose, "POSE (temporary) - re-record ghost hands");
        }

        /// <summary>Forget the current patient / kneel spot and start over in the same mode.</summary>
        public void ResetScan()
        {
            if (disc != null) disc.ResetCount();
            if (kneel != null) kneel.ResetIndicator();
            Debug.Log($"CPRMode: reset ({CurrentMode})");
        }

        void LeavePose()
        {
            if (recorder != null && recorder.EditMode) recorder.ToggleEditMode();
            if (CurrentMode == Mode.Pose && disc != null) disc.StopPractice();
        }

        void Enter(Mode mode, string banner)
        {
            CurrentMode = mode;
            if (disc != null && m_ColorsSaved)
            {
                disc.baseColor = mode == Mode.Default ? defaultBaseColor : m_PracticeBase;
                disc.reachedColor = mode == Mode.Default ? defaultReachedColor : m_PracticeReached;
            }
            CPRHud.Set("0-mode", banner + "\n(menu button: change mode)");
            CloseMenu();
            Debug.Log($"CPRMode: {mode}");
        }

        /// <summary>Upward-facing surface hit by a ray from the eyes, angled down, within 2.5 m.</summary>
        bool TryLookSurface(Transform head, out Vector3 point)
        {
            point = default;
            if (pose == null || pose.raycastManager == null || !Meta.XR.EnvironmentRaycastManager.IsSupported) return false;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var dir = Quaternion.AngleAxis(35f, Vector3.Cross(Vector3.up, fwd)) * fwd; // 35 degrees down
            if (!pose.raycastManager.Raycast(new Ray(head.position, dir), out var hit, 2.5f)) return false;
            if (Vector3.Dot(hit.normal, Vector3.up) < 0.6f) return false;
            point = hit.point;
            return true;
        }

        /// <summary>Floor under a point ~1 m in front of the user: depth ray, else the last measured floor.</summary>
        float FindFloor(Transform head)
        {
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var probe = head.position + fwd * 0.8f;
            if (pose != null && pose.raycastManager != null && Meta.XR.EnvironmentRaycastManager.IsSupported &&
                pose.raycastManager.Raycast(new Ray(probe, Vector3.down), out var hit, 3f))
                return hit.point.y;
            return pose != null ? pose.FloorY : head.position.y - 1.6f;
        }
    }
}
