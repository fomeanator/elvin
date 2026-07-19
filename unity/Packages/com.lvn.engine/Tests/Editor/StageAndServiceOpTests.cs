using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Ladder B2 tails — the last uncovered ops:
    ///
    /// `blur` routes through the stage dispatch (its visuals are a render-layer
    /// concern; the contract here is that the op never falls out of the flow).
    ///
    /// `obj` shares the actor placement rules — one placement grammar for
    /// everything placeable is what keeps point-and-click content portable.
    ///
    /// "hotspot" is not an op: it is obj + on_click driving Player.GoTo — the
    /// out-of-band jump that must also REVIVE a finished player (an end screen
    /// with buttons is itself a pause; this is what makes button-driven games
    /// buildable on the engine).
    ///
    /// The services ext-ops (wallet_earn/spend, leaderboard_submit,
    /// daily_claim, ad_reward) are fire-and-forget product hooks: with the
    /// registry armed and NO backend (offline), the story must flow through
    /// all of them cleanly — a partner script must never brick offline.
    /// </summary>
    public class StageAndServiceOpTests
    {
        [TearDown]
        public void Clean() => LvnOps.Clear();

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
        public void Blur_RoutesToTheStageAndFlowContinues()
        {
            var json = @"{""script"":[
                {""op"":""blur"",""alpha"":0.7},
                {""op"":""say"",""text"":""сквозь дымку""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);
            player.Advance();
            Assert.Contains("blur", stage.Ops, "blur не дошёл до стейджа");
            Assert.AreEqual(1, stage.Lines.Count, "история не продолжилась после blur");
        }

        [Test]
        public void Obj_SharesTheActorPlacementGrammar()
        {
            var obj = new JObject { ["op"] = "obj", ["id"] = "key", ["position"] = "left" };
            Assert.AreEqual(0.25f, Lvn.UI.VnStage.PlacementFrom(obj).X, 0.001f,
                "obj должен размещаться теми же правилами, что actor");
            var explicitXy = new JObject { ["op"] = "obj", ["id"] = "key", ["x"] = 0.9, ["y"] = 0.2 };
            var p = Lvn.UI.VnStage.PlacementFrom(explicitXy);
            Assert.AreEqual(0.9f, p.X, 0.001f);
            Assert.AreEqual(0.2f, p.Y, 0.001f);
        }

        [Test]
        public void Hotspot_GoToJumpsMidStory()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""экран с кнопками""},
                {""op"":""say"",""text"":""никогда по клику""},
                {""op"":""label"",""id"":""door""},
                {""op"":""say"",""text"":""за дверью""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);
            player.Advance();
            player.GoTo("door"); // the obj's on_click hook
            player.Advance();
            Assert.AreEqual("за дверью", stage.Lines[stage.Lines.Count - 1],
                "клик по хотспоту должен вести на метку");
        }

        [Test]
        public void Hotspot_GoToRevivesAFinishedPlayer()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""титры""},
                {""op"":""label"",""id"":""bonus""},
                {""op"":""say"",""text"":""бонусная сцена""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);
            player.Advance();
            player.Advance();
            player.Advance();
            Assert.IsTrue(player.Finished, "предусловие: история дочитана");

            player.GoTo("bonus"); // end-screen button
            Assert.IsFalse(player.Finished, "GoTo обязан оживить завершённый плеер");
            player.Advance();
            Assert.AreEqual("бонусная сцена", stage.Lines[stage.Lines.Count - 1]);
        }

        [Test]
        public void ServiceOps_OfflineStoryFlowsThroughAllProductHooks()
        {
            Lvn.Services.LvnServiceOps.RegisterAll(); // no backend configured — every hook is an offline no-op
            var json = @"{""script"":[
                {""op"":""wallet_earn"",""currency"":""crystals"",""amount"":5},
                {""op"":""wallet_spend"",""currency"":""crystals"",""amount"":2},
                {""op"":""leaderboard_submit"",""board"":""main"",""score"":10},
                {""op"":""daily_claim""},
                {""op"":""ad_reward"",""placement"":""bonus""},
                {""op"":""say"",""text"":""история едет дальше""}
            ]}";
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(json), stage);
            player.Advance();
            Assert.AreEqual(1, stage.Lines.Count,
                "продуктовые ext-опы без сети обязаны быть no-op, не стеной");
        }
    }
}
