using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Contract tests for the two flow ops that had zero coverage (ladder B2):
    ///
    /// `input` — the story pauses at the op (the stage shows a text-entry
    /// overlay), the host writes the variable and re-Advances; the next line
    /// interpolates the fresh value. The pause is single-shot by design: a
    /// host that re-Advances WITHOUT writing continues with the old/empty
    /// value — the player never deadlocks waiting.
    ///
    /// `load` — the player hands the op to the stage and does NOT move the
    /// cursor: restoring the snapshot and resuming (ReplayVisuals +
    /// ContinueFrom) is the stage's job, and that resume drives its own
    /// Advance. A stage that ignores the op would see it again on the next
    /// Advance — that repeat is the contract making the hand-off explicit.
    /// </summary>
    public class InputAndLoadOpTests
    {
        private sealed class RecordingStage : ILvnStage
        {
            public readonly List<string> Ops = new List<string>();
            public readonly List<string> Lines = new List<string>();

            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void OnEnd() { }
            public void ApplyStage(JObject c) => Ops.Add((string)c["op"]);
        }

        [Test]
        public void Input_PausesThenInterpolatesTheHostWrittenValue()
        {
            var json = @"{""script"":[
                {""op"":""input"",""key"":""hero"",""prompt"":""Как вас зовут?""},
                {""op"":""say"",""text"":""Привет, {hero}!""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);

            player.Advance();
            Assert.Contains("input", stage.Ops, "стейдж не получил input-оп");
            Assert.IsEmpty(stage.Lines, "история не встала на паузу на input");
            Assert.IsFalse(player.Finished);

            player.SetVar("hero", "Мира");
            player.Advance();
            Assert.AreEqual(1, stage.Lines.Count, "после ввода история не продолжилась");
            StringAssert.Contains("Мира", stage.Lines[0], "введённое значение не подставилось");
        }

        [Test]
        public void Input_ResumeWithoutWritingNeverDeadlocks()
        {
            var json = @"{""script"":[
                {""op"":""input"",""key"":""hero""},
                {""op"":""say"",""text"":""дальше""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);

            player.Advance(); // pause at input
            player.Advance(); // host skipped the write — the story must go on
            Assert.AreEqual(1, stage.Lines.Count, "история застряла на input");
        }

        [Test]
        public void Load_HandsOffToTheStageWithoutMovingTheCursor()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""перед загрузкой""},
                {""op"":""load"",""slot"":""quick""},
                {""op"":""say"",""text"":""недостижимо без стейджа""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);

            player.Advance();
            Assert.AreEqual(1, stage.Lines.Count);

            player.Advance();
            Assert.AreEqual(new[] { "load" }, stage.Ops, "стейдж не получил load-оп");
            Assert.IsFalse(player.Finished, "load не завершение истории");

            // The cursor must NOT advance past the op on its own: the repeat on
            // the next Advance is what makes the stage's responsibility explicit.
            player.Advance();
            Assert.AreEqual(2, stage.Ops.Count, "load должен оставаться текущим опом до рестора стейджем");
            Assert.AreEqual(1, stage.Lines.Count, "плеер проскочил load без рестора");
        }
    }
}
