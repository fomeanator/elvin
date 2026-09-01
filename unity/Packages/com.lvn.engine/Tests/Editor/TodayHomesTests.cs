using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Lvn;
using Lvn.UI;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// ДОМА, ВЫДЕЛЕННЫЕ 01.09 — с проверкой правил, а не только сборки.
    ///
    /// <para>Внешнее ревью нашло в этот день две критические поломки, и обе
    /// оказались в домах, сведённых БЕЗ ТЕСТОВ. Там, где тесты писались —
    /// якорь, ближайшее по написанию, обрезка по рунам, — ревью не нашло
    /// ничего. Совпадение слишком ровное, чтобы быть совпадением.</para>
    /// </summary>
    public sealed class TodayHomesTests
    {
        // ── Поле-признак: присутствует и не отменено словом ──────────────────

        [Test]
        public void ПризнакаНетКогдаПоляНет()
        {
            Assert.IsFalse(LvnBool.Flag(null), "нет поля — нет признака");
        }

        [Test]
        public void ПризнакЕстьКогдаПолеПустоеИлиИстинное()
        {
            // Компилятор кладёт true; голое `fx off` в сыром .lvn может
            // приехать чем угодно непустым — и это тоже «да».
            Assert.IsTrue(LvnBool.Flag(new JValue(true)));
            Assert.IsTrue(LvnBool.Flag(new JValue(1)));
            Assert.IsTrue(LvnBool.Flag(new JValue("")), "поле есть — значит признак поднят");
        }

        [Test]
        public void СловоОтмениловшееПризнакЕгоОтменяет()
        {
            // Ради этого правило и заводили: рукописный `"off": false`
            // означает «не выключать», а пять мест рантайма выключали.
            Assert.IsFalse(LvnBool.Flag(new JValue(false)), "false отменяет");
            Assert.IsFalse(LvnBool.Flag(new JValue(0)), "ноль отменяет");
            Assert.IsFalse(LvnBool.Flag(new JValue("no")), "«no» отменяет");
            Assert.IsFalse(LvnBool.Flag(new JValue("нет")), "«нет» отменяет");
        }

        // ── Есть ли у команды зримый ассет ──────────────────────────────────

        [Test]
        public void ЗримыйАссетЕстьУАктёраПредметаИФона()
        {
            foreach (var op in new[] { "actor", "obj", "bg", "bg3d" })
                Assert.IsTrue(LvnOpKind.CarriesArt(op), $"«{op}» тянет картинку");
        }

        [Test]
        public void УЗвуковогоЭффектаКартинкиНет()
        {
            // Тонкое место: sfx относится к АКТЁРУ, но файла за собой не
            // тянет — потому вопрос и не свёлся к «про кого команда».
            Assert.AreEqual(LvnOpSubject.Actor, LvnOpKind.Of("sfx"));
            Assert.IsFalse(LvnOpKind.CarriesArt("sfx"),
                "предзагрузка выкачает пустоту, если считать sfx картинкой");
            Assert.IsFalse(LvnOpKind.CarriesArt("fade"));
        }

        // ── Цвета до темы ───────────────────────────────────────────────────

        [Test]
        public void ДоТемыЦветаСвоиИОдинаковыеУОбоихЭкранов()
        {
            var was = LvnDawn.ThemeArrived;
            LvnDawn.ThemeArrived = false;
            try
            {
                // Земля одна на вуаль и на выбор сервера — ради этого дом и
                // заводили: раньше они расходились на два почти-чёрных.
                Assert.AreNotEqual(default(Color), LvnDawn.Ground);
                Assert.AreEqual(LvnDawn.Ground, LvnDawn.Ground, "земля постоянна");
                Assert.AreNotEqual(LvnDawn.Ink, LvnDawn.InkDim, "тихое отличается от основного");
            }
            finally { LvnDawn.ThemeArrived = was; }
        }

        [Test]
        public void КогдаТемаПриехалаРолиБерутЕё()
        {
            var was = LvnDawn.ThemeArrived;
            LvnDawn.ThemeArrived = true;
            try
            {
                Assert.AreEqual(LvnTheme.Current.Bg, LvnDawn.Ground,
                    "экран настроек открывают и после загрузки — там он обязан "
                    + "выглядеть как остальная игра");
                Assert.AreEqual(LvnTheme.Current.Accent, LvnDawn.Accent);
            }
            finally { LvnDawn.ThemeArrived = was; }
        }

        [Test]
        public void МаркаОстаётсяСвоейИПослеТемы()
        {
            var was = LvnDawn.ThemeArrived;
            try
            {
                LvnDawn.ThemeArrived = false;
                var before = LvnDawn.Brand;
                LvnDawn.ThemeArrived = true;
                Assert.AreEqual(before, LvnDawn.Brand,
                    "до манифеста мы не знаем, чья игра; подставлять марке "
                    + "движка чужой акцент неправильно");
            }
            finally { LvnDawn.ThemeArrived = was; }
        }

        // ── Отступ и огранка одним решением ─────────────────────────────────

        [Test]
        public void ОтступДвумяЧисламиСтавитВсеЧетыреСтороны()
        {
            var el = new VisualElement();
            LvnAir.Pad(el, 7f, 3f);
            Assert.AreEqual(7f, el.style.paddingLeft.value.value, "слева");
            Assert.AreEqual(7f, el.style.paddingRight.value.value, "справа");
            Assert.AreEqual(3f, el.style.paddingTop.value.value, "сверху");
            Assert.AreEqual(3f, el.style.paddingBottom.value.value, "снизу");
        }

        [Test]
        public void ОгранкаБезРамкиСнимаетПрежнююОбводку()
        {
            // Ради этого у роли есть вид без цвета: элемент переодевают, и
            // прежняя рамка иначе остаётся поверх нового вида.
            var el = new VisualElement();
            LvnChrome.Frame(el, 8f, Color.red, 2f);
            Assert.AreEqual(2f, el.style.borderTopWidth.value, "рамка встала");
            LvnChrome.Frame(el, 8f);
            Assert.AreEqual(0f, el.style.borderTopWidth.value, "рамка снята");
            Assert.AreEqual(8f, el.style.borderTopLeftRadius.value.value, "скругление осталось");
        }

        // ── Шкала: правила, которые вызывающие держали в уме ────────────────

        [Test]
        public void РадиусЗаливкиПоловинаВысотыДорожки()
        {
            var track = LvnStyler.Bar(16f, 0.5f);
            Assert.AreEqual(1, track.childCount, "заливка — первый ребёнок дорожки");
            Assert.AreEqual(8f, track[0].style.borderTopLeftRadius.value.value,
                "углы заливки обязаны совпадать с дорожкой, иначе она торчит");
        }

        [Test]
        public void ЗаливкаЗанимаетДорожкуПоВысоте()
        {
            var track = LvnStyler.Bar(12f, 0.25f);
            Assert.AreEqual(LengthUnit.Percent, track[0].style.height.value.unit,
                "высота процентом, а не числом: иначе смена высоты дорожки "
                + "молча оставит заливку прежней");
        }

        [Test]
        public void НоваяШкалаБезПереходаЧтобыОткатБылМгновенным()
        {
            // Постоянный переход на заливке отменял бы обещание «назад —
            // сразу» у продвижения прогресса.
            var track = LvnStyler.Bar(10f, 0.4f);
            var d = track[0].style.transitionDuration;
            Assert.IsTrue(d.keyword == StyleKeyword.Null || d.value == null || d.value.Count == 0,
                "шкала рождается без перехода — как ходить, решает вызывающий");
        }

        // ── Совет устройства ────────────────────────────────────────────────

        [Test]
        public void СоветУстройстваОдинИзТрёхСтупенейИНеМеняется()
        {
            var a = LvnDeviceProfile.RecommendedArtQuality();
            CollectionAssert.Contains(new[] { "2k", "1440", "1k" }, a,
                "ступень не из словаря даст адрес, которого сервер не собирал");
            Assert.AreEqual(a, LvnDeviceProfile.RecommendedArtQuality(),
                "совет обязан быть запомненным: Screen и SystemInfo отдаёт "
                + "только главный поток, а адреса строит и фон");
        }

        [Test]
        public void БоксКачестваБезПрисваиванияРавенСовету()
        {
            var was = DownloadPolicy.PreferredSuffix;
            DownloadPolicy.PreferredSuffix = null;
            try
            {
                Assert.AreEqual(DownloadPolicy.SuffixFor(LvnDeviceProfile.RecommendedArtQuality()),
                                DownloadPolicy.PreferredSuffix,
                    "иначе прогрев и показ берут разные файлы — так 01.09 "
                    + "картинка ехала дважды, второй раз растром");
            }
            finally { DownloadPolicy.PreferredSuffix = was; }
        }
        // ── Избранная героиня: имя без облика — не выбор ─────────────────────

        private static LvnManifest СМастями(params string[] сОбликом)
        {
            var m = new LvnManifest { sprites = new System.Collections.Generic.Dictionary<string, LvnSpriteEntity>() };
            foreach (var id in сОбликом) m.sprites[id] = new LvnSpriteEntity();
            return m;
        }

        [Test]
        public void ИзбраннаяБерётсяКогдаУНеёЕстьОблик()
        {
            var было = LvnPrefs.MenuFavorite;
            try
            {
                var m = СМастями("hill", "anna");
                LvnPrefs.MenuFavorite = "anna";
                Assert.AreEqual("anna", Lvn.UI.Screens.LvnFavorite.Entity(m));
            }
            finally { LvnPrefs.MenuFavorite = было; }
        }

        [Test]
        public void ИзбраннаяБезОбликаУступаетЗапаснойИзМанифеста()
        {
            var было = LvnPrefs.MenuFavorite;
            try
            {
                // Выбранная ИСЧЕЗЛА из новеллы — обновился контент, сменился
                // титул. Имя в настройках осталось, рисовать нечем.
                var m = СМастями("hill");
                m.ui = new LvnUiConfig { wardrobe = new WardrobeConfig { entity = "hill" } };
                LvnPrefs.MenuFavorite = "призрак";
                Assert.AreEqual("hill", Lvn.UI.Screens.LvnFavorite.Entity(m));
            }
            finally { LvnPrefs.MenuFavorite = было; }
        }

        [Test]
        public void ЗапаснаяБезОбликаТожеНеВыбор()
        {
            // РАСХОЖДЕНИЕ, РАДИ КОТОРОГО ДОМ И ЗАВЕДЁН. Вкладка гардероба
            // брала запасную из манифеста КАК ЕСТЬ и показывала пустоту, а
            // быстрое меню в том же приложении честно отвечало «никого».
            var было = LvnPrefs.MenuFavorite;
            try
            {
                var m = СМастями("hill");
                m.ui = new LvnUiConfig { wardrobe = new WardrobeConfig { entity = "её-тут-нет" } };
                LvnPrefs.MenuFavorite = "";
                Assert.IsNull(Lvn.UI.Screens.LvnFavorite.Entity(m),
                    "имя без облика — не выбор: экран получил бы имя и нарисовал пустоту");
            }
            finally { LvnPrefs.MenuFavorite = было; }
        }

        // ── Медаль: место на подиуме — вопрос к теме ─────────────────────────

        [Test]
        public void МедальРазнаяУТрёхМестИНичьяЧетвёртому()
        {
            Assert.AreEqual(LvnTokens.Gold, LvnTokens.Medal(1));
            Assert.AreEqual(LvnTokens.Silver, LvnTokens.Medal(2));
            Assert.AreEqual(LvnTokens.Bronze, LvnTokens.Medal(3));
            // Подиума дальше третьего нет: место без медали берёт тихую грань,
            // а не золото по умолчанию.
            Assert.AreEqual(LvnTokens.Border, LvnTokens.Medal(4));
            Assert.AreEqual(LvnTokens.Border, LvnTokens.Medal(0));
        }

        // ── Обводка выбора: акцент выбранному, грань темы остальным ──────────

        [Test]
        public void ВыбранноеНоситАкцентИГраньПотолще()
        {
            var акцент = new Color(1f, 0.2f, 0.5f);
            var el = LvnStyler.Chosen(new VisualElement(), true, акцент);
            Assert.AreEqual(акцент, el.style.borderTopColor.value);
            Assert.AreEqual(LvnStyler.ChosenEdge, el.style.borderTopWidth.value, 0.001f);
        }

        [Test]
        public void НевыбранноеБерётГраньТемыАНеБелыйНавсегда()
        {
            var el = LvnStyler.Chosen(new VisualElement(), false, new Color(1f, 0.2f, 0.5f));
            Assert.AreEqual(LvnTokens.Border, el.style.borderTopColor.value,
                "невыбранное красили белым числом — в кибер-теме он чужой, в светлой невидим");
            Assert.AreEqual(LvnStyler.QuietEdge, el.style.borderTopWidth.value, 0.001f);
        }

        [Test]
        public void ОбводкаСтавитВсеЧетыреСтороны()
        {
            // Четыре стороны — обязательны все: поставь три, и на карточке
            // останется незакрытый край, который видно только на устройстве.
            var el = LvnStyler.Chosen(new VisualElement(), true, Color.red);
            Assert.AreEqual(Color.red, el.style.borderRightColor.value);
            Assert.AreEqual(Color.red, el.style.borderBottomColor.value);
            Assert.AreEqual(Color.red, el.style.borderLeftColor.value);
        }
        // ── Картинка из байтов: или текстура, или ничего, но без следа ───────

        [Test]
        public void ПустыеБайтыДаютНичего()
        {
            Assert.IsNull(AssetMemory.Decode(null));
            Assert.IsNull(AssetMemory.Decode(new byte[0]));
        }

        [Test]
        public void БитыеБайтыДаютНичегоАНеПустуюТекстуру()
        {
            // Здесь и была утечка: одна из четырёх копий обряда возвращала
            // null, НЕ уничтожив заведённую текстуру. Битый файл оставлял
            // пустую текстуру в памяти при каждой попытке — без ошибки и без
            // строки в логе.
            var мусор = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            Assert.IsNull(AssetMemory.Decode(мусор),
                "неразобранные байты обязаны дать ничего, а не пустую текстуру");
        }

        [Test]
        public void НастоящаяКартинкаСтановитсяТекстуройСвоегоРазмера()
        {
            var исходник = new Texture2D(4, 3, TextureFormat.RGBA32, false);
            var пиксели = new Color32[12];
            for (int i = 0; i < пиксели.Length; i++) пиксели[i] = new Color32(200, 30, 90, 255);
            исходник.SetPixels32(пиксели); исходник.Apply();
            var png = исходник.EncodeToPNG();
            Object.DestroyImmediate(исходник);

            var tex = AssetMemory.Decode(png);
            try
            {
                Assert.NotNull(tex, "настоящий PNG обязан разобраться");
                Assert.AreEqual(4, tex.width);
                Assert.AreEqual(3, tex.height);
            }
            finally { if (tex != null) Object.DestroyImmediate(tex); }
        }
    }
}
