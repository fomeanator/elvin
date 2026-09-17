using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Shell.Tests
{
    /// <summary>
    /// ЭКРАНЫ ПО МАКЕТАМ 17.09: список новелл, попап детали и сюжет
    /// реальности в облике «сцена». Проверяем СОСТАВ, а не пиксели: что
    /// карточка несёт окно, ход и плашку «Открыть», что деталь рисует мир,
    /// жанры и статус, что комната сообщений различает прочитанное, новое и
    /// закрытое и что фильтр прячет прочитанное.
    /// </summary>
    public sealed class StageViewsTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private const string Skin = "/test/skin/";

        [TearDown]
        public void Restore() => LvnStageSkin.Reset();

        private static LvnTitle Title(string id, string name, int chapters = 3, string unlock = null, string type = null)
        {
            var t = new LvnTitle { id = id, name = name, unlock = unlock, type = type, seasons = new List<LvnSeason>() };
            var s = new LvnSeason { chapters = new List<LvnChapter>() };
            for (int i = 1; i <= chapters; i++) s.chapters.Add(new LvnChapter { id = $"{id}-{i}", number = i, name = $"Глава {i}" });
            t.seasons.Add(s);
            return t;
        }

        private static BrowseHub StagedHub(List<LvnCollection> collections, List<LvnTitle> titles)
        {
            var hub = new BrowseHub(new BrowseConfig { skin = Skin, news_empty_text = "Нет новых сообщений" }, new NoAssets());
            hub.SetData(collections, titles);
            return hub;
        }

        private static LvnManifest StagedManifest(string extra = "")
            => JsonConvert.DeserializeObject<LvnManifest>(
                "{\"ui\":{\"browse\":{\"skin\":\"" + Skin + "\"" + extra + "}}}");

        // ── список ───────────────────────────────────────────────────────────

        [Test]
        public void ListCardCarriesWindowCourseAndOpenPlate()
        {
            var t = Title("a", "Тайна");
            t.subtitle = "Англия 1864 г.";
            var hub = StagedHub(null, new List<LvnTitle> { t });

            var card = hub.TitleCardFor(t);

            Assert.AreEqual("stage-title-card", card.name, "карточка списка — карточка макета");
            Assert.NotNull(card.Q(name: "stage-glow"), "светящаяся рамка под карточкой");
            Assert.NotNull(card.Q(name: "stage-card-window"), "окно постера");
            Assert.NotNull(card.Q(name: "stage-window"), "кромка окна поверх постера");
            Assert.NotNull(card.Q(name: "stage-course-bar"), "не пройдена — полоса хода");
            Assert.IsNull(card.Q(name: "stage-course-done"), "не пройдена — без «Пройдено»");
            Assert.NotNull(card.Q(name: "stage-card-open"), "плашка «Открыть»");
            StringAssert.Contains("ТАЙНА", card.Q<Label>("stage-card-name").text, "название прописными");
            Assert.AreNotEqual(DisplayStyle.None, card.Q(name: "stage-card-era").style.display.value, "эпоха дана — строка есть");
        }

        [Test]
        public void ListCardWithoutASubtitleHidesTheEraLineInsteadOfShowingTheId()
        {
            var t = Title("bg3d", "3D-сцена");
            var hub = StagedHub(null, new List<LvnTitle> { t });

            var card = hub.TitleCardFor(t);

            Assert.AreEqual(DisplayStyle.None, card.Q(name: "stage-card-era").style.display.value,
                "без подзаголовка строка эпохи спрятана — иначе игрок видел бы id новеллы");
        }

        [Test]
        public void ListHeaderNamesTheRoomAndBackGoesHome()
        {
            var screen = new TitlesScreen(new NoAssets());
            int back = 0;
            screen.Back = () => back++;
            screen.Titles = () => new List<LvnTitle>();
            screen.SetContent(StagedManifest());

            Assert.AreEqual("CURRENT EXPEDITIONS", screen.Q<Label>("stage-header-title").text, "заголовок макета прописными");
            Assert.AreEqual("CHOOSE AN ERA", screen.Q<Label>("stage-header-hint").text, "подзаголовок макета");
            Assert.NotNull(screen.Q(name: "stage-header-back"), "стрелка «назад»");
            Assert.NotNull(screen.Q(name: "stage-scrim-top"), "затемнение под шапкой");
            Assert.NotNull(screen.Q(name: "stage-scrim-bottom"), "затемнение над лентой");
        }

        // ── витрина: сюжет реальности ────────────────────────────────────────

        [Test]
        public void RealityTitlesFollowTheRealityCollectionOrder()
        {
            var a = Title("a", "А", type: "reality");
            var b = Title("b", "Б");
            var c = Title("c", "В", unlock: "never_set_flag");
            var hub = StagedHub(new List<LvnCollection>
            {
                new LvnCollection { id = "r", type = "reality", titles = new List<string> { "c", "a" } },
            }, new List<LvnTitle> { a, b, c });

            var reality = hub.RealityTitles;

            CollectionAssert.AreEqual(new[] { "c", "a" }, new[] { reality[0].id, reality[1].id }, "порядок подборки, не каталога");
            Assert.AreEqual(LvnTitleMark.Locked, hub.MarkOf(c), "замок хаба — «заблокировано»");
            Assert.AreEqual(LvnTitleMark.New, hub.MarkOf(a), "открытая и не пройденная — «новое»");
            Assert.AreEqual(1, hub.NewsCount, "считаются только новые");
            Assert.AreEqual(2, hub.WorldNumberOf(a), "номер мира — место в подборке");
            Assert.AreEqual(2, hub.WorldNumberOf(b), "без подборки — место в каталоге");
        }

        [Test]
        public void WithoutACollectionRealityTitlesAreThoseOfTheRealityType()
        {
            var a = Title("a", "А", type: "reality");
            var b = Title("b", "Б");
            var hub = StagedHub(null, new List<LvnTitle> { b, a });

            var reality = hub.RealityTitles;

            Assert.AreEqual(1, reality.Count);
            Assert.AreEqual("a", reality[0].id);
        }

        // ── комната сообщений ────────────────────────────────────────────────

        [Test]
        public void RealityRowsTellReadNewAndLockedApartAndTheFilterHidesTheRest()
        {
            var read = Title("r", "Пролог"); read.date = "12.04.2116";
            var fresh = Title("n", "Первая смена");
            var shut = Title("l", "Кто он?");
            var marks = new Dictionary<string, LvnTitleMark>
            {
                ["r"] = LvnTitleMark.Done, ["n"] = LvnTitleMark.New, ["l"] = LvnTitleMark.Locked,
            };
            var screen = new RealityScreen(new NoAssets());
            screen.Titles = () => new List<LvnTitle> { read, fresh, shut };
            screen.MarkOf = t => marks[t.id];
            LvnTitle opened = null;
            screen.Open = t => opened = t;
            screen.SetContent(StagedManifest(",\"news_title\":\"Сюжет реальности\""));

            var rows = screen.Query(name: "reality-row").ToList();
            Assert.AreEqual(3, rows.Count, "все три сообщения");
            Assert.AreEqual("READ", rows[0].Q<Label>("reality-row-state").text);
            Assert.AreEqual("NEW MESSAGE!", rows[1].Q<Label>("reality-row-state").text);
            Assert.AreEqual("LOCKED", rows[2].Q<Label>("reality-row-state").text);
            Assert.NotNull(rows[2].Q(name: "reality-row-locked"), "закрытое — знак вопроса вместо постера");
            Assert.IsNull(rows[0].Q(name: "reality-row-locked"));
            Assert.AreEqual("СЮЖЕТ РЕАЛЬНОСТИ", screen.Q<Label>("stage-header-title").text, "шапка — слово панели главной");
            Assert.NotNull(screen.Q(name: "reality-filter-all"));
            Assert.NotNull(screen.Q(name: "reality-filter-new"));

            typeof(RealityScreen).GetMethod("Toggle", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(screen, new object[] { true });

            Assert.IsTrue(screen.OnlyNew);
            rows = screen.Query(name: "reality-row").ToList();
            Assert.AreEqual(1, rows.Count, "«только новые» прячет прочитанное и закрытое");
            Assert.AreEqual("NEW MESSAGE!", rows[0].Q<Label>("reality-row-state").text);
            Assert.IsNull(screen.Q(name: "reality-empty"));
        }

        [Test]
        public void RealityWithoutMessagesSaysSo()
        {
            var screen = new RealityScreen(new NoAssets());
            screen.Titles = () => new List<LvnTitle>();
            screen.SetContent(StagedManifest(",\"news_empty_text\":\"Нет новых сообщений\""));

            Assert.AreEqual(0, screen.Query(name: "reality-row").ToList().Count);
            Assert.AreEqual("НЕТ НОВЫХ СООБЩЕНИЙ", screen.Q<Label>("reality-empty").text);
        }

        // ── деталь ───────────────────────────────────────────────────────────

        [Test]
        public void DetailPopupDrawsWorldEraGenresStatusAndThreePlates()
        {
            var t = Title("a", "Тайна старинного ожерелья");
            t.subtitle = "Англия 1864 г.";
            t.genres = new List<string> { "Детектив", "Мистика" };
            t.status = "Новинка";
            t.card = new LvnCardArt { description = "В 1864 году…" };
            var screen = new TitleDetailScreen(new NoAssets()) { Title = t, WorldNumber = 5 };
            screen.SetContent(StagedManifest(",\"genre_colors\":{\"Детектив\":\"#656565\"}"));

            Assert.NotNull(screen.Q(name: "stage-detail-sheet"), "лист попапа");
            Assert.NotNull(screen.Q(name: "stage-detail-ribbon"), "лента-заголовок");
            Assert.NotNull(screen.Q(name: "stage-detail-close"), "крестик");
            Assert.NotNull(screen.Q(name: "stage-detail-window"), "окно постера");
            Assert.NotNull(screen.Q(name: "stage-course-counter"), "«Глава N/M» в окне");
            Assert.AreEqual("ТАЙНА СТАРИННОГО ОЖЕРЕЛЬЯ", screen.Q<Label>("stage-detail-name").text);
            Assert.AreEqual("5", screen.Q<Label>("stage-detail-world").text, "номер мира от хоста");
            Assert.AreEqual("Англия 1864 г.", screen.Q<Label>("stage-detail-era").text);
            Assert.AreEqual("В 1864 году…", screen.Q<Label>("stage-detail-desc").text);
            Assert.AreEqual(2, screen.Q(name: "stage-detail-genres").Query(name: "stage-chip").ToList().Count, "две плашки жанров");
            Assert.AreEqual(1, screen.Q(name: "stage-detail-status").Query(name: "stage-chip").ToList().Count, "плашка статуса");
            Assert.AreEqual(2, screen.Query(name: "stage-divider").ToList().Count, "два разделителя");
            Assert.NotNull(screen.Q(name: "stage-detail-more-btn"), "закладка");
            Assert.NotNull(screen.Q(name: "stage-detail-play"), "«Играть»");
            Assert.NotNull(screen.Q(name: "stage-detail-restart"), "«заново»");
            Assert.IsNull(screen.Q(name: "stage-detail-more"), "главы и сохранения спрятаны за закладкой");
        }

        [Test]
        public void DetailWithoutGenresStatusOrWorldSkipsThoseRows()
        {
            var t = Title("a", "Тайна");
            var screen = new TitleDetailScreen(new NoAssets()) { Title = t };
            screen.SetContent(StagedManifest());

            Assert.IsNull(screen.Q(name: "stage-detail-genres"));
            Assert.IsNull(screen.Q(name: "stage-detail-status"));
            Assert.IsNull(screen.Q(name: "stage-detail-world"));
        }

        [Test]
        public void BookmarkRevealsChaptersUnderTheSecondDivider()
        {
            var t = Title("a", "Тайна", chapters: 4);
            var screen = new TitleDetailScreen(new NoAssets()) { Title = t };
            screen.SetContent(StagedManifest());

            typeof(TitleDetailScreen).GetMethod("ToggleMore", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(screen, null);

            Assert.IsTrue(screen.StageMore);
            var more = screen.Q(name: "stage-detail-more");
            Assert.NotNull(more, "раздел раскрыт");
            Assert.GreaterOrEqual(more.Query<Label>().ToList().Count, 4, "главы перечислены");
        }

        // ── паспорт ──────────────────────────────────────────────────────────

        [Test]
        public void PassportKnowsTheNewFramesAndTakesOverrides()
        {
            LvnStageSkin.Apply(null);
            Assert.AreEqual(360f, LvnStageSkin.Glow.Width);
            Assert.AreEqual(102f, LvnStageSkin.Glow.CornerPx);
            Assert.AreEqual(375f, LvnStageSkin.Popup.Width);

            LvnStageSkin.Apply(new StageSkinMetrics
            {
                frames = new Dictionary<string, StageFrame> { ["glow"] = new StageFrame { corner_px = 90f } },
            });
            Assert.AreEqual(90f, LvnStageSkin.Glow.CornerPx, "названное поле доходит");
            Assert.AreEqual(1152f, LvnStageSkin.Glow.ImageW, "неназванное остаётся");
        }
    }
}
