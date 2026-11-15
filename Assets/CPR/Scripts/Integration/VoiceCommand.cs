using System;
using System.IO;
using UnityEngine;

namespace CPR.Integration
{
    /// <summary>
    /// Hands-free voice: listens on the headset microphone, cuts each spoken phrase out with a simple
    /// voice-activity detector, and sends it to the laptop bridge, which transcribes it with the team's
    /// local speech-to-text and extracts facts with the team's transcriber.
    ///
    /// When a phrase says the person is unresponsive / not breathing (team facts
    /// patient.responsive=false or patient.breathing=absent|gasping - or a trigger phrase such as
    /// "isn't breathing" that their extractor doesn't catch yet), Default mode starts a fresh scan.
    /// The trigger only starts the scan; facts stay the team pipeline's job.
    /// Log tag: "CPRVoice".
    /// </summary>
    public class VoiceCommand : MonoBehaviour
    {
        public XrAedClient client;
        public CPRModeManager modes;

        [Header("Voice activity detection")]
        [Tooltip("Speech starts when loudness is this many times the background noise...")]
        public float startRatio = 3.5f;
        [Tooltip("...and above this absolute level (RMS, 0-1).")]
        public float minStartLevel = 0.012f;
        [Tooltip("A phrase ends after this much quiet (s).")]
        public float endSilence = 0.8f;
        [Tooltip("Longest phrase sent (s).")]
        public float maxPhrase = 8f;
        [Tooltip("Shortest phrase sent (s) - coughs and clicks are dropped.")]
        public float minPhrase = 0.5f;
        [Tooltip("Process while the user is still talking: once a phrase is this long (s)...")]
        public float chunkAfter = 2.5f;
        [Tooltip("...send it at the next short gap between words (s) and keep listening.")]
        public float chunkGap = 0.3f;

        [Header("Trigger")]
        [Tooltip("Fallback phrases that start a scan even if the team extractor finds no fact.")]
        public string[] triggerPhrases = { "isn't breathing", "not breathing", "stopped breathing", "passed out", "unconscious", "unresponsive", "collapsed", "won't wake up", "not waking up" };

        /// <summary>If set (SOS flow), every recognised phrase goes here instead of the built-in scan trigger.</summary>
        public Action<XrAedClient.SpeechResult> phraseHandler;
        /// <summary>True while the mic is handed to the live call.</summary>
        public bool IsPaused { get; private set; }
        /// <summary>True while someone is mid-phrase or a phrase is still being transcribed.</summary>
        public bool IsHearing => m_InPhrase || m_Waiting || m_Queue.Count > 0;

        /// <summary>Release the microphone (the live dispatcher call needs it).</summary>
        public void PauseListening()
        {
            IsPaused = true;
            m_InPhrase = false;
            if (m_Clip != null) Microphone.End(m_Device);
            m_Clip = null;
            Debug.Log("CPRVoice: paused (mic handed to the call)");
        }

        public void ResumeListening()
        {
            IsPaused = false;
            Debug.Log("CPRVoice: resumed");
        }

        const int Rate = 16000;
        const int BufferSeconds = 20;
        AudioClip m_Clip;
        string m_Device;
        int m_ReadPos;
        float m_Noise = 0.005f;
        bool m_InPhrase;
        int m_PhraseStart; // absolute sample index (wraps handled via ring copy)
        float m_Quiet, m_PhraseLen;
        readonly float[] m_Window = new float[Rate / 20]; // 50 ms
        bool m_Waiting;
        readonly System.Collections.Generic.Queue<byte[]> m_Queue = new System.Collections.Generic.Queue<byte[]>();

        void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
        }

        void Update()
        {
            if (IsPaused) return;
            if (m_Clip == null) { TryStartMic(); return; }
            var pos = Microphone.GetPosition(m_Device);
            var available = (pos - m_ReadPos + m_Clip.samples) % m_Clip.samples;
            while (available >= m_Window.Length)
            {
                m_Clip.GetData(m_Window, m_ReadPos);
                ProcessWindow(Rms(m_Window));
                m_ReadPos = (m_ReadPos + m_Window.Length) % m_Clip.samples;
                available -= m_Window.Length;
            }
        }

        void TryStartMic()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone)) return;
