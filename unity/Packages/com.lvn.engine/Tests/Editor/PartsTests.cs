using System.Collections.Generic;
using System.Linq;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ОПИСЬ КОНТЕНТА — единственный ответ на вопрос «из чего это состоит».
    ///
    /// <para>Знание было записано шестью обходами с разными глаголами, и они
    /// уже разошлись: один брал арт карточки как <c>card.image ?? cover_url</c>,
    /// другой — только <c>card.image</c>. Тесты держат перечень целым: поле,
    /// добавленное главе и забытое здесь, красит сборку.</para>
    /// </summary>
    public class PartsTests
    {
        private static LvnChapter Глава() => new LvnChapter
        {
            id = "c1",
            number = 1,
            script_url = "ch1.lvn",
            bg_url = "bg/room.jpg",
            assets = new Dictionary<string, LvnAssetMeta>
            {
                ["voice/hi.ogg"] = new LvnAssetMeta { kind = "audio", size = 1024 },
                ["sprite/hill.png"] = null,   // автор объявил адрес без подробностей
            },
        };

        private static List<string> Адреса(IEnumerable<LvnPart> parts) => parts.Select(p => p.Url).ToList();

        [Test]
        public void ГлаваЭтоСкриптФонИОбъявленныеАссеты()
        {
            var parts = LvnParts.OfChapter(Глава()).ToList();
            CollectionAssert.AreEquivalent(
                new[] { "ch1.lvn", "bg/room.jpg", "voice/hi.ogg", "sprite/hill.png" }, Адреса(parts));

            var voice = parts.First(p => p.Url == "voice/hi.ogg");
            Assert.AreEqual("audio", voice.Kind, "вид ассета — авторский");
            Assert.AreEqual(1024, voice.Size, "и размер тоже: по нему считают «сколько качать»");

            var sprite = parts.First(p => p.Url == "sprite/hill.png");
            Assert.AreEqual("sprite", sprite.Kind, "без подробностей — картинка");
            Assert.AreEqual(0, sprite.Size, "размер неизвестен, и ноль здесь честнее выдумки");
        }

        [Test]
        public void ПустыеАдресаВОписьНеПопадают()
        {
            var ч = new LvnChapter { id = "c2", script_url = "", bg_url = null };
            CollectionAssert.IsEmpty(LvnParts.OfChapter(ч).ToList(), "нечего перечислять");
            CollectionAssert.IsEmpty(LvnParts.OfChapter(null).ToList(), "и главы нет — не падаем");
        }

        [Test]
        public void ОбложкаИАртКарточкиРазныеФайлы()
        {
            var t = new LvnTitle
            {
                id = "tr",
                cover_url = "cover.jpg",
                card = new LvnCardArt { image = "card.jpg" },
            };
            CollectionAssert.AreEquivalent(new[] { "cover.jpg", "card.jpg" }, Адреса(LvnParts.OfTitleArt(t)),
                "карусель рисует обложку, хаб — карточку; грели одно — второе ждало сеть");
        }

        [Test]
        public void БезСвоейКарточкиАртНеУдваивается()
        {
            var t = new LvnTitle { id = "tr", cover_url = "cover.jpg" };
            CollectionAssert.AreEqual(new[] { "cover.jpg" }, Адреса(LvnParts.OfTitleArt(t)),
                "карточка падает на обложку — это тот же файл, а не второй");
        }

        [Test]
        public void ВитринаРисуетИФоныГлавИАртКоллекций()
        {
            var m = new LvnManifest
            {
                titles = new List<LvnTitle>
                {
                    new LvnTitle
                    {
                        id = "tr", cover_url = "cover.jpg",
                        seasons = new List<LvnSeason>
                        {
                            new LvnSeason { chapters = new List<LvnChapter> { Глава() } },
                        },
                    },
                },
                collections = new List<LvnCollection>
                {
                    new LvnCollection { id = "hot", card = new LvnCardArt { image = "col.jpg" } },
                },
            };

            var art = Адреса(LvnParts.OfMenuArt(m));
            CollectionAssert.Contains(art, "cover.jpg");
            CollectionAssert.Contains(art, "bg/room.jpg", "фон главы показывает экран загрузки");
            CollectionAssert.Contains(art, "col.jpg", "арт коллекции рисует хаб");
            CollectionAssert.DoesNotContain(art, "ch1.lvn", "скрипт — не картинка витрины");
        }

        [Test]
        public void ВсёВместеНичегоНеТеряет()
        {
            var m = new LvnManifest
            {
                titles = new List<LvnTitle>
                {
                    new LvnTitle
                    {
                        id = "tr", cover_url = "cover.jpg",
                        card = new LvnCardArt { image = "card.jpg" },
                        seasons = new List<LvnSeason>
                        {
                            new LvnSeason { chapters = new List<LvnChapter> { Глава() } },
                        },
                    },
                },
                ui = new LvnUiConfig
                {
                    browse = new BrowseConfig { music = "menu.ogg" },
                    sounds = new SoundsConfig { click = "click.ogg" },
                },
            };

            var all = new HashSet<string>(Адреса(LvnParts.OfAll(m)));
            CollectionAssert.AreEquivalent(
                new[] { "cover.jpg", "card.jpg", "ch1.lvn", "bg/room.jpg",
                        "voice/hi.ogg", "sprite/hill.png", "menu.ogg", "click.ogg" },
                all,
                "полный перечень: арт новеллы, все файлы глав, звучание оболочки");
        }
    }
}
