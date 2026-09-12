using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    /// <summary>
    /// КОМПОЗИЦИЯ ГЛАВНОЙ В ОБЛИКЕ «СЦЕНА» — ДОГОВОР С МАКЕТОМ.
    ///
    /// <para>Столбик панелей прижат к низу над рисованным меню, и любая
    /// кнопка, поставленная ПОД него, молча поднимает весь столбик: 11.09
    /// кнопка круток сдвинула панели на 47 dp от макета, а заметили это
    /// сверкой кадров через сутки, не сборкой. Здесь раскладка меряется по
    /// worldBound в панели телефона — без GPU и без арта: рамки не грузятся,
    /// но места занимают те же.</para>
    /// </summary>
    public sealed class HubStageCompositionTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
                => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private PanelSettings _settings;
        private RenderTexture _texture;
        private GameObject _go;

        /// <summary>Панель телефона в пикселях: раскладка считается, рисовать
        /// не обязательно.</summary>
        private VisualElement Root(int width, int height)
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _texture = new RenderTexture(width, height, 24);
            _settings.targetTexture = _texture;
            _settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _go = new GameObject("hub-stage-test");
            var document = _go.AddComponent<UIDocument>();
            document.panelSettings = _settings;
            LvnFonts.ApplyDefault(document.rootVisualElement);
            return document.rootVisualElement;
        }

        private static IEnumerator Layout(int frames = 4)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        private static bool Measured(VisualElement el)
            => el != null && !float.IsNaN(el.worldBound.height) && el.worldBound.height > 0f;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.Destroy(_go);
            if (_settings != null) Object.Destroy(_settings);
            if (_texture != null) Object.Destroy(_texture);
        }

        [UnityTest]
        public IEnumerator SpinButton_JoinsTheAdRow_AndLeavesTheColumnWhereTheMockupPutIt()
        {
            var root = Root(1170, 2532);
            var hub = new BrowseHub(new BrowseConfig { skin = "/skin/", layout = "hub" }, new NoAssets());
            root.Add(hub);
            hub.SetContent(new LvnManifest
            {
                titles = new List<LvnTitle> { new LvnTitle { id = "a", name = "Первая" } },
            });
            yield return Layout();

            // Низ столбика панелей — кнопка «Открыть» карточки экспедиции.
            var card = hub.Q("stage-open-card");
            if (!Measured(card))
                Assert.Ignore("панель UITK в этой среде не считает раскладку — размеры проверить нечем");
            float cardBottomBefore = card.worldBound.yMax;

            hub.OnSpin = () => { };
            yield return Layout();

            var spin = hub.Q("stage-spin");
            Assert.IsNotNull(spin, "кнопка круток есть и названа — по имени её находят тесты и тур");
            Assert.AreEqual(DisplayStyle.Flex, spin.resolvedStyle.display, "есть кому открывать — кнопка показана");
            Assert.AreEqual(cardBottomBefore, card.worldBound.yMax, 0.5f,
                "кнопка круток встала В РЯД с наградой, а не под ней: столбик панелей остался там, где его поставил макет");
            Assert.AreEqual(LvnStageKit.D(LvnStageSkin.Adv.Height), spin.worldBound.height, 1f,
                "высота — как у кнопки награды");
            Assert.AreEqual(LvnStageKit.D(LvnStageSkin.Adv.Width), spin.worldBound.width, 1f,
                "ширина — как у кнопки награды: рамка не сплющена под слово");
            Assert.GreaterOrEqual(spin.worldBound.yMin, card.worldBound.yMax, "ряд кнопок ниже карточки");
        }

        /// <summary>Кадр главной в облике «сцена» без арта (рамки не грузятся,
        /// слова и плашки — да) против эталона: съехавший шрифт, цвет или
        /// раскладку числа выше не видят, а глаз — да.</summary>
        [UnityTest]
        public IEnumerator StageHub_1170x2532_MatchesTheGoldenFrame()
        {
            TestPixels.RequireGraphics();
            var root = Root(1170, 2532);
            var hub = new BrowseHub(new BrowseConfig { skin = "/skin/", layout = "hub" }, new NoAssets());
            root.Add(hub);
            hub.SetContent(new LvnManifest
            {
                titles = new List<LvnTitle> { new LvnTitle { id = "a", name = "Первая" } },
            });
            hub.OnSpin = () => { };
            yield return Layout(8);
            if (!Measured(hub.Q("stage-open-card")))
                Assert.Ignore("панель UITK в этой среде не считает раскладку — кадр сравнивать не с чем");
            var shot = TestPixels.Read(_texture);
            try { TestPixels.AssertGolden(shot, "hub-stage-1170x2532"); }
            finally { Object.Destroy(shot); }
        }
    }
}
