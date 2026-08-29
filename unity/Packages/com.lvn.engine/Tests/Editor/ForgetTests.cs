using System.Collections.Generic;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// Забвение: игрок попросил себя забыть — и хранилища личного пусты.
    /// Проверяется не «метод позвался», а то, ради чего он есть: НИ ОДНО из
    /// хранилищ не пережило обряд.
    /// </summary>
    public class ForgetTests
    {
        private const string T = "test-forget-title";
        private const string Hero = "test-forget-hero";

        [SetUp]
        [TearDown]
        public void Clean()
        {
            PlayerPrefs.DeleteKey("lvn_slots_" + T);
            PlayerPrefs.DeleteKey("lvn.gallery." + T);
            PlayerPrefs.DeleteKey("lvn.read." + T);
            PlayerPrefs.DeleteKey("lvn_state_" + T);
            PlayerPrefs.DeleteKey("lvn_state_base_" + T);
            PlayerPrefs.DeleteKey("lvn_wardrobe_" + Hero);
            PlayerPrefs.DeleteKey("lvn_state___global");
        }

        private static void FillTitle()
        {
            LvnSaveStore.Put(T, "slot1", new LvnSaveSlot
            {
                Snap = new LvnPlayer.LvnSnapshot { Index = 7, CallStack = new int[0] },
                ChapterId = "ch01",
                Preview = "линия",
            });
            LvnGalleryStore.Unlock(T, "cg-01");
            LvnReadStore.MarkRead(T, "Майя", "Привет");
            PlayerPrefs.SetString("lvn_state_" + T, "{\"vars\":{\"gold\":5}}");
            PlayerPrefs.SetString("lvn_state_base_" + T, "{\"gold\":5}");
        }

        [Test]
        public void TitleWipesEveryPersonalStoreOfThatNovel()
        {
            FillTitle();

            LvnForget.Title(T);

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "сейвы");
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(T, "cg-01"), "галерея");
            Assert.IsFalse(LvnReadStore.IsRead(T, "Майя", "Привет"), "прочитанное");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state_" + T, ""), "переменные");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state_base_" + T, ""),
                "база синхронизации: пережив стирание, она вернула бы значения с сервера");
        }

        [Test]
        public void TitleKeepsPlayerStatsBecauseTheyOutliveTheExpedition()
        {
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");

            LvnForget.Title(T);

            Assert.AreNotEqual("", PlayerPrefs.GetString("lvn_state___global", ""),
                "«начать заново» стирает экспедицию, а не игрока");
        }

        [Test]
        public void AllWipesWhatOutlivesASingleNovel()
        {
            FillTitle();
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");
            LvnWardrobe.Equip(Hero, "hair", "long");
            LvnPlayerName.Set("Майя");
            LvnPrefs.IntroDone = true;

            LvnForget.All(new[] { T }, new[] { Hero });

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count, "сейвы");
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""), "статы игрока");
            Assert.AreEqual(0, LvnWardrobe.Equipped(Hero).Count, "гардероб");
            Assert.AreEqual("", LvnPlayerName.Current, "имя");
            Assert.IsFalse(LvnPrefs.IntroDone, "флаг вступления");
        }

        [Test]
        public void RegisteredStoreIsAskedByBothRites()
        {
            string forgottenTitle = null;
            bool forgotAll = false;
            LvnForget.Register("тестовое хранилище", id => forgottenTitle = id, () => forgotAll = true);

            LvnForget.Title(T);
            Assert.AreEqual(T, forgottenTitle, "хранилище оболочки не спросили про новеллу");

            LvnForget.All(null);
            Assert.IsTrue(forgotAll, "хранилище оболочки не спросили про игрока");

            LvnForget.Register("тестовое хранилище", null, null); // не мешать соседям
        }

        [Test]
        public void OneFailingStoreDoesNotStopTheRest()
        {
            FillTitle();
            LvnForget.Register("падучее", _ => throw new System.InvalidOperationException("нарочно"), null);

            LvnForget.Title(T);

            Assert.AreEqual(0, LvnSaveStore.Slots(T).Count,
                "упавшее хранилище остановило забвение — половина игрока осталась");
            LvnForget.Register("падучее", null, null);
        }

        [Test]
        public void ForgettingNobodyIsANoOp()
        {
            // «Начать заново» без выбранной новеллы не имеет права стирать
            // ЧТО-НИБУДЬ наугад.
            FillTitle();

            LvnForget.Title(null);
            LvnForget.Title("");

            Assert.AreEqual(1, LvnSaveStore.Slots(T).Count, "стёрли не ту новеллу");
        }

        [Test]
        public void ForgettingOneNovelLeavesTheNeighbourAlone()
        {
            const string other = "test-forget-other";
            try
            {
                FillTitle();
                LvnGalleryStore.Unlock(other, "cg-01");

                LvnForget.Title(T);

                Assert.IsTrue(LvnGalleryStore.IsUnlocked(other, "cg-01"),
                    "«начать заново» стирает ОДНУ экспедицию, а не соседнюю");
            }
            finally { PlayerPrefs.DeleteKey("lvn.gallery." + other); }
        }

        [Test]
        public void AccountDeletionWithoutACatalogStillForgetsThePlayer()
        {
            // Список новелл может не доехать (нет сети) — личное игрока обязано
            // уйти всё равно.
            LvnPlayerName.Set("Майя");
            PlayerPrefs.SetString("lvn_state___global", "{\"vars\":{\"karma\":3}}");

            LvnForget.All(null);

            Assert.AreEqual("", LvnPlayerName.Current);
            Assert.AreEqual("", PlayerPrefs.GetString("lvn_state___global", ""));
        }

        [Test]
        public void ReRegisteringTheSameStoreDoesNotDoubleIt()
        {
            // Подъём приложения не обязан начинаться с чистой статики: вторая
            // регистрация того же имени ЗАМЕНЯЕТ первую, а не добавляет второго.
            int asked = 0;
            LvnForget.Register("двойное хранилище", _ => asked++, null);
            LvnForget.Register("двойное хранилище", _ => asked++, null);

            LvnForget.Title(T);

            Assert.AreEqual(1, asked);
            LvnForget.Register("двойное хранилище", null, null);
        }
    }
}
