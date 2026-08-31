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

        // ── СЛОВАРЬ КОНТЕКСТНЫЙ: ФИГУРА ЦЕЛИКОМ ≠ ОДИН СЛОЙ ─────────────────
        //
        // Набор был ПЛОСКИМ, а исполнитель нет. `screen_x` со слоем проходил
        // проверку и молча отбрасывался; `frame` без слоя — наоборот. То есть
        // проверка говорила «всё в порядке» ровно там, где ничего не игралось:
        // автор видел зелёную сборку, чистый лог и неподвижную куклу.

        /// <summary>Поймать жалобу движка на одно действие. Нужна не «была ли
        /// жалоба», а ЕЁ ТЕКСТ: две разные беды, сказанные одними словами, —
        /// это одна беда, и вторую автор не найдёт.</summary>
        private static string Поймать(System.Action действие)
        {
            string текст = null;
            void OnLog(string cond, string stack, LogType type)
            {
                if (type == LogType.Warning && cond.Contains("[lvn-anim]")) текст = cond;
            }
            Application.logMessageReceived += OnLog;
            try { действие(); }
            finally { Application.logMessageReceived -= OnLog; }
            return текст;
        }

        /// <summary>Перечень «Здесь можно: …» — то, что жалоба предлагает
        /// вместо промаха. Именно он был общим и потому врал.</summary>
        private static string ЗдесьМожно(string жалоба)
        {
            const string маркер = "Здесь можно: ";
            var i = жалоба.IndexOf(маркер, System.StringComparison.Ordinal);
            Assert.Greater(i, -1, "жалоба обязана назвать, что можно ЗДЕСЬ: " + жалоба);
            return жалоба.Substring(i + маркер.Length);
        }

        /// <summary>Семь имён, которые есть у фигуры и у слоя одинаково.</summary>
        private static readonly string[] ОбщиеСемь =
            { "x", "y", "scale", "scalex", "scaley", "rotation", "alpha" };

        [Test]
        public void ЭкранноеМестоЕстьУФигурыИНетУСлоя()
        {
            // По экрану ходит фигура, а не её рукав: у слоя своего места нет.
            Assert.IsTrue(LvnAnimProp.Check("screen_x"), "трек без слоя двигает саму фигуру — это законно");
            Assert.IsTrue(LvnAnimProp.Check("screen_y"));

            var жалоба = Поймать(() => Assert.IsFalse(LvnAnimProp.Check("screen_x", "рукав"),
                "со слоем экранного места нет — трек молча отбрасывался, и проверка это одобряла"));
            Assert.IsNotNull(жалоба, "молчание здесь и было бедой: сборка зелёная, кукла неподвижна");
            StringAssert.Contains("screen_x", жалоба);
            StringAssert.Contains("рукав", жалоба, "без имени слоя искать промах негде — треков у куклы десятки");
            // Перечень обязан описывать ЭТО место. Общий перечень предлагал бы
            // автору ровно то слово, на которое сам же и пожаловался.
            StringAssert.DoesNotContain("screen_x", ЗдесьМожно(жалоба),
                "нельзя предлагать то, что здесь и не играет");
            StringAssert.Contains("frame", ЗдесьМожно(жалоба), "у слоя есть кадр — о нём и речь");
        }

        [Test]
        public void КадрЕстьУСлояИНетУФигурыЦеликом()
        {
            // Кадром подменяют картинку СЛОЯ (кукла, спрайтовый лист). У фигуры
            // целиком кадра нет — менять нечего.
            Assert.IsTrue(LvnAnimProp.Check("frame", "волосы"));

            var жалоба = Поймать(() => Assert.IsFalse(LvnAnimProp.Check("frame"),
                "без слоя кадр не играет — раньше проверка молчала и трек пропадал"));
            Assert.IsNotNull(жалоба);
            StringAssert.Contains("frame", жалоба);
            StringAssert.Contains("layer=", жалоба, "лечение — назвать слой; жалоба обязана это сказать");
            StringAssert.DoesNotContain("frame", ЗдесьМожно(жалоба));
            StringAssert.Contains("screen_x", ЗдесьМожно(жалоба), "фигура целиком ходит по экрану — вот перечень");
        }

        [Test]
        public void ОбщиеСемьСвойствГодятсяВОбоихМестах()
        {
            // Разделение наборов не должно было ничего отнять: смещение, размер,
            // поворот и прозрачность есть и у фигуры, и у каждого её слоя.
            foreach (var p in ОбщиеСемь)
            {
                Assert.IsTrue(LvnAnimProp.Check(p), p + " — трек без слоя");
                Assert.IsTrue(LvnAnimProp.Check(p, "слой"), p + " — трек со слоем");
            }
            LogAssert.NoUnexpectedReceived();

            // Всего имён десять: семь общих плюс два экранных и кадр. Число
            // зашито намеренно — новое имя обязано пройти через оба набора и
            // через валидатор, а не появиться в одном месте.
            Assert.AreEqual(ОбщиеСемь.Length + 3, LvnAnimProp.Known.Count,
                "словарь разошёлся с наборами — где-то имя есть, а где-то нет");
        }

        [Test]
        public void ЖалобаНаЗнакомоеИмяНеВСвоёмМестеОтличаетсяОтНезнакомого()
        {
            // ГЛАВНОЕ ЗДЕСЬ — не факт жалобы, а её слова. «Неизвестное
            // свойство» на имени, которое движок ЗНАЕТ, отправляет автора
            // искать опечатку там, где опечатки нет: он перечитывает `screen_y`
            // по буквам, а дело в лишнем `layer=`.
            var знакомое = Поймать(() => LvnAnimProp.Check("screen_y", "воротник"));
            var выдуманное = "выдуманное_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var чужое = Поймать(() => LvnAnimProp.Check(выдуманное, "воротник"));

            Assert.IsNotNull(знакомое);
            Assert.IsNotNull(чужое);
            StringAssert.DoesNotContain("неизвестно", знакомое,
                "screen_y движку известен — сказать обратное значит послать искать несуществующую описку");
            StringAssert.Contains("неизвестно", чужое, "а вот выдуманное имя движку и правда незнакомо");
            Assert.AreNotEqual(знакомое, чужое, "две разные беды не могут звучать одинаково");
        }

        [Test]
        public void МолчаниеЗапоминаетсяПоПареМестоИмя()
        {
            // Одно и то же слово промахивается по-разному: `frame` без слоя и
            // `frame` со слоем — две разные ошибки автора в двух разных
            // строках. Задвижка по одному имени спрятала бы вторую за первой, и
            // лог сказал бы, что вторая строка в порядке.
            var промах = "промах_мест_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            Assert.IsNotNull(Поймать(() => LvnAnimProp.Check(промах)),
                "первая жалоба — про трек без слоя");
            Assert.IsNotNull(Поймать(() => LvnAnimProp.Check(промах, "рукав")),
                "вторая жалоба — про трек со слоем: место другое, и сказать о нём надо отдельно");

            // А вот третий раз в ТОМ ЖЕ месте — молчание: трек сэмплируется
            // каждый кадр, и повтор превратил бы лог в шум. Имя слоя на это не
            // влияет — место одно и то же, «со слоем».
            Assert.IsNull(Поймать(() => LvnAnimProp.Check(промах, "воротник")),
                "жалоба помнит МЕСТО, а не имя слоя — иначе кукла из тридцати слоёв даст тридцать строк");
        }
    }
}
