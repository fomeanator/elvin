using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    public sealed class ContentLoaderRecoveryTests
    {
        private string _cache;
        private ContentLoader _loader;
        private bool _forced, _online;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "elvin-recovery-" + Guid.NewGuid().ToString("N"));
            _loader = new ContentLoader("https://unused.invalid", _cache);
            _forced = LvnNetworkStatus.ForceOffline;
            _online = LvnNetworkStatus.IsOnline;
            LvnNetworkStatus.ForceOffline = true;
        }

        [TearDown]
        public void TearDown()
        {
            _loader.Dispose();
            LvnNetworkStatus.ForceOffline = _forced;
            if (_online) LvnNetworkStatus.MarkOnline(); else LvnNetworkStatus.MarkOffline();
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private string CachePath(string url, string version, string ext = ".bin")
            => Path.Combine(_cache, "assets", ContentLoader.HashKey(url, version) + ext);

        [TestCase("/content/bg/room@2k.jpg")]
        [TestCase("/content/bg/room@1440.jpg")]
        [TestCase("/content/bg/room@1k.jpg")]
        [TestCase("/content/bg/room@mini.jpg")]
        public void SpriteDecodeIdentityTracksItsSourceButNotUnrelatedEdits(string url)
        {
            var unknown = _loader.SpriteCacheKey(url);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/room.jpg"] = "v1" }, null);
            var first = _loader.SpriteCacheKey(url);
            Assert.AreNotEqual(unknown, first);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["scripts/ch.lvn"] = "edited" }, null);
            Assert.AreEqual(first, _loader.SpriteCacheKey(url));
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/room.jpg"] = "v2" }, null);
            Assert.AreNotEqual(first, _loader.SpriteCacheKey(url));
            _loader.ApplyVersionDelta(null, new[] { "bg/room.jpg" });
            Assert.AreEqual(unknown, _loader.SpriteCacheKey(url));
        }

        [Test]
        public void ExplicitGpuEncodeVersionAlsoInvalidatesTheSprite()
        {
            const string url = "/content/bg/room@1440.jpg";
            _loader.ApplyVersionDelta(new Dictionary<string, string> {
                ["bg/room.jpg"] = "source", ["bg/room@1440.ktx2"] = "code1" }, null);
            var first = _loader.SpriteCacheKey(url);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/room@1440.ktx2"] = "code2" }, null);
            Assert.AreNotEqual(first, _loader.SpriteCacheKey(url));
        }

        // Seed the real shared-flight coordinator with controlled completions.
        // No texture decoder/network is needed to exercise the public reader.
        private SharedDownloads<Sprite> Decodes => (SharedDownloads<Sprite>)typeof(ContentLoader)
            .GetField("_decoding", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_loader);

        [Test]
        public async Task NewSpriteReadersDoNotJoinAnOldVersionStillDecoding()
        {
            const string url = "/content/bg/room@mini.jpg";
            var old = new TaskCompletionSource<Sprite>();
            var fresh = new TaskCompletionSource<Sprite>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var oldFlight = Decodes.Run(_loader.SpriteCacheKey(url), _ => old.Task, stop.Token);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/room.jpg"] = "v2" }, null);
            var freshFlight = Decodes.Run(_loader.SpriteCacheKey(url), _ => fresh.Task, stop.Token);
            try
            {
                var reader = _loader.DownloadSpriteAsync(url, stop.Token);
                fresh.SetResult(null);
                Assert.IsNull(await reader);
                Assert.IsFalse(oldFlight.IsCompleted, "The new version must not wait for the old decoder");
            }
            finally
            {
                old.TrySetResult(null);
                fresh.TrySetResult(null);
                await Task.WhenAll(oldFlight, freshFlight);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExistingSpriteReaderFollowsTheUpdatedVersion(bool oldFails)
        {
            const string url = "/content/bg/room@mini.jpg";
            var old = new TaskCompletionSource<Sprite>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var oldFlight = Decodes.Run(_loader.SpriteCacheKey(url), _ => old.Task, stop.Token);
            var reader = _loader.DownloadSpriteAsync(url, stop.Token);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/room.jpg"] = "v2" }, null);
            // A failed replacement is deliberately distinguishable from the old
            // result. Keep this flight pending until the reader has joined it.
            var fresh = new TaskCompletionSource<Sprite>();
            var freshKey = _loader.SpriteCacheKey(url);
            var freshFlight = Decodes.Run(freshKey, _ => fresh.Task, stop.Token);
            try
            {
                if (oldFails) old.SetException(new InvalidOperationException("old"));
                else old.SetResult(null);
                await WaitForDecodeReaders(freshKey, 2, stop.Token);
                fresh.SetException(new InvalidOperationException("fresh"));
                try { await reader; Assert.Fail("The reader must observe the current version's outcome"); }
                catch (InvalidOperationException ex) { Assert.AreEqual("fresh", ex.Message); }
            }
            finally
            {
                old.TrySetResult(null);
                fresh.TrySetResult(null);
                try { await Task.WhenAll(oldFlight, freshFlight, reader); }
                catch (InvalidOperationException) { /* controlled test outcomes, all observed */ }
            }
        }

        private async Task WaitForDecodeReaders(string key, int readers, CancellationToken ct)
        {
            var flights = (IDictionary)typeof(SharedDownloads<Sprite>)
                .GetField("_flights", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Decodes);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (flights)
                {
                    var flight = flights[key];
                    if (flight != null && (int)flight.GetType()
                        .GetField("Readers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flight) >= readers)
                        return;
                }
                await Task.Delay(1, ct);
            }
        }

        [Test]
        public void AndroidJarIsLocalButNotAPlainFile()
        {
            const string jar = "jar:file:///data/app/base.apk!/assets/font.ttf";
            Assert.IsTrue(LvnUrl.Local(jar));
            Assert.IsFalse(LvnUrl.PlainFile(jar));
            Assert.IsTrue(LvnUrl.PlainFile("file:///bundle/my font.ttf"));
            Assert.IsFalse(LvnUrl.PlainFile("https://host/font.ttf"));
            Assert.IsFalse(LvnUrl.PlainFile(null));
        }

        [Test]
        public async Task DeltaSurvivesReloadWithoutNetwork()
        {
            const string url = "/content/audio/music.ogg";
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["audio/music.ogg"] = "v1", ["bg/removed.jpg"] = "gone" }, null);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["audio/music.ogg"] = "v2" }, new[] { "bg/removed.jpg" });
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(_cache, "asset-versions.json")));
            Assert.AreEqual("v2", persisted["audio/music.ogg"]);
            Assert.IsFalse(persisted.ContainsKey("bg/removed.jpg"));
            var path = CachePath(url, "v2");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            // Clear only memory to model a fresh launch, keeping disk untouched.
            typeof(ContentLoader).GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_loader, new Dictionary<string, string>());
            await _loader.LoadAssetVersionsAsync();
            Assert.AreEqual(path, await _loader.EnsureCachedFile(url));
        }

        [Test]
        public async Task RemovingLastVersionPersistsAnEmptyIndex()
        {
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["bg/removed.jpg"] = "gone" }, null);
            _loader.ApplyVersionDelta(null, new[] { "bg/removed.jpg" });
            Assert.AreEqual("{}", File.ReadAllText(Path.Combine(_cache, "asset-versions.json")));
            typeof(ContentLoader).GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_loader, new Dictionary<string, string> { ["bg/removed.jpg"] = "stale" });
            await _loader.LoadAssetVersionsAsync();
            var map = (Dictionary<string, string>)typeof(ContentLoader)
                .GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_loader);
            Assert.IsEmpty(map);
        }

        [Test]
        public async Task PrefetchedAudioIsThePlaybackFileOffline()
        {
            const string url = "/content/audio/music.ogg";
            var path = CachePath(url, null);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Assert.AreEqual(path, await _loader.EnsureCachedFile(url));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await _loader.DownloadAssetBytes(url));
            Assert.IsFalse(File.Exists(CachePath(url, null, ".audio")));
        }

        [Test]
        public async Task LegacyAudioMigratesBeforeTheOfflineGate()
        {
            const string url = "/content/audio/music.ogg";
            var old = CachePath(url, null, ".audio");
            File.WriteAllBytes(old, new byte[] { 4, 5, 6 });
            var path = await _loader.EnsureCachedFile(url);
            Assert.AreEqual(CachePath(url, null), path);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(old), "Deleting the canonical cache must not resurrect a stale legacy file");
        }

        [Test]
        public async Task CancelledCacheReadsDoNotReturnSuccess()
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            foreach (var task in new Task[] {
                _loader.EnsureCachedFile("/content/audio/music.ogg", stop.Token),
                _loader.LoadAssetVersionsAsync(stop.Token),
                _loader.DownloadScriptCached("/content/ch.lvn", stop.Token) })
            {
                try { await task; Assert.Fail("A cancelled caller must not receive success"); }
                catch (OperationCanceledException) { }
            }
        }

        [TestCase(404)]
        [TestCase(410)]
        [TestCase(429)]
        [TestCase(500)]
        public void HttpErrorsNeverMeanTheNetworkIsGone(long status)
            => Assert.IsFalse(ContentLoader.IsConnectionFailure(status, 0, "HTTP error"));

        [Test]
        public void OnlyAConnectionFailurePinsOffline()
        {
            Assert.IsTrue(ContentLoader.IsConnectionFailure(0, 0, "Could not resolve host"));
            Assert.IsFalse(ContentLoader.IsConnectionFailure(0, 12, "Connection reset"));
            Assert.IsFalse(ContentLoader.IsConnectionFailure(0, 0, "Request timeout"));
            Assert.IsFalse(ContentLoader.IsConnectionFailure(0, 0, "User Aborted"));
        }

        [UnityTest]
        public IEnumerator ServerIgnoringRangeReplacesThePartialFileAndItsByteCount()
        {
            LvnNetworkStatus.ForceOffline = false;
            LvnNetworkStatus.MarkOnline();
            var body = new byte[] { 9, 8, 7, 6 };
            using var server = new TestHttpServer(new Dictionary<string, byte[]> { ["content/asset.bin"] = body });
            using var loader = new ContentLoader(server.Root, _cache);
            var part = Path.Combine(_cache, "range-test.part");
            File.WriteAllBytes(part, new byte[] { 1, 2, 3 });
            var fetch = typeof(ContentLoader).GetMethod("FetchResumable", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task<byte[]>)fetch.Invoke(loader,
                new object[] { "/content/asset.bin", part, 3L, CancellationToken.None });
            while (!task.IsCompleted) yield return null;
            CollectionAssert.AreEqual(body, task.GetAwaiter().GetResult());
            CollectionAssert.AreEqual(body, File.ReadAllBytes(part));
            Assert.AreEqual(body.Length, loader.Transfers().Received, "HTTP 200 discarded the old prefix; it is not downloaded twice");
        }

        [UnityTest]
        public IEnumerator MissingChapterIsReportedWithoutTakingTheGameOffline()
        {
            LvnNetworkStatus.ForceOffline = false;
            LvnNetworkStatus.MarkOnline();
            using var server = new TestHttpServer(new Dictionary<string, byte[]>());
            using var loader = new ContentLoader(server.Root, _cache);
            var task = loader.DownloadScriptCached("/content/missing.lvn");
            while (!task.IsCompleted) yield return null;
            Assert.IsTrue(task.IsFaulted);
            var error = task.Exception.GetBaseException() as LvnFetchException;
            Assert.IsNotNull(error);
            Assert.IsTrue(error.MissingOnServer);
            Assert.IsTrue(LvnNetworkStatus.IsOnline, "One absent chapter must not disable the whole library");
        }

        [UnityTest]
        public IEnumerator RemovedChapterCanStillUseItsOwnCachedFallback()
        {
            LvnNetworkStatus.ForceOffline = false;
            LvnNetworkStatus.MarkOnline();
            const string url = "/content/ch.lvn";
            var files = new Dictionary<string, byte[]> { ["content/ch.lvn"] = Encoding.UTF8.GetBytes("cached chapter") };
            using var server = new TestHttpServer(files);
            using var loader = new ContentLoader(server.Root, _cache);
            var first = loader.DownloadScriptCached(url);
            while (!first.IsCompleted) yield return null;
            Assert.AreEqual("cached chapter", first.GetAwaiter().GetResult());
            files.Remove("content/ch.lvn");
            loader.ApplyVersionDelta(new Dictionary<string, string> { ["ch.lvn"] = "new" }, null);
            var fallback = loader.DownloadScriptCached(url);
            while (!fallback.IsCompleted) yield return null;
            Assert.AreEqual("cached chapter", fallback.GetAwaiter().GetResult());
        }
    }
}
