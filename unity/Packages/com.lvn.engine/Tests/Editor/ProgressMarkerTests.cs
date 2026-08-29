using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ТОЧКА ПРОДОЛЖЕНИЯ — где игрок остановился и что считается пройденным.
    ///
    /// <para>Здесь проверяется то, чем этот дом расплачивался в жизни: новелла с
    /// первой главой под номером 0 объявлялась пройденной на ЧИСТОМ устройстве, а
    /// переименование глав при переимпорте отбирало у игрока прохождение
    /// целиком.</para>
    /// </summary>
    public sealed class ProgressMarkerTests
    {
        private const string Id = "t_progress_marker";

        private static LvnTitle Title(params (string id, int number)[] chapters)
        {
            var list = new List<LvnChapter>();
            foreach (var (id, number) in chapters) list.Add(new LvnChapter { id = id, number = number });
            return new LvnTitle { id = Id, seasons = new List<LvnSeason> { new LvnSeason { chapters = list } } };
        }

        private static LvnTitle Three() => Title(("c1", 1), ("c2", 2), ("c3", 3));

        [SetUp]
        [TearDown]
        public void Clean() => LvnProgress.ResetTitle(Id);

        // ── непочатая ──

        [Test]
        public void НепочатаяНовеллаНеПройдена()
        {
            // Живой дефект: первая глава под номером 0 давала «дошёл до 0» ≥
            // «последняя 0», и воронка не включалась ни разу.
            var pilot = Title(("pilot", 0));
            Assert.IsFalse(LvnProgress.Finished(pilot));
            Assert.IsFalse(LvnProgress.Finished(Three()));
        }

        [Test]
        public void НовеллаБезГлавНеПройденаИНеСчитается()
        {
            var empty = new LvnTitle { id = Id, seasons = new List<LvnSeason>() };
            Assert.IsFalse(LvnProgress.Finished(empty));
            Assert.AreEqual(0, LvnProgress.Done(empty));
            Assert.AreEqual(0f, LvnProgress.Fraction(empty));
        }

        [Test]
        public void НаЧистомУстройствеПродолжатьНечего()
        {
            Assert.IsNull(LvnProgress.Current(Three()));
            Assert.AreEqual(0, LvnProgress.Reached(Three()));
            Assert.IsNull(LvnProgress.Current(null), "новеллы нет — и точки нет");
            Assert.AreEqual(0, LvnProgress.Reached(null));
        }

        // ── движение точки ──

        [Test]
        public void ДостигнутоеТолькоРастёт()
        {
            var t = Three();
            LvnProgress.StartChapter(t, t.ChaptersOf()[2]);
            Assert.AreEqual(3, LvnProgress.Reached(t));

            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);
            Assert.AreEqual(3, LvnProgress.Reached(t),
                "переигранная ранняя глава не имеет права снова запереть поздние");
            Assert.AreEqual("c1", LvnProgress.Current(t).id, "а точка продолжения переезжает свободно");
        }

        [Test]
        public void ФиналБезСледующейСнимаетТочкуНоНеДостигнутое()
        {
            var t = Three();
            LvnProgress.StartChapter(t, t.ChaptersOf()[2]);
            LvnProgress.FinishChapter(t, null);

            Assert.IsNull(LvnProgress.Current(t), "повтор начнётся с начала");
            Assert.AreEqual(3, LvnProgress.Reached(t), "список глав остаётся открытым");
            Assert.IsTrue(LvnProgress.Finished(t));
        }

        [Test]
        public void ФиналСоСледующейПереводитТочкуВперёд()
        {
            // Прогресс двигает ИМЕННО ФИНАЛ: выход через меню конца главы
            // раньше оставлял точку на пройденной, и «Играть» её переигрывал.
            var t = Three();
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);
            LvnProgress.FinishChapter(t, t.ChaptersOf()[1]);

            Assert.AreEqual("c2", LvnProgress.Current(t).id);
            Assert.AreEqual(2, LvnProgress.Reached(t));
        }

        [Test]
        public void ПереименованиеГлавНеОтбираетПрохождение()
        {
            // Переимпорт сменил идентификаторы — позиция игрока принадлежит ему,
            // идентификаторы наши.
            var before = Three();
            LvnProgress.StartChapter(before, before.ChaptersOf()[1]);

            var after = Title(("ep_one", 1), ("ep_two", 2), ("ep_three", 3));
            var recovered = LvnProgress.Current(after);

            Assert.IsNotNull(recovered, "прохождение потеряно из-за переименования");
            Assert.AreEqual(2, recovered.number);
            Assert.AreEqual("ep_two", LvnProgress.Current(after).id, "метка вылечена на месте");
        }

        [Test]
        public void СовсемЧужойМанифестНеВыдумываетПозицию()
        {
            var before = Three();
            LvnProgress.StartChapter(before, before.ChaptersOf()[1]);

            var other = Title(("x", 77));
            Assert.IsNull(LvnProgress.Current(other), "восстанавливать нечего — начинаем чисто");
        }

        // ── отложенный перезапуск ──

        [Test]
        public void ПросьбаПереигратьСтавитФлагИПереводитТочку()
        {
            var t = Three();
            LvnProgress.RestartChapter(t, t.ChaptersOf()[1]);

            Assert.AreEqual("c2", LvnProgress.Current(t).id);
            Assert.AreEqual("c2", LvnProgress.PendingRestart(Id));
        }

        [Test]
        public void СознательныйВыборГлавыСильнееОтложенногоПерезапуска()
        {
            var t = Three();
            LvnProgress.RestartChapter(t, t.ChaptersOf()[1]);
            LvnProgress.ChooseChapter(t, t.ChaptersOf()[0]);

            Assert.AreEqual("", LvnProgress.PendingRestart(Id),
                "иначе выбранная глава села бы на чужой чекпойнт");
            Assert.AreEqual("c1", LvnProgress.Current(t).id);
        }

        [Test]
        public void ПодсмотретьЗапросНеЗначитЕгоПотратить()
        {
            LvnProgress.RequestRestart(Id, "c2");
            Assert.AreEqual("c2", LvnProgress.PendingRestart(Id));
            Assert.AreEqual("c2", LvnProgress.PendingRestart(Id), "подглядывание не одноразовое");
        }

        [Test]
        public void ЗалежавшийсяЗапросГаснетНаПервомЖеВходе()
        {
            // Иначе неудачная загрузка оставляет флаг, и он срабатывает на
            // ПОСТОРОННЕЙ главе через полчаса.
            LvnProgress.RequestRestart(Id, "c2");
            Assert.IsFalse(LvnProgress.TakeRestart(Id, "c3"), "вошла не та глава — перезапуска нет");
            Assert.AreEqual("", LvnProgress.PendingRestart(Id), "но запрос всё равно погашен");
        }

        [Test]
        public void ЗапросСрабатываетРовноОдинРаз()
        {
            LvnProgress.RequestRestart(Id, "c2");
            Assert.IsTrue(LvnProgress.TakeRestart(Id, "c2"));
            Assert.IsFalse(LvnProgress.TakeRestart(Id, "c2"), "второй вход — уже не перезапуск");
        }

        [Test]
        public void БезЗапросаПерезапускаНет()
        {
            Assert.IsFalse(LvnProgress.TakeRestart(Id, "c2"));
        }

        // ── чекпойнты входа в главу ──

        [Test]
        public void ЧекпойнтВозвращаетПеременныеВходаАНеНакопленные()
        {
            // Статы из будущего, протёкшие в прошлое, перекосили бы гейты выборов.
            LvnProgress.SaveCheckpoint(Id, "c2", new JObject { ["rep"] = 3 });
            var back = LvnProgress.Checkpoint(Id, "c2");

            Assert.IsNotNull(back);
            Assert.AreEqual(3, (int)back["rep"]);
        }

        [Test]
        public void ЧекпойнтыГлавНеПерепутываются()
        {
            LvnProgress.SaveCheckpoint(Id, "c1", new JObject { ["rep"] = 1 });
            LvnProgress.SaveCheckpoint(Id, "c2", new JObject { ["rep"] = 2 });

            Assert.AreEqual(1, (int)LvnProgress.Checkpoint(Id, "c1")["rep"]);
            Assert.AreEqual(2, (int)LvnProgress.Checkpoint(Id, "c2")["rep"]);
        }

        [Test]
        public void НепосещённаяГлаваЧекпойнтаНеИмеет()
        {
            Assert.IsNull(LvnProgress.Checkpoint(Id, "c9"), "посева нет — начнём с пустого");
            Assert.IsNull(LvnProgress.Checkpoint(Id, null));
        }

        [Test]
        public void ЧекпойнтБезИмениГлавыНеПишется()
        {
            Assert.DoesNotThrow(() => LvnProgress.SaveCheckpoint(Id, null, new JObject()));
            Assert.DoesNotThrow(() => LvnProgress.SaveCheckpoint(Id, "c1", null),
                "чекпойнт — удобство, а не обязательство: падать он не имеет права");
        }

        // ── полный сброс ──

        [Test]
        public void СбросНовеллыЗабываетВсёСразу()
        {
            var t = Three();
            LvnProgress.StartChapter(t, t.ChaptersOf()[2]);
            LvnProgress.SaveCheckpoint(Id, "c3", new JObject { ["rep"] = 5 });
            LvnProgress.RequestRestart(Id, "c3");

            LvnProgress.ResetTitle(Id);

            Assert.IsNull(LvnProgress.Current(t));
            Assert.AreEqual(0, LvnProgress.Reached(t));
            Assert.IsNull(LvnProgress.Checkpoint(Id, "c3"));
            Assert.AreEqual("", LvnProgress.PendingRestart(Id));
        }

        [Test]
        public void ВосстановлениеИзВолтаНеПонижаетДостигнутое()
        {
            var t = Three();
            LvnProgress.StartChapter(t, t.ChaptersOf()[2]);
            LvnProgress.RestoreMarker(Id, "c1", number: 1, reached: 1);

            Assert.AreEqual(3, LvnProgress.Reached(t),
                "старый бэкап не должен отбирать уже открытые главы");
            Assert.AreEqual("c1", LvnProgress.Current(t).id, "а точку он переносит");
        }

        [Test]
        public void ВосстановлениеБезНовеллыНичегоНеТрогает()
        {
            Assert.DoesNotThrow(() => LvnProgress.RestoreMarker(null, "c1", 1, 1));
            Assert.DoesNotThrow(() => LvnProgress.RestoreMarker("", "c1", 1, 1));
        }
    }
}
