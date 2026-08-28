using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Перевод слов оболочки: накладывается поверх авторского набора и
    /// сообщает экранам, что пора переодеться.</summary>
    public class WordsTranslateTests
    {
        [SetUp]
        [TearDown]
        public void Clean() { LvnWords.Learn(null); LvnWords.Translate(null); }

        [Test]
        public void TranslationOverlaysTheAuthorsWords()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["play"] = "Играть", ["shop"] = "Магазин" });
            LvnWords.Translate(new Dictionary<string, string> { ["play"] = "Play" });

            Assert.AreEqual("Play", LvnWords.Of("play", "PLAY"), "переведённое берётся из перевода");
            Assert.AreEqual("Магазин", LvnWords.Of("shop", "Shop"),
                "непереведённое остаётся АВТОРСКИМ словом, а не английским умолчанием движка");
        }

        [Test]
        public void EmptyTranslationReturnsTheOriginal()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["play"] = "Играть" });
            LvnWords.Translate(new Dictionary<string, string> { ["play"] = "Play" });
            LvnWords.Translate(null);

            Assert.AreEqual("Играть", LvnWords.Of("play", "PLAY"), "«язык оригинала» снимает наложение целиком");
        }

        [Test]
        public void ScreensAreToldToRedress()
        {
            int told = 0;
            LvnWords.Changed += () => told++;
            try
            {
                LvnWords.Translate(new Dictionary<string, string> { ["play"] = "Play" });
                Assert.AreEqual(1, told,
                    "без сигнала перевод доедет только до экранов, открытых ПОСЛЕ смены языка");
            }
            finally { LvnWords.Changed -= () => told++; }
        }

        [Test]
        public void KeysAreCaseInsensitiveLikeTheAuthorsSet()
        {
            LvnWords.Translate(new Dictionary<string, string> { ["Play"] = "Play" });
            Assert.AreEqual("Play", LvnWords.Of("play", "PLAY"));
        }
    }
}
