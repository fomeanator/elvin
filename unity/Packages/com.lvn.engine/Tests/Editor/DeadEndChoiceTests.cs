using System.Collections.Generic;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВЫБОР БЕЗ ЕДИНОГО ДОСТУПНОГО ВАРИАНТА НЕ ОСТАНАВЛИВАЕТ ИГРУ.
    ///
    /// <para>Условие с натуры: автор закрыл каждый вариант порогом стата
    /// («Взломать» — ловкость 5, «Выбить» — сила 5), а игрок пришёл сюда с
    /// нулями. Показывать нечего. Замер 05.09 показал, что происходило: плеер
    /// показывал пустую стопку и ждал выбора, которого игрок сделать не может
    /// — <c>choice:0</c>, <c>AtChoice=true</c>, и каждый следующий тап снова
    /// <c>choice:0</c>. Глава стояла навсегда; выход — только через меню.</para>
    ///
    /// <para>Теперь курсор идёт дальше, автор узнаёт об этом строкой в журнале,
    /// числом в телеметрии (написано N, показано 0) и предупреждением
    /// <c>lvnconv validate</c> ещё на публикации. Тот же закон записан в
    /// браузерном плеере и в корпусе сверки (<c>39-choice-all-gated</c>).</para>
    /// </summary>
    public class DeadEndChoiceTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<string> Events = new List<string>();
            public void ShowSay(string who, string text, string style) => Events.Add("say:" + text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Events.Add("choice:" + (options?.Count ?? -1));
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command, Lvn.LvnSender sender) => ApplyStage(command);
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() => Events.Add("end");
        }

        private static LvnDocument Дверь(int ловкость, int сила) => LvnDocument.Parse(@"{""scene"":""t"",""script"":[
            {""op"":""set"",""key"":""ловкость"",""value"":" + ловкость + @"},
            {""op"":""set"",""key"":""сила"",""value"":" + сила + @"},
            {""op"":""say"",""text"":""Дверь заперта.""},
            {""op"":""choice"",""options"":[
                {""text"":""Взломать"",""goto"":""внутрь"",""requires_stat"":""ловкость"",""requires_min"":5},
                {""text"":""Выбить"",""goto"":""внутрь"",""requires_stat"":""сила"",""requires_min"":5}]},
            {""op"":""say"",""text"":""Ты остаёшься снаружи.""},
            {""op"":""goto"",""label"":""__end""},
            {""op"":""label"",""id"":""внутрь""},
            {""op"":""say"",""text"":""Ты внутри.""}
        ]}");

        [Test]
        public void ЗакрытыВсеВариантыИграИдётДальше()
        {
            var stage = new RecStage();
            var p = new LvnPlayer(Дверь(0, 0), stage);

            p.Advance();
            p.Advance();
            p.Advance();

            CollectionAssert.DoesNotContain(stage.Events, "choice:0",
                "пустая стопка вариантов вместо продолжения — игрок стоит навсегда");
            CollectionAssert.Contains(stage.Events, "say:Ты остаёшься снаружи.",
                "курсор обязан пойти дальше по скрипту");
            Assert.IsFalse(p.AtChoice, "плеер ждёт выбора, которого сделать нельзя");
            Assert.IsTrue(p.Finished, "глава обязана дойти до конца, а не зациклиться на пустом выборе");
        }

        [Test]
        public void ХотяБыОдинДоступныйВыборПоказывается()
        {
            var stage = new RecStage();
            var p = new LvnPlayer(Дверь(9, 0), stage);

            p.Advance();
            p.Advance();

            CollectionAssert.Contains(stage.Events, "choice:1",
                "доступный вариант обязан дойти до игрока");
            CollectionAssert.DoesNotContain(stage.Events, "say:Ты остаёшься снаружи.",
                "пока выбор открыт, история дальше не уходит");
            Assert.IsTrue(p.AtChoice);
        }

        // Телеметрия видит именно то, что случилось: написано два, показано ноль.
        [Test]
        public void ТелеметрияСчитаетНаписанноеИПоказанное()
        {
            var stage = new RecStage();
            var p = new LvnPlayer(Дверь(0, 0), stage);
            int written = -1, shown = -1;
            // Событие статическое (одно на процесс) — снимаем подписку в finally,
            // иначе соседний тест получит чужой обработчик.
            System.Action<int, int, int> ear = (w, s, ip) => { written = w; shown = s; };
            LvnPlayer.ChoiceShown += ear;
            try
            {
                p.Advance();
                p.Advance();
            }
            finally { LvnPlayer.ChoiceShown -= ear; }

            Assert.AreEqual(2, written, "в сценарии два варианта");
            Assert.AreEqual(0, shown, "игроку не досталось ни одного");
        }
    }
}
