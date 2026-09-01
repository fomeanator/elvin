using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Lvn;
using Lvn.UI;

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
    }
}
