using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class OfflinePinTests
    {
        private string _root;
        private ContentLoader _loader;
        private bool _forcedOffline, _networkOffline;
        [SetUp] public void SetUp()
        {
            _forcedOffline = LvnNetworkStatus.ForceOffline;
            _networkOffline = LvnNetworkStatus.IsOffline;
            _root = Path.Combine(Path.GetTempPath(), "lvn-offline-pins-" + Guid.NewGuid().ToString("N"));
            _loader = new ContentLoader("http://content.test", _root);
        }
        [TearDown] public void TearDown()
        {
            _loader.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            LvnNetworkStatus.ForceOffline = _forcedOffline;
            if (_networkOffline) LvnNetworkStatus.MarkOffline("restore offline pin test state");
            else LvnNetworkStatus.MarkOnline("restore offline pin test state");
        }
        private string Seed(string url)
        {
            var path = Path.Combine(_root, "assets", ContentLoader.HashKey(url, null) + ".bin");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            return path;
        }
        [Test] public async Task ExplicitDownloadsSurviveRestartAndZeroQuotaSweep()
        {
            const string pinned = "/chapter.png", streamed = "/streamed.png";
            var keep = Seed(pinned); var evict = Seed(streamed);
            _loader.PinOfflineAssets(new[] { pinned });
            _loader.Dispose();
            _loader = new ContentLoader("http://content.test", _root);
            var result = await _loader.SweepAssetCacheAsync(new HashSet<string>(), new HashSet<string>(), 0);
            Assert.IsTrue(File.Exists(keep), "pins survive even if no longer in the latest manifest");
            Assert.IsFalse(File.Exists(evict));
            Assert.AreEqual(1, result.removed);
            Assert.IsFalse(_loader.DeleteCachedAsset(pinned, preserveOffline: true));
            Assert.IsTrue(File.Exists(keep));
        }
        [Test] public async Task ExplicitClearRemovesPinsAndDownloadedFiles()
        {
            const string url = "/chapter.png";
            Seed(url); _loader.PinOfflineAssets(new[] { url });
            Assert.AreEqual(3, await _loader.ClearAssetCacheAsync());
            var again = Seed(url);
            await _loader.SweepAssetCacheAsync(null, null, 0);
            Assert.IsFalse(File.Exists(again), "cleared pins must not retain future streaming cache");
        }
        [Test] public async Task CorruptPrimaryPinIndexRecoversBackup()
        {
            var keep = Seed("/chapter.png");
            _loader.PinOfflineAssets(new[] { "/chapter.png" });
            File.WriteAllText(Path.Combine(_root, "offline-pins.json"), "broken");
            _loader.Dispose(); _loader = new ContentLoader("http://content.test", _root);
            await _loader.SweepAssetCacheAsync(null, null, 0);
            Assert.IsTrue(File.Exists(keep));
        }

        [Test] public async Task DownloadedButNeverOpenedScriptPlaysWithoutNetwork()
        {
            const string script = "/never-opened.lvn";
            var path = Seed(script);
            File.WriteAllText(path, "{\"commands\":[]}");
            bool previous = LvnNetworkStatus.ForceOffline;
            LvnNetworkStatus.ForceOffline = true;
            try
            {
                Assert.IsTrue(_loader.IsScriptCached(script));
                Assert.AreEqual("{\"commands\":[]}", await _loader.DownloadScriptCached(script));
            }
            finally { LvnNetworkStatus.ForceOffline = previous; }
        }
    }
}
