using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// SOS mode's text-only HUD: small text fixed at the top-center of the view (head-locked, drawn on top), so nothing
    /// takes up space in the room. Lines are posted per channel and shown in channel order:
    ///   SosHud.Set("1-call", "On call with dispatcher 01:12");
    /// </summary>
    public class SosHud : MonoBehaviour
    {
        [Tooltip("Position relative to the eyes (m): x = left/right, y = up/down, z = distance.")]
        public Vector3 offset = new Vector3(0f, 0.07f, 0.55f); // centered, a little above the middle of the view
        public float characterSize = 0.0048f;

        static SosHud s_Instance;
        readonly SortedDictionary<string, string> m_Lines = new SortedDictionary<string, string>();
        TextMesh m_Text;
        MeshRenderer m_TextRenderer;
        Transform m_Back;
        Transform m_Head;
        bool m_Dirty;

        public static void Set(string channel, string text)
        {
            var hud = Instance;
            if (hud == null) return;
            if (string.IsNullOrEmpty(text)) { if (hud.m_Lines.Remove(channel)) hud.m_Dirty = true; return; }
            if (!hud.m_Lines.TryGetValue(channel, out var old) || old != text) { hud.m_Lines[channel] = text; hud.m_Dirty = true; }
        }

        public static void ClearAll()
        {
            if (s_Instance == null) return;
            s_Instance.m_Lines.Clear();
            s_Instance.m_Dirty = true;
        }

        static SosHud Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                if (!Application.isPlaying) return null;
                s_Instance = new GameObject("SOS HUD").AddComponent<SosHud>();
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
            // Drawn on top of everything (no depth test), so nothing in the room covers it: it reads as a
            // HUD on the view rather than a panel in the room.
            var textMat = new Material(font.material) { renderQueue = 4001 };
            textMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            m_TextRenderer = textGo.GetComponent<MeshRenderer>();
            m_TextRenderer.sharedMaterial = textMat;
            m_Text.fontSize = 48;
            m_Text.characterSize = characterSize;
            m_Text.anchor = TextAnchor.UpperCenter;
            m_Text.alignment = TextAlignment.Center;
            m_Text.color = Color.white;

            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Backing";
            bg.transform.SetParent(transform, false);
            var bgMat = new Material(Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default")) { color = new Color(0f, 0f, 0f, 0.45f), renderQueue = 4000 };
            bgMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            bg.GetComponent<Renderer>().sharedMaterial = bgMat;
            m_Back = bg.transform;
        }

        void LateUpdate()
        {
            if (m_Head == null && Camera.main != null)
            {
                m_Head = Camera.main.transform;
                transform.SetParent(m_Head, false); // head-locked
            }
            transform.localPosition = offset;
            transform.localRotation = Quaternion.identity;

            if (m_Dirty)
            {
                var sb = new StringBuilder();
                foreach (var kv in m_Lines) { if (sb.Length > 0) sb.Append('\n'); sb.Append(kv.Value); }
                m_Text.text = sb.ToString();
                m_Dirty = false;
            }
            // The dark box hugs the text exactly (measured from the rendered text, small padding).
            var tb = m_TextRenderer != null ? m_TextRenderer.localBounds : default;
            if (tb.size.x > 1e-4f)
            {
                const float pad = 0.008f;
                m_Back.localScale = new Vector3(tb.size.x + 2f * pad, tb.size.y + 2f * pad, 1f);
                m_Back.localPosition = new Vector3(tb.center.x, tb.center.y, 0.001f);
            }
            var visible = m_Lines.Count > 0;
            m_Text.gameObject.SetActive(visible);
            m_Back.gameObject.SetActive(visible);
        }
    }
}
