using System;

namespace Lvn
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
                LvnLog.Info($"[lvn-net] {(online ? "online" : "offline")}: {reason}");
            try { Changed?.Invoke(online); } catch { /* a bad subscriber must not break status */ }
        }
    }

    /// <summary>
    /// A content-fetch failure carrying the HTTP status and a short machine code
    /// (<c>"network"</c> for connectivity misses, <c>"http_NNN"</c> for bad
    /// responses). Retry loops branch on these: a <c>4xx</c> is permanent (give
    /// up), a <c>"network"</c> while offline is pointless to retry.
    /// </summary>
}
