using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// Fire-and-forget product analytics: <c>LvnAnalytics.Track(LvnEvents.ChapterStart,
    /// ("ch", "ch1"))</c> queues an event; batches flush every 20 events / 30
    /// seconds / on pause. The queue survives restarts (PlayerPrefs) and drops
    /// its oldest beyond 500 — analytics must never grow unbounded or block
    /// the game. Anonymous by design; the server stamps the user when the
    /// session is signed in.
    /// </summary>
    public static class LvnAnalytics
    {
        private const string PQueue = "lvn.svc.analytics.queue";
        private const int FlushAt = 20;
        private const float FlushEverySec = 30f;
        internal const int QueueCap = 500;

        private static readonly List<JObject> _queue = new List<JObject>();
        private static bool _loaded, _flushing;
        private static float _lastFlush;

        /// <summary>
        /// Метка запуска игры. Без неё отчёт о здоровье умеет считать только
        /// «доля ИГРОКОВ, у которых что-то сломалось» — а это не то же самое,
        /// что доля сессий: один невезучий за месяц и один невезучий за вечер
        /// дают одинаковое число. Guid живёт ровно один запуск процесса;
        /// связывать сессии между собой не нужно, нужно их различать.
        /// </summary>
        public static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 16);

        /// <summary>
        /// Новелла, внутри которой сейчас игрок. Ставится оболочкой на входе в
        /// новеллу и снимается на выходе: без неё половина событий приходит без
        /// title, и отчёт не может отнести сбой к конкретной истории (сейчас
        /// таких — 104 события из 199).
        /// </summary>
        public static string CurrentTitle
        {
            get => LvnWhereabouts.Title;
            set => LvnWhereabouts.Enter(value, LvnWhereabouts.Chapter);
        }

        /// <summary>Глава, которую сейчас играют. Тот же смысл, что у
        /// <see cref="CurrentTitle"/>: без неё событие внутри главы нельзя
        /// поставить на воронку, а именно воронка и есть вопрос «где
        /// отваливаются».</summary>
        public static string CurrentChapter
        {
            get => LvnWhereabouts.Chapter;
            set => LvnWhereabouts.Enter(LvnWhereabouts.Title, value);
        }

        public static void Track(string name, params (string key, object value)[] props)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (string.IsNullOrEmpty(LvnBackend.BaseUrl)) return; // pure-offline game: no queue growth
            EnsureLoaded();
            var ev = new JObject
            {
                ["name"] = name,
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            var p = new JObject { ["sid"] = SessionId };
            if (!string.IsNullOrEmpty(CurrentTitle)) p["title"] = CurrentTitle;
            if (!string.IsNullOrEmpty(CurrentChapter)) p["chapter"] = CurrentChapter;
            // Группы A/B — в КАЖДОЕ событие. Знать деление и не знать, что
            // случилось в каждой половине, бесполезно: сравнивать было бы
            // нечего, а досыпать группу задним числом невозможно — события
            // уже записаны.
            foreach (var kv in LvnExperiments.Assignments)
                if (!string.IsNullOrEmpty(kv.Value)) p["ab_" + kv.Key] = kv.Value;
            if (props != null && props.Length > 0)
            {
                foreach (var (key, value) in props)
                    if (!string.IsNullOrEmpty(key))
                        p[key] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            ev["props"] = p;
            lock (_queue)
            {
                _queue.Add(ev);
                while (_queue.Count > QueueCap) _queue.RemoveAt(0);
            }
            Persist();
            Runner.Ensure();
            if (_queue.Count >= FlushAt) LvnAsync.Fire(FlushAsync(), "Flush");
        }

        /// <summary>Send everything queued; keeps the queue on failure.</summary>
        public static async Task FlushAsync()
        {
            if (_flushing) return;
            EnsureLoaded();
            JArray batch;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                batch = new JArray(_queue.GetRange(0, Math.Min(_queue.Count, 100)));
            }
            _flushing = true;
            try
            {
                var (code, _) = await LvnBackend.PostAsync("/v1/analytics/events", batch.ToString());
                if (code == 200)
                {
                    lock (_queue) _queue.RemoveRange(0, Math.Min(_queue.Count, batch.Count));
                    Persist();
                }
            }
            finally { _flushing = false; _lastFlush = Time.realtimeSinceStartup; }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var raw = LvnKeep.Get(PQueue, "");
                if (!string.IsNullOrEmpty(raw))
                    foreach (var t in JArray.Parse(raw))
                        if (t is JObject o) _queue.Add(o);
            }
            catch { /* a corrupt queue is not worth crashing analytics over */ }
        }

        private static void Persist()
        {
            try
            {
                // Карандашом: очередь переписывается на КАЖДОЕ событие, а теряется
                // разве что хвост — фиксация приедет с уходом в фон.
                lock (_queue) LvnKeep.Jot(PQueue, new JArray(_queue).ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }   // аналитика не отправилась — это НИКОГДА не повод мешать игре
        }

        // The invisible pump: flush on a timer and when the app pauses/quits.
        private sealed class Runner : MonoBehaviour
        {
            private static Runner _inst;

            public static void Ensure()
            {
                if (_inst != null || !Application.isPlaying) return;
                var go = new GameObject("LvnAnalytics") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<Runner>();
            }

            private void Update()
            {
                if (Time.realtimeSinceStartup - _lastFlush > FlushEverySec && _queue.Count > 0)
                    LvnAsync.Fire(FlushAsync(), "Flush");
            }

            private void OnApplicationPause(bool paused)
            {
                if (paused) LvnAsync.Fire(FlushAsync(), "Flush");
            }

            private void OnApplicationQuit() => Persist();
        }
    }
}
