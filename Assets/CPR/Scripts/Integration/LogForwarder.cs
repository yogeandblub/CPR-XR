using System.Collections.Concurrent;
using System.Text;
using UnityEngine;

namespace CPR.Integration
{
    /// <summary>
    /// Sends the app's CPR log lines (and any errors) to the laptop bridge every 2 s -> bridge/headset.log,
    /// so test runs can be debugged over Wi-Fi without a USB cable.
    /// </summary>
    public class LogForwarder : MonoBehaviour
    {
        public XrAedClient client;
        public float interval = 2f;

        readonly ConcurrentQueue<string> m_Lines = new ConcurrentQueue<string>();
        float m_Next;

        void OnEnable() => Application.logMessageReceivedThreaded += OnLog;
        void OnDisable() => Application.logMessageReceivedThreaded -= OnLog;

        void OnLog(string message, string stack, LogType type)
        {
            var problem = type == LogType.Error || type == LogType.Exception;
            if (!problem && !message.StartsWith("CPR") && !message.StartsWith("[QuestCallBridge]")) return;
            if (m_Lines.Count > 500) return; // never grow without bound if the bridge is unreachable
            m_Lines.Enqueue($"{System.DateTime.Now:HH:mm:ss.fff} {(problem ? type.ToString().ToUpper() + " " : "")}{message}");
        }

        void Update()
        {
            if (client == null || !client.IsOnline || Time.time < m_Next) return;
            m_Next = Time.time + interval;
            if (m_Lines.IsEmpty) return;
            var sb = new StringBuilder();
            while (sb.Length < 60000 && m_Lines.TryDequeue(out var l)) sb.Append(l).Append('\n');
            client.PostLog(sb.ToString());
        }
    }
}
