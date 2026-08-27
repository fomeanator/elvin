using System.Text;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОСА ПРОГРЕССА ОБЯЗАНА ГОВОРИТЬ ПРАВДУ.
    ///
    /// <para>Считать её от номера команды в файле нельзя: импорт линеаризует
    /// ветки, и тела выборов лежат в ХВОСТЕ. В живой главе Time Romance
    /// (<c>cold-ch08</c>) спина кончается на 1847-й команде из 2295 — пройдя
    /// главу целиком, игрок видел 80% и рывок на 100%. Это и сообщил партнёр.
    /// Прежняя защита (высшая отметка + учёт «отлучек» в хвост) лечила спайк,
    /// но не потолок, и вдобавок откатывала полосу назад на дальнем прыжке.</para>
    ///
    /// <para>Правило: доля = пройдено / (пройдено + осталось-до-конца), где
    /// пройдено — реальные шаги ЭТОГО маршрута, а осталось — кратчайший путь
    /// до конца (BFS). Отсюда четыре обещания, и каждое закреплено ниже:
    /// в конце ровно сто любым маршрутом; назад никогда; длинная ветка отдаёт
    /// проценты медленнее; и полоса НЕ ЗАМИРАЕТ — прежняя формула «по
    /// кратчайшему» прибивала её к магистрали, и в длинной ветке игрок
    /// смотрел на мёртвые 80% («дальше 80 не проходит»).</para>
    /// </summary>
    public class ProgressBarTruthTests
    {
        private sealed class NullStage : ILvnStage
        {
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(System.Collections.Generic.IReadOnlyList<LvnOption> options) { }
            // Подписанная дверь: заглушке различать отправителей незачем —
            // она просто записывает команду, как и раньше.
            public void ApplyStage(JObject c, Lvn.LvnSender sender) => ApplyStage(c);

            public void ApplyStage(JObject c) { }
            public void OnEnd() { }
        }

        // A cold-shaped script: a short spine whose choice jumps into a body
        // placed at the far tail (padded well past the FarJump window), which
        // returns to the spine. Tail padding sits after __tail so it is never
        // executed — it only stretches the file the way linearization does.
        private static string TailBodyScript(int pad = 900)
        {
            var sb = new StringBuilder();
            sb.Append(@"{""script"":[
                {""op"":""say"",""text"":""intro""},
                {""op"":""choice"",""options"":[
                    {""text"":""dive"",""goto"":""body""}
                ]},
                {""op"":""label"",""id"":""back""},
                {""op"":""say"",""text"":""spine again""},
                {""op"":""say"",""text"":""spine tail""},
                {""op"":""goto"",""label"":""__end""},");
            for (int i = 0; i < pad; i++)
                sb.Append(@"{""op"":""label"",""id"":""pad" + i + @"""},");
            sb.Append(@"
                {""op"":""label"",""id"":""body""},
                {""op"":""say"",""text"":""inside the body""},
                {""op"":""say"",""text"":""still inside""},
                {""op"":""goto"",""label"":""back""}
            ]}");
            return sb.ToString();
        }

        private static int Pct(LvnPlayer p) => Content.Percent.Value(p.ProgressIndex, p.ProgressTotal);

        [Test]
        public void ChapterEndsAtExactlyOneHundred_NoMatterHowLongTheFileTail()
        {
            // Хвост в 900 команд — ровно то, что делает линеаризация импорта.
            var p = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            int guard = 0;
            p.Advance();
            while (!p.Finished && guard++ < 50)
            {
                if (!p.Finished && p.AtChoice) p.Choose(0);
                else p.Advance();
            }
            Assert.IsTrue(p.Finished, "sanity: глава доиграна");
            Assert.AreEqual(100, Pct(p),
                "в конце главы полоса обязана быть ровно на ста — иначе будет рывок с 80 на 100");
        }

        [Test]
        public void TailBodyIsRealProgress_NotAFrozenBar()
        {
            var p = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            p.Advance();                       // intro (say pauses; choice shows with it)
            int atChoice = Pct(p);
            Assert.Less(atChoice, 40, "начало главы не может читаться как её середина");

            p.Choose(0);                       // «телепорт» в хвост файла
            p.Advance();                       // "inside the body"
            Assert.GreaterOrEqual(Pct(p), atChoice,
                "реплика внутри тела — такой же шаг истории, назад полоса не ходит");
            Assert.Less(Pct(p), 100, "и это ещё не конец главы");
        }

        [Test]
        public void LongerBranchJustEarnsPercentsSlower()
        {
            // Две ветки к одному финалу: короткая в один шаг, длинная в три.
            const string json = @"{""script"":[
                {""op"":""say"",""text"":""развилка""},
                {""op"":""choice"",""options"":[
                    {""text"":""коротко"",""goto"":""short""},
                    {""text"":""длинно"",""goto"":""long""}
                ]},
                {""op"":""label"",""id"":""short""},
                {""op"":""say"",""text"":""финал""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""long""},
                {""op"":""say"",""text"":""раз""},
                {""op"":""say"",""text"":""два""},
                {""op"":""say"",""text"":""три""},
                {""op"":""goto"",""label"":""short""},
                {""op"":""label"",""id"":""__end""}
            ]}";
            var quick = new LvnPlayer(LvnDocument.Parse(json), new NullStage());
            quick.Advance(); quick.Choose(0); quick.Advance();
            var slow = new LvnPlayer(LvnDocument.Parse(json), new NullStage());
            slow.Advance(); slow.Choose(1); slow.Advance();

            Assert.Greater(Pct(quick), Pct(slow),
                "после одного шага короткая ветка обязана быть ближе к концу, чем длинная");

            int guard = 0;
            while (!slow.Finished && guard++ < 20) slow.Advance();
            Assert.IsTrue(slow.Finished);
            Assert.AreEqual(100, Pct(slow), "длинная ветка тоже кончается ровно на ста");
        }

        /// <summary>Регрессия «дальше 80 не проходит»: внутри ветки длиннее
        /// кратчайшей полоса обязана расти на каждом такте, а не стоять до
        /// слияния с магистралью.</summary>
        [Test]
        public void InsideALongTailBody_TheBarKeepsRising()
        {
            var p = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            p.Advance();
            p.Choose(0);
            p.Advance();                       // "inside the body"
            int a = p.ProgressIndex;
            p.Advance();                       // "still inside"
            int b = p.ProgressIndex;
            Assert.Greater(b, a,
                "полоса замерла внутри длинной ветки — игрок смотрит на мёртвый процент");
        }

        [Test]
        public void ResumeInsideATailBody_ReadsHonestlyAtOnce()
        {
            // Сейв, снятый ВНУТРИ тела выбора, лежит физически в конце файла.
            // Прежний счёт по номеру команды показывал такому игроку ~99% и
            // «лечил» это только на выходе. Расстояние до конца врать негде.
            var doc = LvnDocument.Parse(TailBodyScript());
            var live = new LvnPlayer(doc, new NullStage());
            live.Advance();
            live.Choose(0);
            live.Advance();                    // пауза внутри тела (хвост файла)
            var snap = live.Save();

            var resumed = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            resumed.Restore(snap);
            Assert.Less(Pct(resumed), 90,
                "возобновление внутри тела не должно читаться как «глава почти пройдена»");
        }

        [Test]
        public void HubLoop_NeverRollsTheBarBack()
        {
            // Возврат в хаб — обычная форма импортированной главы. Курсор при
            // этом уезжает НАЗАД по файлу; полоса не имеет права.
            const string json = @"{""script"":[
                {""op"":""label"",""id"":""hub""},
                {""op"":""say"",""text"":""хаб""},
                {""op"":""set"",""key"":""n"",""value"":1,""default"":true},
                {""op"":""if"",""expr"":""n >= 2"",""then"":""out""},
                {""op"":""inc"",""key"":""n""},
                {""op"":""say"",""text"":""круг""},
                {""op"":""goto"",""label"":""hub""},
                {""op"":""label"",""id"":""out""},
                {""op"":""say"",""text"":""наружу""}
            ]}";
            var p = new LvnPlayer(LvnDocument.Parse(json), new NullStage());
            int last = 0, guard = 0;
            p.Advance();
            while (!p.Finished && guard++ < 30)
            {
                Assert.GreaterOrEqual(Pct(p), last, "полоса откатилась назад на возврате в хаб");
                last = Pct(p);
                p.Advance();
            }
            Assert.IsTrue(p.Finished, "sanity: глава доиграна");
            Assert.AreEqual(100, Pct(p));
        }

        [Test]
        public void NearBranches_KeepTheClassicClimb()
        {
            // an inline (near) skip must keep raising the bar exactly as before
            var json = @"{""script"":[
                {""op"":""say"",""text"":""one""},
                {""op"":""set"",""key"":""flag"",""value"":true},
                {""op"":""if"",""expr"":""flag"",""then"":""skip""},
                {""op"":""say"",""text"":""never""},
                {""op"":""label"",""id"":""skip""},
                {""op"":""say"",""text"":""two""},
                {""op"":""say"",""text"":""three""}
            ]}";
            var p = new LvnPlayer(LvnDocument.Parse(json), new NullStage());
            int last = -1;
            p.Advance();
            int guard = 0;
            while (!p.Finished && guard++ < 20)
            {
                Assert.GreaterOrEqual(p.ProgressIndex, last, "near flow stays monotonic");
                last = p.ProgressIndex;
                p.Advance();
            }
            Assert.IsTrue(p.Finished, "sanity: the walk completed");
            Assert.Greater(last, 4, "the bar climbed past the skip");
        }
    }
}
