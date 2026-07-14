using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// Field diagnostics: ships the device's warnings, errors, exceptions
    /// (with stack traces) and the engine's "[lvn-boot]"/"[lvn-perf]" timing
    /// marks to the server's <c>/v1/log/client</c> — reading a partner
    /// phone's crash is a curl on <c>/v1/admin/client-logs</c>, no adb.
    ///
    /// Same discipline as <see cref="LvnAnalytics"/>: batched, capped, and
    /// never blocking the game. Exceptions additionally persist the queue at
    /// once, so a crash's own trace survives the crash and ships on the next
    /// launch. Plain Debug.Log chatter stays on the device — only the levels
    /// above plus our bracketed telemetry go to the wire.
    /// </summary>
    public static class LvnLogShip
    {
        private const string PQueue = "lvn.svc.logship.queue";
        private const int FlushAt = 25;
        private const float FlushEverySec = 15f;
        private const int QueueCap = 300;

        private static readonly List<JObject> _queue = new List<JObject>();
        private static readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 12);
        private static bool _booted, _flushing, _dirty;
        private static int _mainThreadId;
        private static float _lastFlush;
        private static string _lastMsg;
        private static JObject _lastLine;

        /// <summary>Start capturing. Call once, as early as possible.</summary>
        public static void Boot()
        {
            if (_booted || string.IsNullOrEmpty(LvnBackend.BaseUrl)) return;
            _booted = true;
            _mainThreadId = Environment.CurrentManagedThreadId;
            LoadPersisted();
            // Threaded variant: exceptions on worker threads (asset decodes,
            // tasks) are exactly the ones a main-thread hook would miss.
            Application.logMessageReceivedThreaded += OnLog;
            Runner.Ensure();
            Enqueue("info", $"session start · {SystemInfo.deviceModel} · {SystemInfo.operatingSystem} " +
                            $"· app {Application.version} · mem {SystemInfo.systemMemorySize}MB " +
                            $"· gpu {SystemInfo.graphicsDeviceName}", null, persist: false);
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            string level;
            switch (type)
            {
                case LogType.Exception: level = "exception"; break;
                case LogType.Error:
                case LogType.Assert: level = "error"; break;
                case LogType.Warning: level = "warning"; break;
                default:
                    // Info ships only our own bracketed telemetry ([lvn-boot],
                    // [lvn-perf], [novelapp]…) — not the whole Debug.Log firehose.
                    if (message == null || message.Length == 0 || message[0] != '[') return;
                    level = "info";
                    break;
            }
            Enqueue(level, message, string.IsNullOrEmpty(stack) ? null : stack,
                persist: type == LogType.Exception || type == LogType.Error);
        }

        private static void Enqueue(string level, string msg, string stack, bool persist)
        {
            lock (_queue)
            {
                // A stuck loop logging the same line must not flood the wire —
                // consecutive repeats collapse into a counter on the first one.
                if (msg == _lastMsg && _lastLine != null)
                {
                    _lastLine["n"] = ((int?)_lastLine["n"] ?? 1) + 1;
                    return;
                }
                var line = new JObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    ["level"] = level,
                    ["msg"] = msg,
                };
                if (stack != null) line["stack"] = stack;
                _queue.Add(line);
                _lastMsg = msg; _lastLine = line;
                while (_queue.Count > QueueCap) _queue.RemoveAt(0);
            }
            // PlayerPrefs and UnityWebRequest are main-thread-only; a worker
            // thread (threaded log callback) just marks the queue dirty and
            // the Runner's next Update persists/flushes on the main thread.
            bool mainThread = Environment.CurrentManagedThreadId == _mainThreadId;
            if (persist)
            {
                if (mainThread) Persist(); // the crash's own trace must survive the crash
                else _dirty = true;
            }
            if (_queue.Count >= FlushAt && mainThread) _ = FlushAsync();
        }

        /// <summary>Send everything queued; keeps the queue on failure.</summary>
        public static async Task FlushAsync()
        {
            if (_flushing) return;
            JArray lines;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                lines = new JArray(_queue.GetRange(0, Math.Min(_queue.Count, 200)));
            }
            _flushing = true;
            try
            {
                var body = new JObject
                {
                    ["device"] = new JObject
                    {
                        ["id"] = SystemInfo.deviceUniqueIdentifier,
                        ["session"] = _session,
                        ["model"] = SystemInfo.deviceModel,
                        ["os"] = SystemInfo.operatingSystem,
                        ["app"] = Application.version,
                    },
                    ["lines"] = lines,
                };
                var (code, _) = await LvnBackend.PostAsync("/v1/log/client", body.ToString(Newtonsoft.Json.Formatting.None));
                if (code == 200)
                {
                    lock (_queue)
                    {
                        _queue.RemoveRange(0, Math.Min(_queue.Count, lines.Count));
                        if (_queue.Count == 0) { _lastMsg = null; _lastLine = null; }
                    }
                    Persist();
                }
            }
            catch { /* offline — the queue holds until the next flush tick */ }
            finally { _flushing = false; _lastFlush = Time.realtimeSinceStartup; }
        }

        private static void LoadPersisted()
        {
            try
            {
                var raw = PlayerPrefs.GetString(PQueue, "");
                if (!string.IsNullOrEmpty(raw))
                    foreach (var t in JArray.Parse(raw))
                        if (t is JObject o) _queue.Add(o);
            }
            catch { /* a corrupt queue is not worth crashing diagnostics over */ }
        }

        private static void Persist()
        {
            try
            {
                lock (_queue) PlayerPrefs.SetString(PQueue, new JArray(_queue).ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save(); // survive a hard kill, not just a clean quit
            }
            catch { }
        }

        private sealed class Runner : MonoBehaviour
        {
            private static Runner _inst;

            public static void Ensure()
            {
                if (_inst != null || !Application.isPlaying) return;
                var go = new GameObject("LvnLogShip") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<Runner>();
            }

            private void Update()
            {
                if (_dirty) { _dirty = false; Persist(); }
                if (Time.realtimeSinceStartup - _lastFlush > FlushEverySec && _queue.Count > 0)
                    _ = FlushAsync();
            }

            private void OnApplicationPause(bool paused)
            {
                if (paused) { Persist(); _ = FlushAsync(); }
            }

            private void OnApplicationQuit() => Persist();
        }
    }
}
