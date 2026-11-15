using UnityEngine;

namespace CPR
{
    /// <summary>120 bpm metronome to keep compressions on beat (AHA: 100-120/min). Generated click, no asset.</summary>
    public class Metronome : MonoBehaviour
    {
        public float bpm = 120f;
        [Range(0, 1)] public float volume = 0.6f;

        AudioSource m_Source;
        AudioClip m_Tick;
        double m_NextTick;
        public bool IsPlaying { get; private set; }

        void Awake()
        {
            m_Source = gameObject.AddComponent<AudioSource>();
            m_Source.playOnAwake = false;
            m_Source.spatialBlend = 0f; // in the ears, not placed in the room
            const int rate = 44100;
            var n = rate / 25; // 40 ms
            var data = new float[n];
            for (var i = 0; i < n; i++) { var t = (float)i / rate; data[i] = Mathf.Sin(2f * Mathf.PI * 1500f * t) * Mathf.Exp(-t / 0.008f); }
            m_Tick = AudioClip.Create("MetronomeTick", n, 1, rate, false);
            m_Tick.SetData(data, 0);
        }

        public void Play()
        {
            if (IsPlaying) return;
            IsPlaying = true;
            m_NextTick = AudioSettings.dspTime + 0.1;
            Debug.Log($"CPRMetronome: on ({bpm} bpm)");
        }

        public void Stop()
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            Debug.Log("CPRMetronome: off");
        }

        void Update()
        {
            if (!IsPlaying) return;
            // Schedule on the audio clock so the beat stays steady even if a frame hitches.
            var interval = 60.0 / bpm;
            while (m_NextTick < AudioSettings.dspTime + 0.2)
            {
                m_Source.clip = m_Tick;
                m_Source.volume = volume;
                m_Source.PlayScheduled(m_NextTick);
                m_NextTick += interval;
                if (m_NextTick < AudioSettings.dspTime) m_NextTick = AudioSettings.dspTime + interval;
                break; // one scheduled tick per frame on a single source
            }
        }
    }
}
