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

        private static readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 12);
        private static bool _booted;
        private static int _mainThreadId;
        private static string _lastMsg;
        private static JObject _lastLine;

        // Устройство очереди — у ЯЩИКА. Здесь остаётся диагностическое: какие
        // уровни лога ехать достойны, как схлопывать повтор и что приложить к
        // пачке от устройства.
        private static readonly LvnOutbox _box = new LvnOutbox(
            "logship", PQueue, cap: QueueCap, flushAt: FlushAt, everySec: FlushEverySec,
            durable: true,       // набело: след падения обязан пережить падение
            batchMax: 200,
            send: SendAsync);

        /// <summary>Start capturing. Call once, as early as possible.</summary>
        /// <summary>
        /// Последние строки лога — для отзыва из игры. Берём из ТОГО ЖЕ
        /// буфера, что уходит на сервер: второй буфер означал бы вторую
        /// правду, и они разошлись бы ровно в тот момент, когда сверить их
        /// важнее всего.
        ///
        /// <para>Очередь может быть уже отправлена и опустошена — тогда хвост
        /// пуст, и это честнее, чем показать старое.</para>
        /// </summary>
        public static string Tail(int lines = 40)
        {
            var sb = new System.Text.StringBuilder();
            _box.Modify(q =>
            {
                int from = q.Count - lines;
                if (from < 0) from = 0;
                for (int i = from; i < q.Count; i++)
                {
                    var msg = q[i]["msg"];
                    if (msg != null) sb.Append(msg.ToString()).Append('\n');
                }
            });
            return sb.ToString();
        }

        public static void Boot()
        {
            if (_booted || string.IsNullOrEmpty(LvnBackend.BaseUrl)) return;
            _booted = true;
            _mainThreadId = Environment.CurrentManagedThreadId;
            _box.Load();
            // Threaded variant: exceptions on worker threads (asset decodes,
            // tasks) are exactly the ones a main-thread hook would miss.
            Application.logMessageReceivedThreaded += OnLog;
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
            bool collapsed = false;
            JObject line = null;
            _box.Modify(q =>
            {
                // A stuck loop logging the same line must not flood the wire —
                // consecutive repeats collapse into a counter on the first one.
                if (msg == _lastMsg && _lastLine != null && q.Contains(_lastLine))
                {
                    _lastLine["n"] = ((int?)_lastLine["n"] ?? 1) + 1;
                    collapsed = true;
                    return;
                }
                line = new JObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    ["level"] = level,
                    ["msg"] = msg,
                };
                if (stack != null) line["stack"] = stack;
            });
            if (collapsed) return;

            // PlayerPrefs and UnityWebRequest are main-thread-only; a worker
            // thread (threaded log callback) just marks the queue dirty and
            // the shared pump persists/flushes on the main thread.
            bool mainThread = Environment.CurrentManagedThreadId == _mainThreadId;
            _box.Add(line, persistNow: persist, mainThread: mainThread);
            _lastMsg = msg; _lastLine = line;
        }

        /// <summary>Отправить накопленное; при отказе очередь остаётся.</summary>
        public static Task FlushAsync() => _box.FlushAsync();

        // Тело пачки: от устройства — то, без чего строка лога не читается на
        // той стороне (какой телефон, какая сессия, какая сборка).
        private static async Task<long> SendAsync(JArray lines)
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
            var (code, _) = await LvnBackend.PostAsync("/v1/log/client",
                body.ToString(Newtonsoft.Json.Formatting.None));
            if (code >= 200 && code < 300) { _lastMsg = null; _lastLine = null; }
            return code;
        }
    }
}
