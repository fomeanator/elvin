using System.Threading;
using System.Threading.Tasks;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class ILvnAssetsTests
    {
        private readonly Мусор _мусор = new Мусор();

        [TearDown]
        public void Прибрать() => _мусор.Убрать();

        private sealed class StubAssets : ILvnAssets
        {
            public int LoadSpriteCount;
            public int LoadAudioCount;
            public int PreloadCount;
            public int LoadPrefabCount;
            public int UnloadCount;
            public int UnloadAllCount;
            public GameObject Prefab;

            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                LoadSpriteCount++;
                return Task.FromResult<Sprite>(null);
            }

            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            {
                LoadAudioCount++;
                return Task.FromResult<AudioClip>(null);
            }

            public Task<GameObject> LoadPrefabAsync(string id, CancellationToken ct)
            {
                LoadPrefabCount++;
                return Task.FromResult(Prefab);
            }

            public Task PreloadAsync(System.Collections.Generic.IReadOnlyList<string> urls, string kind, CancellationToken ct)
            {
                PreloadCount++;
                var tasks = new System.Collections.Generic.List<Task>();
                foreach (var url in urls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    tasks.Add(kind == "audio"
                        ? LoadAudioAsync(url, ct).ContinueWith(_ => { })
                        : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
                }
                return Task.WhenAll(tasks);
            }

            public void Unload(string url) => UnloadCount++;
            public void UnloadAll() => UnloadAllCount++;
        }

        [Test]
        public void DefaultPreloadCallsLoadForEachUrl()
        {
            var assets = new StubAssets();
            var urls = new[] { "a.png", "b.png", "c.png" };
            assets.PreloadAsync(urls, "sprite", CancellationToken.None).Wait();
            Assert.AreEqual(3, assets.LoadSpriteCount);
            Assert.AreEqual(1, assets.PreloadCount);
        }

        [Test]
        public void DefaultPreloadSkipsEmptyUrls()
        {
            var assets = new StubAssets();
            var urls = new[] { "a.png", "", null, "b.png" };
            assets.PreloadAsync(urls, "sprite", CancellationToken.None).Wait();
            Assert.AreEqual(2, assets.LoadSpriteCount);
        }

        [Test]
        public void DefaultPreloadRoutesAudioKind()
        {
            var assets = new StubAssets();
            var urls = new[] { "click.wav", "music.ogg" };
            assets.PreloadAsync(urls, "audio", CancellationToken.None).Wait();
            Assert.AreEqual(2, assets.LoadAudioCount);
            Assert.AreEqual(0, assets.LoadSpriteCount);
        }

        [Test]
        public void Default3DPreloadWarmsPrefabLoaderWithoutInstantiation()
        {
            var stub = new StubAssets { Prefab = _мусор.Беречь(new GameObject("warm-set-prefab")) };
            ILvnAssets assets = stub;

            assets.Preload3DSetAsync("forest", CancellationToken.None).Wait();

            Assert.AreEqual(1, stub.LoadPrefabCount);
            Assert.AreEqual("warm-set-prefab", stub.Prefab.name);
        }

        [Test]
        public void StubUnloadAndUnloadAll()
        {
            var assets = new StubAssets();
            assets.Unload("a.png");
            Assert.AreEqual(1, assets.UnloadCount);
            assets.UnloadAll();
            Assert.AreEqual(1, assets.UnloadAllCount);
        }

        [Test]
        public void DirectoryAssetsUnloadRemovesFromCache()
        {
            var dir = new DirectoryAssets("/nonexistent");
            dir.Unload("missing.png");
            dir.UnloadAll();
        }
    }
}
