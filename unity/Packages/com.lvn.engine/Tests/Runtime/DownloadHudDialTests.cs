using System.Collections;
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
    /// КРУЖОК ЗАГРУЗОК САДИТСЯ В ЧАСЫ ЛОГОТИПА.
    ///
    /// <para>Буква «O» в ROMANCE нарисована карманными часами, и кольцо
    /// загрузок задумано вокруг её циферблата, «по размеру её» (Илья
    /// 08.09): шапка знает, где часы (<c>LvnTopBar.LogoDialRect</c>), кружок
    /// только спрашивает. Строку, которая их связывала, потеряли при сведении
    /// 09.09, и кружок остался баблик-по-центру — ровно поверх слова TIME.
    /// Здесь связь проверяется геометрией: дали циферблат — кружок на нём;
    /// лист по-прежнему растёт из строки бара по центру; циферблата нет —
    /// кружок в строке бара, как в обычной шапке.</para>
    /// </summary>
    public sealed class DownloadHudDialTests
    {
        private PanelSettings _settings;
        private RenderTexture _texture;
        private GameObject _go;
        private System.Func<float> _clock;
        private bool _wasInChapter;

        [SetUp]
        public void SetUp()
        {
            _clock = LvnClock.Wall;
            _wasInChapter = LvnScreenDirector.Current.InChapter;
        }

        [TearDown]
        public void TearDown()
        {
            LvnClock.Wall = _clock;
            LvnScreenDirector.Current.SetChapter(_wasInChapter);
            if (_go != null) Object.Destroy(_go);
            if (_settings != null) Object.Destroy(_settings);
            if (_texture != null) Object.Destroy(_texture);
        }

        private VisualElement Root(int width, int height)
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _texture = new RenderTexture(width, height, 24);
            _settings.targetTexture = _texture;
            _settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _go = new GameObject("download-dial-test");
            var document = _go.AddComponent<UIDocument>();
            document.panelSettings = _settings;
            LvnFonts.ApplyDefault(document.rootVisualElement);
            return document.rootVisualElement;
        }

        private static TransferSnapshot Work(int seconds)
            => new TransferSnapshot(2, 12, seconds, (long)seconds * 5 << 20, 50 << 20, 50 << 20, 0, null, 1);

        private static bool Measured(VisualElement el)
            => el != null && !float.IsNaN(el.worldBound.width) && el.worldBound.width > 0f;

        [UnityTest]
        public IEnumerator MiniRing_SitsOnTheDial_AndTheSheetStillGrowsFromTheBar()
        {
            var root = Root(1170, 2532);
            LvnScreenDirector.Current.SetChapter(false);
            float now = 1f;
            LvnClock.Wall = () => now;

            var hud = new DownloadHud();
            root.Add(hud);
            // Часы в логотипе облика — 53 px на телефоне: крупнее прежнего
            // баблика, и кольцо вокруг них не должно резаться его коробкой.
            var dial = new Rect(470f, 210f, 60f, 60f);
            hud.MiniAnchor = () => dial;
            hud.Tick(Work(2));
            for (int i = 0; i < 6; i++) yield return null;

            var capsule = hud.Q("download-capsule");
            if (!Measured(capsule))
                Assert.Ignore("панель UITK в этой среде не считает раскладку — размеры проверить нечем");
            Assert.AreEqual(dial.center.x, capsule.worldBound.center.x, 1f, "кружок стоит по центру циферблата");
            Assert.AreEqual(dial.center.y, capsule.worldBound.center.y, 1f, "кружок стоит по центру циферблата");
            var ring = hud.Q("download-ring");
            Assert.IsNotNull(ring, "кольцо названо — его меряют");
            Assert.GreaterOrEqual(ring.worldBound.width, dial.width, "кольцо обнимает часы");
            Assert.LessOrEqual(ring.worldBound.width, dial.width * 1.7f, "кольцо по размеру часов, а не баблик поверх логотипа");
            Assert.GreaterOrEqual(capsule.worldBound.width, ring.worldBound.width,
                "капсула вмещает кольцо целиком — иначе коробка с overflow:hidden срезает его до невидимых дужек (стенд 12.09)");

            hud.SetExpanded(true);
            now = 4f;
            hud.Tick(Work(4));
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.AreEqual(1170f * 0.5f, capsule.worldBound.center.x, 2f, "лист растёт по центру экрана");
            Assert.Less(capsule.worldBound.yMin, dial.yMin, "лист начинается в строке бара, выше циферблата");
            // Раскрытый лист в облике темы — к эталону: график, ряды, кнопки.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                var shot = TestPixels.Read(_texture);
                try { TestPixels.AssertGolden(shot, "download-sheet-1170x2532"); }
                finally { Object.Destroy(shot); }
            }

            hud.SetExpanded(false);
            yield return new WaitForSecondsRealtime(0.6f);
            hud.MiniAnchor = () => null;
            now = 5f;
            hud.Tick(Work(5));
            for (int i = 0; i < 6; i++) yield return null;
            Assert.AreEqual(1170f * 0.5f, capsule.worldBound.center.x, 2f, "без циферблата кружок — центр строки бара, как в обычной шапке");
        }
    }
}
