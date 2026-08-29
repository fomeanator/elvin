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

        [Test]
        public void EmptyInputIsNotAReasonToCrash()
        {
            Assert.IsFalse(LvnTranslit.HasCyrillic(null));
            Assert.IsFalse(LvnTranslit.HasCyrillic(""));
            Assert.IsNull(LvnTranslit.ToLatin(null));
            Assert.AreEqual("", LvnTranslit.ToLatin(""));
        }

        [Test]
        public void SilentSignsDisappearInsteadOfBecomingApostrophes()
        {
            // Апостроф вместо мягкого знака только мешает читать.
            Assert.AreEqual("obem", LvnTranslit.ToLatin("объем"));
            StringAssert.DoesNotContain("'", LvnTranslit.ToLatin("Дальность"));
        }

        [Test]
        public void MultiLetterReplacementsKeepTheirCase()
        {
            // Заглавной становится ТОЛЬКО первая буква замены — иначе «Юля»
            // превращается в «YUlya».
            Assert.AreEqual("Yuliya", LvnTranslit.ToLatin("Юлия"));
            Assert.AreEqual("Zhanna", LvnTranslit.ToLatin("Жанна"));
            Assert.AreEqual("Chekhov", LvnTranslit.ToLatin("Чехов"));
        }

        [Test]
        public void MixedStringsStayMixed()
        {
            // Строка может быть смешанной: латиница и знаки остаются как есть.
            Assert.AreEqual("Cold: Roman i Viktoriya (2)",
                LvnTranslit.ToLatin("Cold: Роман и Виктория (2)"));
        }

        [Test]
        public void CyrillicIsDetectedByLettersNotByLanguage()
        {
            Assert.IsTrue(LvnTranslit.HasCyrillic("Cold: Роман"), "хватает одной буквы");
            Assert.IsFalse(LvnTranslit.HasCyrillic("Cold 13 — dash and digits"));
        }
    }
}
