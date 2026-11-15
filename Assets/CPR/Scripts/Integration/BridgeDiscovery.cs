using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CPR.Integration
{
    /// <summary>
    /// Finds the laptop bridge on Wi-Fi with no IP typing: the bridge broadcasts
    /// {"service":"cprxr-bridge","port":8077} on UDP 8078 every 2 s; the first one heard sets the
    /// client's address. Networks that block device-to-device traffic (guest Wi-Fi): the bridge publishes
    /// its Cloudflare tunnel address, scrambled with the shared key, on ntfy.sh under a secret topic; this
    /// reads and unscrambles it and connects over the internet. Local network is preferred when available.
    /// Last resort: the USB link (localhost via adb reverse). Log tag: "CPRNet".
    /// </summary>
    public class BridgeDiscovery : MonoBehaviour
    {
        public XrAedClient client;
        public int beaconPort = 8078;
        [Tooltip("Use the USB link (localhost) if neither the local network nor the relay worked within this time (s).")]
        public float usbFallbackAfter = 20f;
        [Header("Any-Wi-Fi relay (ntfy.sh + Cloudflare tunnel)")]
        public bool useRelay = true;
        public string relayHost = "https://ntfy.sh";
        [Tooltip("Prefer the local network: wait this long for its announcement before using the relay (s).")]
        public float relayAfter = 4f;
        public float relayPollSeconds = 15f;
        string m_RelayUrl;
        float m_NextRelayPoll;
        bool m_RelayBusy;

        [Serializable] class NtfyMessage { public string @event; public string message; }
        [Serializable] class RelayPayload { public int v; public string u; public long t; }

        UdpClient m_Udp;
        Thread m_Thread;
        volatile string m_Found;
        string m_Applied;
        float m_StartedAt;
        AndroidJavaObject m_MulticastLock;

        [Serializable] class Beacon { public string service; public int port; }

        void Start()
        {
            m_StartedAt = Time.time;
            AcquireMulticastLock();
            try
            {
                m_Udp = new UdpClient();
                m_Udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_Udp.Client.Bind(new IPEndPoint(IPAddress.Any, beaconPort));
                m_Thread = new Thread(Listen) { IsBackground = true, Name = "CPRXR bridge discovery" };
                m_Thread.Start();
                Debug.Log($"CPRNet: looking for the laptop bridge on Wi-Fi (UDP {beaconPort})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"CPRNet: discovery unavailable ({e.Message}) - using USB link");
            }
        }

        void Listen()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (m_Udp != null)
            {
                try
                {
                    var data = m_Udp.Receive(ref any);
                    var b = JsonUtility.FromJson<Beacon>(Encoding.UTF8.GetString(data));
                    if (b != null && b.service == "cprxr-bridge" && b.port > 0)
                        m_Found = $"http://{any.Address}:{b.port}/v1";
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch (Exception) { /* ignore malformed packets */ }
            }
        }

        void Update()
        {
            if (client == null) return;
            var found = m_Found;
            if (found != null && found != m_Applied)
            {
                m_Applied = found;
                client.baseUrl = found;
                Debug.Log($"CPRNet: laptop bridge found on Wi-Fi at {found}");
                return;
            }
            // No local announcement: ask the relay where the bridge's tunnel is (and keep checking - it changes on restart).
            if (useRelay && found == null && Time.time - m_StartedAt > relayAfter && Time.time >= m_NextRelayPoll && !m_RelayBusy)
            {
                m_NextRelayPoll = Time.time + relayPollSeconds;
                StartCoroutine(PollRelay());
            }
            if (found == null && m_RelayUrl != null && m_RelayUrl != m_Applied)
            {
                m_Applied = m_RelayUrl;
                client.baseUrl = m_RelayUrl;
                Debug.Log("CPRNet: laptop bridge reached through the internet relay (local network blocked)");
                return;
            }
            if (m_Applied == null && Time.time - m_StartedAt > usbFallbackAfter && string.IsNullOrEmpty(client.baseUrl))
            {
                m_Applied = "usb";
                client.baseUrl = "http://localhost:8077/v1";
                Debug.Log("CPRNet: no Wi-Fi bridge heard - trying the USB link (localhost:8077)");
            }
        }

        System.Collections.IEnumerator PollRelay()
        {
            if (string.IsNullOrEmpty(client.deviceKey)) yield break;
            m_RelayBusy = true;
            var url = $"{relayHost}/{Topic(client.deviceKey)}/json?poll=1&since=12h";
            using (var req = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();
                m_RelayBusy = false;
                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success) { Debug.LogWarning($"CPRNet: relay unreachable ({req.error})"); yield break; }
                // One JSON object per line; the newest message wins.
                string latest = null;
                foreach (var line in req.downloadHandler.text.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var m = JsonUtility.FromJson<NtfyMessage>(line); if (m != null && m.@event == "message" && !string.IsNullOrEmpty(m.message)) latest = m.message; } catch { }
                }
                if (latest == null) yield break;
                try
                {
                    var p = JsonUtility.FromJson<RelayPayload>(latest);
                    var tunnel = Unscramble(p.u, client.deviceKey);
                    if (tunnel.StartsWith("https://")) m_RelayUrl = tunnel.TrimEnd('/') + "/v1";
                }
                catch (Exception e) { Debug.LogWarning($"CPRNet: relay message unreadable ({e.Message})"); }
            }
        }

        static string Topic(string key)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes("topic:" + key));
            var sb = new StringBuilder("cprxr-");
            for (var i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Reverse of the bridge's scramble(): XOR with SHA-256("relay:{key}:{i}") blocks.</summary>
        static string Unscramble(string b64, string key)
        {
            var data = Convert.FromBase64String(b64);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var outBytes = new byte[data.Length];
            for (var i = 0; i < data.Length; i += 32)
            {
                var block = sha.ComputeHash(Encoding.UTF8.GetBytes($"relay:{key}:{i / 32}"));
                for (var j = 0; j < 32 && i + j < data.Length; j++) outBytes[i + j] = (byte)(data[i + j] ^ block[j]);
            }
            return Encoding.UTF8.GetString(outBytes);
        }

        /// <summary>Some Android builds drop broadcast packets unless a multicast lock is held.</summary>
        void AcquireMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                m_MulticastLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "cprxr-discovery");
                m_MulticastLock.Call("acquire");
            }
            catch (Exception e) { Debug.LogWarning($"CPRNet: multicast lock unavailable ({e.Message})"); }
#endif
        }

        void OnDestroy()
        {
            var u = m_Udp; m_Udp = null;
            u?.Close();
            try { m_MulticastLock?.Call("release"); } catch { }
        }
    }
}
