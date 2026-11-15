using Meta.XR;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Ray grab for the practice mannequin: point at it with a hand (Quest's pointing ray) or a
    /// controller, pinch / pull the trigger to grab, then point where it should lie and let go.
    /// The figure lands on whatever surface the ray hits (floor, couch, bed, table) using the
    /// headset's depth, lying flat with its head to the user's left. Log tag: "CPRPractice".
    /// </summary>
    public class FigureRayGrabber : MonoBehaviour
    {
        public PracticeMannequin mannequin;
        public EnvironmentRaycastManager raycastManager;
        public Material rayMaterial;
        [Tooltip("Max ray length (m).")]
        public float maxDistance = 6f;
        [Tooltip("Surfaces must face up at least this much (dot with up) to lie on.")]
        public float minUpNormal = 0.6f;

        OVRHand m_LeftHand, m_RightHand;
        Transform m_LeftCtrl, m_RightCtrl;
        LineRenderer m_Ray;
        Transform m_Reticle;
        int m_Holder = -1; // which source is holding: 0 L hand, 1 R hand, 2 L ctrl, 3 R ctrl
        bool[] m_WasSelecting = new bool[4];

        void Start()
        {
            foreach (var h in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                var t = ((OVRSkeleton.IOVRSkeletonDataProvider)h).GetSkeletonType();
                if (t == OVRSkeleton.SkeletonType.HandLeft || t == OVRSkeleton.SkeletonType.XRHandLeft) m_LeftHand = h; else m_RightHand = h;
            }
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) { m_LeftCtrl = rig.leftControllerAnchor; m_RightCtrl = rig.rightControllerAnchor; }

            m_Ray = new GameObject("FigureRay").AddComponent<LineRenderer>();
            m_Ray.transform.SetParent(transform, false);
            m_Ray.positionCount = 2;
            m_Ray.startWidth = 0.006f; m_Ray.endWidth = 0.002f;
            m_Ray.sharedMaterial = rayMaterial;
            m_Ray.enabled = false;

            var ret = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(ret.GetComponent<Collider>());
            ret.name = "SurfaceReticle";
            ret.transform.SetParent(transform, false);
            ret.transform.localScale = new Vector3(0.12f, 0.002f, 0.12f);
            ret.GetComponent<Renderer>().sharedMaterial = rayMaterial;
            m_Reticle = ret.transform;
            m_Reticle.gameObject.SetActive(false);
        }

        void Update()
        {
            if (mannequin == null || !mannequin.IsSpawned) { Hide(); m_Holder = -1; return; }

            // Holding: follow the holder's ray to a surface; release when they let go.
            if (m_Holder >= 0)
            {
                if (!TryGetRay(m_Holder, out var ray, out var selecting) || !selecting)
                {
                    Debug.Log($"CPRPractice: figure placed at {mannequin.RootPosition}");
                    m_Holder = -1;
                    Hide();
                    return;
                }
                var end = ray.GetPoint(maxDistance);
                if (TrySurface(ray, out var hit, out var normal))
                {
                    end = hit;
                    var fwd = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
                    mannequin.PlaceOnSurface(hit, fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward);
                    ShowReticle(hit);
                }
                else m_Reticle.gameObject.SetActive(false);
                Draw(ray.origin, end, new Color(0.2f, 1f, 0.5f, 0.9f));
                return;
            }

            // Not holding: show a ray only while pointing at the figure; grab on pinch / trigger.
            Hide();
            for (var i = 0; i < 4; i++)
            {
                if (!TryGetRay(i, out var ray, out var selecting)) { m_WasSelecting[i] = false; continue; }
                var onFigure = mannequin.Raycast(ray, maxDistance, out var figureHit);
                var pressed = selecting && !m_WasSelecting[i];
                m_WasSelecting[i] = selecting;
                if (!onFigure) continue;
                Draw(ray.origin, figureHit, new Color(0.4f, 0.8f, 1f, 0.9f));
                if (pressed)
                {
                    m_Holder = i;
                    Debug.Log($"CPRPractice: figure grabbed ({(i < 2 ? "hand" : "controller")} ray)");
                }
                break;
            }
        }

        bool TryGetRay(int i, out Ray ray, out bool selecting)
        {
            ray = default; selecting = false;
            var touch = OVRInput.GetActiveController() != OVRInput.Controller.Hands && OVRInput.GetActiveController() != OVRInput.Controller.None;
            switch (i)
            {
                case 0: case 1:
                    var h = i == 0 ? m_LeftHand : m_RightHand;
                    if (h == null || !h.IsTracked || !h.IsPointerPoseValid) return false;
                    ray = new Ray(h.PointerPose.position, h.PointerPose.forward);
                    selecting = h.GetFingerIsPinching(OVRHand.HandFinger.Index);
                    return true;
                default:
                    var c = i == 2 ? m_LeftCtrl : m_RightCtrl;
                    if (!touch || c == null) return false;
                    ray = new Ray(c.position, c.forward);
                    var ctrl = i == 2 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                    selecting = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ctrl) > 0.6f || OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, ctrl) > 0.6f;
                    return true;
            }
        }

        /// <summary>First upward-facing real surface along the ray (depth-based).</summary>
        bool TrySurface(Ray ray, out Vector3 point, out Vector3 normal)
        {
            point = default; normal = Vector3.up;
            if (raycastManager == null || !EnvironmentRaycastManager.IsSupported) return false;
            if (!raycastManager.Raycast(ray, out var hit, maxDistance)) return false;
            if (Vector3.Dot(hit.normal, Vector3.up) < minUpNormal) return false; // walls etc.
            point = hit.point; normal = hit.normal;
            return true;
        }

        void Draw(Vector3 a, Vector3 b, Color c)
        {
            m_Ray.enabled = true;
            m_Ray.SetPosition(0, a); m_Ray.SetPosition(1, b);
            m_Ray.startColor = m_Ray.endColor = c;
        }

        void ShowReticle(Vector3 p)
        {
            m_Reticle.gameObject.SetActive(true);
            m_Reticle.position = p + Vector3.up * 0.003f;
        }

        void Hide()
        {
            if (m_Ray != null) m_Ray.enabled = false;
            if (m_Reticle != null) m_Reticle.gameObject.SetActive(false);
        }
    }
}
