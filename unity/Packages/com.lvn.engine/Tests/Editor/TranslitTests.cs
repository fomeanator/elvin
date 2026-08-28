using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Транслитерация: не перевод, а способ прочитать чужое имя вслух.</summary>
    public class TranslitTests
    {
        [SetUp]
        [TearDown]
        public void Clean() { LvnWords.Learn(null); LvnWords.Translate(null); }

        [Test]
        public void ReadsRussianNamesAloud()
        {
            Assert.AreEqual("Viktoriya", LvnTranslit.ToLatin("Виктория"));
            Assert.AreEqual("Roman", LvnTranslit.ToLatin("Роман"));
            Assert.AreEqual("Shchuka", LvnTranslit.ToLatin("Щука"), "заглавной становится только первая буква замены");
            Assert.AreEqual("Olga", LvnTranslit.ToLatin("Ольга"), "мягкий знак не звучит");
        }

        [Test]
        public void LeavesEverythingElseAlone()
        {
            Assert.AreEqual("Cold 13", LvnTranslit.ToLatin("Cold 13"));
            Assert.AreEqual("Viktoriya-2", LvnTranslit.ToLatin("Виктория-2"), "цифры и знаки не трогаем");
            Assert.IsFalse(LvnTranslit.HasCyrillic("Hello"));
            Assert.IsTrue(LvnTranslit.HasCyrillic("Привет"));
        }

        [Test]
        public void OriginalLanguageIsNeverLatinised()
        {
            // Игра без переводов не имеет права латинизировать сама себя.
            Assert.AreEqual("Виктория", LvnWords.Readable("Виктория"));
        }

        [Test]
        public void UntranslatedNameIsReadableOnceTheLanguageSwitched()
        {
            LvnWords.Translate(new Dictionary<string, string> { ["play"] = "Play" });

            Assert.AreEqual("Viktoriya", LvnWords.Name("actor", "hill", "Виктория"),
                "перевода нет — но кириллица посреди английского читается как ошибка");
            Assert.AreEqual("Rose", LvnWords.Name("skin", "rose", "Rose"), "латиницу не трогаем");
        }

        [Test]
        public void TranslationStillWinsOverTranslit()
        {
            LvnWords.Translate(new Dictionary<string, string> { ["actor.hill"] = "Victoria" });

            Assert.AreEqual("Victoria", LvnWords.Name("actor", "hill", "Виктория"),
                "перевод — всегда лучше транслита");
        }
    }
}
