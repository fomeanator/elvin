using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// «МЕНЬШЕ ДВИЖЕНИЯ» ДОХОДИТ ДО ВСЕГО ДВИЖЕНИЯ.
    ///
    /// <para>Настройка существовала и уважалась ДВУМЯ местами — тряской экрана
    /// и полноэкранными эффектами. Выезд навбара на 1,2 секунды, подъезд
    /// контента, трёхсекундная катсцена ухода в главу шли полным ходом. Для
    /// человека с вестибулярной чувствительностью это значит, что он включил
    /// настройку и не получил ничего.</para>
    ///
    /// <para>Ручка темпа была в движке с самого начала и описывала себя как
    /// «единственная ручка быстрее/медленнее для всего сразу» — её просто никто
    /// не крутил. Тест закрепляет, что теперь крутит настройка.</para>
    /// </summary>
    public class MotionComfortTests
    {
        private bool _prev;

        [SetUp]
        public void Setup() => _prev = LvnPrefs.ReduceMotion;

        [TearDown]
        public void Teardown() => LvnPrefs.ReduceMotion = _prev;

        [Test]
        public void ОбычныйРежим_ДлительностиКакЗадуманы()
        {
            LvnPrefs.ReduceMotion = false;

            Assert.AreEqual(1f, LvnMotion.Tempo, 1e-4f);
            Assert.AreEqual(LvnMotion.Curtain, LvnMotion.Ms(LvnMotion.Curtain));
            Assert.AreEqual(300, LvnMotion.Ms(300));
        }

        [Test]
        public void ПросилиМеньшеДвижения_ВсёСтановитсяКороче()
        {
            LvnPrefs.ReduceMotion = true;

            Assert.Less(LvnMotion.Tempo, 1f, "темп обязан упасть");
            Assert.Less(LvnMotion.Ms(LvnMotion.Curtain), LvnMotion.Curtain,
                "выезд навбара — самое длинное движение оболочки, он и должен сократиться первым");
            Assert.Less(LvnMotion.Ms(900), 900, "ритм катсцены считается тем же способом");
        }

        [Test]
        public void ДвижениеНеИсчезаетСовсем()
        {
            LvnPrefs.ReduceMotion = true;

            // Мгновенная подмена читается как сбой отрисовки, а не как
            // спокойствие: настройка убирает РАЗМАХ, а не связность.
            Assert.Greater(LvnMotion.Ms(LvnMotion.Curtain), 0);
            Assert.Greater(LvnMotion.Ms(1), 0, "даже единица не схлопывается в ноль");
            Assert.Greater(LvnMotion.Sec(1f), 0f);
        }

        [Test]
        public void ПереключениеРаботаетВОбеСтороны()
        {
            LvnPrefs.ReduceMotion = true;
            int calm = LvnMotion.Ms(LvnMotion.Normal);

            LvnPrefs.ReduceMotion = false;
            int full = LvnMotion.Ms(LvnMotion.Normal);

            Assert.Less(calm, full, "выключил настройку — движение вернулось");
        }

        [Test]
        public void СекундыИМиллисекунды_СчитаютсяОдинаково()
        {
            LvnPrefs.ReduceMotion = true;
            float sec = LvnMotion.Sec(1f);
            int ms = LvnMotion.Ms(1000);

            // Одна и та же длительность, записанная двумя способами, обязана
            // сократиться одинаково — иначе части одной сцены разъедутся.
            Assert.AreEqual(sec * 1000f, ms, 5f);
        }
    }
}
