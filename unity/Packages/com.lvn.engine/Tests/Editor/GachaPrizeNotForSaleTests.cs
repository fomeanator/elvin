using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПРИЗ КРУТОК НЕ ПРОДАЁТСЯ И НЕ ДАЁТСЯ ДАРОМ (TR-47).
    ///
    /// <para>У вещи гардероба признак <c>gacha</c> был с самого начала, а у
    /// ПОЛОТНА меню — нет, хотя один из трёх призов барабана именно полотно.
    /// Приз вёл себя хуже продаваемого: цены у него не названо, а «цена ноль»
    /// для витрины значит «уже твоё» — фон, ради которого крутят барабан,
    /// надевался в два касания. Поймано сверкой конфига круток с манифестом
    /// прода 11.09.</para>
    ///
    /// <para>Страж держит СВЯЗЬ: поле есть у обоих описаний, и правило
    /// владения обязано считать «ноль без признака» бесплатным, а «ноль с
    /// признаком» — не своим.</para>
    /// </summary>
    public class GachaPrizeNotForSaleTests
    {
        /// <summary>Правило владения, как его считает лист гардероба:
        /// бесплатно — только когда цены нет И вещь не из круток.</summary>
        private static bool FreeToWear(long price, bool gacha) => price <= 0 && !gacha;

        [Test]
        public void AZeroPriceIsFreeOnlyWithoutTheWheelFlag()
        {
            Assert.IsTrue(FreeToWear(0, gacha: false), "обычная бесплатная вещь перестала быть бесплатной");
            Assert.IsFalse(FreeToWear(0, gacha: true), "приз круток отдали даром");
            Assert.IsFalse(FreeToWear(150, gacha: false), "платная вещь стала бесплатной");
        }

        /// <summary>Признак есть у ОБОИХ описаний — вещи и полотна. Разойдись
        /// они, и приз-полотно снова начнёт доставаться даром, а приз-наряд
        /// нет: одна механика с двумя правдами.</summary>
        [Test]
        public void BothAWardrobeItemAndACanvasCanBeWheelOnly()
        {
            var item = new LvnWardrobeItem { value = "winter", gacha = true };
            var canvas = new CanvasOption { id = "winter_cabin", gacha = true };
            Assert.IsTrue(item.gacha);
            Assert.IsTrue(canvas.gacha, "у полотна меню нет признака «только из круток»");
            Assert.IsFalse(FreeToWear(item.price, item.gacha));
            Assert.IsFalse(FreeToWear(canvas.price, canvas.gacha));
        }
    }
}
