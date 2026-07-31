using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class CarouselAndHudTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        // ── CarouselSnap ──
        [Test]
        public void CarouselSnap_OffsetIndexRoundTrip()
        {
            var s = new CarouselSnap(stride: 100f, count: 4);
            Assert.AreEqual(0f, s.OffsetFor(0), 0.001f);
            Assert.AreEqual(200f, s.OffsetFor(2), 0.001f);
            Assert.AreEqual(2, s.IndexAt(180f));   // rounds to nearest
            Assert.AreEqual(2, s.IndexAt(220f));
        }

        [Test]
        public void CarouselSnap_ClampsAndValidates()
        {
            var s = new CarouselSnap(100f, 3);
            Assert.AreEqual(0, s.Clamp(-5));
            Assert.AreEqual(2, s.Clamp(99));
            // out-of-range index clamps to the last card before mapping to an offset
            Assert.AreEqual(200f, s.OffsetFor(99), 0.001f);
            Assert.AreEqual(0f, s.OffsetFor(-3), 0.001f);
            Assert.AreEqual(200f, s.OffsetFor(2), 0.001f);
            Assert.IsTrue(s.IsValid(1));
            Assert.IsFalse(s.IsValid(3));
        }

        [Test]
        public void CarouselSnap_EmptyIsSafe()
        {
            var s = new CarouselSnap(100f, 0);
            Assert.AreEqual(0, s.Clamp(2));
            Assert.AreEqual(0, s.IndexAt(500f));
            Assert.IsFalse(s.IsValid(0));
        }

        // ── Percent ──
        [Test]
        public void Percent_RoundsAndClamps()
        {
            Assert.AreEqual(0, Percent.Value(0, 0));
            Assert.AreEqual(50, Percent.Value(1, 2));
            Assert.AreEqual(100, Percent.Value(3, 3));
            Assert.AreEqual(100, Percent.Value(9, 3));   // clamps over 100
            Assert.AreEqual("50%", Percent.Text(1, 2));
        }

        // ── IdleCreep ──
        [Test]
        public void IdleCreep_StartsAtZeroRisesBoundedByCeiling()
        {
            Assert.AreEqual(0f, LoadingProgressModel.IdleCreepTarget(0f), 0.0001f);
            float a = LoadingProgressModel.IdleCreepTarget(1f);
            float b = LoadingProgressModel.IdleCreepTarget(5f);
            Assert.Greater(b, a);
            Assert.LessOrEqual(b, LoadingProgressModel.IdleCreepCeiling + 0.0001f);
        }

        // ── component smoke ──
        [Test]
        public void BootScreen_Builds()
        {
            var s = new BootScreen(null, new NoAssets());
            Assert.Greater(s.childCount, 0);
        }

        [Test]
        public void TitleCarousel_BuildsFromTitlesAndPlayFires()
        {
            var titles = new List<LvnTitle>
            {
                new LvnTitle { id = "a", name = "Title A", subtitle = "one" },
                new LvnTitle { id = "b", name = "Title B" },
            };
            var c = new TitleCarousel(titles, new CarouselConfig { play_text = "Go" }, new NoAssets());
            c.OnPlay += _ => { };       // subscribable
            c.OnIndexChanged += _ => { };
            Assert.Greater(c.childCount, 0);
            Assert.AreEqual("a", c.Current.id);
            Assert.AreEqual(0, c.Index);
        }

        [Test]
        public void TitleCarousel_RequestPlayBeforeSubscribe_IsLatchedThenConsumed()
        {
            var titles = new List<LvnTitle>
            {
                new LvnTitle { id = "a" }, new LvnTitle { id = "b" }, new LvnTitle { id = "c" },
            };
            var c = new TitleCarousel(titles, new CarouselConfig(), new NoAssets());
            // No OnPlay subscriber yet (mirrors firing during the boot splash).
            c.RequestPlay(2);
            Assert.IsTrue(c.TryConsumePendingPlay(out int idx), "early RequestPlay should latch");
            Assert.AreEqual(2, idx);
            Assert.IsFalse(c.TryConsumePendingPlay(out _), "latch is consumed exactly once");
        }

        [Test]
        public void TitleCarousel_RequestPlayWithSubscriber_FiresImmediatelyAndDoesNotLatch()
        {
            var titles = new List<LvnTitle> { new LvnTitle { id = "a" }, new LvnTitle { id = "b" } };
            var c = new TitleCarousel(titles, new CarouselConfig(), new NoAssets());
            int fired = -1;
            c.OnPlay += i => fired = i;
            c.RequestPlay(1);
            Assert.AreEqual(1, fired, "with a subscriber RequestPlay fires synchronously");
            Assert.IsFalse(c.TryConsumePendingPlay(out _), "nothing latched when delivered live");
        }

        [Test]
        public void GameHud_ProgressAndPills()
        {
            var hud = new GameHud(new HudConfig(), new NoAssets());
            hud.SetProgress(1, 2);
            hud.SetBalance("soft", 1234);
            hud.SetBalances(new Dictionary<string, long> { { "hard", 5 } });
            Assert.Greater(hud.childCount, 0);
        }

        [Test]
        public void NovelShell_FirstChapterPicksLowestNumber()
        {
            var title = new LvnTitle
            {
                id = "t",
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter>
                    {
                        new LvnChapter { id = "c2", number = 2 },
                        new LvnChapter { id = "c1", number = 1 },
                    }},
                },
            };
            Assert.AreEqual("c1", NovelShell.FirstChapter(title).id);
            Assert.IsNull(NovelShell.FirstChapter(new LvnTitle { id = "empty" }));
            Assert.IsNull(NovelShell.FirstChapter(null));
        }

        [Test]
        public void Manifest_TitleNameAndUiSlidersDeserialize()
        {
            var json = @"{
                ""titles"":[{""id"":""t1"",""name"":""Demo"",""subtitle"":""tag""}],
                ""ui"":{
                    ""boot"":{""min_seconds"":2.0,""logo_url"":""/l.png""},
                    ""carousel"":{""card_width"":0.7,""play_text"":""Start""},
                    ""hud"":{""show_progress"":false,""height"":0.05}
                }
            }";
            var m = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            Assert.AreEqual("Demo", m.titles[0].name);
            Assert.AreEqual("tag", m.titles[0].subtitle);
            Assert.AreEqual(2.0f, m.ui.boot.min_seconds);
            Assert.AreEqual(0.7f, m.ui.carousel.card_width);
            Assert.AreEqual("Start", m.ui.carousel.play_text);
            Assert.AreEqual(false, m.ui.hud.show_progress);
        }

        // Якорь лейбла пишут ЧИСЛАМИ — той же парой, что у `obj` ("0.5,0.5"). Раньше
        // разбирались только слова, и число молча падало в «центр»: `anchor="0,0"`
        // не прижимал левый край, а центрировал лейбл по x, унося половину строки
        // за экран. Обе формы обязаны работать.
        [Test]
        public void LabelAnchor_ReadsNumericPairsAndWords()
        {
            Assert.AreEqual((0f, 0f), VnStage.LabelAnchor("0,0"), "0,0 — левый верх");
            Assert.AreEqual((-100f, 0f), VnStage.LabelAnchor("1,0"), "1,0 — правый край");
            Assert.AreEqual((-50f, 0f), VnStage.LabelAnchor("0.5,0"), "0.5,0 — центр по горизонтали");
            Assert.AreEqual((-50f, -50f), VnStage.LabelAnchor("0.5,0.5"), "0.5,0.5 — центр");
            // словесная форма остаётся рабочей
            Assert.AreEqual((0f, 0f), VnStage.LabelAnchor("top-left"));
            Assert.AreEqual((-100f, -100f), VnStage.LabelAnchor("bottom-right"));
            // пустой якорь = левый верх, как было до правки
            Assert.AreEqual((0f, 0f), VnStage.LabelAnchor(null));
        }

        // Доля якоря нужна бюджету ширины: лейбл у правого края растёт ВЛЕВО, и
        // выдавать ему «остаток справа» — значит заставить переносить по букве.
        [Test]
        public void LabelAnchorFractions_DriveTheWidthBudget()
        {
            Assert.AreEqual((1f, 0f), VnStage.LabelAnchorFractions("1,0"));
            Assert.AreEqual((0f, 0f), VnStage.LabelAnchorFractions("0,0"));
            Assert.AreEqual((0.5f, 0.5f), VnStage.LabelAnchorFractions("center"));
        }


        // Числовое поле принимает ВЫРАЖЕНИЕ, а не только литерал. Без этого любая
        // величина, зависящая от состояния (доля здоровья, прогресс), писалась
        // лестницей почти одинаковых веток — по строке на каждое значение.
        [Test]
        public void NumericFields_AcceptLiveExpressions()
        {
            var vars = new Dictionary<string, JToken>
            {
                ["hp"] = 3, ["hp_max"] = 8, ["k"] = 0.25f,
            };
            var cmd = JObject.Parse(@"{""op"":""obj"",""id"":""bar"",
                ""fill"":""{hp / hp_max}"", ""width"":""{k}"", ""height"":0.05}");

            var p = VnStage.PlacementFrom(cmd, vars: vars);

            Assert.AreEqual(0.375f, p.Fill.Value, 1e-4f, "доля заливки — из живых переменных");
            Assert.AreEqual(0.25f, p.Width.Value, 1e-4f, "выражение в width");
            Assert.AreEqual(0.05f, p.Height.Value, 1e-4f, "обычный литерал по-прежнему работает");
        }

        // Без переменных (пересборка сцены до старта, тесты) поле не должно
        // валить кадр — просто «значения нет».
        [Test]
        public void NumericExpression_WithoutVars_IsSilentlyEmpty()
        {
            var cmd = JObject.Parse(@"{""op"":""obj"",""id"":""bar"",""fill"":""{hp / hp_max}""}");
            var p = VnStage.PlacementFrom(cmd);
            Assert.IsNull(p.Fill, "нечего вычислять — поле пустое, а не 0 и не исключение");
        }


        // Глубина — одно число вместо пары «размер + порядок» на каждую дистанцию.
        [Test]
        public void Depth_ScalesByPerspective()
        {
            Assert.AreEqual(1f, WorldPlacement.DepthScale(null), 1e-4f, "без глубины — натуральный размер");
            Assert.AreEqual(1f, WorldPlacement.DepthScale(0f), 1e-4f, "план камеры");
            Assert.AreEqual(0.5f, WorldPlacement.DepthScale(WorldPlacement.FocalDepth), 1e-4f,
                "на фокусной дистанции фигура вдвое меньше");
            Assert.Greater(WorldPlacement.DepthScale(2f), WorldPlacement.DepthScale(8f),
                "дальше — мельче, монотонно");
            Assert.Greater(WorldPlacement.DepthScale(-3f), 1f, "ближе плана — крупнее");
        }

        // Процент экрана — одна и та же запись и для долей, и для процентов.
        [Test]
        public void PercentSuffix_WorksInPlacementFields()
        {
            var cmd = JObject.Parse(@"{""op"":""obj"",""id"":""x"",""x"":""50%"",""y"":0.25,""width"":""10%""}");
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.5f, p.X, 1e-4f, "50% = половина экрана");
            Assert.AreEqual(0.25f, p.Y, 1e-4f, "доля по-прежнему доля");
            Assert.AreEqual(0.1f, p.Width.Value, 1e-4f);
        }

        // Точка в наборе разбирается вместе с выражениями.
        [Test]
        public void WorldPoint_ParsesWithExpressions()
        {
            var vars = new Dictionary<string, JToken> { ["z"] = 8.5f };
            var cmd = JObject.Parse(@"{""op"":""actor"",""id"":""s"",""world"":""12.4, 0, {z}""}");
            var p = VnStage.PlacementFrom(cmd, vars: vars);
            Assert.IsNotNull(p.World);
            Assert.AreEqual(12.4f, p.World.Value.x, 1e-3f);
            Assert.AreEqual(8.5f, p.World.Value.z, 1e-3f);
        }

    }
}
