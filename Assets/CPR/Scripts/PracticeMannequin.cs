using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Practice mode patient: a glowing aura mannequin (adult / child / infant) lying on the floor.
    /// It feeds its joints to <see cref="PoseOverlayTest.SetVirtualPatient"/>, so the kneel spot,
    /// button disc, ghost hands and coaching all work exactly as on a real person - with the camera
    /// model switched off. Move it with <see cref="FigureRayGrabber"/> (point, pinch/trigger, point
    /// at a surface, release): it lies flat on whatever surface it's put on - floor, couch, table.
    /// </summary>
    public class PracticeMannequin : MonoBehaviour
    {
        [Header("References")]
        public PoseOverlayTest pose;
        public Material auraMaterial;

        [Header("Sizes (crown to heel, m)")]
        public float adultLength = 1.75f;
        public float childLength = 1.2f;   // ~7 years
        public float infantLength = 0.65f; // ~6 months

        public PoseOverlayTest.BodyClass CurrentClass { get; private set; } = PoseOverlayTest.BodyClass.Unknown;
        public bool IsSpawned => m_Root != null;

        Transform m_Root;          // the figure; lies on m_FloorY (the surface it was placed on)
        Transform m_Body;          // aura pieces live here (local: x = across, z = toward head, y = up)
        BoxCollider m_Box;         // for pointing rays
        readonly Vector3[] m_LocalJoints = new Vector3[33];
        readonly Vector3[] m_WorldJoints = new Vector3[33];
        Vector3 m_LocalCrown;
        float m_Length, m_HalfThickness, m_FloorY;

        // BlazePose indices used to build the figure.
        const int NOSE = 0, L_EAR = 7, R_EAR = 8, L_SHOULDER = 11, R_SHOULDER = 12, L_ELBOW = 13, R_ELBOW = 14,
                  L_WRIST = 15, R_WRIST = 16, L_HIP = 23, R_HIP = 24, L_KNEE = 25, R_KNEE = 26,
                  L_ANKLE = 27, R_ANKLE = 28, L_HEEL = 29, R_HEEL = 30, L_FOOT = 31, R_FOOT = 32;

        /// <summary>Spawn (or replace) the mannequin ~1.2 m in front of the user, lying across their view.</summary>
        public void Spawn(PoseOverlayTest.BodyClass cls, Transform head, float floorY)
        {
            Despawn();
            CurrentClass = cls == PoseOverlayTest.BodyClass.Unknown ? PoseOverlayTest.BodyClass.Adult : cls;
            m_Length = cls == PoseOverlayTest.BodyClass.Infant ? infantLength : cls == PoseOverlayTest.BodyClass.Child ? childLength : adultLength;
            m_FloorY = floorY;
            BuildJoints();

            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var distance = 0.55f + 0.25f * (m_Length / adultLength); // smaller bodies spawn closer
            var pos = head.position + fwd * distance;
            pos.y = m_FloorY;
            // Head to the user's left, so they kneel at its side (chest straight ahead).
            var bodyHeadDir = Vector3.Cross(fwd, Vector3.up); // the user's left
            var rot = Quaternion.LookRotation(bodyHeadDir, Vector3.up);

            // Built inactive so the Interaction SDK components get their references before they wake up.
            var rootGo = new GameObject($"Practice Mannequin ({CurrentClass})");
            rootGo.SetActive(false);
            m_Root = rootGo.transform;
            m_Root.SetPositionAndRotation(pos, rot);
            m_Body = new GameObject("Body").transform;
            m_Body.SetParent(m_Root, false);
            BuildAura();
            AddCollider(rootGo);
            rootGo.SetActive(true);

            Debug.Log($"CPRPractice: spawned {CurrentClass} mannequin ({m_Length:F2} m) at {pos}");
        }

        public void Despawn()
        {
            if (m_Root != null) Destroy(m_Root.gameObject);
            m_Root = null;
            m_Box = null;
            if (pose != null) pose.ClearVirtualPatient();
            CurrentClass = PoseOverlayTest.BodyClass.Unknown;
        }

        /// <summary>World position of the figure (center, on its surface).</summary>
        public Vector3 RootPosition => m_Root != null ? m_Root.position : default;

        /// <summary>Does this ray hit the figure? (for pointing / grabbing)</summary>
        public bool Raycast(Ray ray, float maxDistance, out Vector3 point)
        {
            point = default;
            if (m_Box == null || !m_Box.Raycast(ray, out var hit, maxDistance)) return false;
            point = hit.point;
            return true;
        }

        /// <summary>Lay the figure on a surface point, across the given viewing direction (head to the viewer's left).</summary>
        public void PlaceOnSurface(Vector3 point, Vector3 viewForward)
        {
            if (m_Root == null) return;
            m_FloorY = point.y;
            var headDir = Vector3.Cross(viewForward, Vector3.up);
            m_Root.SetPositionAndRotation(point, Quaternion.LookRotation(headDir, Vector3.up));
        }

        void LateUpdate()
        {
            if (m_Root == null) return;

            for (var i = 0; i < 33; i++) m_WorldJoints[i] = m_Root.TransformPoint(m_LocalJoints[i]);
            if (pose != null)
                pose.SetVirtualPatient(m_WorldJoints, CurrentClass, m_Length, m_Root.TransformPoint(m_LocalCrown), m_FloorY, m_HalfThickness);
        }

        // ---------- Body proportions ----------

        void BuildJoints()
        {
            var L = m_Length;
            // Head is a much larger share of length in infants (~1/4) than adults (~1/7.5).
            var headFrac = CurrentClass == PoseOverlayTest.BodyClass.Infant ? 0.25f : CurrentClass == PoseOverlayTest.BodyClass.Child ? 0.17f : 0.13f;
            var hipFrac = CurrentClass == PoseOverlayTest.BodyClass.Infant ? 0.6f : CurrentClass == PoseOverlayTest.BodyClass.Child ? 0.52f : 0.48f;
            var shoulderHalf = (CurrentClass == PoseOverlayTest.BodyClass.Infant ? 0.15f : 0.115f) * L;
            var hipHalf = 0.055f * L * (CurrentClass == PoseOverlayTest.BodyClass.Infant ? 1.4f : 1f);
            m_HalfThickness = (CurrentClass == PoseOverlayTest.BodyClass.Infant ? 0.1f : 0.066f) * L; // chest depth / 2
            var y = m_HalfThickness; // joints at body mid-depth

            // z: crown at +L/2 (toward head), heels at -L/2. Distances measured from the crown.
            float Z(float fromCrown) => 0.5f * L - fromCrown * L;
            var hl = headFrac;
            var shoulderZ = Z(hl + 0.04f);
            var hipZ = Z(hipFrac);
            var kneeZ = Z(hipFrac + (1f - hipFrac) * 0.5f);
            var ankleZ = Z(0.96f);

            m_LocalCrown = new Vector3(0, y, 0.5f * L);
            Set(NOSE, 0, y + 0.3f * m_HalfThickness, Z(0.5f * hl));
            Set(L_EAR, -0.35f * hl * L, y, Z(0.5f * hl)); Set(R_EAR, 0.35f * hl * L, y, Z(0.5f * hl));
            for (var i = 1; i <= 10; i++) if (i != L_EAR && i != R_EAR) Set(i, 0, y + 0.2f * m_HalfThickness, Z(0.45f * hl)); // other face points
            Set(L_SHOULDER, -shoulderHalf, y, shoulderZ); Set(R_SHOULDER, shoulderHalf, y, shoulderZ);
            Set(L_ELBOW, -shoulderHalf - 0.02f * L, y, shoulderZ - 0.19f * L); Set(R_ELBOW, shoulderHalf + 0.02f * L, y, shoulderZ - 0.19f * L);
            Set(L_WRIST, -shoulderHalf - 0.03f * L, y, shoulderZ - 0.35f * L); Set(R_WRIST, shoulderHalf + 0.03f * L, y, shoulderZ - 0.35f * L);
            for (var i = 17; i <= 22; i++) { var left = i % 2 == 1; Set(i, (left ? -1 : 1) * (shoulderHalf + 0.03f * L), y, shoulderZ - 0.39f * L); }
            Set(L_HIP, -hipHalf, y, hipZ); Set(R_HIP, hipHalf, y, hipZ);
            Set(L_KNEE, -hipHalf, y, kneeZ); Set(R_KNEE, hipHalf, y, kneeZ);
            Set(L_ANKLE, -hipHalf, y, ankleZ); Set(R_ANKLE, hipHalf, y, ankleZ);
            Set(L_HEEL, -hipHalf, y * 0.6f, Z(1f)); Set(R_HEEL, hipHalf, y * 0.6f, Z(1f));
            Set(L_FOOT, -hipHalf, y * 1.6f, Z(0.97f)); Set(R_FOOT, hipHalf, y * 1.6f, Z(0.97f));
        }

        void Set(int i, float x, float y, float z) => m_LocalJoints[i] = new Vector3(x, y, z);

        // ---------- Look: same soft aura as the real-patient outline ----------

        void BuildAura()
        {
            var size = m_Length / adultLength;
            var shoulderMid = 0.5f * (m_LocalJoints[L_SHOULDER] + m_LocalJoints[R_SHOULDER]);
            var hipMid = 0.5f * (m_LocalJoints[L_HIP] + m_LocalJoints[R_HIP]);
            var torsoHalfWidth = Mathf.Abs(m_LocalJoints[L_SHOULDER].x) * 0.95f;
            Piece(PrimitiveType.Capsule, "Torso", shoulderMid, hipMid, torsoHalfWidth, m_HalfThickness);
            var headR = Mathf.Abs(m_LocalJoints[L_EAR].x) * 1.15f;
            var head = Piece(PrimitiveType.Sphere, "Head", Vector3.zero, Vector3.zero, headR, headR);
            head.localPosition = new Vector3(0, m_HalfThickness, m_LocalCrown.z - headR);
            head.localScale = new Vector3(2f * headR, 2f * headR * 0.9f, 2f * headR * 1.15f);
            Piece(PrimitiveType.Capsule, "Neck", new Vector3(0, m_HalfThickness, head.localPosition.z - headR), shoulderMid, 0.04f * size + 0.01f, 0.04f * size + 0.01f);

            var limbs = new (int a, int b, float r)[]
            {
                (L_SHOULDER, L_ELBOW, 0.045f), (L_ELBOW, L_WRIST, 0.036f), (R_SHOULDER, R_ELBOW, 0.045f), (R_ELBOW, R_WRIST, 0.036f),
                (L_HIP, L_KNEE, 0.07f), (L_KNEE, L_ANKLE, 0.05f), (R_HIP, R_KNEE, 0.07f), (R_KNEE, R_ANKLE, 0.05f),
                (L_ANKLE, L_FOOT, 0.035f), (R_ANKLE, R_FOOT, 0.035f),
            };
            foreach (var (a, b, r) in limbs) { var rr = r * Mathf.Max(0.45f, size); Piece(PrimitiveType.Capsule, $"Limb_{a}_{b}", m_LocalJoints[a], m_LocalJoints[b], rr, rr); }
        }

        Transform Piece(PrimitiveType type, string name, Vector3 a, Vector3 b, float halfWidth, float halfDepth)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            var t = go.transform;
            t.SetParent(m_Body, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = auraMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var d = b - a;
            if (d.sqrMagnitude > 1e-6f)
            {
                t.localPosition = 0.5f * (a + b);
                // Capsule's local Y runs along the limb; local X stays across the body, local Z ends up vertical (depth).
                t.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
                t.localScale = new Vector3(2f * halfWidth, 0.5f * d.magnitude + halfWidth, 2f * halfDepth);
            }
            return t;
        }

        // ---------- Pointing target ----------

        void AddCollider(GameObject rootGo)
        {
            // One box around the whole body: easy to point at from anywhere.
            m_Box = rootGo.AddComponent<BoxCollider>();
            var width = 2f * Mathf.Abs(m_LocalJoints[L_WRIST].x) + 0.1f;
            m_Box.center = new Vector3(0, m_HalfThickness, 0);
            m_Box.size = new Vector3(width, 2f * m_HalfThickness + 0.08f, m_Length);
        }

        void OnDisable() => Despawn();
    }
}
