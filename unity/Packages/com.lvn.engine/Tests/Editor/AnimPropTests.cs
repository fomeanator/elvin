using System.Text.RegularExpressions;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>Что можно анимировать: незнакомое имя жалуется ОДИН раз, а не молчит.
    /// Молчание здесь — «анимация просто не играет», и сказать об этом больше некому.</summary>
    public class AnimPropTests
    {
        [Test]
        public void ОбаИсполнителяЗнаютОдинНабор()
        {
            foreach (var p in new[] { "x", "y", "screen_x", "screen_y",
                                      "scale", "scalex", "scaley", "rotation", "alpha", "frame" })
                Assert.IsTrue(LvnAnimProp.IsKnown(p), p);
        }

        [Test]
        public void ЧастыеОпечаткиАвтораСчитаютсяНезнакомыми()
        {
            // Ровно те промахи, ради которых дом и заведён.
            Assert.IsFalse(LvnAnimProp.IsKnown("opacity"), "правильное имя — alpha");
            Assert.IsFalse(LvnAnimProp.IsKnown("rot"), "правильное имя — rotation");
            Assert.IsFalse(LvnAnimProp.IsKnown("Alpha"), "имена свойств разбираются точно, регистр значим");
            // Не выдумка: `scale_x` лежал в фикстуре компилятора этого же
            // репозитория — описку никто не замечал, потому что замечать было
            // нечем.
            Assert.IsFalse(LvnAnimProp.IsKnown("scale_x"), "правильное имя — scalex, без подчёркивания");
        }

        [Test]
        public void ПустоеИмяНеЖалоба()
        {
            Assert.IsFalse(LvnAnimProp.IsKnown(null));
            Assert.IsFalse(LvnAnimProp.IsKnown(""));
            Assert.IsTrue(LvnAnimProp.Check(null), "трек без свойства отбрасывают раньше — жаловаться не на что");
            Assert.IsTrue(LvnAnimProp.Check(""));
        }

        [Test]
        public void ЗнакомоеИмяПроходитМолча()
        {
            Assert.IsTrue(LvnAnimProp.Check("alpha"));
            Assert.IsTrue(LvnAnimProp.Check("frame", "hair"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void НезнакомоеИмяЖалуетсяРовноОдинРаз()
        {
            // Имя уникально на прогон: список пожаловавшихся статический и
            // переживает отдельный тест.
            var prop = "выдуманное_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(prop)));
            Assert.IsFalse(LvnAnimProp.Check(prop, "слой"),
                "false — чтобы вызывающий одним условием и пожаловался, и пропустил трек");

            // Трек сэмплируется каждый кадр: повтор превратил бы лог в шум ровно
            // там, где его читают. Второй вызов обязан молчать.
            Assert.IsFalse(LvnAnimProp.Check(prop, "слой"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void МолчаниеЗапоминаетсяПоИмени_АНеНаВесьЗапуск()
        {
            // Замолкать нужно про ОДНО имя, а не про свойства вообще. Общая
            // задвижка спрятала бы вторую описку автора за первой: он починил
            // бы одну строку, а вторая осталась бы такой же немой — и лог,
            // который он перечитывает, сказал бы, что всё в порядке.
            var первое = "промах_а_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var второе = "промах_б_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(первое)));
            LvnAnimProp.Check(первое);

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(второе)));
            LvnAnimProp.Check(второе);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ЖалобаНазываетСлойЧтобыБылоГдеИскать()
        {
            // «Свойство неизвестно» без адреса — это «ищи по всей главе».
            // Слой называют, потому что у куклы треков десятки и промах в
            // одном из них иначе не найти.
            var prop = "промах_слоя_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            LogAssert.Expect(LogType.Warning, new Regex("волосы"));
            LvnAnimProp.Check(prop, "волосы");
        }

        [Test]
        public void ЖалобаНазываетИзвестныеИмена()
        {
            var prop = "тоже_выдуманное_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            LogAssert.Expect(LogType.Warning, new Regex("alpha.*rotation|rotation.*alpha"));
            LvnAnimProp.Check(prop);
        }
    }
}
