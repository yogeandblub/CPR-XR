using UnityEngine;

namespace CPR
{
    /// <summary>
    /// App modes, started by voice (see Integration.SOSController):
    ///   Idle     - passthrough only, nothing on screen; the mic listens for "SOS" / "PRACTICE".
    ///   SOS      - a real person: scan, chest button, kneel spot, guided steps. Text-only HUD in the
    ///              top-left of the view (SosHud); no world panels. Calls the demo dispatcher.
    ///   Practice - glowing mannequin (Adult / Child / Infant) with the world-space panel and menu,
    ///              which follow the user about 2 ft in front. Camera inference off.
    /// The hidden Pose tool (re-record ghost hands) is still reachable from code via SetPose().
    /// Log tag: "CPRMode".
    /// </summary>
    public class CPRModeManager : MonoBehaviour
    {
        public enum Mode { Idle, SOS, Practice, Pose }

        [Header("References")]
        public PoseOverlayTest pose;
        public KneelIndicator kneel;
        public CompressionDisc disc;
        public GhostHands ghostHands;
        public HandPoseRecorder recorder;
        public PracticeMannequin mannequin;
        [Tooltip("Old pinch / A-B-X test shortcuts. Disabled: everything is voice + menu now.")]
        public CPRDemoControls legacyControls;
        [Tooltip("Root of the Practice menu panel (world-space canvas).")]
        public GameObject menu;
        [Tooltip("Team pipeline client.")]
        public Integration.XrAedClient client;

        [Header("Startup")]
        [Tooltip("Start with nothing on screen (passthrough only) until SOS / PRACTICE is heard.")]
        public bool startIdle = true;
        /// <summary>True once SOS or PRACTICE has started.</summary>
        public bool IsActivated { get; private set; }

        [Header("Button colors")]
        [Tooltip("SOS: blue button turning red at full depth (high visibility on a real person).")]
        public Color defaultBaseColor = new Color(0.15f, 0.45f, 1f, 0.75f);
        public Color defaultReachedColor = new Color(1f, 0.15f, 0.15f, 0.9f);
        Color m_PracticeBase, m_PracticeReached;
        bool m_ColorsSaved;

        [Header("Practice menu: follows the user")]
        [Tooltip("Distance in front of the user (m). 0.6 m = about 2 ft.")]
        public float menuDistance = 0.6f;
        public float menuBelowEye = 0.22f;
        [Tooltip("How quickly the menu catches up when the user turns or walks.")]
        public float menuFollowSpeed = 2.5f;

        public Mode CurrentMode { get; private set; } = Mode.Idle;

        void Start()
        {
            if (disc != null) { m_PracticeBase = disc.baseColor; m_PracticeReached = disc.reachedColor; m_ColorsSaved = true; }
            if (legacyControls != null) legacyControls.enabled = false;
            if (menu != null) menu.SetActive(false);
            if (startIdle) SetIdle(); else StartSOS();
        }

        void Update()
        {
            // Left controller menu (hamburger) button / left-hand menu pinch: Practice menu only.
            if (OVRInput.GetDown(OVRInput.Button.Start) && CurrentMode == Mode.Practice) ToggleMenu();
            if (menu != null && menu.activeSelf) FollowUser(false);
        }

        // ---------- Practice menu ----------

        public void ToggleMenu()
        {
            if (menu == null || CurrentMode != Mode.Practice) return;
            var open = !menu.activeSelf;
            menu.SetActive(open);
            if (open) FollowUser(true);
        }

        public void CloseMenu() { if (menu != null) menu.SetActive(false); }

        /// <summary>Keep the menu ~2 ft in front of the user, slightly below eye level, easing after them.</summary>
        void FollowUser(bool snap)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.up;
            fwd.Normalize();
            var target = cam.transform.position + fwd * menuDistance - Vector3.up * menuBelowEye;
            var t = snap ? 1f : 1f - Mathf.Exp(-menuFollowSpeed * Time.deltaTime);
            menu.transform.position = Vector3.Lerp(menu.transform.position, target, t);
            var face = Vector3.ProjectOnPlane(menu.transform.position - cam.transform.position, Vector3.up);
            if (face.sqrMagnitude < 1e-4f) face = fwd;
            // Faces the user, tilted back a little so it's easy to point at and tap.
            menu.transform.rotation = Quaternion.Slerp(menu.transform.rotation, Quaternion.LookRotation(face.normalized, Vector3.up) * Quaternion.Euler(15f, 0f, 0f), t);
        }

        // ---------- Modes ----------

