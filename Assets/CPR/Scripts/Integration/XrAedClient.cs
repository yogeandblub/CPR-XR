using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CPR.Integration
{
    /// <summary>
    /// Unity side of the team's XR-AED dispatcher pipeline (separate repo: immerse_the_bay_2026).
    /// Matches their contract at commit 44374ec: api/openapi.yaml + api/request-examples.json.
    ///
    /// Rules from their DESIGN / TEAM_HANDOFF that this client follows:
    ///   - Unity sends only: final speech transcripts, explicit help requests, location updates,
    ///     and a TEST-dispatcher call request. It never sends patient facts or camera/CV output.
    ///   - Every event uses the shared envelope: event_id, session_id, type, source,
    ///     observed_at, received_at, payload.
    ///   - The call path never waits on AI / CV / knowledge base.
    ///   - No secrets in the app: the bearer token is set at runtime, never committed.
    ///
    /// With <see cref="baseUrl"/> empty (default) nothing is sent: each request is logged as
    /// "CPRNet: would POST ..." with its exact JSON, for checking against their examples.
    /// Log tag: "CPRNet".
    /// </summary>
    public class XrAedClient : MonoBehaviour
    {
        public const string ContractVersion = "immerse_the_bay_2026@44374ec";

        [Tooltip("e.g. https://staging.example.com/v1  - empty = offline (log only).")]
        public string baseUrl = "";
        [Tooltip("Shared key for the laptop bridge on Wi-Fi (bridge/config.json device_key).")]
        public string deviceKey = "";
        [Tooltip("Set at runtime from a secure source. Never commit a real token.")]
        [NonSerialized] public string bearerToken = "";
        public float timeoutSeconds = 8f;
        [Tooltip("Create a session automatically when the app starts.")]
        public bool autoStartSession = true;

        public string SessionId { get; private set; }
        public bool IsOnline => !string.IsNullOrEmpty(baseUrl);
        int m_EventCounter;

        /// <summary>What the laptop bridge heard and extracted from one spoken phrase.</summary>
        [Serializable]
        public class SpeechResult
        {
            public string transcript;
            public float confidence;
            public string[] fact_strings;   // e.g. "patient.responsive=false" (team transcriber facts)
            public int stt_ms;
            public string spoken_text;      // the team dispatcher briefing after this line
            public string error;
        }

        [Serializable]
        public class HealthResult { public bool ok; public bool speech_model_ready; public string call_server_url; public bool call_ready; public bool call_mock; public string error; }

        [Serializable]
        public class DispatchResult { public bool ok; public string announcement; public string call_sid; public bool mock; public string status; public string location_source; public string error; }

        /// <summary>Bridge status, including the call server's current https address.</summary>
        public void GetHealth(Action<HealthResult> done)
        {
            if (!IsOnline) { done?.Invoke(new HealthResult { error = "offline" }); return; }
            StartCoroutine(GetJson($"{baseUrl.TrimEnd('/')}/health", done));
        }

        /// <summary>Short summary + coordinates -> the team's demo dispatcher call (bridge -> QuestCPR backend).
        /// Pass the headset's own location if it has one; otherwise the bridge uses the laptop's.</summary>
        public void Dispatch(Action<DispatchResult> done, double? lat = null, double? lon = null, float? accuracyM = null, bool practice = false)
        {
            if (!HasSession()) return;
            if (!IsOnline) { Debug.Log("CPRNet: would POST dispatch"); done?.Invoke(new DispatchResult { error = "offline" }); return; }
            var body = new Json().Obj().Num("latitude", lat).Num("longitude", lon).Num("accuracy_m", accuracyM).Bool("practice", practice).End();
            StartCoroutine(PostJson($"{baseUrl.TrimEnd('/')}/sessions/{SessionId}/dispatch", body, done, 30));
        }

        /// <summary>Headset log lines -> bridge/headset.log (debugging without a USB cable).</summary>
        public void PostLog(string lines)
        {
            if (!IsOnline || string.IsNullOrEmpty(lines)) return;
            StartCoroutine(PostRaw($"{baseUrl.TrimEnd('/')}/log", Encoding.UTF8.GetBytes(lines), "text/plain", 5));
        }

        bool m_FrameInFlight;
        /// <summary>One JPEG camera frame for the laptop's live feed. Skipped while the previous one is still sending.</summary>
        public void PostFrame(byte[] jpg)
        {
            if (!IsOnline || m_FrameInFlight || jpg == null) return;
            m_FrameInFlight = true;
            StartCoroutine(PostRaw($"{baseUrl.TrimEnd('/')}/feed", jpg, "image/jpeg", 3, err =>
            {
                m_FrameInFlight = false;
                if (err == null) { FramesDelivered++; return; }
                if (Time.time - m_LastFrameErrorLog > 10f) { m_LastFrameErrorLog = Time.time; Debug.LogWarning($"CPRFeed: frame upload failed ({err})"); }
            }));
        }
        /// <summary>Camera frames the laptop has accepted (live feed).</summary>
        public int FramesDelivered { get; private set; }
        float m_LastFrameErrorLog = -100f;

        IEnumerator PostRaw(string url, byte[] data, string contentType, int timeout, Action<string> done = null)
        {
            using var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(data),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeout,
            };
            req.SetRequestHeader("Content-Type", contentType);
            Auth(req);
            yield return req.SendWebRequest();
            done?.Invoke(req.result == UnityWebRequest.Result.Success ? null : $"{req.responseCode} {req.error}");
        }

        void Auth(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(deviceKey)) req.SetRequestHeader("X-CPRXR-Key", deviceKey);
        }

        IEnumerator GetJson<T>(string url, Action<T> done) where T : new()
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 5;
            Auth(req);
            yield return req.SendWebRequest();
            done?.Invoke(Parse<T>(req));
        }

        IEnumerator PostJson<T>(string url, string json, Action<T> done, int timeout) where T : new()
        {
            using var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeout,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            Auth(req);
            yield return req.SendWebRequest();
            done?.Invoke(Parse<T>(req));
        }

        static T Parse<T>(UnityWebRequest req) where T : new()
        {
            var text = req.downloadHandler?.text;
            if (string.IsNullOrEmpty(text))
            {
                var empty = new T();
                typeof(T).GetField("error")?.SetValue(empty, $"{req.responseCode} {req.error}");
                return empty;
            }
            try { return JsonUtility.FromJson<T>(text); }
            catch (Exception e) { var bad = new T(); typeof(T).GetField("error")?.SetValue(bad, e.Message); return bad; }
        }

        void Start()
        {
            if (autoStartSession && string.IsNullOrEmpty(SessionId)) CreateSession();
        }

        /// <summary>
        /// Bridge-only extension: send one spoken phrase (16 kHz mono PCM16 WAV). The laptop transcribes it
        /// with the team's local speech-to-text, stores it as their speech_transcript event, and returns the
        /// transcript + facts their extractor found. Offline: logs and returns nothing.
        /// </summary>
        public void PostSpeech(byte[] wav, Action<SpeechResult> done, string source = "operator_report")
        {
            if (!HasSession()) return;
            if (!IsOnline) { Debug.Log($"CPRNet: would POST speech ({wav.Length / 1024} kB)"); return; }
            StartCoroutine(SpeechRoutine($"{baseUrl.TrimEnd('/')}/sessions/{SessionId}/speech?source={source}", wav, done));
        }

        IEnumerator SpeechRoutine(string url, byte[] wav, Action<SpeechResult> done)
        {
            using var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(wav),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 20, // transcription takes ~1-2 s on the laptop
            };
            req.SetRequestHeader("Content-Type", "audio/wav");
            Auth(req);
            yield return req.SendWebRequest();
            SpeechResult result;
            if (req.result != UnityWebRequest.Result.Success)
                result = new SpeechResult { error = $"{req.responseCode} {req.error}" };
            else
                try { result = JsonUtility.FromJson<SpeechResult>(req.downloadHandler.text); }
                catch (Exception e) { result = new SpeechResult { error = "bad response: " + e.Message }; }
            if (!string.IsNullOrEmpty(result.error)) Debug.LogWarning($"CPRNet: speech failed ({result.error})");
            done?.Invoke(result);
        }

        // ---------- Public API ----------

        /// <summary>POST /sessions - start an emergency simulation session (location unknown unless supplied).</summary>
        public void CreateSession(string clientSessionId = null)
        {
            SessionId = clientSessionId ?? $"sim_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            m_EventCounter = 0;
            var body = new Json().Obj()
                .Str("mode", "emergency_simulation")
                .Str("client_session_id", SessionId)
                .Raw("location", LocationJson(null, null, null, "unknown", null))
                .End();
            Send("POST", "/sessions", body);
        }

        /// <summary>Explicit help request from a UI action (the only fact Unity may assert, per the example).</summary>
        public void SendHelpRequest()
        {
            var payload = new Json().Obj().Raw("facts", "[{\"field\":\"incident.help_requested\",\"value\":true}]").End();
            SendEvent("help_request", "app", payload);
        }

        /// <summary>A FINAL Meta speech-to-text line. source = operator_report (headset wearer) or dispatcher_confirmed.</summary>
        public void SendTranscript(string text, float? confidence, string source = "operator_report", string sttProvider = "meta_unity_ai_stt")
        {
            if (source != "operator_report" && source != "dispatcher_confirmed")
            {
                Debug.LogWarning($"CPRNet: refused transcript with source '{source}' (contract allows operator_report / dispatcher_confirmed)");
                return;
            }
            var payload = new Json().Obj()
                .Str("transcript", text)
                .Str("stt_provider", sttProvider)
                .Bool("is_final", true)
                .Num("confidence", confidence)
                .End();
            SendEvent("speech_transcript", source, payload);
        }

        /// <summary>Authorized location update (e.g. from a paired phone). Unknown values stay null.</summary>
        public void SendLocation(double? lat, double? lon, double? accuracyM, string source = "companion_phone")
        {
            var payload = new Json().Obj()
                .Num("lat", lat).Num("lon", lon).Num("accuracy_m", accuracyM)
                .Str("status", lat.HasValue && lon.HasValue ? "fix" : "unavailable")
                .End();
            SendEvent("location_update", source, payload);
        }

        /// <summary>POST /sessions/{id}/calls/test - the allowlisted TEST dispatcher only.</summary>
        public void RequestTestCall()
        {
            if (!HasSession()) return;
            var body = new Json().Obj()
                .Str("destination_id", "test_dispatcher")
                .Str("audio_route", "paired_phone")
                .Bool("simulation_only", true)
                .End();
            Send("POST", $"/sessions/{SessionId}/calls/test", body, idempotencyKey: $"{SessionId}_testcall_001");
        }

        // ---------- Envelope ----------

        void SendEvent(string type, string source, string payloadJson)
        {
            if (!HasSession()) return;
            var now = Iso(DateTime.UtcNow);
            var body = new Json().Obj()
                .Str("event_id", $"evt_{++m_EventCounter:000}")
                .Str("session_id", SessionId)
                .Str("type", type)
                .Str("source", source)
                .Str("observed_at", now)
                .Str("received_at", now)
                .Raw("payload", payloadJson)
                .End();
            Send("POST", $"/sessions/{SessionId}/events", body);
        }

        bool HasSession()
        {
            if (!string.IsNullOrEmpty(SessionId)) return true;
            Debug.LogWarning("CPRNet: no session yet - call CreateSession() first");
            return false;
        }

        void Send(string method, string path, string json, string idempotencyKey = null)
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                Debug.Log($"CPRNet: would {method} {path} {json}");
                return;
            }
            StartCoroutine(SendRoutine(method, baseUrl.TrimEnd('/') + path, json, idempotencyKey));
        }

        IEnumerator SendRoutine(string method, string url, string json, string idempotencyKey)
        {
            using var req = new UnityWebRequest(url, method)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.CeilToInt(timeoutSeconds),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            Auth(req);
            if (!string.IsNullOrEmpty(bearerToken)) req.SetRequestHeader("Authorization", "Bearer " + bearerToken);
            if (!string.IsNullOrEmpty(idempotencyKey)) req.SetRequestHeader("Idempotency-Key", idempotencyKey);
            yield return req.SendWebRequest();

            // Keep the app usable whatever happens: log, never block the UI or the call path.
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"CPRNet: {method} {url} failed ({req.responseCode} {req.error})");
            else
                Debug.Log($"CPRNet: {method} {url} -> {req.responseCode} (accepted is not 'connected'; check the response)");
        }

        // ---------- JSON helpers (JsonUtility can't write nulls) ----------

        static string LocationJson(double? lat, double? lon, double? acc, string source, string capturedAt) =>
            new Json().Obj().Num("lat", lat).Num("lon", lon).Num("accuracy_m", acc).Str("source", source).Str("captured_at", capturedAt).End();

        static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        sealed class Json
        {
            readonly StringBuilder m_Sb = new StringBuilder();
            bool m_First = true;
            public Json Obj() { m_Sb.Append('{'); return this; }
            public string End() { m_Sb.Append('}'); return m_Sb.ToString(); }
            Json Key(string k) { if (!m_First) m_Sb.Append(','); m_First = false; m_Sb.Append('"').Append(k).Append("\":"); return this; }
            public Json Str(string k, string v) { Key(k); if (v == null) m_Sb.Append("null"); else m_Sb.Append('"').Append(Escape(v)).Append('"'); return this; }
            public Json Bool(string k, bool v) { Key(k); m_Sb.Append(v ? "true" : "false"); return this; }
            public Json Num(string k, double? v) { Key(k); m_Sb.Append(v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : "null"); return this; }
            public Json Num(string k, float? v) => Num(k, v.HasValue ? (double?)Math.Round(v.Value, 4) : null); // 0.88f, not 0.8799999952
            public Json Raw(string k, string json) { Key(k); m_Sb.Append(json); return this; }
            static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