#endif
            if (Microphone.devices.Length == 0) return;
            m_Device = Microphone.devices[0];
            m_Clip = Microphone.Start(m_Device, true, BufferSeconds, Rate);
            m_ReadPos = 0;
            Debug.Log($"CPRVoice: listening on '{m_Device}' ({(client != null && client.IsOnline ? "bridge " + client.baseUrl : "offline - phrases are only logged")})");
            if (phraseHandler == null) CPRHud.Set("5-voice", "Listening... say what's happening", 4f);
        }

        void ProcessWindow(float level)
        {
            var dt = (float)m_Window.Length / Rate;
            if (!m_InPhrase)
            {
                // Track background noise slowly; start a phrase on a clear jump in loudness.
                m_Noise = Mathf.Lerp(m_Noise, level, 0.02f);
                if (level > Mathf.Max(minStartLevel, m_Noise * startRatio))
                {
                    m_InPhrase = true;
                    m_Quiet = 0f; m_PhraseLen = 0f;
                    // Include ~300 ms before the trigger so the first syllable isn't cut.
                    m_PhraseStart = (m_ReadPos - Rate * 3 / 10 + m_Clip.samples) % m_Clip.samples;
                }
                return;
            }

            m_PhraseLen += dt;
            m_Quiet = level < Mathf.Max(minStartLevel * 0.6f, m_Noise * startRatio * 0.6f) ? m_Quiet + dt : 0f;
            if (m_PhraseLen >= chunkAfter && m_Quiet >= chunkGap && m_Quiet < endSilence)
            {
                // Still talking: send what's been said so far and carry on listening (no gap in the audio).
                var cut = (m_ReadPos + m_Window.Length) % m_Clip.samples;
                SendPhrase(m_PhraseStart, cut);
                m_PhraseStart = cut;
                m_PhraseLen = 0f;
                return;
            }
            if (m_Quiet >= endSilence || m_PhraseLen >= maxPhrase)
            {
                m_InPhrase = false;
                var end = (m_ReadPos + m_Window.Length) % m_Clip.samples;
                if (m_PhraseLen - m_Quiet >= minPhrase) SendPhrase(m_PhraseStart, end);
            }
        }

        void SendPhrase(int start, int end)
        {
            var count = (end - start + m_Clip.samples) % m_Clip.samples;
            var samples = new float[count];
            // Ring buffer: copy in up to two parts.
            var first = Mathf.Min(count, m_Clip.samples - start);
            var tmp = new float[first];
            m_Clip.GetData(tmp, start);
            Array.Copy(tmp, samples, first);
            if (count > first)
            {
                var rest = new float[count - first];
                m_Clip.GetData(rest, 0);
                Array.Copy(rest, 0, samples, first, rest.Length);
            }

            if (client == null) return;
            m_Queue.Enqueue(ToWav(samples)); // in order, none dropped
            SendNext();
        }

        void SendNext()
        {
            if (m_Waiting || m_Queue.Count == 0 || client == null) return;
            m_Waiting = true;
            if (phraseHandler == null) CPRHud.Set("5-voice", "...", 10f);
            var t0 = Time.realtimeSinceStartup;
            client.PostSpeech(m_Queue.Dequeue(), r => { m_Waiting = false; OnResult(r, Time.realtimeSinceStartup - t0); SendNext(); });
            if (!client.IsOnline) { m_Waiting = false; m_Queue.Clear(); }
        }

        void OnResult(XrAedClient.SpeechResult r, float roundTrip)
        {
            if (phraseHandler != null)
            {
                if (string.IsNullOrEmpty(r.error) && !string.IsNullOrEmpty(r.transcript))
                    Debug.Log($"CPRVoice: heard \"{r.transcript}\" ({r.stt_ms} ms on laptop, {roundTrip * 1000f:F0} ms round trip) facts [{string.Join(", ", r.fact_strings ?? Array.Empty<string>())}]");
                phraseHandler(r);
                return;
            }
            if (!string.IsNullOrEmpty(r.error))
            {
                CPRHud.Set("5-voice", "Voice: laptop bridge not reachable", 4f);
                return;
            }
            if (string.IsNullOrEmpty(r.transcript)) { CPRHud.Set("5-voice", null); return; }

            var facts = r.fact_strings ?? Array.Empty<string>();
            var trigger = TriggerReason(r.transcript, facts);
            Debug.Log($"CPRVoice: heard \"{r.transcript}\" ({r.stt_ms} ms on laptop, {roundTrip * 1000f:F0} ms round trip) " +
                      $"facts [{string.Join(", ", facts)}]" + (trigger != null ? $" -> TRIGGER ({trigger})" : ""));
            CPRHud.Set("5-voice", $"You said: \"{r.transcript}\"" + (trigger != null ? "\nStarting scan..." : ""), 5f);

            if (trigger != null && modes != null) modes.VoiceTrigger(trigger);
        }

        /// <summary>Team facts first; phrase fallback only for wording their extractor misses.</summary>
        string TriggerReason(string transcript, string[] facts)
        {
            foreach (var f in facts)
                if (f == "patient.responsive=false" || f == "patient.breathing=absent" || f == "patient.breathing=gasping")
                    return f;
            var t = transcript.ToLowerInvariant().Replace('’', '\'');
            foreach (var p in triggerPhrases)
                if (t.Contains(p)) return $"phrase '{p}'";
            return null;
        }

        static float Rms(float[] x)
        {
            double s = 0;
            for (var i = 0; i < x.Length; i++) s += x[i] * x[i];
            return Mathf.Sqrt((float)(s / x.Length));
        }

        /// <summary>16 kHz mono PCM16 WAV.</summary>
        static byte[] ToWav(float[] samples)
        {
            using var ms = new MemoryStream(44 + samples.Length * 2);
            using var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + samples.Length * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E' }); w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), short.MinValue, short.MaxValue));
            return ms.ToArray();
        }

        void OnDisable()
        {
            if (m_Clip != null) Microphone.End(m_Device);
            m_Clip = null;
        }
    }
}
