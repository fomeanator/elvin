using NUnit.Framework;
using Lvn;

namespace Lvn.Tests
{
    /// <summary>
    /// ПАСПОРТИСТ: правила выдачи меток. Проверяем не «выдаётся ли строка» —
    /// проверяем ровно то, из-за чего роль и появилась: одна метка на запуск и
    /// два дома у постоянной.
    /// </summary>
    public class MarkTests
    {
        private const string Name = "test_mark_дом";

        [TearDown] public void Clean() => LvnMark.Forget(Name);

        [Test]
        public void ЗапускМетитсяОдинРаз()
        {
            // Ровно тот случай, что был сломан: аналитика и отправщик логов
            // спрашивают метку по отдельности и обязаны получить ОДНУ.
            Assert.AreEqual(LvnMark.Run, LvnMark.Run);
            Assert.AreEqual(16, LvnMark.Run.Length);
            Assert.AreEqual(LvnMark.Run, Lvn.Services.LvnAnalytics.SessionId,
                "аналитика метит запуск той же меткой, что и все");
        }

        [Test]
        public void ПостояннаяМеткаТаЖе()
        {
            var first = LvnMark.Steady(Name);
            Assert.AreEqual(64, first.Length);
            Assert.AreEqual(first, LvnMark.Steady(Name), "второй запрос — та же метка");
        }

        [Test]
        public void КнижкаЧинитПотерянныйФайл()
        {
            var mark = LvnMark.Steady(Name);
            var file = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, Name + ".id");
            System.IO.File.Delete(file);

            Assert.AreEqual(mark, LvnMark.Steady(Name));
            Assert.IsTrue(System.IO.File.Exists(file), "уцелевший дом чинит потерянный");
        }

        [Test]
        public void ФайлЧинитПотеряннуюКнижку()
        {
            // Это и есть цена вопроса: сброс prefs без второго дома отнимал бы
            // у игрока учётку вместе с кошельком.
            var mark = LvnMark.Steady(Name);
            LvnKeep.Drop(Name);

            Assert.AreEqual(mark, LvnMark.Steady(Name), "метка пережила потерю книжки");
            Assert.AreEqual(mark, LvnKeep.Get(Name, ""), "и вернулась в книжку");
        }

        [Test]
        public void ЗабытьЗначитОбаДома()
        {
            LvnMark.Steady(Name);
            LvnMark.Forget(Name);

            var file = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, Name + ".id");
            Assert.IsFalse(System.IO.File.Exists(file), "переживший файл вернул бы удалённую учётку");
            Assert.AreEqual("", LvnKeep.Get(Name, ""));
        }

        [Test]
        public void ЗабвениеЗнаетОВсехМетках()
        {
            // Забвение не держит список имён — его держит тот, кто их выдал.
            // Иначе следующая метка переживёт удаление аккаунта незаметно.
            var mark = LvnMark.Steady(Name);
            LvnMark.ForgetAll();

            Assert.AreEqual("", LvnKeep.Get(Name, ""), "метка не пережила удаление аккаунта");
            Assert.AreNotEqual(mark, LvnMark.Steady(Name), "после забвения выдана новая");
        }

        [Test]
        public void РазоваяМеткаКаждыйРазНовая()
        {
            Assert.AreNotEqual(LvnMark.Once(), LvnMark.Once(),
                "повтор с той же меткой сервер счёл бы тем же начислением");
        }

        [Test]
        public void РазоваяМеткаНеХранится()
        {
            // Она нужна ровно на одну операцию: сохранённая, она превратила бы
            // КАЖДЫЙ повтор в «то же начисление».
            var once = LvnMark.Once();
            Assert.AreEqual(32, once.Length);
            Assert.AreEqual("", LvnKeep.Get(once, ""), "разовая метка в книжку не попадает");
        }

        [Test]
        public void БезымяннуюМеткуВыдатьНельзя()
        {
            // Имя — это и есть дом метки: без него её негде искать при
            // следующем запуске, и «постоянная» окажется разовой.
            Assert.Throws<System.ArgumentException>(() => LvnMark.Steady(null));
            Assert.Throws<System.ArgumentException>(() => LvnMark.Steady(""));
        }

        [Test]
        public void ЗабвениеЗабираетВСЕВыданныеМетки()
        {
            // Перепись живёт в книжке, а не в памяти: половина меток на момент
            // удаления аккаунта может быть в этом запуске ещё не спрошена.
            const string second = "test_mark_второй";
            try
            {
                var a = LvnMark.Steady(Name);
                var b = LvnMark.Steady(second);
                Assert.AreNotEqual(a, b, "разным именам — разные метки");

                LvnMark.ForgetAll();

                Assert.AreEqual("", LvnKeep.Get(Name, ""));
                Assert.AreEqual("", LvnKeep.Get(second, ""), "вторая метка пережила удаление аккаунта");
            }
            finally { LvnMark.Forget(second); }
        }

        [Test]
        public void ЗабытьНесуществующуюМеткуБезвредно()
        {
            Assert.DoesNotThrow(() => LvnMark.Forget("никогда_не_выдавалась"));
            Assert.DoesNotThrow(() => LvnMark.Forget(null));
            Assert.DoesNotThrow(LvnMark.ForgetAll);
        }
    }
}
