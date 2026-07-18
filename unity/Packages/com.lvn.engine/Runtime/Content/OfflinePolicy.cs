using System;
using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// Pure, deterministic offline decision layer (ported from the shipping
    /// Liminal client). Takes plain inputs — is the device online? is the script
    /// on disk? which assets are on disk? — plus the chapter's release set, and
    /// returns a fully-decided plan for entering a chapter. NO Unity types, NO
    /// network, NO disk I/O here, so every branch is unit-testable with synthetic
    /// inputs.
    ///
    /// The runtime supplies the side-effecting predicates
    /// (<see cref="ContentLoader.IsScriptCached"/> /
    /// <see cref="ContentLoader.IsAssetCached"/>) and the connectivity probe; all
    /// judgement lives here where it can be proven. A bundled (offline export)
    /// build reports everything cached and reachable, so it lands on
    /// <see cref="ChapterEntryMode.ReadyFromCache"/>.
    /// </summary>
    public enum ChapterEntryMode
    {
        /// <summary>Script + every required asset already on disk. Plays
        /// instantly; the loading screen is a short title splash.</summary>
        ReadyFromCache,

        /// <summary>Online and something in the release set is still missing. The
        /// scheduler downloads it; loading waits for the required subset.</summary>
        OnlineDownload,

        /// <summary>Offline but the script is cached: plays from disk. Missing art
        /// is skipped (graceful degradation) — the game never hangs.</summary>
        OfflineDegraded,

        /// <summary>Offline and the script was never cached: cannot start.</summary>
        Unavailable,
    }

    /// <summary>How much of a chapter is already on disk. Required = assets
    /// needed at/near the start (<see cref="LvnAssetMeta.critical"/>); deferred =
    /// the rest.</summary>
    public readonly struct ChapterReadiness
    {
        public readonly bool ScriptCached;
        public readonly int RequiredTotal;
        public readonly int RequiredCached;
        public readonly int DeferredTotal;
        public readonly int DeferredCached;

        public ChapterReadiness(bool scriptCached,
            int requiredTotal, int requiredCached,
            int deferredTotal, int deferredCached)
        {
            ScriptCached = scriptCached;
            RequiredTotal = requiredTotal;
            RequiredCached = requiredCached;
            DeferredTotal = deferredTotal;
            DeferredCached = deferredCached;
        }

        public int RequiredMissing => Math.Max(0, RequiredTotal - RequiredCached);
        public bool RequiredComplete => RequiredMissing == 0;
        public int AssetTotal => RequiredTotal + DeferredTotal;
        public int AssetCached => RequiredCached + DeferredCached;
        public int AssetMissing => Math.Max(0, AssetTotal - AssetCached);

        /// <summary>Script and every asset (required AND deferred) are on disk —
        /// the whole chapter can run offline with full art.</summary>
        public bool FullyCached => ScriptCached && AssetMissing == 0;

        /// <summary>0..1 over the whole set (the script counts as one unit so an
        /// asset-less chapter still reports a meaningful fraction).</summary>
        public float Completeness
        {
            get
            {
                int total = AssetTotal + 1;            // +1 for the script
                int have = AssetCached + (ScriptCached ? 1 : 0);
                return total <= 0 ? 1f : (float)have / total;
            }
        }
    }

    /// <summary>The decided plan for entering a chapter. Every flag is derived
    /// purely from (online, readiness) so the runtime contains no offline
    /// branching of its own — it just reads these.</summary>
    public readonly struct ChapterEntryPlan
    {
        public readonly ChapterEntryMode Mode;
        /// <summary>False only when Unavailable (offline + script never cached).</summary>
        public readonly bool CanPlay;
        /// <summary>Call online-only endpoints. Online only.</summary>
        public readonly bool CallOnlineEndpoints;
        /// <summary>Start the prioritized asset scheduler. Only when online AND
        /// something is actually missing.</summary>
        public readonly bool RunScheduler;
        /// <summary>The loading screen blocks on the scheduler's required-complete
        /// event.</summary>
        public readonly bool LoadingWaitsForRequired;
        /// <summary>Show only a short title splash (no progress bar) — fully cached.</summary>
        public readonly bool ShowTitleSplashOnly;

        public ChapterEntryPlan(ChapterEntryMode mode, bool canPlay,
            bool callOnlineEndpoints, bool runScheduler,
            bool loadingWaitsForRequired, bool showTitleSplashOnly)
        {
            Mode = mode;
            CanPlay = canPlay;
            CallOnlineEndpoints = callOnlineEndpoints;
            RunScheduler = runScheduler;
            LoadingWaitsForRequired = loadingWaitsForRequired;
            ShowTitleSplashOnly = showTitleSplashOnly;
        }

        public static ChapterEntryPlan From(bool online, in ChapterReadiness r)
        {
            var mode = OfflinePolicy.Decide(online, r);
            bool canPlay = mode != ChapterEntryMode.Unavailable;
            bool callOnline = online;
            // Nothing to fetch if the whole set is already on disk; nothing we
            // CAN fetch when offline.
            bool runScheduler = online && !r.FullyCached;
            bool waitRequired = online && !r.FullyCached;
            bool splash = r.FullyCached;
            return new ChapterEntryPlan(mode, canPlay, callOnline, runScheduler, waitRequired, splash);
        }
    }

    public static class OfflinePolicy
    {
        /// <summary>True if a content URL is the chapter script itself (.lvn),
        /// tracked separately from art/audio in readiness accounting.</summary>
        public static bool IsScriptUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            int q = url.IndexOf('?');
            var u = q >= 0 ? url.Substring(0, q) : url;
            return u.EndsWith(".lvn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Builds a readiness snapshot from the server's release set and
        /// pure predicates. The script entry inside the release set is ignored
        /// here (counted via scriptCached); only art/audio feed required/deferred.</summary>
        public static ChapterReadiness ComputeReadiness(
            bool scriptCached,
            IReadOnlyDictionary<string, LvnAssetMeta> releaseSet,
            Func<string, bool> isAssetCached)
        {
            int rt = 0, rc = 0, dt = 0, dc = 0;
            if (releaseSet != null)
            {
                foreach (var kv in releaseSet)
                {
                    var url = kv.Key;
                    if (string.IsNullOrEmpty(url) || IsScriptUrl(url)) continue;
                    var meta = kv.Value;
                    // Everything is REQUIRED now (the full-preload rule): a
                    // chapter is playable only когда она целиком на диске.
                    bool cached = isAssetCached != null && isAssetCached(url);
                    rt++; if (cached) rc++;
                    _ = meta;
                }
            }
            return new ChapterReadiness(scriptCached, rt, rc, dt, dc);
        }

        /// <summary>Core decision. Pure function of connectivity + readiness.</summary>
        public static ChapterEntryMode Decide(bool online, in ChapterReadiness r)
        {
            if (!online)
                return r.ScriptCached ? ChapterEntryMode.OfflineDegraded : ChapterEntryMode.Unavailable;

            // Online from here: the script can always be (re)fetched.
            return r.FullyCached ? ChapterEntryMode.ReadyFromCache : ChapterEntryMode.OnlineDownload;
        }
    }
}
