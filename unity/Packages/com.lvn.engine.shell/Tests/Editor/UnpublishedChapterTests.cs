using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// СНЯТАЯ С ПУБЛИКАЦИИ ГЛАВА НЕ СТИРАЕТ ПРОХОЖДЕНИЕ.
    ///
    /// <para>Живой контент значит, что каталог меняется под играющим: автор
    /// правит главу и на время убирает её из манифеста, переносит эпизод в
    /// другой сезон, откатывает неудачную выкладку. Игрок в этот момент стоит
    /// ровно в той главе. Вопрос не «покажем ли мы её» — показать нечего, — а
    /// «что станет с его прохождением»: если отметки обнулятся, вернувшаяся
    /// через час глава встретит игрока пустой полкой, и он начнёт всё заново.</para>
    ///
    /// <para>Условие: отметка «докуда дошёл» переживает исчезновение главы, а
    /// возвращение главы возвращает и точку продолжения. Падать при этом
    /// нельзя ни на одном шаге.</para>
    /// </summary>
    public class UnpublishedChapterTests
    {
        private const string TitleId = "test-unpublish";

        private static LvnTitle Title(params int[] numbers)
        {
            var chapters = new List<LvnChapter>();
            foreach (var n in numbers)
                chapters.Add(new LvnChapter { id = "ch" + n, number = n, name = "Глава " + n });
            return new LvnTitle
            {
                id = TitleId,
                name = "Проба",
                seasons = new List<LvnSeason> { new LvnSeason { chapters = chapters } },
            };
        }

        [SetUp]
        [TearDown]
        public void Clean()
        {
            foreach (var k in new[] { "lvn_chapter_", "lvn_chapter_num_", "lvn_reached_", "lvn_entry_" })
                PlayerPrefs.DeleteKey(k + TitleId);
            PlayerPrefs.Save();
        }

        [Test]
        public void ИсчезновениеГлавыНеОбнуляетДостигнутое()
        {
            var full = Title(1, 2, 3);
            var third = full.seasons[0].chapters[2];
            LvnProgress.StartChapter(full, third);

            Assert.AreEqual(3, LvnProgress.Reached(full), "стенд не поставил игрока в третью главу");
            Assert.AreEqual("ch3", LvnProgress.Current(full)?.id);

            // Автор снял третью главу — каталог отдаёт только первые две.
            var trimmed = Title(1, 2);
            Assert.DoesNotThrow(() => LvnProgress.Current(trimmed),
                "снятая глава роняет вопрос «где я остановился»");
            Assert.AreEqual(3, LvnProgress.Reached(trimmed),
                "снятая глава обнулила достигнутое — вернувшаяся глава встретит игрока пустой полкой");
            Assert.IsTrue(LvnProgress.Touched(trimmed),
                "новелла перестала считаться начатой — карточка предложит «Начать», а не «Продолжить»");
        }

        [Test]
        public void ВозвращениеГлавыВозвращаетТочкуПродолжения()
        {
            var full = Title(1, 2, 3);
            LvnProgress.StartChapter(full, full.seasons[0].chapters[2]);

            var trimmed = Title(1, 2);
            LvnProgress.Current(trimmed);           // игрок заходил, пока главы не было

            var back = Title(1, 2, 3);
            Assert.AreEqual("ch3", LvnProgress.Current(back)?.id,
                "после возвращения главы точка продолжения не восстановилась — прохождение потеряно");
            Assert.AreEqual(3, LvnProgress.Reached(back));
        }

        [Test]
        public void ПереименованнаяГлаваНаходитсяПоНомеру()
        {
            var full = Title(1, 2, 3);
            LvnProgress.StartChapter(full, full.seasons[0].chapters[2]);

            // Переимпорт переименовал главы, номера сохранились.
            var renamed = Title(1, 2, 3);
            renamed.seasons[0].chapters[2].id = "chapter-three-2026";
            Assert.AreEqual("chapter-three-2026", LvnProgress.Current(renamed)?.id,
                "переименование главы потеряло прохождение");
        }
    }
}
