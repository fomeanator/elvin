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
            // ПРАВИЛО ИЗМЕНИЛОСЬ ОСОЗНАННО (10.09). Раньше непереведённое
            // оставалось авторским словом — и это давало игроку КАШУ: подписи
            // манифеста написаны на языке новеллы, поэтому, выбрав English, он
            // видел «Загрузки» над английским списком и «Осталось скачать» под
            // ним («переключение языка какое-то странное» — Илья). Теперь при
            // живом переводе база отключается целиком: чего нет в каталоге,
            // берётся из умолчания кода — оно английское и читается как ЯЗЫК, а
            // не как смесь. Авторская база возвращается на выборе оригинала —
            // это держит соседний тест (EmptyTranslationReturnsTheOriginal).
            Assert.AreEqual("Shop", LvnWords.Of("shop", "Shop"),
                "при живом переводе непереведённое обязано брать умолчание кода, " +
                "иначе игрок снова получит половину экрана на чужом языке");
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
