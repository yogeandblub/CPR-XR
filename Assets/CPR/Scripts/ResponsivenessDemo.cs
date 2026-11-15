using UnityEngine;

namespace CPR
{
    /// <summary>
    /// A 2-second looping "how to check for responsiveness" demo that hovers in front of the rescuer:
    /// a person lying down, two hands tapping their shoulders, and "Are you OK?" flashing on each tap.
    /// Built from primitives (no image or video files). Drawn on top of the room so it's always readable.
    ///   ResponsivenessDemo.Show();  ResponsivenessDemo.Hide();
    /// </summary>
    public class ResponsivenessDemo : MonoBehaviour
    {
        [Tooltip("Distance in front of the eyes (m) and how far below eye level (m).")]
        public float distance = 0.55f, belowEye = 0.12f;
        [Tooltip("Length of one loop (s): two pairs of taps.")]
        public float loopSeconds = 2f;

        static ResponsivenessDemo s_Instance;
        Transform m_LeftHand, m_RightHand;
        TextMesh m_Shout;
        Vector3 m_LeftRest, m_RightRest;
        float m_ShownAt;

        public static void Show()
        {
            if (!Application.isPlaying) return;
            if (s_Instance == null) s_Instance = new GameObject("Responsiveness Demo").AddComponent<ResponsivenessDemo>();
            if (!s_Instance.gameObject.activeSelf) { s_Instance.gameObject.SetActive(true); s_Instance.m_ShownAt = Time.time; s_Instance.Place(true); }
        }

        public static void Hide()
        {
            if (s_Instance != null) s_Instance.gameObject.SetActive(false);
        }

        void Awake()
        {
            m_ShownAt = Time.time;
            // Card behind the figure (the whole demo is ~32 x 22 cm).
            Part(PrimitiveType.Quad, new Vector3(0f, 0f, 0.002f), new Vector3(0.32f, 0.22f, 1f), new Color(0f, 0f, 0f, 0.6f), 4000);
            // Person lying on their back, seen from the side: head on the left.
            var skin = new Color(0.85f, 0.85f, 0.88f, 1f);
            Part(PrimitiveType.Sphere, new Vector3(-0.105f, -0.045f, 0f), Vector3.one * 0.04f, skin, 4002);                                   // head
            Part(PrimitiveType.Capsule, new Vector3(-0.02f, -0.052f, 0f), new Vector3(0.035f, 0.07f, 0.035f), skin, 4002, 90f);            // torso
            Part(PrimitiveType.Capsule, new Vector3(0.085f, -0.058f, 0f), new Vector3(0.024f, 0.06f, 0.024f), skin, 4002, 90f);            // legs
            Part(PrimitiveType.Quad, new Vector3(0f, -0.08f, 0.001f), new Vector3(0.28f, 0.004f, 1f), new Color(1f, 1f, 1f, 0.35f), 4001); // floor line
            // Rescuer's hands above the shoulders.
            var hand = new Color(1f, 0.62f, 0.25f, 1f);
            m_LeftRest = new Vector3(-0.07f, -0.008f, -0.002f);
            m_RightRest = new Vector3(-0.045f, -0.008f, -0.002f);
            m_LeftHand = Part(PrimitiveType.Sphere, m_LeftRest, new Vector3(0.022f, 0.014f, 0.014f), hand, 4003);
            m_RightHand = Part(PrimitiveType.Sphere, m_RightRest, new Vector3(0.022f, 0.014f, 0.014f), hand, 4003);
            // Instruction + the shout.
            Label("Tap their shoulders and shout", new Vector3(0f, 0.085f, -0.003f), 0.0022f, Color.white);
            m_Shout = Label("\"Are you OK?\"", new Vector3(0.035f, 0.04f, -0.003f), 0.0028f, new Color(1f, 0.85f, 0.3f, 1f));
        }

        void LateUpdate()
        {
            Place(false);
            // Two taps per second: hands dip onto the shoulders and give a small shake.
            var t = ((Time.time - m_ShownAt) % loopSeconds) / loopSeconds * 4f; // 0..4 = four taps per loop
            var phase = t - Mathf.Floor(t);
            var down = phase < 0.4f ? Mathf.Sin(phase / 0.4f * Mathf.PI) : 0f;
            var shake = Mathf.Sin(phase * Mathf.PI * 6f) * 0.003f * down;
            var dip = new Vector3(shake, -0.018f * down, 0f);
            m_LeftHand.localPosition = m_LeftRest + dip;
            m_RightHand.localPosition = m_RightRest + dip;
            m_Shout.gameObject.SetActive(down > 0.15f || phase > 0.85f);
        }

        void Place(bool snap)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.up;
            var target = cam.transform.position + fwd.normalized * distance - Vector3.up * belowEye;
            transform.position = snap ? target : Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-4f * Time.deltaTime));
            var away = transform.position - cam.transform.position;
            if (away.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        Transform Part(PrimitiveType type, Vector3 pos, Vector3 scale, Color color, int queue, float rollDeg = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, rollDeg);
            go.transform.localScale = scale;
            var mat = new Material(Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default")) { color = color, renderQueue = queue };
            mat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always); // on top of the room
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }

        TextMesh Label(string text, Vector3 pos, float size, Color color)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            tm.fontSize = 48;
            tm.characterSize = size;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            tm.text = text;
            var mat = new Material(font.material) { renderQueue = 4004 };
            mat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return tm;
        }
    }
}
