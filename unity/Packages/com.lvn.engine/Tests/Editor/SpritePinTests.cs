using System;
using System.Collections.Generic;
using System.IO;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАКРЕПЛЁННОЕ НЕ УМИРАЕТ.
    ///
    /// <para>Живой корень «белого прямоугольника вместо героини» (28.08):
    /// обновление контента звало <c>Unload</c>, а тот, в отличие от LRU, про
    /// пины не знал — и уничтожал текстуры, которые в этот миг рисовались на
    /// экране. Фигура оставалась стоять с пустыми слоями, и лечить её было
    /// нечем: арт уничтожили под ней.</para>
    ///
    /// <para>Правило одно и проверяется здесь: пока сцена ДЕРЖИТ спрайт, его
    /// нельзя уничтожить никаким путём — из кэша он уйти может (следующая
    /// загрузка возьмёт свежий файл), а из памяти нет.</para>
    /// </summary>
    public class SpritePinTests
    {
        private ContentLoader _loader;
        private Sprite _sprite;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lvn-pin-" + Guid.NewGuid().ToString("N"));
            _loader = new ContentLoader("http://127.0.0.1/", _root);
            var tex = new Texture2D(4, 4);
            _sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
        }

        [TearDown]
        public void TearDown()
        {
            _loader?.Dispose();   // иначе подписка на lowMemory переживёт весь прогон
            if (_sprite != null)
            {
                if (_sprite.texture != null) UnityEngine.Object.DestroyImmediate(_sprite.texture);
                UnityEngine.Object.DestroyImmediate(_sprite);
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        // ЖИВОЙ СЛУЧАЙ: сервер отдал новую версию файла, сцена в этот момент
        // рисует старую. Забрать картинку из-под неё нельзя.
        [Test]
        public void AnUnloadCannotTakeArtFromTheStage()
        {
            _loader.CacheSpriteForTest("/content/art/hero.png", _sprite, 64);
            _loader.PinSprite(_sprite, true);          // сцена держит

            _loader.Unload("/content/art/hero.png");   // обновление контента

            Assert.IsTrue(_sprite != null, "закреплённый спрайт уничтожили");
            Assert.IsTrue(_sprite.texture != null, "у закреплённого отобрали текстуру");
        }

        // Но из КЭША он уйти обязан: следующая загрузка того же адреса должна
        // взять новый файл, а не вернуть прежнюю картинку навсегда.
        [Test]
        public void TheCacheStillLetsGoSoTheNextLoadIsFresh()
        {
            _loader.CacheSpriteForTest("/content/art/hero.png", _sprite, 64);
            _loader.PinSprite(_sprite, true);
            _loader.Unload("/content/art/hero.png");

            Assert.IsNull(_loader.CachedSpriteForTest("/content/art/hero.png"),
                "адрес остался в кэше — обновление контента не доедет до экрана");
        }

        // Та же защита на пути «выгрузить всё по признаку» (уход из главы).
        [Test]
        public void ABulkUnloadSpareTheHeldArtToo()
        {
            _loader.CacheSpriteForTest("/content/art/hero.png", _sprite, 64);
            _loader.PinSprite(_sprite, true);

            _loader.UnloadWhere(u => u.StartsWith("/content/art/"));

            Assert.IsTrue(_sprite != null && _sprite.texture != null,
                "массовая выгрузка убила арт, который держит сцена");
        }

        // Повторная загрузка ТОГО ЖЕ адреса не должна терять прежнюю запись:
        // раньше словарь просто перезаписывался, и вместе со старой записью
        // исчезал её пин — сцена держала спрайт, о котором кэш уже не знал.
        [Test]
        public void ReplacingTheSameUrlKeepsTheHeldSpriteAlive()
        {
            _loader.CacheSpriteForTest("/content/art/hero.png", _sprite, 64);
            _loader.PinSprite(_sprite, true);

            var tex2 = new Texture2D(4, 4);
            var fresh = Sprite.Create(tex2, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            try
            {
                _loader.CacheSpriteForTest("/content/art/hero.png", fresh, 64);
                Assert.IsTrue(_sprite != null && _sprite.texture != null,
                    "новая загрузка того же адреса убила арт на экране");
                Assert.AreSame(fresh, _loader.CachedSpriteForTest("/content/art/hero.png"));
            }
            finally
            {
                if (fresh != null) UnityEngine.Object.DestroyImmediate(fresh);
                if (tex2 != null) UnityEngine.Object.DestroyImmediate(tex2);
            }
        }

        [Test]
        public void ADecodeFromBeforeTheUpdateCannotRefillTheCache()
        {
            const string url = "/content/art/hero.png";
            var oldKey = _loader.SpriteCacheKey(url);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["art/hero.png"] = "fresh" }, null);

            Assert.IsNull(_loader.CacheSprite(url, oldKey, _sprite, 64));
            Assert.IsNull(_loader.CachedSpriteForTest(url), "A late old decode resurrected stale art");
        }

        [Test]
        public void ALateOldDecodeCannotReplaceTheFreshSprite()
        {
            const string url = "/content/art/hero.png";
            var oldKey = _loader.SpriteCacheKey(url);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["art/hero.png"] = "fresh" }, null);
            var tex = new Texture2D(4, 4);
            var fresh = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            try
            {
                _loader.CacheSpriteForTest(url, fresh, 64);
                _loader.PinSprite(fresh, true);
                Assert.IsNull(_loader.CacheSprite(url, oldKey, _sprite, 64));
                Assert.AreSame(fresh, _loader.CachedSpriteForTest(url));
                Assert.IsTrue(fresh != null && fresh.texture != null);
            }
            finally
            {
                _loader.PinSprite(fresh, false);
                if (fresh != null) UnityEngine.Object.DestroyImmediate(fresh);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void VersionUpdatesEvictOnlyChangedArtAndKeepItsPins()
        {
            const string url = "/content/art/hero.png";
            _loader.CacheSpriteForTest(url, _sprite, 64);
            _loader.PinSprite(_sprite, true);
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["scripts/ch.lvn"] = "edited" }, null);
            Assert.AreSame(_sprite, _loader.CachedSpriteForTest(url));
            _loader.ApplyVersionDelta(new Dictionary<string, string> { ["art/hero.png"] = "fresh" }, null);
            Assert.IsNull(_loader.CachedSpriteForTest(url));
            Assert.IsTrue(_sprite != null && _sprite.texture != null,
                "Updating the index must not destroy art still held by the stage");
        }

        [Test]
        public void CachingTheSameSpriteTwicePreservesItsPinsAndByteCount()
        {
            const string url = "/content/art/hero.png";
            _loader.CacheSpriteForTest(url, _sprite, 64);
            _loader.PinSprite(_sprite, true);
            var bytes = typeof(ContentLoader).GetField("_spriteBytes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var before = (long)bytes.GetValue(_loader);
            _loader.CacheSpriteForTest(url, _sprite, 64);
            Assert.AreEqual(before, (long)bytes.GetValue(_loader));
            _loader.Unload(url);
            Assert.AreEqual(0, (long)bytes.GetValue(_loader));
            Assert.IsTrue(_sprite != null && _sprite.texture != null, "The second insertion lost the pin");
        }
    }
}
