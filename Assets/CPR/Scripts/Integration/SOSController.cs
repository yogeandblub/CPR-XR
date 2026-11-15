using System.Collections;
using UnityEngine;

namespace CPR.Integration
{
    /// <summary>
    /// Voice-started modes and the SOS call:
    ///   Idle      - passthrough only; the mic listens for "SOS" or "PRACTICE".
    ///   "PRACTICE"- practice mannequin + world panel (CPRModeManager.StartPractice).
    ///   "SOS"     - real-person guidance + small top-left text HUD, live camera feed to the laptop, and
    ///               an open question: "What happened? How did they collapse?" Everything said is
    ///               transcribed and parsed by the team's tool; after the answer (pause or "call"), the
    ///               team's call code phones the demo dispatcher with a short summary + location, and the
    ///               headset joins the live voice room. The HUD shows the outgoing call and elapsed time.
    /// Demo dispatcher only (the call backend refuses emergency numbers). Log tag: "CPRSOS".
    /// </summary>
    public class SOSController : MonoBehaviour
    {
        public enum State { Idle, Practice, Asking, Calling, InCall, CallEnded }

        public XrAedClient client;
        public VoiceCommand voice;
        public CPRModeManager modes;
        [Tooltip("Team call package component (hidden voice room).")]
        public QuestCallBridge callBridge;
        public LiveFeed liveFeed;
        public HeadsetLocation headsetLocation;

        [Header("Opening question")]
        [Tooltip("Open-ended on purpose: the more the rescuer describes, the better prepared the responders are.")]
        public string question = "What happened to them?\nTell me how they collapsed.";
        [Tooltip("After the rescuer has said something: call after this long without a new phrase (s).")]
        public float pauseToCall = 6f;
        [Tooltip("Before they've said anything: wait this long for an answer (s).")]
        public float firstAnswerWait = 25f;
        [Tooltip("Call at the latest this long after SOS (s).")]
        public float maxAsk = 45f;
        [Tooltip("Listen to the rescuer's answer this long after SOS, then call (s).")]
        public float listenSeconds = 8f;
        [Tooltip("If they're mid-sentence when that time is up, wait at most this much longer for it (s).")]
        public float finishPhraseGrace = 3f;
        public string[] callWords = { "call now", "call them", "call 911", "call the dispatcher", "send it", "send now", "send", "call", "dispatch" };

        public State CurrentState { get; private set; } = State.Idle;
        float m_ActivatedAt, m_LastPhraseAt, m_CallStartedAt = -1f, m_ConnectedAt = -1f;
        int m_Phrases;
        bool m_Joined;
        /// <summary>True when the current run is practice: same flow, but the call is simulated (never dialed).</summary>
        public bool IsPractice { get; private set; }
        bool m_PracticePending;

        void Start()
        {
            if (voice != null) voice.phraseHandler = OnPhrase;
            // Practice: the question + simulated call start once a dummy has been chosen on the menu.
            if (modes != null) modes.PracticeSpawned += () => { if (m_PracticePending) { m_PracticePending = false; BeginAsking(true); } };
            if (callBridge != null)
            {
                callBridge.onJoined.AddListener(() => m_Joined = true);
                callBridge.onDispatcherConnected.AddListener(() => { CurrentState = State.InCall; m_ConnectedAt = Time.time; Debug.Log("CPRSOS: dispatcher connected"); });
                callBridge.onCallEnded.AddListener(EndCall);
                callBridge.onError.AddListener(e => { Debug.LogWarning($"CPRSOS: call error: {e}"); SosHud.Set("1-call", "Call problem - check Wi-Fi"); EndCall(); });
            }
        }

        void Update()
        {
            switch (CurrentState)
            {
                case State.Asking:
                    // Listen for a fixed few seconds, then call (finishing a sentence in progress first).
                    var asked = Time.time - m_ActivatedAt;
                    var talking = voice != null && voice.IsHearing;
                    if ((asked >= listenSeconds && !talking) || asked >= listenSeconds + finishPhraseGrace)
                        Dispatch(m_Phrases == 0 ? "no answer" : $"listened {asked:F1} s");
                    break;
                case State.Calling:
                    SosHud.Set("1-call", IsPractice ? $"Practice call (simulated)  {Clock(Time.time - m_CallStartedAt)}"
                                                    : $"Calling dispatcher...  {Clock(Time.time - m_CallStartedAt)}");
                    break;
                case State.InCall:
                    SosHud.Set("1-call", $"On call with dispatcher  {Clock(Time.time - m_CallStartedAt)}");
                    break;
            }
        }

        // ---------- voice ----------

        void OnPhrase(XrAedClient.SpeechResult r)
        {
            if (!string.IsNullOrEmpty(r.error) || string.IsNullOrEmpty(r.transcript)) return;
            var t = Normalize(r.transcript);
            if (t.Contains(" restart ")) { KillSwitch.Restart("voice"); return; } // kill switch
            // The mode manager is the truth for "idle" (e.g. the Practice menu's Exit returns there).
            var appIdle = modes == null || modes.CurrentMode == CPRModeManager.Mode.Idle;
            if (appIdle && CurrentState != State.Idle) CurrentState = State.Idle;
            switch (CurrentState)
            {
                case State.Idle:
                case State.Practice:
                    if (IsSOS(t)) StartSOS(r.transcript);
                    else if (appIdle && t.Contains(" practice ")) StartPractice();
                    break;
                case State.Asking:
                    m_LastPhraseAt = Time.time;
                    if (IsSOS(t) && m_Phrases == 0) return; // repeated "SOS"
                    m_Phrases++;
                    if (IsCallWord(t)) Dispatch("asked to call");
                    break;
            }
        }

