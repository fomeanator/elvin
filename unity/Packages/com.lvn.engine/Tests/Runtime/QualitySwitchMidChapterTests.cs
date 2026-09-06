using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ИГРОК МЕНЯЕТ КАЧЕСТВО ПОСРЕДИ ГЛАВЫ — И НИЧЕГО НЕ ЛОМАЕТСЯ.
    ///
    /// <para>В таблице обещаний условие записано именно так: «переключить
    /// качество, имея сейв в середине». Соседние проверки берут другие
    /// половины: `ArtRungOverTheWireTests` — что с провода уходит выбранная
    /// ступень, `qa/quality-rung-cost-check.sh` — что «поменьше» и правда
    /// дешевле. Обе меряют ПОКУПКУ арта, а не смену настройки под живой
    /// сценой.</para>
    ///
    /// <para>А ломаться тут есть чему. Настройка меняется в момент, когда на
    /// экране стоят фон и люди: если их спрайты выдернуть до прихода новых,
    /// игрок увидит пустоту; если не перекачать вовсе, настройка окажется
    /// украшением (живой репорт «героиню не перекачала»); если сбить позицию
    /// плеера, сейв середины главы перестанет быть серединой.</para>
    /// </summary>
    public class QualitySwitchMidChapterTests
    {
        private sealed class UrlSpy : ILvnAssets
        {
            public readonly List<string> Asked = new List<string>();
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                lock (Asked) Asked.Add(url);
                return Task.FromResult<Sprite>(null);
            }
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
            {
                if (urls != null) lock (Asked) Asked.AddRange(urls);
                return Task.CompletedTask;
            }
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private const string Doc = @"{""script"":[
            {""op"":""bg"",""sprite_url"":""bg/зал.jpg""},
            {""op"":""actor"",""id"":""она"",""sprite_url"":""art/она.png"",""show"":true},
            {""op"":""say"",""text"":""первая""},
            {""op"":""say"",""text"":""вторая""},
            {""op"":""say"",""text"":""третья""}
        ]}";

        private GameObject _go;
        private VnStage _stage;
        private UrlSpy _spy;
        private string _suffixWas;

        [SetUp]
        public void SetUp()
        {
            _suffixWas = DownloadPolicy.PreferredSuffix;
            _go = new GameObject("quality-stage", typeof(UIDocument));
            _go.GetComponent<UIDocument>().panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _spy = new UrlSpy();
            _stage = _go.AddComponent<VnStage>();
            _stage.Assets = _spy;
        }

        [TearDown]
        public void TearDown()
        {
            DownloadPolicy.PreferredSuffix = _suffixWas;
            if (_go != null) Object.Destroy(_go);
        }

        private List<string> Спрошено()
        {
            lock (_spy.Asked) return _spy.Asked.ToList();
        }

        [UnityTest]
        public IEnumerator СменаКачестваПересобираетКадр_НеТеряяЕгоИНеСбиваяПозицию()
        {
            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q2k;
            _stage.Play(Doc);
            yield return new WaitForSecondsRealtime(0.4f);
            _stage.Player.Advance();                    // стоим на второй реплике — середина
            yield return new WaitForSecondsRealtime(0.2f);

            var кадрДо = _stage.ActorsInFrame().OrderBy(x => x).ToList();
            int позицияДо = _stage.Player.Index;
            int спрошеноДо = Спрошено().Count;
            CollectionAssert.Contains(кадрДо, "она", "стенд встал не там: в кадре нет актёра");

            // ── игрок открыл настройки и выбрал «поменьше» ──────────────────
            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            _stage.RefreshArtQuality();
            yield return new WaitForSecondsRealtime(0.6f);

            var новые = Спрошено().Skip(спрошеноДо).ToList();
            TestContext.WriteLine("после смены спросили: " + string.Join(", ", новые));

            // 1. Настройка не украшение: видимое перекачивается сразу.
            Assert.IsTrue(новые.Any(u => u.Contains("art/она")),
                "актёр на экране не перекачался — настройка качества подействовала бы "
              + "только на будущие показы (живой репорт «героиню не перекачала»)");
            Assert.IsTrue(новые.Any(u => u.Contains("bg/зал")),
                "фон не перекачался при смене качества");

            // 2. Кадр цел: те же люди, никто не пропал.
            var кадрПосле = _stage.ActorsInFrame().OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(кадрДо, кадрПосле,
                "смена качества изменила состав кадра — игрок увидел бы, как кто-то исчез");

            // 3. Середина главы осталась серединой.
            Assert.AreEqual(позицияДо, _stage.Player.Index,
                "смена настройки сдвинула позицию — сейв середины перестал быть серединой");
        }

        // ОБРАТНЫЙ ХОД. Настройку меняют не в одну сторону: игрок, вернувшийся
        // на Wi-Fi, ждёт прежнего качества, а не «поменьше навсегда».
        [UnityTest]
        public IEnumerator ВозвратКрупногоКачестваТожеПересобираетКадр()
        {
            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            _stage.Play(Doc);
            yield return new WaitForSecondsRealtime(0.4f);
            int спрошеноДо = Спрошено().Count;

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q2k;
            _stage.RefreshArtQuality();
            yield return new WaitForSecondsRealtime(0.6f);

            var новые = Спрошено().Skip(спрошеноДо).ToList();
            Assert.IsTrue(новые.Any(u => u.Contains("art/она") || u.Contains("bg/зал")),
                "возврат к крупному качеству ничего не перекачал — игрок остался с мелким артом");
        }
    }
}
