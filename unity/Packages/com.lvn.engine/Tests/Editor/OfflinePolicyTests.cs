using System;
using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Pure, deterministic tests for the offline decision layer (OfflinePolicy),
    /// ported from the shipping Liminal client: no Unity runtime, no network, no
    /// disk — every branch driven by synthetic inputs, so the offline contract
    /// has a calculable, repeatable result.
    public class OfflinePolicyTests
    {
        private static LvnAssetMeta Asset(bool critical, long size = 1024) =>
            new LvnAssetMeta { critical = critical, size = size, tier = "mini", scope = "chapter", sha = "x" };

        private static Dictionary<string, LvnAssetMeta> Set(params (string url, bool critical)[] items)
        {
            var d = new Dictionary<string, LvnAssetMeta>();
            foreach (var (url, critical) in items) d[url] = Asset(critical);
            return d;
        }

        private static Func<string, bool> Cached(params string[] cached)
        {
            var set = new HashSet<string>(cached);
            return url => set.Contains(url);
        }

        // ---- ComputeReadiness ------------------------------------------------

        [Test]
        public void ComputeReadiness_SplitsCriticalAndDeferred()
        {
            var set = Set(
                ("/content/bg/a.png", true),
                ("/content/bg/b.png", true),
                ("/content/bg/c.png", false));
            var r = OfflinePolicy.ComputeReadiness(
                scriptCached: true, releaseSet: set,
                isAssetCached: Cached("/content/bg/a.png"));

            // The full-preload rule: everything is required — the critical
            // flag no longer buys entry, only queue priority.
            Assert.AreEqual(3, r.RequiredTotal);
            Assert.AreEqual(1, r.RequiredCached);
            Assert.AreEqual(0, r.DeferredTotal);
            Assert.AreEqual(2, r.RequiredMissing);
            Assert.IsFalse(r.RequiredComplete);
            Assert.IsFalse(r.FullyCached);
        }

        [Test]
        public void ComputeReadiness_IgnoresScriptEntryInSet()
        {
            var set = Set(
                ("/content/scripts/ch1.lvn", true),
                ("/content/bg/a.png", true));
            var r = OfflinePolicy.ComputeReadiness(true, set, Cached("/content/bg/a.png"));

            Assert.AreEqual(1, r.RequiredTotal, "script must not count as an asset");
            Assert.AreEqual(1, r.RequiredCached);
            Assert.IsTrue(r.FullyCached);
        }

        [Test]
        public void ComputeReadiness_NullSet_IsEmptyButScriptAware()
        {
            var r = OfflinePolicy.ComputeReadiness(true, null, Cached());
            Assert.AreEqual(0, r.AssetTotal);
            Assert.IsTrue(r.RequiredComplete);
            Assert.IsTrue(r.FullyCached, "no assets + script cached = fully cached");
        }

        [Test]
        public void ComputeReadiness_RequiredCompleteButDeferredMissing()
        {
            var set = Set(
                ("/content/bg/a.png", true),
                ("/content/bg/late.png", false));
            var r = OfflinePolicy.ComputeReadiness(true, set, Cached("/content/bg/a.png"));

            // Under full preload the missing non-critical BLOCKS completeness.
            Assert.IsFalse(r.RequiredComplete);
            Assert.IsFalse(r.FullyCached);
        }

        [Test]
        public void Completeness_CountsScriptAndAssets()
        {
            var set = Set(("/content/bg/a.png", true), ("/content/bg/b.png", false));
            var r = OfflinePolicy.ComputeReadiness(true, set, Cached("/content/bg/a.png"));
            Assert.AreEqual(2f / 3f, r.Completeness, 0.001f);
        }

        // ---- Decide ----------------------------------------------------------

        [Test]
        public void Decide_OfflineNoScript_Unavailable()
        {
            var r = OfflinePolicy.ComputeReadiness(false, Set(("/content/bg/a.png", true)), Cached());
            Assert.AreEqual(ChapterEntryMode.Unavailable, OfflinePolicy.Decide(online: false, r));
        }

        [Test]
        public void Decide_OfflineScriptCached_Degraded()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)), Cached());
            Assert.AreEqual(ChapterEntryMode.OfflineDegraded, OfflinePolicy.Decide(online: false, r));
        }

        [Test]
        public void Decide_OnlineFullyCached_ReadyFromCache()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)),
                Cached("/content/bg/a.png"));
            Assert.AreEqual(ChapterEntryMode.ReadyFromCache, OfflinePolicy.Decide(online: true, r));
        }

        [Test]
        public void Decide_OnlinePartial_OnlineDownload()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)), Cached());
            Assert.AreEqual(ChapterEntryMode.OnlineDownload, OfflinePolicy.Decide(online: true, r));
        }

        [Test]
        public void Decide_OnlineNoScriptYet_OnlineDownload()
        {
            var r = OfflinePolicy.ComputeReadiness(false, Set(("/content/bg/a.png", true)), Cached());
            Assert.AreEqual(ChapterEntryMode.OnlineDownload, OfflinePolicy.Decide(online: true, r));
        }

        // ---- ChapterEntryPlan ------------------------------------------------

        [Test]
        public void Plan_OfflineNoScript_CannotPlay_NoOnline_NoScheduler()
        {
            var r = OfflinePolicy.ComputeReadiness(false, Set(("/content/bg/a.png", true)), Cached());
            var p = ChapterEntryPlan.From(online: false, in r);

            Assert.AreEqual(ChapterEntryMode.Unavailable, p.Mode);
            Assert.IsFalse(p.CanPlay);
            Assert.IsFalse(p.CallOnlineEndpoints);
            Assert.IsFalse(p.RunScheduler);
            Assert.IsFalse(p.LoadingWaitsForRequired);
        }

        [Test]
        public void Plan_OfflineScriptCached_PlaysFromDisk_NoNetwork()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)), Cached());
            var p = ChapterEntryPlan.From(online: false, in r);

            Assert.AreEqual(ChapterEntryMode.OfflineDegraded, p.Mode);
            Assert.IsTrue(p.CanPlay);
            Assert.IsFalse(p.CallOnlineEndpoints, "offline must not call online endpoints (no hang)");
            Assert.IsFalse(p.RunScheduler, "offline cannot download");
            Assert.IsFalse(p.LoadingWaitsForRequired);
        }

        [Test]
        public void Plan_OnlineFullyCached_SplashOnly_NoScheduler()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)),
                Cached("/content/bg/a.png"));
            var p = ChapterEntryPlan.From(online: true, in r);

            Assert.AreEqual(ChapterEntryMode.ReadyFromCache, p.Mode);
            Assert.IsTrue(p.CanPlay);
            Assert.IsTrue(p.ShowTitleSplashOnly, "fully cached -> short title splash, no bar");
            Assert.IsFalse(p.RunScheduler);
            Assert.IsFalse(p.LoadingWaitsForRequired);
        }

        [Test]
        public void Plan_OnlinePartial_RunsSchedulerAndWaits()
        {
            var r = OfflinePolicy.ComputeReadiness(true, Set(("/content/bg/a.png", true)), Cached());
            var p = ChapterEntryPlan.From(online: true, in r);

            Assert.AreEqual(ChapterEntryMode.OnlineDownload, p.Mode);
            Assert.IsTrue(p.CanPlay);
            Assert.IsTrue(p.CallOnlineEndpoints);
            Assert.IsTrue(p.RunScheduler);
            Assert.IsTrue(p.LoadingWaitsForRequired);
            Assert.IsFalse(p.ShowTitleSplashOnly);
        }

        [Test]
        public void Plan_OnlineRequiredCachedButDeferredMissing_StillRunsScheduler()
        {
            var set = Set(("/content/bg/a.png", true), ("/content/bg/late.png", false));
            var r = OfflinePolicy.ComputeReadiness(true, set, Cached("/content/bg/a.png"));
            var p = ChapterEntryPlan.From(online: true, in r);

            Assert.AreEqual(ChapterEntryMode.OnlineDownload, p.Mode);
            Assert.IsFalse(r.RequiredComplete, "the not-yet-cached file gates entry now");
            Assert.IsTrue(p.RunScheduler, "the gate scheduler fetches the remainder");
        }

        [Test]
        public void IsScriptUrl_DetectsLvnIgnoringQuery()
        {
            Assert.IsTrue(OfflinePolicy.IsScriptUrl("/content/scripts/ch1.lvn"));
            Assert.IsTrue(OfflinePolicy.IsScriptUrl("/content/scripts/ch1.lvn?v=abc123"));
            Assert.IsFalse(OfflinePolicy.IsScriptUrl("/content/bg/a.png"));
            Assert.IsFalse(OfflinePolicy.IsScriptUrl(null));
        }
    }
}
