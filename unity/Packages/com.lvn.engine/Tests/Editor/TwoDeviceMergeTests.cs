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
    /// ДВА УСТРОЙСТВА ОДНОГО ИГРОКА — телефон и планшет под одним аккаунтом.
    ///
    /// <para>До этих правил правил не было: побеждала последняя запись. Копия,
    /// записавшаяся позже, затирала другую ЦЕЛИКОМ — прохождение, открытые
    /// картинки, гардероб. Игрок, читавший вечером на планшете, утром открывал
    /// телефон и «терял вечер» — а сервер при этом честно хранил ровно то, что
    /// ему прислали.</para>
    ///
    /// <para>Правило здесь не одно на всё, а СВОЁ У КАЖДОГО ВИДА ДАННЫХ,
    /// потому что цена ошибки у них разная: потолок глав только растёт (открытое
    /// не отбирается), галерея и «встреченное» только доливаются (второго
    /// показа не будет), точка продолжения и надетое идут за тем устройством,
    /// где игрок был ПОЗЖЕ (это его последняя воля), а незнакомое переезжает
    /// как есть (свёрток не забывает). Чего здесь НЕТ намеренно: кошелёк — его
    /// сливать нельзя вообще (сложить два баланса значит выдать деньги из
    /// воздуха), он живёт сервер-авторитетным леджером операций.</para>
    /// </summary>
    public sealed class TwoDeviceMergeTests
    {
        private const string Ид = "t_2dev_novel";
        private const string Вторая = "t_2dev_other";
        private const string Героиня = "t_2dev_hero";

        private string _имяБыло;
        private string _сейфБыл;

        private static string ПутьСейфа =>
            System.IO.Path.Combine(Application.persistentDataPath, "lvn_progress.json");

        [SetUp]
        public void Приготовить()
        {
            _имяБыло = LvnPrefs.PlayerName;
            _сейфБыл = System.IO.File.Exists(ПутьСейфа) ? System.IO.File.ReadAllText(ПутьСейфа) : null;
            Стереть();
        }

        [TearDown]
        public void Убрать()
        {
            Стереть();
            LvnPrefs.PlayerName = _имяБыло ?? "";
            if (_сейфБыл != null) System.IO.File.WriteAllText(ПутьСейфа, _сейфБыл);
            else if (System.IO.File.Exists(ПутьСейфа)) System.IO.File.Delete(ПутьСейфа);
        }

        private static void Стереть()
        {
            LvnProgress.ResetTitle(Ид);
            LvnProgress.ResetTitle(Вторая);
            LvnGalleryStore.Clear(Ид);
            LvnGalleryStore.Clear(Вторая);
            LvnWardrobe.Clear(Героиня);
            LvnPrefs.PlayerName = "";
            ProgressVault.Forget();
        }

        // ── сборка данных ───────────────────────────────────────────────────

        private static LvnTitle Title(string id, params (string id, int number)[] chapters)
        {
            var list = new List<LvnChapter>();
            foreach (var (cid, number) in chapters) list.Add(new LvnChapter { id = cid, number = number });
            return new LvnTitle { id = id, seasons = new List<LvnSeason> { new LvnSeason { chapters = list } } };
        }

        private static LvnTitle Три() => Title(Ид, ("c1", 1), ("c2", 2), ("c3", 3));

        private static LvnManifest Манифест(params LvnTitle[] t)
        {
            return new LvnManifest
            {
                titles = new List<LvnTitle>(t),
                sprites = new Dictionary<string, LvnSpriteEntity>
                {
                    [Героиня] = new LvnSpriteEntity
                    {
                        name = "Хилл",
                        wardrobe = new Dictionary<string, LvnWardrobeSlot>
                        {
                            ["dress"] = new LvnWardrobeSlot { name = "Платье" },
                            ["hat"] = new LvnWardrobeSlot { name = "Шляпа" },
                        },
                    },
                },
            };
        }

        /// <summary>Свёрток одного устройства: как его снял бы Collect.</summary>
        private static JObject Свёрток(long at, string name = null)
            => new JObject
            {
                ["v"] = 1,
                ["at"] = at,
                ["name"] = name,
                ["titles"] = new JObject(),
                ["wardrobe"] = new JObject(),
            };

        private static JObject Метка(string cur, int? num, int reached, long? at = null, params string[] gallery)
        {
            var e = new JObject { ["cur"] = cur, ["num"] = num, ["reached"] = reached };
            if (at != null) e["at"] = at;
            if (gallery != null && gallery.Length > 0) e["gallery"] = new JArray(gallery);
            return e;
        }

        private static JObject Запись(JObject свёрток, string id) => свёрток?["titles"]?[id] as JObject;

        // ════════════════════════════════════════════════════════════════════
        // Слияние свёртков: чистые правила, по одному на вид данных
        // ════════════════════════════════════════════════════════════════════

        [Test]
        public void ПотолокГлавНеОпускаетсяНиВОднуСторону()
        {
            // Планшет дошёл до пятой, телефон до третьей. Кто бы ни писал
            // последним — открытые главы не запираются обратно.
            var телефон = Свёрток(2000);
            телефон["titles"][Ид] = Метка("c3", 3, 3, 2000);
            var планшет = Свёрток(1000);
            планшет["titles"][Ид] = Метка("c2", 2, 5, 1000);

            Assert.AreEqual(5, (int)Запись(ProgressVault.Merge(телефон, планшет), Ид)["reached"],
                "потолок отстающей стороны выше — он и остаётся");
            Assert.AreEqual(5, (int)Запись(ProgressVault.Merge(планшет, телефон), Ид)["reached"],
                "и с обратным порядком аргументов ответ тот же");
        }

        [Test]
        public void ТочкаПродолженияИдётЗаПоследнимИгравшимУстройством()
        {
            // «Продолжить» должно вести туда, где игрок был ВЧЕРА ВЕЧЕРОМ,
            // а не туда, где осталась копия, писавшая на сервер удачливее.
            var старое = Свёрток(1000);
            старое["titles"][Ид] = Метка("c1", 1, 1, 1000);
            var свежее = Свёрток(2000);
            свежее["titles"][Ид] = Метка("c3", 3, 3, 2000);

            Assert.AreEqual("c3", (string)Запись(ProgressVault.Merge(старое, свежее), Ид)["cur"],
                "точка едет за более свежим устройством");
            Assert.AreEqual("c3", (string)Запись(ProgressVault.Merge(свежее, старое), Ид)["cur"],
                "в обе стороны: свежесть решает, а не порядок аргументов");
        }

        [Test]
        public void ГалереиДвухУстройствОбъединяютсяБезПотерь()
        {
            // CG открывают один раз за прохождение: на телефоне игрок увидел
            // одну сцену, на планшете другую — потерять любую значит отнять
            // то, чего второй раз не покажут.
            var а = Свёрток(2000);
            а["titles"][Ид] = Метка("c2", 2, 2, 2000, "cg_a", "cg_both");
            var б = Свёрток(1000);
            б["titles"][Ид] = Метка("c1", 1, 1, 1000, "cg_b", "cg_both");

            var слитые = (Запись(ProgressVault.Merge(а, б), Ид)["gallery"] as JArray)?.ToObject<string[]>();
            CollectionAssert.AreEquivalent(new[] { "cg_a", "cg_b", "cg_both" }, слитые,
                "объединение без дублей: каждая открытая картинка ровно один раз");
        }

        [Test]
        public void ФиналНаСвежемУстройствеЗакрываетНовеллуВСвёртке()
        {
            // На планшете новелла дочитана: точку снял финал, потолок остался.
            // Телефон, застрявший в середине, не должен «воскрешать» точку —
            // игрок закончил историю, и обе копии обязаны с этим согласиться.
            var телефон = Свёрток(1000);
            телефон["titles"][Ид] = Метка("c2", 2, 2, 1000);
            var планшет = Свёрток(2000);
            планшет["titles"][Ид] = Метка(null, null, 3, 2000);

            var слитый = Запись(ProgressVault.Merge(телефон, планшет), Ид);
            Assert.IsNull((string)слитый["cur"], "финал — тоже прогресс, и он свежее");
            Assert.AreEqual(3, (int)слитый["reached"]);
        }

        [Test]
        public void СтарыйФиналНеСнимаетЖивуюТочкуПовтора()
        {
            // Обратный случай: новелла давно пройдена на планшете, а на
            // телефоне игрок ПЕРЕИГРЫВАЕТ её и стоит в середине. Свежее — его
            // повтор; старый финал не имеет права выдёргивать закладку.
            var повтор = Свёрток(2000);
            повтор["titles"][Ид] = Метка("c2", 2, 3, 2000);
            var финал = Свёрток(1000);
            финал["titles"][Ид] = Метка(null, null, 3, 1000);

            Assert.AreEqual("c2", (string)Запись(ProgressVault.Merge(повтор, финал), Ид)["cur"],
                "живой повтор свежее старого финала — закладка остаётся");
        }

        [Test]
        public void НовеллаИгравшаясяНаОдномУстройствеПереезжаетЦеликом()
        {
            // На планшете играли то, чего телефон не открывал. При слиянии эта
            // новелла — не конфликт, а просто чужая половина жизни игрока.
            var телефон = Свёрток(2000);
            телефон["titles"][Ид] = Метка("c1", 1, 1, 2000);
            var планшет = Свёрток(1000);
            планшет["titles"][Вторая] = Метка("x2", 2, 2, 1000, "cg_x");

            var слитый = ProgressVault.Merge(телефон, планшет);
            Assert.IsNotNull(Запись(слитый, Ид), "своя новелла на месте");
            Assert.IsNotNull(Запись(слитый, Вторая), "чужая переехала целиком");
            Assert.AreEqual(2, (int)Запись(слитый, Вторая)["reached"]);
        }

        [Test]
        public void ИмяНепустоеСильнееПустогоАСпорРешаетСвежесть()
        {
            // Имя спрашивают один раз за установку. Пустая сторона не стирает
            // имя; если игрок называл себя по-разному, действует более позднее
            // решение — как и любая другая его последняя воля.
            Assert.AreEqual("Женя",
                (string)ProgressVault.Merge(Свёрток(2000), Свёрток(1000, "Женя"))["name"],
                "пустое не побеждает имя, даже будучи свежее");
            Assert.AreEqual("Вика",
                (string)ProgressVault.Merge(Свёрток(2000, "Вика"), Свёрток(1000, "Женя"))["name"],
                "два имени — берётся более позднее решение игрока");
            Assert.AreEqual("Вика",
                (string)ProgressVault.Merge(Свёрток(1000, "Женя"), Свёрток(2000, "Вика"))["name"],
                "и с обратным порядком аргументов — тоже позднее");
        }

        [Test]
        public void ГардеробныеОсиСкладываютсяАСпорнаяДостаётсяСвежемуУстройству()
        {
            // Надетое — предпочтение, как настройка: правильный ответ «как
            // игрок одел её В ПОСЛЕДНИЙ РАЗ». Но ось, тронутая только на одном
            // устройстве, не снимается — доливается.
            var телефон = Свёрток(1000);
            телефон["wardrobe"][Героиня] = new JObject
            {
                ["worn"] = new JObject { ["dress"] = "casual", ["hat"] = "red" },
                ["at"] = 1000,
            };
            var планшет = Свёрток(2000);
            планшет["wardrobe"][Героиня] = new JObject
            {
                ["worn"] = new JObject { ["dress"] = "gala" },
                ["at"] = 2000,
            };

            var worn = ProgressVault.Merge(телефон, планшет)["wardrobe"]?[Героиня]?["worn"] as JObject;
            Assert.AreEqual("gala", (string)worn?["dress"], "спорную ось решает свежесть");
            Assert.AreEqual("red", (string)worn?["hat"], "нетронутая на свежей стороне ось не снимается");
        }

        [Test]
        public void ВстреченныеНарядыОбъединяются()
        {
            // «Встреченное» — прогрессия по нарядам, как галерея: сюжет уже
            // показал вещь, второго знакомства не будет.
            var а = Свёрток(2000);
            а["wardrobe"][Героиня] = new JObject
            { ["seen"] = new JObject { ["dress"] = new JArray("casual") }, ["at"] = 2000 };
            var б = Свёрток(1000);
            б["wardrobe"][Героиня] = new JObject
            { ["seen"] = new JObject { ["dress"] = new JArray("gala"), ["hat"] = new JArray("red") }, ["at"] = 1000 };

            var seen = ProgressVault.Merge(а, б)["wardrobe"]?[Героиня]?["seen"] as JObject;
            CollectionAssert.AreEquivalent(new[] { "casual", "gala" },
                (seen?["dress"] as JArray)?.ToObject<string[]>(),
                "ось, встреченная на обоих устройствах, — объединение");
            CollectionAssert.AreEquivalent(new[] { "red" },
                (seen?["hat"] as JArray)?.ToObject<string[]>(),
                "ось с одного устройства переезжает");
        }

        [Test]
        public void НезнакомоеПолеСвёрткаПереживаетСлияние()
        {
            // Правило «свёрток не забывает» действует и между устройствами:
            // поле из будущей схемы, которого эта сборка не знает, — чей-то
            // прогресс, и слияние обязано его пронести.
            var старая = Свёрток(2000);
            var новая = Свёрток(1000);
            новая["achievements"] = new JObject { ["first_kiss"] = true };

            Assert.IsNotNull(ProgressVault.Merge(старая, новая)["achievements"],
                "незнакомое не выбрасывается — его дом появится в следующей сборке");
        }

        // ════════════════════════════════════════════════════════════════════
        // Впитывание чужого свёртка в живые сторы (подъём не-чистого устройства)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Файловый сейф этого устройства: маркер со штампом — так его
        /// оставил бы Collect после последнего хода прогресса.</summary>
        private static void СейфУстройства(long at, JObject метка)
        {
            var св = Свёрток(at);
            св["titles"][Ид] = метка;
            ProgressVault.WriteLocal(св);
        }

        [Test]
        public void СвежееЧужоеПрохождениеДвигаетЖивуюТочкуВперёд()
        {
            // Вечером играл на планшете до третьей главы; утром открыл телефон,
            // где стоял на первой. «Продолжить» обязано вести в третью — иначе
            // игрок перечитывает главу и решает, что прогресс пропал.
            var т = Три();
            LvnProgress.StartChapter(т, т.ChaptersOf()[0]);
            СейфУстройства(1000, Метка("c1", 1, 1, 1000));

            var планшет = Свёрток(2000);
            планшет["titles"][Ид] = Метка("c3", 3, 3, 2000);
            ProgressVault.Absorb(планшет, Манифест(т));

            Assert.AreEqual("c3", LvnProgress.Current(т)?.id, "точка догнала планшет");
            Assert.AreEqual(3, LvnProgress.Reached(т), "и потолок тоже");
        }

        [Test]
        public void СтароеЧужоеУстройствоНеОткатываетЖивуюТочку()
        {
            // Планшет, лежавший неделю, не имеет права вернуть игрока в
            // прошлое: свежесть у живой точки выше.
            var т = Три();
            LvnProgress.StartChapter(т, т.ChaptersOf()[2]);
            СейфУстройства(2000, Метка("c3", 3, 3, 2000));

            var планшет = Свёрток(1000);
            планшет["titles"][Ид] = Метка("c1", 1, 1, 1000);
            ProgressVault.Absorb(планшет, Манифест(т));

            Assert.AreEqual("c3", LvnProgress.Current(т)?.id, "живая точка не тронута");
        }

        [Test]
        public void ПотолокИГалереяДоливаютсяДажеОтОтставшегоУстройства()
        {
            // Даже когда чужая копия старее, её НАКОПИТЕЛЬНАЯ часть — правда:
            // до пятой главы игрок доходил, картинку открывал. Отстающий
            // штамп — не повод забыть то, что уже случилось.
            var т = Три();
            LvnProgress.StartChapter(т, т.ChaptersOf()[1]);
            СейфУстройства(2000, Метка("c2", 2, 2, 2000));

            var планшет = Свёрток(1000);
            планшет["titles"][Ид] = Метка("c1", 1, 3, 1000, "cg_tablet");
            ProgressVault.Absorb(планшет, Манифест(т));

            Assert.AreEqual("c2", LvnProgress.Current(т)?.id, "точка осталась своя, свежая");
            Assert.AreEqual(3, LvnProgress.Reached(т), "потолок долился");
            Assert.IsTrue(LvnGalleryStore.IsUnlocked(Ид, "cg_tablet"), "картинка долилась");
        }

        [Test]
        public void ФиналСДругогоУстройстваЗакрываетНовеллуИЗдесь()
        {
            // Игрок ДОЧИТАЛ историю на планшете. Телефон, застрявший в
            // середине, обязан признать финал: карточка «пройдена», повтор — с
            // начала. Держать старую закладку значит врать игроку, что финала
            // не было.
            var т = Три();
            LvnProgress.StartChapter(т, т.ChaptersOf()[1]);
            СейфУстройства(1000, Метка("c2", 2, 2, 1000));

            var планшет = Свёрток(2000);
            планшет["titles"][Ид] = Метка(null, null, 3, 2000);
            ProgressVault.Absorb(планшет, Манифест(т));

            Assert.IsNull(LvnProgress.Current(т), "точку снял чужой финал");
            Assert.IsTrue(LvnProgress.Finished(т), "новелла пройдена и на этом устройстве");
        }

        [Test]
        public void ЧужаяСвежаяОдеждаПереодеваетГероинюИЗдесь()
        {
            // Игрок переодел героиню на планшете — на телефоне она обязана
            // выйти в том же. Наряд — последняя воля игрока, а не устройства.
            LvnWardrobe.Equip(Героиня, "dress", "casual");
            var сейф = Свёрток(1000);
            сейф["wardrobe"][Героиня] = new JObject
            { ["worn"] = new JObject { ["dress"] = "casual" }, ["at"] = 1000 };
            ProgressVault.WriteLocal(сейф);

            var планшет = Свёрток(2000);
            планшет["wardrobe"][Героиня] = new JObject
            { ["worn"] = new JObject { ["dress"] = "gala" }, ["at"] = 2000 };
            ProgressVault.Absorb(планшет, Манифест(Три()));

            Assert.AreEqual("gala", LvnWardrobe.Equipped(Героиня)["dress"],
                "свежее решение игрока победило, на каком бы устройстве он его ни принял");
        }

        [Test]
        public void СтараяОдеждаНеПереодеваетСвежую()
        {
            LvnWardrobe.Equip(Героиня, "dress", "gala");
            var сейф = Свёрток(2000);
            сейф["wardrobe"][Героиня] = new JObject
            { ["worn"] = new JObject { ["dress"] = "gala" }, ["at"] = 2000 };
            ProgressVault.WriteLocal(сейф);

            var планшет = Свёрток(1000);
            планшет["wardrobe"][Героиня] = new JObject
            { ["worn"] = new JObject { ["dress"] = "casual" }, ["at"] = 1000 };
            ProgressVault.Absorb(планшет, Манифест(Три()));

            Assert.AreEqual("gala", LvnWardrobe.Equipped(Героиня)["dress"],
                "лежавший планшет не переодевает героиню задним числом");
        }

        // ════════════════════════════════════════════════════════════════════
        // Переменные истории: сверка при загрузке — тем же пополевым правилом,
        // что и конфликт при записи
        // ════════════════════════════════════════════════════════════════════

        private static JObject Док(string vars, string updatedAt)
            => new JObject { ["vars"] = JObject.Parse(vars), ["updatedAt"] = updatedAt };

        [Test]
        public void ОфлайновыеПравкиПереживаютЧужойСвежийДокумент()
        {
            // Телефон играл в самолёте: статы записаны локально, PUT не ушёл.
            // Планшет тем временем писал на сервер. Первая же ОНЛАЙН-ЗАГРУЗКА
            // раньше брала «новее целиком» — и офлайн-сессия исчезала, хотя
            // для конфликта ЗАПИСИ пополевое слияние давно существовало.
            var база = JObject.Parse(@"{""gold"":10,""bond"":1}");
            var локальный = Док(@"{""gold"":10,""bond"":5}", "2026-08-30T10:00:00Z");
            var серверный = Док(@"{""gold"":99,""bond"":1}", "2026-08-31T10:00:00Z");

            var vars = LocalStateStore.Vars(HttpStateStore.Reconcile(серверный, локальный, база, null));
            Assert.AreEqual(99, (int)vars["gold"], "чужой заработок не потерян");
            Assert.AreEqual(5, (int)vars["bond"], "своя офлайн-сессия не потеряна");
        }

        [Test]
        public void БезБазыСверкаПрежняяНовееПобеждаетЦеликом()
        {
            // Свежая установка без базы синхронизации: сервер — единственная
            // правда, и перекрывать его локальными нулями нельзя.
            var локальный = Док(@"{""gold"":0}", "2026-08-30T10:00:00Z");
            var серверный = Док(@"{""gold"":99}", "2026-08-31T10:00:00Z");

            var vars = LocalStateStore.Vars(HttpStateStore.Reconcile(серверный, локальный, null, null));
            Assert.AreEqual(99, (int)vars["gold"], "без базы действует прежнее «новее побеждает»");
        }

        [Test]
        public void ОбластьСоСвоимПравиломСливаетсяИменноИм()
        {
            // Свёрток прогресса — не плоский набор статов: пополевое слияние
            // видит в нём пять ключей и отдаёт «titles» целиком одной стороне.
            // Область с зарегистрированным правилом сливается своим правилом,
            // какие бы штампы ни стояли на документах.
            JObject правило(JObject мои, JObject чужие)
            {
                var р = (JObject)чужие.DeepClone();
                foreach (var p in мои.Properties()) р[p.Name] = p.Value.DeepClone();
                р["merged"] = true;
                return р;
            }

            var локальный = Док(@"{""a"":1}", "2026-08-30T10:00:00Z");
            var серверный = Док(@"{""b"":2}", "2026-08-31T10:00:00Z");

            var vars = LocalStateStore.Vars(HttpStateStore.Reconcile(серверный, локальный, null, правило));
            Assert.IsTrue((bool)vars["merged"], "работало именно правило области");
            Assert.AreEqual(1, (int)vars["a"]);
            Assert.AreEqual(2, (int)vars["b"], "обе стороны в деле — ни одна не затёрта целиком");
        }
    }
}
