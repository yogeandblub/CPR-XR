using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CPR
{
    /// <summary>
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
        [Tooltip("Controller tip offset along the controller's forward axis (m).")]
        public float controllerTipOffset = 0.06f;

        readonly List<Button> m_Buttons = new List<Button>();
        readonly Dictionary<Button, Color> m_BaseColors = new Dictionary<Button, Color>();
        OVRSkeleton[] m_Skeletons = new OVRSkeleton[0];
        Transform m_LeftCtrl, m_RightCtrl;
        readonly bool[] m_Armed = { true, true, true, true }; // L finger, R finger, L controller, R controller

        void Start()
        {
            m_Skeletons = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) { m_LeftCtrl = rig.leftControllerAnchor; m_RightCtrl = rig.rightControllerAnchor; }
            if (menu != null) foreach (var b in menu.GetComponentsInChildren<Button>(true)) { m_Buttons.Add(b); m_BaseColors[b] = b.image.color; }
        }

        void Update()
        {
            if (menu == null || !menu.gameObject.activeInHierarchy) return;
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
                if (b != null && m_Armed[slot] && d <= pressDepth && d > -0.05f)
                {
                    m_Armed[slot] = false;
                    Debug.Log($"CPRMenu: pressed '{b.name.Replace("Button - ", "")}'");
                    b.onClick.Invoke();
                    return; // the menu may have closed
                }
            }
            foreach (var b in m_Buttons)
                b.image.color = b == hovered ? Color.Lerp(m_BaseColors[b], Color.white, 0.35f) : m_BaseColors[b];
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
