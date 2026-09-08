using NUnit.Framework;
using UnityEngine.UIElements;
using Lvn.UI;
using Lvn.UI.Screens;

namespace Lvn.Tests
{
    /// <summary>
    /// БУРГЕР ЖИВЁТ ТОЛЬКО В ГЛАВЕ.
    ///
    /// <para>Он открывает игровое меню — сохранения, историю, настройки
    /// сцены. В витрине (главная, гардероб, магазин) открывать ему нечего:
    /// те же вещи лежат по вкладкам навбара. А висел он там всегда, потому
    /// что полный ряд бара показывается как раз ВНЕ главы — и три полоски
    /// ехали вместе с ним (скрин Ильи 08.09: гардероб, бургер поверх
    /// героини).</para>
    ///
    /// <para>Смену режима на живом экране проверяет PlayMode-собрат
    /// TopBarBurgerLiveTests: сигнал доходит до бара, только пока он в
    /// панели (LvnLeash.WhileOnScreen), а здесь панели нет.</para>
    /// </summary>
    public class TopBarBurgerTests
    {
        private static VisualElement Burger(LvnTopBar bar) => bar.Q<VisualElement>("burger");

        [Test]
        public void Burger_IsHiddenAtBirth_InTheShowcase()
        {
            LvnScreenDirector.Current.AnnounceChapter(false);   // витрина

            var bar = new LvnTopBar();
            var burger = Burger(bar);
            Assert.IsNotNull(burger, "бургер не найден в баре");
            Assert.AreEqual(DisplayStyle.None, burger.style.display.value,
                "бар родился в витрине с бургером на экране: вид применяется "
                + "только по смене режима, а она в витрине не наступает");
        }
    }
}
