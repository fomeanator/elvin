using System.Collections.Generic;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Привратник: что игроку открыто. Правило «первая всегда, дальше по
    /// дошедшему» стояло дословно дважды, «первая глава» — тремя способами, и
    /// расхождение видно на живом экране: карусель пускает, карточка нет.</summary>
    public class GatekeeperTests
    {
        private static LvnTitle Title(params int[] numbers)
        {
            var chapters = new List<LvnChapter>();
            foreach (var n in numbers) chapters.Add(new LvnChapter { id = "ch" + n, number = n });
            return new LvnTitle { id = "t", seasons = new List<LvnSeason> { new LvnSeason { chapters = chapters } } };
        }

        // ── какая глава открыта ──

        [Test]
        public void ПерваяГлаваОткрытаВсегда()
        {
            // Иначе новеллу нельзя начать вовсе.
            Assert.IsTrue(LvnGatekeeper.ChapterOpen(1, reached: 0, firstNumber: 1));
        }

        [Test]
        public void НомерПервойЗадаётАвторАНеЕдиница()
        {
            // Пилот с номером 0 — тоже начало новеллы.
            Assert.IsTrue(LvnGatekeeper.ChapterOpen(0, reached: -1, firstNumber: 0));
            Assert.IsFalse(LvnGatekeeper.ChapterOpen(1, reached: 0, firstNumber: 0),
                "первая — только одна: следующая ждёт, пока до неё дойдут");
        }

        [Test]
        public void ОткрытоДоДошедшейВключительно()
        {
            Assert.IsTrue(LvnGatekeeper.ChapterOpen(3, reached: 3, firstNumber: 1), "текущая глава открыта");
            Assert.IsTrue(LvnGatekeeper.ChapterOpen(2, reached: 3, firstNumber: 1), "пройденную можно переиграть");
            Assert.IsFalse(LvnGatekeeper.ChapterOpen(4, reached: 3, firstNumber: 1), "следующая закрыта");
        }

        [Test]
        public void ПустойГлавыНеБывает()
        {
            Assert.IsFalse(LvnGatekeeper.ChapterOpen((LvnChapter)null, 5, 1), "null не открывают");
        }

        [Test]
        public void ГлаваЦеликомСпрашиваетсяТемЖеПравилом()
        {
            var c = new LvnChapter { id = "ch2", number = 2 };
            Assert.IsFalse(LvnGatekeeper.ChapterOpen(c, reached: 1, firstNumber: 1));
            Assert.IsTrue(LvnGatekeeper.ChapterOpen(c, reached: 2, firstNumber: 1));
        }

        // ── какая глава первая ──

        [Test]
        public void ПерваяЭтоНаименьшийНомерАНеПерваяВСписке()
        {
            // Порядок в файле — случайность формата, номер — авторский замысел.
            var t = Title(3, 1, 2);
            Assert.AreEqual(1, LvnGatekeeper.First(t).number);
            Assert.AreEqual(1, LvnGatekeeper.FirstNumber(t));
        }

        [Test]
        public void ПерваяИщетсяПоВсемСезонам()
        {
            var t = new LvnTitle
            {
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter> { new LvnChapter { number = 5 } } },
                    new LvnSeason { chapters = new List<LvnChapter> { new LvnChapter { number = 2 } } },
                }
            };
            Assert.AreEqual(2, LvnGatekeeper.FirstNumber(t));
        }

        [Test]
        public void НовеллаБезГлавНеРонаетЭкран()
        {
            Assert.IsNull(LvnGatekeeper.First(null));
            Assert.AreEqual(0, LvnGatekeeper.FirstNumber(null), "ноль, если глав нет");
            Assert.IsNull(LvnGatekeeper.First(new LvnTitle()));
            Assert.AreEqual(0, LvnGatekeeper.FirstNumber(new LvnTitle { seasons = new List<LvnSeason>() }));
        }

        [Test]
        public void ДыркиВСписоЧнойСтруктуреПропускаются()
        {
            var t = new LvnTitle
            {
                seasons = new List<LvnSeason> { null, new LvnSeason(), new LvnSeason
                    { chapters = new List<LvnChapter> { null, new LvnChapter { number = 4 } } } }
            };
            Assert.AreEqual(4, LvnGatekeeper.FirstNumber(t), "битый манифест не повод падать");
        }

        // ── открыта ли новелла ──

        [Test]
        public void БезУсловияНовеллаОткрыта()
        {
            Assert.IsFalse(LvnGatekeeper.TitleLocked(null, null));
            Assert.IsFalse(LvnGatekeeper.TitleLocked(new LvnTitle(), null));
            Assert.IsFalse(LvnGatekeeper.TitleLocked(new LvnTitle { unlock = "" }, null));
        }

        [Test]
        public void УсловиеЧитаетсяНадКроссНовелльнымиСтатами()
        {
            var t = new LvnTitle { unlock = LvnGlobalStats.VarName + ".exp_1_done" };
            Assert.IsTrue(LvnGatekeeper.TitleLocked(t, new JObject()), "флага нет — закрыто");
            Assert.IsFalse(LvnGatekeeper.TitleLocked(t, new JObject { ["exp_1_done"] = true }));
        }

        [Test]
        public void СравнениеЧиселВУсловииРаботает()
        {
            var t = new LvnTitle { unlock = LvnGlobalStats.VarName + ".rep >= 5" };
            Assert.IsTrue(LvnGatekeeper.TitleLocked(t, new JObject { ["rep"] = 4 }));
            Assert.IsFalse(LvnGatekeeper.TitleLocked(t, new JObject { ["rep"] = 5 }));
        }

        [Test]
        public void СломанноеВыражениеНеЗакрываетИгру()
        {
            // Опечатка автора не должна превращаться в стену для игрока.
            var t = new LvnTitle { unlock = "&& ) кривое ((" };
            Assert.IsFalse(LvnGatekeeper.TitleLocked(t, new JObject()));
        }

        [Test]
        public void ОтсутствующиеСтатыНеРонаютРазбор()
        {
            var t = new LvnTitle { unlock = LvnGlobalStats.VarName + ".exp_1_done" };
            Assert.DoesNotThrow(() => LvnGatekeeper.TitleLocked(t, null),
                "статы могли ещё не доехать с сервера — экран рисуется и без них");
        }
    }
}
