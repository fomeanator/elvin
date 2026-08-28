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
    }
}