        /// <summary>Passthrough only: nothing on screen, no camera inference; the mic listens for SOS / PRACTICE.</summary>
        public void SetIdle()
        {
            IsActivated = false;
            LeavePose();
            if (mannequin != null) mannequin.Despawn();
            if (disc != null) disc.StopPractice();
            if (kneel != null) kneel.ResetIndicator();
            if (pose != null) pose.SetFrozen(true); // camera model off until needed (battery)
            CloseMenu();
            CPRHud.Hidden = false;
            CPRHud.ClearAll();
            SosHud.ClearAll(); // idle = nothing on screen
            CurrentMode = Mode.Idle;
            Debug.Log("CPRMode: Idle (passthrough only, listening for SOS / PRACTICE)");
        }

        /// <summary>SOS: a real person. Scan + guidance; text HUD only (world panel hidden).</summary>
        public void StartSOS()
        {
            IsActivated = true;
            LeavePose();
            if (mannequin != null) mannequin.Despawn();
            if (pose != null) { pose.mode = PoseOverlayTest.PatientMode.Auto; pose.SetFrozen(false); }
            if (disc != null) disc.StopPractice();
            if (ghostHands != null) ghostHands.requireKneel = true;
            if (kneel != null) kneel.ResetIndicator(); // also resets + resumes pose tracking
            CloseMenu();
            CPRHud.ClearAll();
            CPRHud.Hidden = true; // SOS uses the small top-left text HUD instead
            Enter(Mode.SOS);
        }

        /// <summary>Fires when a practice dummy has been spawned from the menu.</summary>
        public event System.Action PracticeSpawned;

        /// <summary>Voice "PRACTICE": practice mode with only the menu - nothing spawns until a menu choice.</summary>
        public void OpenPractice()
        {
            IsActivated = true;
            LeavePose();
            if (mannequin != null) mannequin.Despawn();
            if (disc != null) disc.StopPractice();
            if (kneel != null) kneel.ResetIndicator();
            CPRHud.ClearAll();
            CPRHud.Hidden = true; // practice uses the same head-locked text HUD as SOS
            Enter(Mode.Practice);
            SosHud.ClearAll();
            SosHud.Set("0-sos", "PRACTICE - choose Adult, Child or Infant on the menu");
            if (menu != null && !menu.activeSelf) { menu.SetActive(true); FollowUser(true); }
        }

        public void SetPracticeAdult() => StartPractice(PoseOverlayTest.BodyClass.Adult);
        public void SetPracticeChild() => StartPractice(PoseOverlayTest.BodyClass.Child);
        public void SetPracticeInfant() => StartPractice(PoseOverlayTest.BodyClass.Infant);

        /// <summary>Practice on the mannequin, with the world panel and a menu that follows the user.</summary>
        public void StartPractice(PoseOverlayTest.BodyClass cls)
        {
            IsActivated = true;
            LeavePose();
            CPRHud.ClearAll();
            CPRHud.Hidden = true; // practice uses the same head-locked text HUD as SOS
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
            Enter(Mode.Practice);
            if (menu != null && !menu.activeSelf) { menu.SetActive(true); FollowUser(true); }
            PracticeSpawned?.Invoke();
        }

        public void SetPose()
        {
            if (mannequin != null) mannequin.Despawn();
            if (ghostHands != null) ghostHands.requireKneel = false;
            CPRHud.Hidden = false;
            if (pose != null && pose.mode == PoseOverlayTest.PatientMode.Auto) pose.mode = PoseOverlayTest.PatientMode.Adult;
            if (recorder != null && !recorder.EditMode) recorder.ToggleEditMode(); // starts the practice disc
            Enter(Mode.Pose);
        }

        /// <summary>Forget the current patient / kneel spot and start over in the same mode.</summary>
        public void ResetScan()
        {
            if (disc != null) disc.ResetCount();
            if (kneel != null) kneel.ResetIndicator();
            Debug.Log($"CPRMode: reset ({CurrentMode})");
        }

        /// <summary>Menu button: leave Practice and go back to passthrough-only idle.</summary>
        public void ExitToIdle() => SetIdle();

        /// <summary>Fallback voice trigger (used by VoiceCommand only when no SOSController handles phrases).</summary>
        public void VoiceTrigger(string reason)
        {
            if (CurrentMode != Mode.Idle) return;
            Debug.Log($"CPRMode: voice trigger ({reason}) - SOS");
            StartSOS();
        }

        // Kept for old menu wiring.
        public void SetDefault() => StartSOS();
        public void RequestHelp() { if (client != null) client.SendHelpRequest(); }

        void LeavePose()
        {
            if (recorder != null && recorder.EditMode) recorder.ToggleEditMode();
            if (CurrentMode == Mode.Pose && disc != null) disc.StopPractice();
        }

        void Enter(Mode mode)
        {
            CurrentMode = mode;
            if (disc != null && m_ColorsSaved)
            {
                disc.baseColor = mode == Mode.SOS ? defaultBaseColor : m_PracticeBase;
                disc.reachedColor = mode == Mode.SOS ? defaultReachedColor : m_PracticeReached;
            }
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
