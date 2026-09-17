using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Shell.Tests
{
    /// <summary>Карта комнат витрины — как нарисовал Илья 08.09.</summary>
    public class RoomMapTests
    {
        [Test]
        public void Rooms_StandWhereTheDrawingPutsThem()
        {
            // профиль----------------
            // --------Главная--------
            // Гардероб--------Магазин
            Assert.AreEqual(new Vector2(0f, 0f),     LvnTabs.Room(LvnTabs.Profile),  "профиль — слева сверху");
            Assert.AreEqual(new Vector2(0.5f, 0.5f), LvnTabs.Room(LvnTabs.Home),     "главная — центр");
            Assert.AreEqual(new Vector2(0f, 1f),     LvnTabs.Room(LvnTabs.Wardrobe), "гардероб — слева снизу");
            Assert.AreEqual(new Vector2(1f, 1f),     LvnTabs.Room(LvnTabs.Store),    "магазин — справа снизу");
            Assert.AreEqual(new Vector2(1f,   0f),   LvnTabs.Room(LvnTabs.Titles),   "список новелл — дальний угол, справа сверху");
            Assert.AreEqual(new Vector2(0.5f, 0.5f), LvnTabs.Room(99),               "незнакомая вкладка — центр, а не край");
        }

        [Test]
        public void Rooms_AreInScreenAxes_YGrowsDown()
        {
            // Гардероб НИЖЕ главной по карте: страница обязана приезжать снизу,
            // а полотно — показывать низ картины (это проверяет страж витрины).
            Assert.Greater(LvnTabs.Room(LvnTabs.Wardrobe).y, LvnTabs.Room(LvnTabs.Home).y);
            Assert.Less(LvnTabs.Room(LvnTabs.Profile).y, LvnTabs.Room(LvnTabs.Home).y);
            Assert.Less(LvnTabs.Room(LvnTabs.Titles).y, LvnTabs.Room(LvnTabs.Home).y,
                "список новелл — выше главной: туда едут вверх");
            Assert.Greater(LvnTabs.Room(LvnTabs.Titles).x, LvnTabs.Room(LvnTabs.Home).x,
                "и в дальний угол, а не по прямой — переезд должен читаться");
        }

        [Test]
        public void Rooms_HaveAKind_TheStoreIsItsOwn()
        {
            // Композиция героини — по РОДУ комнаты: магазин не «боковая как
            // все», у него своё место и план («в магазине героиня наоборот
            // слева и больше на 5 процентов» — Илья 08.09).
            Assert.AreEqual(LvnMenuStage.Room.Home,  LvnTabs.RoomOf(LvnTabs.Home));
            Assert.AreEqual(LvnMenuStage.Room.Store, LvnTabs.RoomOf(LvnTabs.Store));
            Assert.AreEqual(LvnMenuStage.Room.Side,  LvnTabs.RoomOf(LvnTabs.Wardrobe));
            Assert.AreEqual(LvnMenuStage.Room.Side,  LvnTabs.RoomOf(LvnTabs.Profile));
            Assert.AreEqual(LvnMenuStage.Room.Side,  LvnTabs.RoomOf(LvnTabs.Gallery));
            Assert.AreEqual(LvnMenuStage.Room.Side,  LvnTabs.RoomOf(99), "чужой номер — боковая, не главная");
        }

        [Test]
        public void MenuArt_IsWarmedFromTheManifest_OnceEach()
        {
            // Бут греет ровно то, что витрина потом покажет: рамки облика из
            // папки skin (тот же список, что кладёт главная), лого, аватар и
            // значки валют. Повтор адреса — один декод, а не два.
            var b = new BrowseConfig
            {
                skin = "/content/ui/stage",           // без косой черты на конце
                logo = "/content/ui/stage/logo.png",
                avatar = "/content/ui/stage/avatar.png",
                currency_icons = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["crystals"] = "/content/ui/stage/icon-crystal.png",
                    ["watch"] = "/content/ui/stage/icon-watch.png",
                    ["dup"] = "/content/ui/stage/logo.png",   // тот же файл, что лого
                },
            };
            var urls = NovelApp.MenuArtUrls(b);
            foreach (var file in LvnStageKit.SkinFiles)
                CollectionAssert.Contains(urls, "/content/ui/stage/" + file, $"рамка {file} греется");
            CollectionAssert.Contains(urls, b.logo);
            CollectionAssert.Contains(urls, b.avatar);
            CollectionAssert.Contains(urls, "/content/ui/stage/icon-crystal.png");
            CollectionAssert.AllItemsAreUnique(urls, "один адрес — один декод");
            Assert.AreEqual(LvnStageKit.SkinFiles.Length + 4, urls.Count);

            CollectionAssert.IsEmpty(NovelApp.MenuArtUrls(null), "без манифеста греть нечего");
            CollectionAssert.IsEmpty(NovelApp.MenuArtUrls(new BrowseConfig()), "без облика — тоже");
            Assert.AreEqual("/content/ui/stage/nav.png", LvnStageKit.SkinUrl("/content/ui/stage/", "nav.png"),
                "косая черта на конце папки не удваивается");
        }

        [Test]
        public void ОбликВитриныДляУборкиЦеликом()
        {
            // TR-101: уборка диска считала облик витрины мёртвым и стирала его
            // каждый запуск. Список для уборки — всё, что греет бут, плюс
            // полотно, его покупные варианты и портреты на выбор.
            var m = new LvnManifest
            {
                ui = new LvnUiConfig
                {
                    currency_look = new System.Collections.Generic.Dictionary<string, CurrencyLook>
                    {
                        ["crystals"] = new CurrencyLook { image = "/content/ui/stage/icon-crystal.png" },
                    },
                    browse = new BrowseConfig
                    {
                        skin = "/content/ui/stage/",
                        logo = "/content/ui/stage/logo.png",
                        canvas = "/content/ui/stage/canvas.jpg",
                        canvas_options = new System.Collections.Generic.List<CanvasOption>
                        {
                            new CanvasOption { id = "hall", url = "/content/bg/menu/hall.jpg", preview = "/content/bg/menu/hall@mini.jpg" },
                        },
                        avatars = new System.Collections.Generic.List<AvatarChoice>
                        {
                            new AvatarChoice { id = "free1", url = "/content/art/face.png" },
                        },
                    },
                },
            };
            var urls = NovelApp.ShellArtUrls(m);
            foreach (var file in LvnStageKit.SkinFiles)
                CollectionAssert.Contains(urls, "/content/ui/stage/" + file, $"рамка {file} живая");
            foreach (var file in LvnStageKit.ViewFiles)
                CollectionAssert.Contains(urls, "/content/ui/stage/" + file, $"арт экранов {file} живой");
            CollectionAssert.Contains(urls, "/content/ui/stage/logo.png");
            CollectionAssert.Contains(urls, "/content/ui/stage/canvas.jpg", "полотно меню живое");
            CollectionAssert.Contains(urls, "/content/bg/menu/hall.jpg", "покупной фон живой");
            CollectionAssert.Contains(urls, "/content/bg/menu/hall@mini.jpg", "мини покупного фона живая");
            CollectionAssert.Contains(urls, "/content/art/face.png", "портрет на выбор живой");
            CollectionAssert.Contains(urls, "/content/ui/stage/icon-crystal.png", "картинка валюты живая");
            CollectionAssert.AllItemsAreUnique(urls);
            CollectionAssert.IsEmpty(NovelApp.ShellArtUrls(null));
            CollectionAssert.IsEmpty(NovelApp.ShellArtUrls(new LvnManifest()));
        }
    }
}
