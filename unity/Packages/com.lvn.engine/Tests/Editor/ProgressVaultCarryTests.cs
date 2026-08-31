using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// СВЁРТОК НЕ ЗАБЫВАЕТ — вторая половина того же правила.
    ///
    /// <para>Восстановление всегда было аддитивным: потолок только растёт,
    /// точка садится только на пустое место, галерея доливается. А съёмка
    /// строилась по ЖИВОМУ манифесту с нуля, и запись заменяла обе копии
    /// целиком. Новелла, которой в этом манифесте нет — урезанный каталог
    /// беты, сменившийся id, манифест, поднятый из кэша, — вылетала и из
    /// файла, и с сервера. А съёмку дёргает каждый ход прогресса и каждая
    /// пауза приложения, так что хватало одного запуска.</para>
    ///
    /// <para>Игрок при этом ничего не замечает: карточка исчезнувшей новеллы
    /// вернётся в каталог позже — уже пустой, и восстанавливать её будет
    /// НЕОТКУДА. Свёрток существует ровно затем, чтобы такого не случалось,
    /// и потому обе его половины обязаны жить по одному правилу.</para>
    /// </summary>
    public sealed class ProgressVaultCarryTests
    {
        private const string Живая = "t_carry_live";
        private const string Ушедшая = "t_carry_gone";

        private string _имяБыло;

        private static LvnTitle Title(string id, params (string id, int number)[] chapters)
        {
            var list = new List<LvnChapter>();
            foreach (var (cid, number) in chapters) list.Add(new LvnChapter { id = cid, number = number });
            return new LvnTitle { id = id, seasons = new List<LvnSeason> { new LvnSeason { chapters = list } } };
        }

        private static LvnTitle Три(string id) => Title(id, ("c1", 1), ("c2", 2), ("c3", 3));
        private static LvnManifest Манифест(params LvnTitle[] t) => new LvnManifest { titles = new List<LvnTitle>(t) };
        private static LvnChapter Гл(LvnTitle t, int i) => t.ChaptersOf()[i];
        private static JObject Запись(JObject св, string id) => св?["titles"]?[id] as JObject;

        [SetUp]
        public void Приготовить()
        {
            _имяБыло = LvnPrefs.PlayerName;
            Стереть();
        }

        [TearDown]
        public void Убрать()
        {
            Стереть();
            LvnPrefs.PlayerName = _имяБыло ?? "";
        }

        private static void Стереть()
        {
            LvnProgress.ResetTitle(Живая);
            LvnProgress.ResetTitle(Ушедшая);
            LvnGalleryStore.Clear(Живая);
            LvnGalleryStore.Clear(Ушедшая);
            LvnPrefs.PlayerName = "";
        }

        /// <summary>Прежний свёрток с одной пройденной новеллой — как если бы
        /// её сняли, пока она ещё была в каталоге.</summary>
        private static JObject Прежний(string id, int reached, params string[] gallery)
        {
            var ent = new JObject { ["cur"] = null, ["num"] = 0, ["reached"] = reached };
            if (gallery != null && gallery.Length > 0) ent["gallery"] = new JArray(gallery);
            return new JObject
            {
                ["v"] = 1,
                ["name"] = "Женя",
                ["titles"] = new JObject { [id] = ent },
                ["wardrobe"] = new JObject(),
            };
        }

        // ── новелла, выпавшая из каталога ───────────────────────────────────

        [Test]
        public void НовеллаВыпавшаяИзКаталогаОстаётсяВСвёртке()
        {
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 0));

            var свёрток = ProgressVault.Collect(Манифест(живая), Прежний(Ушедшая, 3));

            Assert.IsNotNull(Запись(свёрток, Ушедшая),
                "новеллы нет в ЭТОМ манифесте — но её прохождение уже нигде больше не записано");
            Assert.AreEqual(3, (int)Запись(свёрток, Ушедшая)["reached"],
                "перенесли запись, но потеряли потолок — это та же потеря, только тише");
            Assert.IsNotNull(Запись(свёрток, Живая), "живая новелла при этом никуда не делась");
        }

        [Test]
        public void ГалереяУшедшейНовеллыПереезжаетЦеликом()
        {
            var свёрток = ProgressVault.Collect(Манифест(Три(Живая)), Прежний(Ушедшая, 2, "cg_a", "cg_b"));
            var cg = Запись(свёрток, Ушедшая)?["gallery"] as JArray;
            Assert.IsNotNull(cg, "картинки открывают один раз за прохождение — второй попытки не будет");
            Assert.AreEqual(2, cg.Count);
        }

        [Test]
        public void БезПрежнегоСвёрткаСъёмкаПрежняя()
        {
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 0));

            var один = ProgressVault.Collect(Манифест(живая));
            var два = ProgressVault.Collect(Манифест(живая), null);

            Assert.IsNotNull(Запись(один, Живая));
            Assert.IsNull(Запись(один, Ушедшая), "чего не было — того съёмка не выдумывает");
            Assert.AreEqual(один["titles"].ToString(), два["titles"].ToString());
        }

        // ── знакомая новелла: то же правило, что и у восстановления ─────────

        [Test]
        public void СтёртыеНастройкиНеОпускаютПотолокЗнакомойНовеллы()
        {
            // Живая новелла есть в каталоге, но записная книжка устройства
            // пуста (её потеряли). Съёмка по живому дала бы потолок 0 и
            // записала бы ноль поверх облачной тройки — прохождение стёрлось
            // бы ровно тем действием, которое его должно было спасти.
            var свёрток = ProgressVault.Collect(Манифест(Три(Живая)), Прежний(Живая, 3));

            Assert.IsNotNull(Запись(свёрток, Живая));
            Assert.AreEqual(3, (int)Запись(свёрток, Живая)["reached"]);
        }

        [Test]
        public void ЖивойПотолокВышеПрежнегоПобеждает()
        {
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 2));   // дошли до третьей

            var свёрток = ProgressVault.Collect(Манифест(живая), Прежний(Живая, 1));

            Assert.AreEqual(3, (int)Запись(свёрток, Живая)["reached"],
                "свёрток бывает старше устройства — но не наоборот");
        }

        [Test]
        public void ГалереяЗнакомойНовеллыДоливается()
        {
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 0));
            LvnGalleryStore.Unlock(Живая, "cg_new");

            var свёрток = ProgressVault.Collect(Манифест(живая), Прежний(Живая, 1, "cg_old"));
            var cg = Запись(свёрток, Живая)?["gallery"] as JArray;

            Assert.IsNotNull(cg);
            CollectionAssert.AreEquivalent(new[] { "cg_new", "cg_old" }, cg.ToObject<string[]>(),
                "картинки только доливаются — как и при восстановлении");
        }

        [Test]
        public void ГалереяНеДублируетсяПриПовторнойСъёмке()
        {
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 0));
            LvnGalleryStore.Unlock(Живая, "cg_same");

            var раз = ProgressVault.Collect(Манифест(живая), Прежний(Живая, 1, "cg_same"));
            var два = ProgressVault.Collect(Манифест(живая), раз);

            Assert.AreEqual(1, ((JArray)Запись(два, Живая)["gallery"]).Count,
                "съёмка идёт на каждом ходе прогресса — список рос бы вечно");
        }

        // ── имя игрока ──────────────────────────────────────────────────────

        [Test]
        public void ИмяИгрокаНеСтираетсяПустымиНастройками()
        {
            LvnPrefs.PlayerName = "";
            var свёрток = ProgressVault.Collect(Манифест(Три(Живая)), Прежний(Ушедшая, 1));
            Assert.AreEqual("Женя", (string)свёрток["name"],
                "имя спрашивают один раз за установку — потерять его значит спросить снова");
        }

        [Test]
        public void ЖивоеИмяСильнееПрежнего()
        {
            LvnPrefs.PlayerName = "Вика";
            var свёрток = ProgressVault.Collect(Манифест(Три(Живая)), Прежний(Ушедшая, 1));
            Assert.AreEqual("Вика", (string)свёрток["name"]);
        }

        // ── гардероб ────────────────────────────────────────────────────────

        [Test]
        public void ГардеробГероиниВыпавшейИзМанифестаПереезжает()
        {
            var прежний = Прежний(Ушедшая, 1);
            prevWardrobe(прежний);

            var свёрток = ProgressVault.Collect(Манифест(Три(Живая)), прежний);

            Assert.IsNotNull(свёрток["wardrobe"]?["hill"],
                "наряды куплены за деньги — их нельзя терять вместе с манифестом");
        }

        private static void prevWardrobe(JObject свёрток)
            => свёрток["wardrobe"] = new JObject
            {
                ["hill"] = new JObject { ["worn"] = new JObject { ["dress"] = "gala" } },
            };

        // ── забвение остаётся забвением ─────────────────────────────────────

        [Test]
        public void ЗабвениеНеВоскресаетИзПрежнегоСвёртка()
        {
            // Обряд забвения сносит файловый сейф, поэтому прежнего свёртка
            // просто нет — и переносить нечего. Если бы Snapshot читал файл
            // после его удаления, стёртое возвращалось бы на первом же ходе.
            var живая = Три(Живая);
            LvnProgress.StartChapter(живая, Гл(живая, 0));
            ProgressVault.WriteLocal(ProgressVault.Collect(Манифест(живая)));

            ProgressVault.Forget();
            Стереть();

            var свёрток = ProgressVault.Snapshot(Манифест(живая));
            Assert.IsNull(Запись(свёрток, Живая),
                "игрок попросил себя забыть — свёрток обязан согласиться");
        }
    }
}
