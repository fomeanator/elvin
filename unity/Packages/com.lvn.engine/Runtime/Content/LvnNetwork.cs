using System;

namespace Lvn.Content
{
    /// <summary>
    /// Process-wide connectivity flag, shared by every content fetch. The boot
    /// probe sets it; a live wire failure during a download self-corrects it to
    /// offline so the next fetch fast-fails into the disk cache instead of
    /// waiting out a socket timeout. Recovery (flipping back online) is owned by
    /// the host's health-check loop. A single volatile bool — written from
    /// background download threads, read from the main thread.
    /// </summary>
    public static class LvnNetworkStatus
    {
        private static volatile bool _online = true;

        // Test/debug kill-switch: when set the app behaves as if the network is
        // permanently dead — IsOnline is forced false and MarkOnline is ignored,
        // so offline paths are fully deterministic without touching a socket.
        private static volatile bool _forceOffline;

        public static bool ForceOffline
        {
            get => _forceOffline;
            set { _forceOffline = value; if (value) Set(false); }
        }

        public static bool IsOnline => !_forceOffline && _online;
        public static bool IsOffline => !IsOnline;

        /// <summary>Raised on every real transition (not on idempotent re-marks).
        /// Argument is the new IsOnline value. May fire from a background thread —
        /// marshal to the main thread before touching Unity objects.</summary>
        public static event Action<bool> Changed;

        public static void MarkOffline(string reason = null) => Set(false, reason);
        public static void MarkOnline(string reason = null) => Set(true, reason);

        private static void Set(bool online, string reason = null)
        {
            if (online && _forceOffline) return; // forced offline wins
            if (_online == online) return;        // idempotent — no spurious events
            _online = online;
            // Log the transition with its cause — the single most useful breadcrumb
            // when chasing "why did we go offline?" in the field.
            if (!string.IsNullOrEmpty(reason))
                LvnLog.Info($"[net] {(online ? "online" : "offline")}: {reason}");
            try { Changed?.Invoke(online); } catch { /* a bad subscriber must not break status */ }
        }
    }

    /// <summary>
    /// A content-fetch failure carrying the HTTP status and a short machine code
    /// (<c>"network"</c> for connectivity misses, <c>"http_NNN"</c> for bad
    /// responses). Retry loops branch on these: a <c>4xx</c> is permanent (give
    /// up), a <c>"network"</c> while offline is pointless to retry.
    /// </summary>
    public sealed class LvnFetchException : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public LvnFetchException(int status, string code, string message)
            : base($"{code} ({status}): {message}")
        {
            Status = status;
            Code = code;
        }
    }

    /// <summary>
    /// КАК СКАЗАТЬ ИГРОКУ, ЧТО СЕТИ НЕТ.
    ///
    /// <para>Состояние одно, а формулировок было три: «нет сети —
    /// переподключение…» на бут-экране, «Нет сети — позже» на кнопке профиля,
    /// «Нет соединения» в заголовке отказа. Игрок читает их в одном сеансе и
    /// не должен гадать, три ли это разные беды.</para>
    ///
    /// <para>Строки, а не константы: их перекрывает локализация — и на лету,
    /// без пересборки.</para>
    /// </summary>
    public static class LvnOfflineText
    {
        // Слова про сеть — через СЛОВАРЬ: до 28.08 они лежали здесь русскими
        // строками, то есть любая другая новелла получала их насильно и не
        // могла переопределить (та же болезнь, что у валют и «Гостя»).
        // Умолчания движка английские, игра называет своё в ui.words.

        /// <summary>Внутри фразы, со строчной: «…{0} — переподключение…».</summary>
        public static string Word => LvnWords.Of("network.word", "no network");

        /// <summary>Заголовком сообщения.</summary>
        public static string Title => LvnWords.Of("network.title", "No connection");

        /// <summary>Пока пытаемся дотянуться до сервера.</summary>
        public static string Reconnecting => LvnWords.Of("network.reconnecting", "no network — reconnecting…");

        /// <summary>Действие не вышло и его стоит повторить позже.</summary>
        public static string TryLater => LvnWords.Of("network.try_later", "No network — later");

        /// <summary>Главу нельзя открыть без сети. Текст стоял ЗАШИТЫМ в хосте,
        /// мимо этого дома, — и потому не переводился вместе с остальными.</summary>
        public static string ChapterNeedsNetwork => LvnWords.Of("network.chapter_needs",
            "This chapter needs a connection. Check it and try again.");
    }

    /// <summary>
    /// Exponential backoff for retrying a failed fetch. Attempt 1 has no delay;
    /// every subsequent attempt doubles (capped) so a flaky link recovers
    /// quickly without hammering a dead one.
    /// </summary>
    public static class LvnBackoff
    {
        public const float DefaultCapSeconds = 30f;

        public static float DelaySeconds(int attempt, float capSeconds = DefaultCapSeconds)
        {
            if (attempt <= 1) return 0f;
            var exp = Math.Min(attempt - 2, 30);           // guard against overflow
            var delay = (float)Math.Pow(2d, exp);
            return Math.Min(capSeconds, delay);
        }
    }
}
