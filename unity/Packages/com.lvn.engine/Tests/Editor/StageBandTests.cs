using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОСА КОМПОЗИЦИИ — ОДНА НА ВСЕ ЭКРАНЫ. Слоты сцены («left», «center»,
    /// «right») называют место внутри той же полосы, в которой стоит оболочка:
    /// на телефоне это весь экран, на планшете — опорная ширина по центру.
    /// Без этого «героиня слева, панели справа» на широком экране разъезжается:
    /// панели по центру, героиня на четверти планшета.
    /// </summary>
    public class StageBandTests
    {
        private const float Ref = 1080f;

        [Test]
        public void OnAPhoneTheBandIsTheWholeScreen()
        {
            Assert.AreEqual(1f, WorldStage.BandFraction(Ref, Ref), 1e-4f, "холст равен опорному");
            Assert.AreEqual(1f, WorldStage.BandFraction(Ref, 900f), 1e-4f, "уже опорного — всё равно весь");
            Assert.AreEqual(0.25f, WorldStage.BandX(0.25f, Ref, Ref), 1e-4f, "слот стоит там, где назван");
        }

        [Test]
        public void TheChromeBandIsHeldByInsetsNotByATranslate()
        {
            // Вырезной контейнер пишет свои left/right сам при каждом пересчёте
            // выреза; сдвиг снаружи он перетирал наполовину — реплика уезжала за
            // левый край. Полоса — его собственное поле, translate не трогается.
            var el = new SafeAreaElement { MaxWidth = Ref };
            Assert.AreEqual(StyleKeyword.Null, el.style.translate.keyword, "никакого сдвига на контейнере выреза");
            Assert.AreEqual(0f, SafeAreaElement.BandInset(Ref, Ref), "на телефоне полей нет");
            Assert.AreEqual(0f, SafeAreaElement.BandInset(900f, Ref), "уже опорного — тоже");
            Assert.AreEqual(1080f, SafeAreaElement.BandInset(3240f, Ref), "планшет: половина остатка с края");
            Assert.AreEqual(0f, SafeAreaElement.BandInset(0f, Ref), "до раскладки — без полей");
            Assert.AreEqual(0f, SafeAreaElement.BandInset(3240f, 0f), "ноль — во всю ширину");
        }

        [Test]
        public void OnAWideScreenTheBandShrinksToTheReference()
        {
            // Планшет на боку: холст матчится по высоте, ширина втрое больше.
            Assert.AreEqual(1f / 3f, WorldStage.BandFraction(Ref, 3240f), 1e-4f);
        }

        [Test]
        public void TheMiddleStaysTheMiddle()
        {
            Assert.AreEqual(0.5f, WorldStage.BandX(0.5f, Ref, 3240f), 1e-4f,
                "центр композиции — центр экрана на любой ширине");
        }

        [Test]
        public void TheEdgesComeInTogether()
        {
            float left = WorldStage.BandX(0.25f, Ref, 3240f);
            float right = WorldStage.BandX(0.75f, Ref, 3240f);
            Assert.AreEqual(0.5f - left, right - 0.5f, 1e-4f, "полоса симметрична");
            Assert.Greater(left, 0.25f, "левый слот подошёл к центру вместе с панелями");
            Assert.Less(left, 0.5f, "но остался слева — композиция та же, что на телефоне");
        }
    }
}
