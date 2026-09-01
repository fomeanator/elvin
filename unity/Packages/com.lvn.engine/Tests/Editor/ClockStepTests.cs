using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ШАГ МЕЖДУ КАДРАМИ НЕ БЫВАЕТ БОЛЬШЕ ПОТОЛКА.
    ///
    /// <para>Свернули приложение на минуту и вернули — кадровые часы отдадут эту
    /// минуту одним куском, и всё, что движется по шагу, ПРЫГНЕТ: частицы
    /// окажутся за краем экрана, вспышка от касания доживёт до конца в первом же
    /// кадре. Выглядит это как сбой отрисовки, а не как возвращение из фона.</para>
    /// </summary>
    public sealed class ClockStepTests
    {
        private System.Func<float> _было;

        [SetUp] public void Запомнить() => _было = LvnClock.Now;
        [TearDown] public void Вернуть() => LvnClock.Now = _было;

        [Test]
        public void ДолгаяПаузаНеДаётОгромногоШага()
        {
            LvnClock.Now = () => 100f;      // вернулись из фона: сто секунд разом
            float отметка = 0f;

            float шаг = LvnClock.Step(ref отметка);

            Assert.AreEqual(LvnClock.StepCap, шаг, 1e-5f, "шаг не ограничен — движение прыгнет");
            Assert.AreEqual(100f, отметка, 1e-5f, "отметка обязана догнать часы, иначе следующий шаг снова огромный");
        }

        [Test]
        public void ОбычныйКадрПроходитКакЕсть()
        {
            LvnClock.Now = () => 1.016f;
            float отметка = 1f;

            Assert.AreEqual(0.016f, LvnClock.Step(ref отметка), 1e-5f);
        }

        // Часы, шагнувшие назад (подмена в тестах, правка системного времени),
        // не должны давать отрицательное время: движение поехало бы вспять.
        [Test]
        public void ЧасыНазадДаютНоль()
        {
            LvnClock.Now = () => 5f;
            float отметка = 9f;

            Assert.AreEqual(0f, LvnClock.Step(ref отметка), 1e-5f);
        }

        // Потолок — про характер эффекта: вспышка живёт доли секунды, и даже
        // десятая доля съела бы её целиком одним шагом.
        [Test]
        public void ПотолокЗадаётЗовущий()
        {
            LvnClock.Now = () => 100f;
            float отметка = 0f;

            Assert.AreEqual(0.05f, LvnClock.Step(ref отметка, 0.05f), 1e-5f);
        }
    }
}
