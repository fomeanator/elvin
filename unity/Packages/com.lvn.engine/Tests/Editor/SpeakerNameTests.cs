using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ИМЯ ГОВОРЯЩЕГО — то же имя, что и везде.
    ///
    /// <para>В живом контенте Time Romance имена персонажей переведены в
    /// словаре оболочки и не переведены в каталогах глав (52 случая из 84
    /// проверенных). Игрок с английским интерфейсом видел «Victoria» в
    /// гардеробе и «Виктория» над репликой той же героини.</para>
    /// </summary>
    public sealed class SpeakerNameTests
    {
        [TearDown]
        public void Clear()
        {
            LvnWords.Translate(null);
            LvnWords.LearnActors(null);
        }

        private static Dictionary<string, LvnSpriteEntity> Cast()
            => new Dictionary<string, LvnSpriteEntity>
            {
                ["victoria"] = new LvnSpriteEntity { name = "Виктория" },
            };

        // Имя из скрипта — авторская строка; перевод лежит по ключу от
        // ИДЕНТИФИКАТОРА актёра. Связывает их карта манифеста.
        [Test]
        public void AuthoredNameResolvesThroughTheActorId()
        {
            LvnWords.LearnActors(Cast());
            LvnWords.Translate(new Dictionary<string, string> { ["actor.victoria"] = "Victoria" });

            Assert.AreEqual("Victoria", LvnWords.Speaker("Виктория"),
                "имя над репликой — то же, что в гардеробе");
            Assert.AreEqual("Victoria", LvnWords.Speaker("victoria"),
                "по идентификатору тоже: скрипт вправе называть актёра им");
        }

        // Того, кого нет в манифесте («Система», «Голос»), автор переводит
        // прямым ключом по самому имени.
        [Test]
        public void NamesOutsideTheCastTranslateByThemselves()
        {
            LvnWords.LearnActors(Cast());
            LvnWords.Translate(new Dictionary<string, string> { ["actor.Система"] = "System" });

            Assert.AreEqual("System", LvnWords.Speaker("Система"));
        }

        // Перевода нет вовсе — остаётся авторское имя, а на латинице оно
        // читается транслитом: кириллица посреди английской сцены выглядит
        // поломкой, а не выбором.
        [Test]
        public void UntranslatedNameStaysAuthoredAndIsReadableInLatin()
        {
            LvnWords.LearnActors(Cast());

            LvnWords.Translate(null);   // язык оригинала
            Assert.AreEqual("Виктория", LvnWords.Speaker("Виктория"),
                "на своём языке авторское имя и есть правильное");

            LvnWords.Translate(new Dictionary<string, string> { ["settings.title"] = "Settings" });
            Assert.AreEqual("Viktoriya", LvnWords.Speaker("Виктория"),
                "перевода имени нет, но игрок читает латиницей");
        }
    }
}
