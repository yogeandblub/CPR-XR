using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CPR.Integration
{
    /// <summary>
    /// SOS live feed: sends the headset's passthrough camera to the laptop bridge as JPEG frames
    /// (~5 fps, 640x480). The laptop shows it at http://localhost:8077/live next to what the rescuer said
    /// and the call status. Shows the real scene (not the app's overlays). Log tag: "CPRFeed".
    /// </summary>
    public class LiveFeed : MonoBehaviour
    {
        public XrAedClient client;
        public PassthroughCameraAccess cameraAccess;
        public float framesPerSecond = 5f;
        public int width = 640, height = 480;
        [Range(10, 95)] public int jpegQuality = 60;

        public bool IsStreaming { get; private set; }
        RenderTexture m_Rt;
        float m_Next;
        bool m_Busy;
        int m_Sent;
        float m_LastNoCamLog = -100f;

        public void Begin()
        {
            if (IsStreaming) return;
            IsStreaming = true;
            m_Sent = 0;
            Debug.Log($"CPRFeed: live camera feed started ({framesPerSecond} fps, {width}x{height})");
        }

        public void Stop()
        {
            if (!IsStreaming) return;
            IsStreaming = false;
            Debug.Log($"CPRFeed: live camera feed stopped after {m_Sent} frames");
        }

        void Update()
        {
            if (!IsStreaming || m_Busy || Time.time < m_Next || client == null || !client.IsOnline) return;
            if (cameraAccess == null || !cameraAccess.IsPlaying)
            {
                if (Time.time - m_LastNoCamLog > 10f) { m_LastNoCamLog = Time.time; Debug.LogWarning($"CPRFeed: no camera image ({(cameraAccess == null ? "no camera component" : "camera not playing")})"); }
                return;
            }
            var tex = cameraAccess.GetTexture();
            if (tex == null) return;
            m_Next = Time.time + 1f / Mathf.Max(0.5f, framesPerSecond);

            if (m_Rt == null) m_Rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, m_Rt);
            m_Busy = true;
            AsyncGPUReadback.Request(m_Rt, 0, TextureFormat.RGBA32, OnReadback); // no GPU stall
        }

        void OnReadback(AsyncGPUReadbackRequest req)
        {
            m_Busy = false;
            if (req.hasError || !IsStreaming) return;
            using NativeArray<byte> jpg = ImageConversion.EncodeNativeArrayToJPG(req.GetData<byte>(), GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, jpegQuality);
            client.PostFrame(jpg.ToArray());
            m_Sent++;
            if (m_Sent % 50 == 0) Debug.Log($"CPRFeed: {m_Sent} frames captured, {client.FramesDelivered} delivered to the laptop");
        }

        void OnDestroy()
        {
            if (m_Rt != null) m_Rt.Release();
        }
    }
}
