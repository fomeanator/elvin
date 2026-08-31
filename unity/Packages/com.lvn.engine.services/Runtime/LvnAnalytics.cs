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

        // Устройство очереди — у ЯЩИКА: накопление, копия на устройстве,
        // пачка, правило ответа сервера и общий насос. Здесь остаётся
        // аналитическое: что считать событием и какие поля к нему приложить.
        private static readonly LvnOutbox _box = new LvnOutbox(
            "analytics", PQueue, cap: QueueCap, flushAt: FlushAt, everySec: FlushEverySec,
            durable: false,      // карандашом: теряется разве что хвост
            batchMax: 100,
            send: async batch =>
            {
                var (code, _) = await LvnBackend.PostAsync("/v1/analytics/events", batch.ToString());
                return code;
            });

        /// <summary>Забыть накопленное этим игроком: очередь переживала обряд
        /// забвения и уходила на сервер уже после него.</summary>
        public static void Forget() => _box.Forget();

        /// <summary>
        /// Метка запуска игры. Без неё отчёт о здоровье умеет считать только
        /// «доля ИГРОКОВ, у которых что-то сломалось» — а это не то же самое,
        /// что доля сессий: один невезучий за месяц и один невезучий за вечер
        /// дают одинаковое число.
        ///
        /// <para>Метку выдаёт ПАСПОРТИСТ, и она ТА ЖЕ, что отправщик логов
        /// ставит на пачку. Раньше здесь порождалась своя — и событие «сбой»
        /// нельзя было свести с логом этого сбоя, хотя сервер об этом прямо
        /// просил в совете отчёта.</para>
        /// </summary>
        public static string SessionId => Lvn.LvnMark.Run;

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
            _box.Add(ev);
            // ВТОРОЙ ПОЛУЧАТЕЛЬ, ЕСЛИ ХОСТ ЕГО ПОДКЛЮЧИЛ. Продуктовые сервисы
            // (AppMetrica, Firebase, свой) живут в проекте игры, а не в движке:
            // их SDK тянет платформенные зависимости, которые библиотеке не
            // нужны. Здесь — только шов: имя события и его поля, ровно те же,
            // что уходят на наш сервер, чтобы отчёты сходились.
            if (Mirror != null)
            {
                try { Mirror(name, p); }
                catch (Exception e)
                {
                    // Чужой SDK не имеет права уронить игру ради своей строчки.
                    Debug.LogWarning($"[lvn-analytics] зеркало события «{name}»: {e.Message}");
                }
            }
        }

        /// <summary>
        /// ЗЕРКАЛО СОБЫТИЙ во внешнюю аналитику. Хост ставит SDK и передаёт
        /// сюда одну функцию; движок зовёт её на КАЖДОЕ событие теми же именами
        /// и полями, что уходят на наш сервер.
        ///
        /// <para>Почему шов, а не SDK внутри: сторонний пакет тянет за собой
        /// платформенные зависимости и свои правила сборки, а движок обязан
        /// собираться и без них. Игра, которой аналитика не нужна, не платит за
        /// неё ничем.</para>
        ///
        /// <para>AppMetrica: <c>LvnAnalytics.Mirror = (name, props) =&gt;
        /// AppMetrica.ReportEvent(name, props.ToString());</c></para>
        /// </summary>
        public static Action<string, JObject> Mirror;

        /// <summary>Отправить накопленное; при отказе очередь остаётся.</summary>
        public static Task FlushAsync() => _box.FlushAsync();

        /// <summary>Сколько событий ждёт отправки — для диагностики.</summary>
        internal static int Pending => _box.Count;
    }
}
