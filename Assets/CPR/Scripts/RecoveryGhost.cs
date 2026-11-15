using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Ghost of a person in the recovery position, shown where the patient lies after the CPR test minute.
    /// PLACEHOLDER: built from glowing capsules (like the practice mannequin). Assign <see cref="modelPrefab"/>
    /// (e.g. a Mixamo character posed in the recovery position) to use a realistic model instead.
    /// Local layout: +Z toward the head, +X the side the person faces (top knee / bottom arm forward), +Y up.
    /// </summary>
    public class RecoveryGhost : MonoBehaviour
    {
        public Material auraMaterial;
        [Tooltip("Optional realistic model already posed in the recovery position (same local axes as above).")]
        public GameObject modelPrefab;

        [Tooltip("Draw the model with its normal skin (practice dummy rolled over) instead of the ghost glow.")]
        public bool solidModel;

        GameObject m_Root;

        public bool IsShown => m_Root != null;

        /// <summary>Show the ghost with its hips at 'hips' on the floor, head toward 'headDirection', facing 'faceDirection'.</summary>
        public void Show(Vector3 hips, Vector3 headDirection, Vector3 faceDirection, float bodyLength = 1.75f)
        {
            Hide();
            var up = Vector3.up;
            var z = Vector3.ProjectOnPlane(headDirection, up).normalized;
            if (z.sqrMagnitude < 1e-4f) z = Vector3.forward;
            var x = Vector3.ProjectOnPlane(faceDirection, up);
            x = (x - Vector3.Dot(x, z) * z).normalized;
            if (x.sqrMagnitude < 1e-4f) x = Vector3.Cross(up, z);
            m_Root = new GameObject("Recovery Position Ghost");
            m_Root.transform.SetPositionAndRotation(hips, Quaternion.LookRotation(z, up));
            // Make sure local +X points at faceDirection (mirror if the person should face the other way).
            if (Vector3.Dot(m_Root.transform.right, x) < 0f) m_Root.transform.localScale = new Vector3(-1f, 1f, 1f);

            if (modelPrefab != null)
            {
                var m = Instantiate(modelPrefab, m_Root.transform, false);
                m.transform.localScale *= bodyLength / 1.75f;
            }
            else BuildCapsules(bodyLength / 1.75f); // original glowing outline (the FBX recovery pose was reverted)
            Debug.Log("CPRSteps: recovery-position ghost shown");
        }

        public void Hide()
        {
            if (m_Root != null) Destroy(m_Root);
            m_Root = null;
        }

        void BuildCapsules(float s)
        {
            // Adult proportions (1.75 m), lying on the left side, hips at the origin.
            Vector3 P(float x, float y, float zz) => new Vector3(x, y, zz) * s;
            var shLow = P(0.00f, 0.08f, 0.55f); var shTop = P(0.00f, 0.42f, 0.55f);
            var hipLow = P(0.00f, 0.07f, 0.00f); var hipTop = P(0.00f, 0.32f, 0.00f);
            var head = P(0.04f, 0.16f, 0.80f);
            // Bottom leg straight; top leg bent with the knee forward on the floor.
            var kneeLow = P(0.00f, 0.07f, -0.45f); var ankleLow = P(0.00f, 0.06f, -0.85f);
            var kneeTop = P(0.42f, 0.12f, -0.18f); var ankleTop = P(0.22f, 0.08f, -0.55f);
            // Bottom arm out in front at a right angle, elbow bent; top hand under the cheek.
            var elbowLow = P(0.30f, 0.05f, 0.62f); var wristLow = P(0.32f, 0.05f, 0.88f);
            var elbowTop = P(0.30f, 0.22f, 0.58f); var wristTop = P(0.14f, 0.10f, 0.80f);

            Limb((shLow + shTop) * 0.5f, (hipLow + hipTop) * 0.5f, 0.16f * s, 0.12f * s);
            var h = Piece(PrimitiveType.Sphere); h.localPosition = head; h.localScale = Vector3.one * 0.22f * s;
            Limb(head, (shLow + shTop) * 0.5f, 0.05f * s, 0.05f * s);
            Limb(hipLow, kneeLow, 0.08f * s, 0.08f * s); Limb(kneeLow, ankleLow, 0.055f * s, 0.055f * s);
            Limb(hipTop, kneeTop, 0.08f * s, 0.08f * s); Limb(kneeTop, ankleTop, 0.055f * s, 0.055f * s);
            Limb(shLow, elbowLow, 0.045f * s, 0.045f * s); Limb(elbowLow, wristLow, 0.038f * s, 0.038f * s);
            Limb(shTop, elbowTop, 0.045f * s, 0.045f * s); Limb(elbowTop, wristTop, 0.038f * s, 0.038f * s);
        }

        void Limb(Vector3 a, Vector3 b, float rWidth, float rDepth)
        {
            var t = Piece(PrimitiveType.Capsule);
            var d = b - a;
            t.localPosition = 0.5f * (a + b);
            t.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
            t.localScale = new Vector3(2f * rWidth, 0.5f * d.magnitude + rWidth, 2f * rDepth);
        }

        Transform Piece(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(m_Root.transform, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = auraMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }
    }
}
