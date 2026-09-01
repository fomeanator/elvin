using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ЯКОРЬ: каким местом поверхность держится за свою точку.
    ///
    /// <para>Правило стояло двумя копиями — у подписи на сцене и у окна
    /// реплики, — и различались они только умолчанием. Проверки закрепляют то,
    /// что копии молча делили: слово превращается в сдвиг САМОЙ поверхности, а
    /// незнакомое слово — не повод не показать.</para>
    /// </summary>
    public sealed class AnchorTests
    {
        [Test]
        public void ПрижатоеСлеваСверхуНеСдвигается()
        {
            var (x, y) = LvnAnchor.Percent("top-left", "center");
            Assert.AreEqual(0f, x); Assert.AreEqual(0f, y);
        }

        [Test]
        public void ПрижатоеСправаСнизуУезжаетНаСвойРазмер()
        {
            var (x, y) = LvnAnchor.Percent("bottom-right", "center");
            Assert.AreEqual(-100f, x); Assert.AreEqual(-100f, y);
        }

        [Test]
        public void СерединаСдвигаетНаПоловину()
        {
            var (x, y) = LvnAnchor.Percent("center", "top-left");
            Assert.AreEqual(-50f, x); Assert.AreEqual(-50f, y);
        }

        // Умолчание — единственное, что вправе отличаться у зовущих: подпись
        // держится за угол (тогда авторские x/y читаются как отступ), окно
        // реплики — за середину.
        [Test]
        public void ПустоеСловоБерётУмолчаниеЗовущего()
        {
            Assert.AreEqual((0f, 0f), LvnAnchor.Percent(null, "top-left"));
            Assert.AreEqual((-50f, -50f), LvnAnchor.Percent("", "center"));
        }

        // Автор написал своё слово — показываем по центру, а не прячем.
        [Test]
        public void НезнакомоеСловоНеПоводНеПоказать()
        {
            var (x, y) = LvnAnchor.Percent("посередине-сбоку", "top-left");
            Assert.AreEqual(-50f, x); Assert.AreEqual(-50f, y);
        }

        // Регистр авторский: «Top-Left» — то же место, что «top-left».
        [Test]
        public void РегистрСловаНеВажен()
        {
            Assert.AreEqual(LvnAnchor.Percent("top-left", "center"),
                            LvnAnchor.Percent("Top-Left", "center"));
        }

        [Test]
        public void ЗнакомыеСловаЧитаютсяОдинаковоНезависимоОтУмолчания()
        {
            foreach (var fallback in new[] { Align.Center, Align.FlexStart, Align.Stretch })
            {
                Assert.AreEqual(Align.FlexStart, LvnAnchor.Across("left", fallback), "«left»");
                Assert.AreEqual(Align.FlexEnd, LvnAnchor.Across("right", fallback), "«right»");
                Assert.AreEqual(Align.Center, LvnAnchor.Across("center", fallback), "«center»");
            }
        }

        [Test]
        public void НезнакомоеСловоОтдаётсяУмолчаниюЗОВУЩЕГО()
        {
            // Умолчание — решение экрана, а не разбора: выборы стоят
            // посередине, окно реплики прижато влево.
            Assert.AreEqual(Align.Center, LvnAnchor.Across("centre", Align.Center));
            Assert.AreEqual(Align.FlexStart, LvnAnchor.Across("centre", Align.FlexStart));
        }

        [Test]
        public void ПустоеИНичегоТожеУходятВУмолчание()
        {
            Assert.AreEqual(Align.Center, LvnAnchor.Across(null, Align.Center));
            Assert.AreEqual(Align.Center, LvnAnchor.Across("", Align.Center));
            Assert.AreEqual(Align.FlexEnd, LvnAnchor.Across("   ", Align.FlexEnd),
                "пробелы — это не слово; разбирать их не наше дело, но и падать нельзя");
        }
    }
}
