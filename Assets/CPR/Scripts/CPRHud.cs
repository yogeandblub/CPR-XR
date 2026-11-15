using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Temporary floating instruction panel: stays ~0.8 m in front of the user, a little below eye
    /// level, and lazily follows the head - so it never ends up inside your body or the patient.
    /// Any component can post a line under its own channel: CPRHud.Set("recorder", "Hold still").
    /// Placeholder for the real UI you'll design.
    /// </summary>
    public class CPRHud : MonoBehaviour
    {
        static CPRHud s_Instance;
        readonly SortedDictionary<string, string> m_Lines = new SortedDictionary<string, string>();
        readonly Dictionary<string, float> m_Expiry = new Dictionary<string, float>();
        TextMesh m_Text;
        Transform m_Background;
        bool m_Dirty;

        public float distance = 0.62f;   // ~2 ft
        public float belowEye = -0.04f;  // just above eye level; the Practice menu sits below it

        /// <summary>Hide the world panel entirely (SOS mode uses the small top-left text HUD instead).</summary>
        public static bool Hidden;
        public float followSpeed = 3f;

        /// <summary>Show (or with null/empty, clear) a line. Optional seconds = auto-clear.</summary>
        public static void Set(string channel, string text, float seconds = 0f)
        {
            var hud = Instance;
            if (hud == null) return;
            if (string.IsNullOrEmpty(text))
            {
                if (hud.m_Lines.Remove(channel)) hud.m_Dirty = true;
                hud.m_Expiry.Remove(channel);
                return;
            }
            if (!hud.m_Lines.TryGetValue(channel, out var old) || old != text) { hud.m_Lines[channel] = text; hud.m_Dirty = true; }
            if (seconds > 0f) hud.m_Expiry[channel] = Time.time + seconds; else hud.m_Expiry.Remove(channel);
        }

        /// <summary>Remove every line (idle = nothing on screen).</summary>
        public static void ClearAll()
        {
            if (s_Instance == null) return;
            s_Instance.m_Lines.Clear();
            s_Instance.m_Expiry.Clear();
            s_Instance.m_Dirty = true;
        }

        static CPRHud Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                if (!Application.isPlaying) return null;
                s_Instance = new GameObject("CPR HUD").AddComponent<CPRHud>();
                return s_Instance;
            }
        }

        void Awake()
        {
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(transform, false);
            m_Text = textGo.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Text.font = font;
            textGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            m_Text.fontSize = 48;
            m_Text.characterSize = 0.0075f;
            m_Text.anchor = TextAnchor.MiddleCenter;
            m_Text.alignment = TextAlignment.Center;
            m_Text.color = Color.white;

            // Dark backing card so text reads over bright passthrough.
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Background";
            bg.transform.SetParent(transform, false);
            bg.transform.localPosition = new Vector3(0f, 0f, 0.002f);
            var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0f, 0f, 0f, 0.55f) };
            bg.GetComponent<Renderer>().sharedMaterial = mat;
            m_Background = bg.transform;
            gameObject.SetActive(true);
        }

        void LateUpdate()
        {
            // Expire timed lines.
            if (m_Expiry.Count > 0)
            {
                List<string> done = null;
                foreach (var kv in m_Expiry) if (Time.time > kv.Value) (done ??= new List<string>()).Add(kv.Key);
                if (done != null) foreach (var key in done) { m_Lines.Remove(key); m_Expiry.Remove(key); m_Dirty = true; }
            }

            if (m_Dirty)
            {
                var sb = new StringBuilder();
                foreach (var kv in m_Lines) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(kv.Value); }
                m_Text.text = sb.ToString();
                var lines = Mathf.Max(1, m_Text.text.Split('\n').Length);
                var widest = 0; foreach (var l in m_Text.text.Split('\n')) widest = Mathf.Max(widest, l.Length);
                m_Background.localScale = new Vector3(0.02f + widest * 0.0178f, 0.02f + lines * 0.039f, 1f);
                m_Dirty = false;
            }
            var visible = m_Lines.Count > 0 && !Hidden;
            m_Text.gameObject.SetActive(visible);
            m_Background.gameObject.SetActive(visible);
            if (!visible) return;

            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.up;
            var target = cam.transform.position + fwd.normalized * distance - Vector3.up * belowEye;
            var k = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, k);
            var away = transform.position - cam.transform.position;
            if (away.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
