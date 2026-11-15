using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CPR
{
    /// <summary>
    /// Ray pointing: each tracked hand's pointing ray (Quest's PointerPose) or a controller's forward ray
    /// highlights the button it hits; an index pinch (or trigger) presses it. Direct tapping also works:
    /// Lets fingertips (index finger of either tracked hand) and controller tips press the buttons on
    /// a world-space Unity UI canvas - no event system or interaction SDK needed.
    /// A button highlights while a tip hovers over it and fires once when the tip pushes through the
    /// canvas surface; it re-arms after the tip pulls back. Log tag: "CPRMenu".
    /// </summary>
    public class MenuPoker : MonoBehaviour
    {
        [Tooltip("The menu canvas root (its buttons are found automatically).")]
        public RectTransform menu;
        [Tooltip("Tip counts as pressing within this distance in front of the canvas (m).")]
        public float pressDepth = 0.008f;
        [Tooltip("Tip must pull back this far (m) before the same finger can press again.")]
        public float releaseDistance = 0.025f;
        [Tooltip("Hover highlight range in front of the canvas (m).")]
        public float hoverDistance = 0.06f;
        [Tooltip("Ignore presses for this long after the menu opens (hands may already be where it appears).")]
        public float openGraceSeconds = 1.5f;
        [Tooltip("Controller tip offset along the controller's forward axis (m).")]
        public float controllerTipOffset = 0.06f;

        [Header("Ray pointing")]
        public Material rayMaterial;
        public float rayLength = 3f;
        [Tooltip("Pinch strength that counts as a click.")]
        public float pinchClick = 0.8f;

        readonly List<Button> m_Buttons = new List<Button>();
        OVRHand m_HandL, m_HandR;
        LineRenderer m_Ray;
        Transform m_Cursor;
        readonly bool[] m_RayWasDown = new bool[4];
        readonly Dictionary<Button, Color> m_BaseColors = new Dictionary<Button, Color>();
        OVRSkeleton[] m_Skeletons = new OVRSkeleton[0];
        Transform m_LeftCtrl, m_RightCtrl;
        // A tip only arms after it has been seen IN FRONT of the menu - a hand already behind it can't press.
        readonly bool[] m_Armed = { false, false, false, false }; // L finger, R finger, L controller, R controller
        float m_OpenedAt;
        bool m_WasOpen;

        void Start()
        {
            m_Skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) { m_LeftCtrl = rig.leftControllerAnchor; m_RightCtrl = rig.rightControllerAnchor; }
            if (menu != null) foreach (var b in menu.GetComponentsInChildren<Button>(true)) { m_Buttons.Add(b); m_BaseColors[b] = b.image.color; }
            foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var t = ((OVRSkeleton.IOVRSkeletonDataProvider)h).GetSkeletonType();
                if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) m_HandL = h; else m_HandR = h;
            }
            m_Ray = new GameObject("MenuRay").AddComponent<LineRenderer>();
            m_Ray.transform.SetParent(transform, false);
            m_Ray.positionCount = 2;
            m_Ray.startWidth = 0.004f; m_Ray.endWidth = 0.002f;
            if (rayMaterial != null) m_Ray.sharedMaterial = rayMaterial;
            m_Ray.startColor = m_Ray.endColor = new Color(0.5f, 0.85f, 1f, 0.9f);
            m_Ray.enabled = false;
            var c = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(c.GetComponent<Collider>());
            c.name = "MenuCursor";
            c.transform.SetParent(transform, false);
            c.transform.localScale = Vector3.one * 0.012f;
            if (rayMaterial != null) c.GetComponent<Renderer>().sharedMaterial = rayMaterial;
            m_Cursor = c.transform;
            m_Cursor.gameObject.SetActive(false);
        }

        void Update()
        {
            var open = menu != null && menu.gameObject.activeInHierarchy;
            if (open && !m_WasOpen) { m_OpenedAt = Time.time; for (var k = 0; k < m_Armed.Length; k++) m_Armed[k] = false; }
            m_WasOpen = open;
            if (!open) { if (m_Ray != null) { m_Ray.enabled = false; m_Cursor.gameObject.SetActive(false); } return; }
            var grace = Time.time - m_OpenedAt < openGraceSeconds;
            Button hovered = null;
            var i = 0;
            foreach (var tip in Tips())
            {
                var slot = i++;
                if (tip == null) continue;
                var p = tip.Value;
                // Signed distance in front of the canvas (+ = toward the user). Canvas +Z points away from the user.
                var d = Vector3.Dot(menu.position - p, menu.forward);
                var b = ButtonAt(p);
                if (d > releaseDistance) m_Armed[slot] = true;
                if (b != null && d < hoverDistance) hovered = b;
                if (!grace && b != null && m_Armed[slot] && d <= pressDepth && d > -0.05f)
                {
                    m_Armed[slot] = false;
                    Debug.Log($"CPRMenu: pressed '{b.name.Replace("Button - ", "")}'");
                    b.onClick.Invoke();
                    return; // the menu may have closed
                }
            }
            // Ray pointing (hands: pointing ray + pinch; controllers: forward ray + trigger).
            var rayHit = PointRays(grace, out var rayButton);
            if (rayHit == null) return; // a ray click happened (the menu may have closed)
            if (hovered == null) hovered = rayButton;

            foreach (var b in m_Buttons)
                b.image.color = b == hovered ? Color.Lerp(m_BaseColors[b], Color.white, 0.35f) : m_BaseColors[b];
        }

        /// <summary>Returns false-y (null) when a click fired; otherwise true, with the button under the first ray.</summary>
        bool? PointRays(bool grace, out Button pointed)
        {
            pointed = null;
            var plane = new Plane(-menu.forward, menu.position);
            var drew = false;
            for (var i = 0; i < 4; i++)
            {
                if (!TryRay(i, out var ray, out var down)) { m_RayWasDown[i] = false; continue; }
                var pressed = down && !m_RayWasDown[i];
                m_RayWasDown[i] = down;
                if (!plane.Raycast(ray, out var dist) || dist > rayLength) continue;
                var hit = ray.GetPoint(dist);
                var b = ButtonAt(hit);
                if (!drew)
                {
                    drew = true;
                    m_Ray.enabled = true;
                    m_Ray.SetPosition(0, ray.origin); m_Ray.SetPosition(1, hit);
                    m_Cursor.gameObject.SetActive(true);
                    m_Cursor.position = hit;
                    pointed = b;
                }
                if (pressed && b != null && !grace)
                {
                    Debug.Log($"CPRMenu: pressed '{b.name.Replace("Button - ", "")}' (ray)");
                    b.onClick.Invoke();
                    return null;
                }
            }
            if (!drew) { m_Ray.enabled = false; m_Cursor.gameObject.SetActive(false); }
            return true;
        }

        bool TryRay(int i, out Ray ray, out bool down)
        {
            ray = default; down = false;
            if (i < 2)
            {
                var h = i == 0 ? m_HandL : m_HandR;
                if (h == null || !h.IsTracked || !h.IsPointerPoseValid) return false;
                ray = new Ray(h.PointerPose.position, h.PointerPose.forward);
                down = h.GetFingerPinchStrength(OVRHand.HandFinger.Index) >= pinchClick;
                return true;
            }
            var ctrl = OVRInput.GetActiveController();
            if (ctrl == OVRInput.Controller.Hands || ctrl == OVRInput.Controller.None) return false;
            var anchor = i == 2 ? m_LeftCtrl : m_RightCtrl;
            if (anchor == null) return false;
            ray = new Ray(anchor.position, anchor.forward);
            down = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, i == 2 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch) > 0.6f;
            return true;
        }

        IEnumerable<Vector3?> Tips()
        {
            // Index fingertips (left, right) from the tracked hand skeletons.
            Vector3? left = null, right = null;
            foreach (var s in m_Skeletons)
            {
                if (s == null || !s.IsDataValid || !s.IsInitialized) continue;
                var type = s.GetSkeletonType();
                var isLeft = type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.XRHandLeft;
                foreach (var bone in s.Bones)
                    if (bone.Id == OVRSkeleton.BoneId.Hand_IndexTip || bone.Id == OVRSkeleton.BoneId.XRHand_IndexTip)
                    {
                        if (isLeft) left = bone.Transform.position; else right = bone.Transform.position;
                        break;
                    }
            }
            yield return left;
            yield return right;
            // Controller tips, only while controllers are in use.
            var ctrl = OVRInput.GetActiveController();
            var touch = (ctrl & OVRInput.Controller.Touch) != 0 || ctrl == OVRInput.Controller.LTouch || ctrl == OVRInput.Controller.RTouch;
            yield return touch && m_LeftCtrl != null ? m_LeftCtrl.position + m_LeftCtrl.forward * controllerTipOffset : (Vector3?)null;
            yield return touch && m_RightCtrl != null ? m_RightCtrl.position + m_RightCtrl.forward * controllerTipOffset : (Vector3?)null;
        }

        Button ButtonAt(Vector3 world)
        {
            foreach (var b in m_Buttons)
            {
                if (!b.gameObject.activeInHierarchy || !b.interactable) continue;
                var rt = (RectTransform)b.transform;
                var local = rt.InverseTransformPoint(world);
                if (rt.rect.Contains(new Vector2(local.x, local.y))) return b;
            }
            return null;
        }
    }
}
