using System;
using System.IO;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Writes every "CPR..." log line to a file on the headset, so test results survive an
    /// unplugged cable, a full logcat buffer or a restart. Also strips Unity's stack traces
    /// from ordinary log lines (errors/exceptions keep theirs).
    ///
    /// Files: /sdcard/Android/data/com.cprxr.prototype/files/cpr-logs/cpr-test-YYYY-MM-DD_HH-mm-ss.txt
    /// Pull:  adb pull /sdcard/Android/data/com.cprxr.prototype/files/cpr-logs
    /// </summary>
    public static class CPRTestLog
    {
        static StreamWriter s_Writer;
        static readonly object s_Lock = new object();

        public static string FilePath { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            // Ordinary logs: no stack trace (each line ~10x smaller). Errors keep full traces.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);

            if (Application.isEditor) return; // Editor already has the Console

            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "cpr-logs");
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, $"cpr-test-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                s_Writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
                s_Writer.WriteLine($"# CPR XR test log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                s_Writer.WriteLine($"# app {Application.version}, unity {Application.unityVersion}, device {SystemInfo.deviceModel}, gpu {SystemInfo.graphicsDeviceName}");
                Application.logMessageReceivedThreaded += OnLog;
                Application.quitting += Close;
                Debug.Log($"CPRLog: writing to {FilePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"CPRLog: could not open log file: {e.Message}");
            }
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            var isCpr = message.StartsWith("CPR", StringComparison.Ordinal);
            var isProblem = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            if (!isCpr && !isProblem) return;

            lock (s_Lock)
            {
                if (s_Writer == null) return;
                s_Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(isProblem ? type.ToString().ToUpper() + " " : "")}{message}");
                if (isProblem && !string.IsNullOrEmpty(stackTrace)) s_Writer.WriteLine("    " + stackTrace.Replace("\n", "\n    "));
            }
        }

        static void Close()
        {
            lock (s_Lock)
            {
                s_Writer?.Dispose();
                s_Writer = null;
            }
        }
    }
}
