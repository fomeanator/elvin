using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// НАБОР ВКЛАДОК НИЖНЕГО МЕНЮ — один перечень вместо пяти.
    ///
    /// <para>Вкладка была размазана: её место в ряду задавала сборка навбара,
    /// подпись — свой switch по номеру, страницу — второй switch в оболочке,
    /// цвет полотна — массив на четыре ячейки, а «перейти на магазин» писалось
    /// числом с пояснением в комментарии. Пять перечней одного набора, и ни
    /// один не знал про остальные: галерея есть в подписях и в ряду, но
    /// СТРАНИЦЫ у неё нет вовсе — она дверь в модаль.</para>
    ///
    /// <para>Здесь закреплено то, что расходится молча: порядок показа (он
    /// намеренно НЕ совпадает с номерами), у кого есть страница, сколько их
    /// всего и по какому правилу берётся подпись.</para>
    /// </summary>
    public sealed class TabSetTests
    {
        [SetUp]
        [TearDown]
        public void Clean() { LvnWords.Learn(null); LvnWords.Translate(null); }

        private static LvnTab Вкладка(int index) => LvnTabs.Of(index);

        // ── порядок ряда ────────────────────────────────────────────────────

        // Номера — это история появления, ряд — то, что видит игрок. Галерея
        // добавлена последней, а стоит ЧЕТВЁРТОЙ, между гардеробом и профилем.
        // Сортируй ряд по номерам — и кнопки под пальцем игрока переедут.
        [Test]
        public void РядИдётВПорядкеПоказаАНеВПорядкеНомеров()
        {
            var порядок = new List<int>();
            foreach (var t in LvnTabs.Shown) порядок.Add(t.Index);

            CollectionAssert.AreEqual(
                new[] { LvnTabs.Home, LvnTabs.Store, LvnTabs.Wardrobe, LvnTabs.Gallery, LvnTabs.Profile },
                порядок,
                "ряд нижнего меню переставили — кнопки уехали из-под пальца игрока");
            Assert.AreEqual(3, порядок.IndexOf(LvnTabs.Gallery),
                "галерея встала не на своё место: её номер последний, а стоит она ЧЕТВЁРТОЙ — " +
                "между гардеробом и профилем");
            Assert.Greater(LvnTabs.Gallery, LvnTabs.Profile,
                "порядок показа сравнялся с порядком номеров — правило перестало быть проверяемым");
        }

        [Test]
        public void КаждаяВкладкаВРядуРовноОдна()
        {
            var номера = new HashSet<int>();
            foreach (var t in LvnTabs.Shown)
                Assert.IsTrue(номера.Add(t.Index), "вкладка " + t.Index + " стоит в ряду дважды");
            Assert.AreEqual(LvnTabs.Shown.Count, номера.Count);
        }

        [Test]
        public void УКаждойВкладкиЕстьСвойЗначокИСвоёСлово()
        {
            var значки = new HashSet<LvnIcon>();
            var слова = new HashSet<string>();
            foreach (var t in LvnTabs.Shown)
            {
                Assert.AreNotEqual(LvnIcon.None, t.Icon, "вкладка без значка — пустое место в ряду");
                Assert.IsTrue(значки.Add(t.Icon), "две вкладки нарисованы одним значком: " + t.Icon);
                Assert.IsFalse(string.IsNullOrEmpty(t.Word), "вкладке нечем подписаться");
                Assert.IsTrue(слова.Add(t.Word), "две вкладки переводятся одним ключом: " + t.Word);
                Assert.IsFalse(string.IsNullOrEmpty(t.Fallback), "нет умолчания движка — подпись пропадёт");
            }
        }

        // ── страница ленты ──────────────────────────────────────────────────

        // ГЛАВНОЕ РАЗЛИЧИЕ НАБОРА. У галереи страницы нет: она открывает
        // модаль, и лента с места не двигается. Пока это знал только switch
        // оболочки, тап по галерее уводил ленту на несуществующую страницу —
        // экран уезжал в пустоту, а вернуть его было нечем.
        [Test]
        public void ГалереяЭтоДверьВМодальАНеСтраницаЛенты()
        {
            Assert.IsFalse(Вкладка(LvnTabs.Gallery).HasPage,
                "у галереи завелась страница ленты — лента уедет туда, где ничего нет");

            foreach (var t in LvnTabs.Shown)
                if (t.Index != LvnTabs.Gallery)
                    Assert.IsTrue(t.HasPage, "вкладка " + t.Index + " осталась без страницы — тап по ней ведёт в никуда");
        }

        // Число страниц считается ПО НАБОРУ. Раньше оно стояло зашитым в
        // ограничитель цвета полотна, и добавленная вкладка разошлась бы с ним
        // молча: новая страница получила бы чужой цвет.
        [Test]
        public void ЧислоСтраницСчитаетсяПоНаборуАНеЗашито()
        {
            int сСтраницей = 0;
            foreach (var t in LvnTabs.Shown) if (t.HasPage) сСтраницей++;

            Assert.AreEqual(сСтраницей, LvnTabs.PageCount, "число страниц разошлось с набором вкладок");
            Assert.AreEqual(4, LvnTabs.PageCount,
                "страниц ленты стало другое число — сверь с ветками NovelShell.TabPage: " +
                "их там ровно столько же, и лишняя вкладка без ветки уводит ленту в пустоту");
        }

        // Номер вкладки СО СТРАНИЦЕЙ — это ещё и индекс в массиве цветов
        // полотна (NovelShell: TabTints[Clamp(tab, 0, PageCount-1)]). Дай
        // странице номер за пределами набора — и она возьмёт чужой цвет, молча
        // и навсегда.
        [Test]
        public void НомераСтраничныхВкладокИдутПодрядОтНуля()
        {
            var номера = new List<int>();
            foreach (var t in LvnTabs.Shown) if (t.HasPage) номера.Add(t.Index);
            номера.Sort();

            for (int i = 0; i < номера.Count; i++)
                Assert.AreEqual(i, номера[i],
                    "номера страничных вкладок перестали идти подряд — страница возьмёт цвет полотна соседа");
            Assert.AreEqual(LvnTabs.PageCount, номера.Count);
        }

        // Номер приходит и снаружи — из сохранённого состояния, из авторского
        // манифеста, из чужой вкладки после обновления. Упасть на нём нижнее
        // меню не имеет права.
        [Test]
        public void НеизвестныйНомерНеРоняетИНеПритворяетсяВкладкой()
        {
            Assert.DoesNotThrow(() => LvnTabs.Of(77));
            Assert.IsFalse(Вкладка(77).HasPage, "неизвестный номер получил страницу ленты");
            Assert.IsTrue(string.IsNullOrEmpty(Вкладка(77).Word), "движок придумал перевод несуществующей вкладке");
            Assert.AreEqual("", LvnTabs.Label(77, new BrowseConfig()),
                "у неизвестной вкладки появилась подпись — в ряду встанет кнопка без назначения");
            Assert.AreEqual("", LvnTabs.Label(-1, null));
            Assert.IsNull(LvnTabs.Authored(77, new BrowseConfig()));
        }

        // ── подпись ─────────────────────────────────────────────────────────

        // ПРАВИЛО СТАРШИНСТВА, и оно одно на все пять вкладок: перевод сильнее
        // авторского поля, авторское сильнее умолчания движка. Пока правило
        // стояло пятью строками одного switch, обновить подписи было негде — и
        // игрок, сменивший язык, получал русский ряд под английскими репликами.
        [Test]
        public void ПереводСильнееАвторскогоПоляААвторскоеСильнееУмолчания()
        {
            var cfg = new BrowseConfig { nav_home = "Главная", nav_store = "Лавка" };

            Assert.AreEqual("Главная", LvnTabs.Label(LvnTabs.Home, cfg),
                "авторское слово проиграло английскому умолчанию движка");

            LvnWords.Translate(new Dictionary<string, string> { ["nav.home"] = "Home" });

            Assert.AreEqual("Home", LvnTabs.Label(LvnTabs.Home, cfg),
                "перевод не перебил авторское поле — игрок сменил язык, а ряд остался прежним");
            Assert.AreEqual("Лавка", LvnTabs.Label(LvnTabs.Store, cfg),
                "непереведённая вкладка обязана остаться АВТОРСКОЙ, а не съехать в английское умолчание");
        }

        [Test]
        public void БезАвтораИБезПереводаОстаётсяУмолчаниеДвижка()
        {
            Assert.AreEqual("Wardrobe", LvnTabs.Label(LvnTabs.Wardrobe, new BrowseConfig()));
            Assert.AreEqual("Gallery", LvnTabs.Label(LvnTabs.Gallery, null),
                "манифеста нет — ряд обязан подписаться сам, а не встать пустым");
        }

        // Словарь новеллы (ui.words) — тоже слово автора, только общее: он
        // сильнее английского умолчания и слабее перевода.
        [Test]
        public void СловарьНовеллыПодписываетВкладкуКогдаПоляНет()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["nav.profile"] = "Досье" });

            Assert.AreEqual("Досье", LvnTabs.Label(LvnTabs.Profile, new BrowseConfig()),
                "словарь новеллы до нижнего меню не дошёл");
        }

        [Test]
        public void ОтсутствиеМанифестаНеРоняетПодпись()
        {
            foreach (var t in LvnTabs.Shown)
                Assert.IsFalse(string.IsNullOrEmpty(LvnTabs.Label(t.Index, null)),
                    "вкладка " + t.Index + " осталась без подписи, когда манифеста нет");
        }

        // ── авторское поле ──────────────────────────────────────────────────

        // Правило старшинства одно на всех, а ПОЛЯ у каждой вкладки свои.
        // Перепутай их — и игрок увидит «Гардероб» на кнопке профиля: ошибка,
        // которую в switch по номеру не видно вовсе.
        [Test]
        public void АвторскоеПолеБерётсяИменноУСвоейВкладки()
        {
            var cfg = new BrowseConfig
            {
                nav_home = "дом",
                nav_store = "лавка",
                nav_wardrobe = "шкаф",
                nav_gallery = "альбом",
                nav_profile = "досье",
            };

            Assert.AreEqual("дом", LvnTabs.Authored(LvnTabs.Home, cfg));
            Assert.AreEqual("лавка", LvnTabs.Authored(LvnTabs.Store, cfg));
            Assert.AreEqual("шкаф", LvnTabs.Authored(LvnTabs.Wardrobe, cfg));
            Assert.AreEqual("альбом", LvnTabs.Authored(LvnTabs.Gallery, cfg));
            Assert.AreEqual("досье", LvnTabs.Authored(LvnTabs.Profile, cfg));

            Assert.IsNull(LvnTabs.Authored(LvnTabs.Home, null), "манифеста нет — и авторского слова нет");
        }
    }
}
