using Lvn.UI.World;
using NUnit.Framework;

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
