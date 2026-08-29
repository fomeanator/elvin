using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Титровальщик: имя эпизода → «Глава N» → id. Четыре копии этого
    /// правила успели разойтись на живом экране — две писали «Chapter 3», две
    /// «Глава 3».</summary>
    public class CaptionsTests
    {
        [SetUp]
        [TearDown]
        public void Clean()
        {
            LvnCaptions.ChapterWord = null;
            LvnWords.Learn(null);
            LvnWords.Translate(null);
        }

        [Test]
        public void ИмяЭпизодаСильнееНомера()
        {
            var c = new LvnChapter { id = "ch3", number = 3, name = "Эпизод 3. Возвращение" };
            Assert.AreEqual("Эпизод 3. Возвращение", LvnCaptions.Chapter(c),
                "титр главы когда-то терял имя и показывал номер даже там, где название есть");
        }

        [Test]
        public void БезИмениПишетсяНомерСоСловом()
        {
            var c = new LvnChapter { id = "ch3", number = 3 };
            Assert.AreEqual("Chapter 3", LvnCaptions.Chapter(c), "умолчание движка — английское");
        }

        [Test]
        public void БезИмениИНомераОстаётсяИдентификатор()
        {
            Assert.AreEqual("pilot", LvnCaptions.Chapter(new LvnChapter { id = "pilot", number = 0 }));
        }

        [Test]
        public void НетНиЧегоПустаяСтрокаЧестнееВыдуманногоЗаголовка()
        {
            Assert.AreEqual(string.Empty, LvnCaptions.Chapter(null));
            Assert.AreEqual(string.Empty, LvnCaptions.Chapter(new LvnChapter()));
            Assert.AreEqual(string.Empty, LvnCaptions.ChapterNumberOnly(null));
        }

        [Test]
        public void СловоГлаваПринадлежитАвтору()
        {
            var c = new LvnChapter { id = "ch3", number = 3 };
            LvnCaptions.ChapterWord = "Дело";
            Assert.AreEqual("Дело 3", LvnCaptions.Chapter(c));
        }

        [Test]
        public void ПустоеПолеМанифестаОтдаётСловоСловарю()
        {
            var c = new LvnChapter { id = "ch3", number = 3 };
            LvnCaptions.ChapterWord = "";
            LvnWords.Learn(new Dictionary<string, string> { ["chapter.word"] = "День" });
            Assert.AreEqual("День 3", LvnCaptions.Chapter(c),
                "два хранилища одного слова разошлись бы на первой правке");
        }

        [Test]
        public void ПолеМанифестаСтаршеСловаря()
        {
            var c = new LvnChapter { id = "ch3", number = 3 };
            LvnCaptions.ChapterWord = "Дело";
            LvnWords.Learn(new Dictionary<string, string> { ["chapter.word"] = "День" });
            Assert.AreEqual("Дело 3", LvnCaptions.Chapter(c));
        }

        [Test]
        public void ТолькоНомерНеПовторяетИмяЭпизода()
        {
            // Титр главы: номер сверху, название под ним — дублировать незачем.
            var c = new LvnChapter { id = "ch3", number = 3, name = "Возвращение" };
            Assert.AreEqual("Chapter 3", LvnCaptions.ChapterNumberOnly(c));
        }

        [Test]
        public void ПилотнаяГлаваНомераНеПоказывает()
        {
            // Номер 0 — пилот; «Глава 0» на титре читается как сбой.
            Assert.AreEqual(string.Empty, LvnCaptions.ChapterNumberOnly(new LvnChapter { id = "pilot" }));
        }
    }
}
