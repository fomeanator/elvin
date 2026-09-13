using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    // Проверяем границы текста после настоящей раскладки, а не назначенные
    // числа стилей: прежние 112 единиц проходили EditMode, обрезая подпись.
    public sealed class BrowseHubLayoutTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private RenderTexture _texture;
        private Rect? _safe;
        private bool _chapter;
        private LvnTheme _theme;
        private const string LongTitle = "Тайна старинного ожерелья";
        private const string Subtitle = "Англия 1864 г. Детектив";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Vector2Int[] Sizes = {
            new Vector2Int(750, 1334), new Vector2Int(1179, 2556),
            new Vector2Int(1320, 2868), new Vector2Int(1080, 2340),
            new Vector2Int(1080, 2400), new Vector2Int(1536, 2048),
            new Vector2Int(2048, 1536)
        };

        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [SetUp]
        public void SetUp()
        {
            _safe = LvnEdges.Simulated;
            _chapter = LvnScreenDirector.Current.InChapter;
            _theme = LvnTheme.Current;
            LvnScreenDirector.Current.AnnounceChapter(false);
        }

        [TearDown]
        public void TearDown()
        {
            DestroyPanel();
            LvnEdges.Simulated = _safe;
            LvnScreenDirector.Current.AnnounceChapter(_chapter);
            LvnTheme.Use(_theme);
        }

        private void DestroyPanel()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_panel != null) { _panel.targetTexture = null; Object.DestroyImmediate(_panel); }
            if (_texture != null) { _texture.Release(); Object.DestroyImmediate(_texture); }
        }

        private VisualElement Panel(Vector2Int size)
        {
            DestroyPanel();
            // Обрезка ScrollView и скруглённых карточек использует stencil.
            _texture = new RenderTexture(size.x, size.y, 24);
            _texture.Create();
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
            _panel.targetTexture = _texture;
            _panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            _panel.referenceResolution = new Vector2Int(1080, 1920);
            _panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            _panel.match = 0;
#if UNITY_EDITOR
            _panel.themeStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss");
#endif
            _go = new GameObject("hub-layout-panel");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = _panel;
            return doc.rootVisualElement;
        }

        private static BrowseHub Hub()
        {
            var hub = new BrowseHub(new BrowseConfig { title = "Library", subtitle = "Choose your path", show_daily = false }, new NoAssets());
            hub.SetData(new List<LvnCollection> {
                new LvnCollection { id = "layout", name = "Stories", titles = new List<string> { "layout-long", "layout-short" } }
            }, new List<LvnTitle> {
                new LvnTitle { id = "layout-long", name = LongTitle, subtitle = Subtitle },
                new LvnTitle { id = "layout-short", name = "Waylight", subtitle = "A fading fairy kingdom, four sparks, and one very stubborn boy." }
            });
            return hub;
        }

        private static Label LabelOf(VisualElement root, string text)
            => root.Query<Label>().ToList().First(l => l.text == text);

        private static void TextFits(Label label, VisualElement card)
        {
            var natural = label.MeasureTextSize(label.text, label.contentRect.width,
                VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
            Assert.Greater(natural.y, 0, "шрифт не загрузился");
            Assert.LessOrEqual(natural.y, label.contentRect.height + 2f, "сам текст обрезан: " + label.text);
            Assert.LessOrEqual(label.worldBound.yMax, card.worldBound.yMax - card.resolvedStyle.borderBottomWidth + 1f,
                "подпись вышла за нижнюю кромку карточки: " + label.text);
        }

        [UnityTest]
        public IEnumerator CaptionsFitAndCardsStayAlignedAcrossDeviceMatrix()
        {
            foreach (var size in Sizes)
            {
                var root = Panel(size);
                LvnEdges.Simulated = new Rect(0, 0, Screen.width, Screen.height);
                var hub = Hub();
                root.Add(hub);
                yield return new WaitForSecondsRealtime(.4f);
                var cards = hub.Query(name: "hub-shelf-card").ToList();
                var strip = hub.Query<ScrollView>().ToList().First(s => s.mode == ScrollViewMode.Horizontal);
                var feed = (ScrollView)typeof(BrowseHub).GetField("_hubRows", Private).GetValue(hub);
                feed.ScrollTo(strip);
                yield return new WaitForSecondsRealtime(.2f);
                SaveFrame("captions-" + size.x + "x" + size.y);
                TextFits(LabelOf(cards[0], LongTitle), cards[0]);
                TextFits(LabelOf(cards[0], Subtitle), cards[0]);
                TextFits(cards[1].Query<Label>().ToList().Last(), cards[1]);
                foreach (var card in cards)
                {
                    Assert.AreEqual(strip.resolvedStyle.height, card.resolvedStyle.height, 1f, size.ToString());
                    Assert.AreEqual(564f, card[0].resolvedStyle.height, 1f, "постер ужался ради текста");
                }
            }
        }

        [UnityTest]
        public IEnumerator CaptionReflowsAfterTextAndFontChangesWithoutGrowingForever()
        {
            var root = Panel(Sizes[0]);
            var hub = Hub();
            root.Add(hub);
            yield return new WaitForSecondsRealtime(.4f);
            var card = hub.Q("hub-shelf-card");
            var name = LabelOf(card, LongTitle);
            float original = card.resolvedStyle.height;
            name.text = LongTitle + ": последняя глава забытой истории";
            float font = name.resolvedStyle.fontSize;
            name.style.fontSize = font * 1.5f;
            yield return new WaitForSecondsRealtime(.3f);
            TextFits(name, card);
            TextFits(LabelOf(card, Subtitle), card);
            float grown = card.resolvedStyle.height;
            Assert.Greater(grown, original, "ряд не вырос под длинный текст");
            yield return new WaitForSecondsRealtime(.3f);
            Assert.AreEqual(grown, card.resolvedStyle.height, 1f, "высота растёт на каждом пересчёте");
            name.text = LongTitle;
            name.style.fontSize = font;
            yield return new WaitForSecondsRealtime(.3f);
            Assert.AreEqual(original, card.resolvedStyle.height, 1f, "ряд не сжался после смены текста");
        }

        [UnityTest]
        public IEnumerator BrandStaysBelowExternalBarWhenNotchAndBarConnectionChange()
        {
            foreach (var size in Sizes)
            {
                var root = Panel(size);
                var hub = Hub();
                hub.ExternalTopBar = true;
                root.Add(hub);
                var bar = new LvnTopBar();
                root.Add(bar);
                yield return null;
                foreach (int notch in new[] { 0, 141, 189 })
                {
                    LvnEdges.Simulated = new Rect(0, 0, Screen.width, Screen.height - notch);
                    yield return new WaitForSecondsRealtime(.65f);
                    var eyebrow = (Label)typeof(BrowseHub).GetField("_hubEyebrow", Private).GetValue(hub);
                    var row = (VisualElement)typeof(LvnTopBar).GetField("_row", Private).GetValue(bar);
                    SaveFrame("header-" + size.x + "x" + size.y + "-" + notch);
                    Assert.Greater(row.worldBound.height, 1f);
                    Assert.GreaterOrEqual(eyebrow.worldBound.yMin, row.worldBound.yMax,
                        $"{size}, вырез {notch}: верхняя панель накрыла надзаголовок");
                }
                SaveFrame("header-" + size.x + "x" + size.y);
                hub.ExternalTopBar = false;
                yield return null;
                var view = (VisualElement)typeof(BrowseHub).GetField("_hubView", Private).GetValue(hub);
                Assert.AreEqual(LvnEdges.Top(hub, LvnEdges.HomeTopMin, LvnEdges.PageTopAir),
                    view.resolvedStyle.paddingTop, 1f, "самостоятельный хаб сохранил отступ чужого бара");
            }
        }

        private void SaveFrame(string name)
        {
            string directory = System.Environment.GetEnvironmentVariable("LVN_HUB_SHOTS");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var pixels = TestPixels.Read(_texture);
            try { File.WriteAllBytes(Path.Combine(directory, name + ".png"), pixels.EncodeToPNG()); }
            finally { Object.DestroyImmediate(pixels); }
        }
    }
}