        void StartPractice()
        {
            Debug.Log("CPRSOS: PRACTICE heard - practice menu (dummy spawns from the menu, then the SOS flow with a simulated call)");
            CurrentState = State.Practice;
            IsPractice = true;
            m_PracticePending = true;
            if (modes != null) modes.OpenPractice(); else { m_PracticePending = false; BeginAsking(true); }
        }

        void StartSOS(string heard)
        {
            Debug.Log($"CPRSOS: SOS heard (\"{heard}\") - SOS mode");
            if (modes != null) modes.StartSOS();
            if (headsetLocation != null) headsetLocation.Begin();
            BeginAsking(false);
        }

        void BeginAsking(bool practice)
        {
            IsPractice = practice;
            CurrentState = State.Asking;
            m_ActivatedAt = m_LastPhraseAt = Time.time;
            m_Phrases = 0;
            m_CallStartedAt = m_ConnectedAt = -1f;
            if (liveFeed != null) liveFeed.Begin();
            SosHud.ClearAll();
            SosHud.Set("0-sos", (practice ? "PRACTICE - " : "SOS - ") + question);
        }

        // ---------- call ----------

        void Dispatch(string why)
        {
            if (CurrentState != State.Asking) return;
            CurrentState = State.Calling;
            m_CallStartedAt = Time.time;
            Debug.Log($"CPRSOS: dispatching ({why}, {m_Phrases} phrase(s))");
            SosHud.Set("0-sos", null);
            SosHud.Set("2-heard", null);
            StartCoroutine(DispatchRoutine());
        }

        IEnumerator DispatchRoutine()
        {
            // 1. Place the call right away; the voice room joins while the dispatcher's phone rings.
            PlaceCall();
            if (IsPractice) yield break; // simulated call: no voice room, keep the mic listening

            // 2. The call server's current https address (changes whenever the team server restarts).
            string url = null;
            if (client != null)
            {
                var got = false;
                client.GetHealth(h => { url = h.call_server_url; got = true; });
                var t = 0f;
                while (!got && t < 6f) { t += Time.unscaledDeltaTime; yield return null; }
            }

            // 3. Hand the mic to the call and join the team's voice room so the dispatcher can be connected.
            if (voice != null) voice.PauseListening();
            if (callBridge != null && !string.IsNullOrEmpty(url))
            {
                m_Joined = false;
                callBridge.serverUrl = url;
                callBridge.JoinAudioOnly();
                var t = 0f;
                while (!m_Joined && t < 15f) { t += Time.unscaledDeltaTime; yield return null; }
                if (!m_Joined) Debug.LogWarning("CPRSOS: voice room not joined after 15 s");
            }
            else Debug.LogWarning($"CPRSOS: no live voice room ({(callBridge == null ? "no call component" : "call server address unknown")})");
        }

        void PlaceCall()
        {
            // Summary of what was said + location -> the team's call to the demo dispatcher.
            if (client == null) return;
            double? lat = null, lon = null; float? acc = null;
            if (headsetLocation != null && headsetLocation.HasFix) { lat = headsetLocation.Latitude; lon = headsetLocation.Longitude; acc = headsetLocation.AccuracyM; }
            client.Dispatch(d =>
            {
                if (!d.ok)
                {
                    Debug.LogWarning($"CPRSOS: dispatch failed: {d.error}");
                    SosHud.Set("1-call", "Could not reach the dispatcher - check the laptop");
                    EndCall();
                    return;
                }
                Debug.Log($"CPRSOS: {(IsPractice ? "practice call simulated" : "call placed")} ({d.status}{(d.mock ? ", MOCK" : "")}, location {d.location_source}): \"{d.announcement}\"");
            }, lat, lon, acc, IsPractice);
        }

        void EndCall()
        {
            if (CurrentState != State.Calling && CurrentState != State.InCall) return;
            var total = m_CallStartedAt >= 0f ? Time.time - m_CallStartedAt : 0f;
            CurrentState = State.CallEnded;
            if (voice != null && voice.IsPaused) voice.ResumeListening();
            SosHud.Set("1-call", $"Call ended  {Clock(total)}");
            Debug.Log($"CPRSOS: call ended after {Clock(total)}");
        }

        /// <summary>Back to idle (e.g. end of a test run).</summary>
        public void ResetToIdle()
        {
            if (liveFeed != null) liveFeed.Stop();
            CurrentState = State.Idle;
            SosHud.ClearAll();
            if (modes != null) modes.SetIdle();
        }

        // ---------- words ----------

        static string Clock(float s) { var t = Mathf.Max(0, Mathf.FloorToInt(s)); return $"{t / 60:00}:{t % 60:00}"; }

        static string Normalize(string s) => " " + s.ToLowerInvariant().Replace('’', '\'').Replace(".", " ").Replace(",", " ").Replace("-", " ").Replace("!", " ").Replace("?", " ") + " ";

        static bool IsSOS(string t) =>
            t.Contains(" sos ") || t.Contains(" s o s ") || t.Contains(" esos ") || t.Contains(" s os ") || t.Contains(" so s ");

        bool IsCallWord(string t)
        {
            foreach (var w in callWords) if (t.Contains(" " + w + " ")) return true;
            return false;
        }

        static string Shorten(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}
