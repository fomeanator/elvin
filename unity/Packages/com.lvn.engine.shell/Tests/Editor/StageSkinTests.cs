using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Shell.Tests
{
    /// <summary>
    /// ПАСПОРТ ОБЛИКА: числа, которыми нарисован арт витрины, приходят из
    /// манифеста, а не из констант кода. Сторожим три обещания — без паспорта
    /// всё как было, названное поле доходит, неназванное не сбивается, — и
    /// пересчёт «пикселей картинки на dp», где легче всего ошибиться на запас
    /// свечения.
    /// </summary>
    public class StageSkinTests
    {
        [TearDown]
        public void Restore() => LvnStageSkin.Reset();

        [Test]
        public void WithoutAPassportTheEngineKeepsItsOwnMeasures()
        {
            LvnStageSkin.Apply(null);
            Assert.AreEqual(390f, LvnStageSkin.DesignWidth, "макет, с которого облик начинали");
            Assert.AreEqual(34f, LvnStageSkin.HomeBar, "домашняя полоса телефона");
            Assert.AreEqual(200f, LvnStageSkin.Panel.Width, "панель новостей");
        }

        [Test]
        public void NamedNumbersArriveAndTheRestStaysPut()
        {
            LvnStageSkin.Apply(new StageSkinMetrics { design_width = 414f, home_bar = 20f });
            Assert.AreEqual(414f, LvnStageSkin.DesignWidth, "названное поле доходит");
            Assert.AreEqual(20f, LvnStageSkin.HomeBar);
            Assert.AreEqual(12f, LvnStageSkin.Bleed, "неназванное остаётся движковым");
            Assert.AreEqual(124f, LvnStageSkin.Panel.Height);
        }

        [Test]
        public void ADroppedFieldGoesBackToTheDefault()
        {
            LvnStageSkin.Apply(new StageSkinMetrics { home_bar = 20f });
            LvnStageSkin.Apply(new StageSkinMetrics());
            Assert.AreEqual(34f, LvnStageSkin.HomeBar,
                "убранное из манифеста поле возвращается к умолчанию, а не донашивается");
        }

        [Test]
        public void AFrameKeepsTheHalfItWasNotToldAbout()
        {
            LvnStageSkin.Apply(new StageSkinMetrics
            {
                frames = new Dictionary<string, StageFrame>
                {
                    ["panel"] = new StageFrame { height = 230f },
                },
            });
            Assert.AreEqual(230f, LvnStageSkin.Panel.Height, "новая высота панели");
            Assert.AreEqual(200f, LvnStageSkin.Panel.Width, "ширину не называли — осталась своя");
            Assert.AreEqual(30f, LvnStageSkin.Panel.TopPx, "нарезку не называли — осталась своя");
        }

        [Test]
        public void PixelsPerDpCountTheGlowBeyondTheEdge()
        {
            // panel.png экспортирован на 200 dp места плюс по 12 dp свечения с
            // каждой стороны: 672 / 224 = 3. Забыть запас — значит получить 3.36
            // и промахнуться нарезкой на десятую часть рамки.
            Assert.AreEqual(3f, LvnStageSkin.Panel.PxPerDp(LvnStageSkin.Bleed), 1e-4f);
            Assert.AreEqual(3.36f, LvnStageSkin.Panel.PxPerDp(0f), 1e-2f,
                "без запаса счёт другой — потому запас и назван отдельно");
        }
    }
}
