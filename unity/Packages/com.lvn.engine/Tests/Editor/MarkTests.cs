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
        public void РазоваяМеткаКаждыйРазНовая()
        {
            Assert.AreNotEqual(LvnMark.Once(), LvnMark.Once(),
                "повтор с той же меткой сервер счёл бы тем же начислением");
        }
    }
}
