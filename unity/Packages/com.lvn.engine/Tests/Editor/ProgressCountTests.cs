using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СКОЛЬКО ПРОЙДЕНО — честное число и честная полоса.
    ///
    /// <para>Полоса на карточке подборки рисовала зашитые 35% — одинаковые у
    /// непочатой новеллы и у почти пройденной. Профиль считал своё: достигнутую
    /// главу он записывал в пройденные, хотя игрок в ней сейчас.</para>
    /// </summary>
    public sealed class ProgressCountTests
    {
        private static LvnTitle Title(string id = "t")
            => new LvnTitle
            {
                id = id,
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter>
                    {
                        new LvnChapter { id = "c1", number = 1 },
                        new LvnChapter { id = "c2", number = 2 },
                        new LvnChapter { id = "c3", number = 3 },
                    } },
                },
            };

        [TearDown]
        public void Clean() => LvnProgress.ResetTitle("t");

        // Не начата — ноль, и полосы нет вовсе.
        [Test]
        public void UntouchedNovelReadsZero()
        {
            var t = Title();
            Assert.AreEqual(0, LvnProgress.Done(t));
            Assert.AreEqual(0f, LvnProgress.Fraction(t));
        }

        // Игрок в третьей главе: пройдены первые две, третья идёт.
        [Test]
        public void TheChapterInProgressIsNotCountedAsDone()
        {
            var t = Title();
            LvnProgress.RestoreMarker(t.id, "c3", number: 3, reached: 3);

            Assert.AreEqual(2, LvnProgress.Done(t), "достигнутая глава ещё играется");
            Assert.AreEqual(2f / 3f, LvnProgress.Fraction(t), 0.001f);
        }

        // Новелла закончена — сыграны все, полоса полная.
        [Test]
        public void FinishedNovelCountsEveryChapter()
        {
            var t = Title();
            LvnProgress.RestoreMarker(t.id, "c3", number: 3, reached: 3);
            LvnProgress.ClearCurrent(t);     // продолжения нет — новелла пройдена

            Assert.IsTrue(LvnProgress.Finished(t));
            Assert.AreEqual(3, LvnProgress.Done(t));
            Assert.AreEqual(1f, LvnProgress.Fraction(t));
        }

        // Номера глав не обязаны идти с единицы: считаем по списку, а не по
        // номеру (у импортированных новелл нумерация бывает любой).
        [Test]
        public void OddChapterNumbersStillCountByList()
        {
            var t = new LvnTitle
            {
                id = "t",
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter>
                    {
                        new LvnChapter { id = "a", number = 0 },
                        new LvnChapter { id = "b", number = 10 },
                        new LvnChapter { id = "c", number = 20 },
                    } },
                },
            };
            LvnProgress.RestoreMarker(t.id, "b", number: 10, reached: 10);

            Assert.AreEqual(1, LvnProgress.Done(t), "пройдена только нулевая");
        }
    }
}
