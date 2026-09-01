using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// The C# half of the cross-runtime conformance corpus. The cases are data in
    /// <c>/conformance/cases</c> (contract and case format: <c>/conformance/README.md</c>);
    /// this file is only a driver — it plays a case with <see cref="LvnPlayer"/> and
    /// diffs the observable effects against what the case declares.
    ///
    /// <para>Why the corpus lives outside this package: <c>.lvn</c> has more than one
    /// player (this runtime, the browser playground, whatever comes next) and each was
    /// free to drift. The corpus is the shared definition of "playing it correctly";
    /// the cheap dispatch-table guard that pairs with it runs in Go
    /// (<c>tools/lvnconv/lvn/conformance_test.go</c>) so it gates every commit without
    /// needing Unity.</para>
    ///
    /// <para>Nothing here decides which cases apply: a case lists the runtimes that
    /// must pass it and the runner filters on that field alone.</para>
    /// </summary>
    public class ConformanceCorpusTests
    {
        const int DefaultMaxSteps = 500;

        static string CorpusDir() => ConformanceCorpus.CasesDir();

        const string NoCorpus = "(no conformance corpus found)";

        static IEnumerable<string> CaseFiles()
        {
            var dir = CorpusDir();
            if (dir == null) return new[] { NoCorpus };
            var files = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).ToList();
            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0) return new[] { NoCorpus };
            return files;
        }

        [Test]
        public void CorpusIsPresentAndNonEmpty()
        {
            var dir = CorpusDir();
            if (dir == null)
                Assert.Ignore("no /conformance/cases above " + Application.dataPath +
                              " — the corpus ships with the repository, not with the UPM package");
            Assert.IsNotEmpty(Directory.GetFiles(dir, "*.json"),
                "/conformance/cases is empty — the op contract has no runtime cases left to check");
        }

        [Test]
        public void PlaysConformanceCase([ValueSource(nameof(CaseFiles))] string file)
        {
            if (file == NoCorpus)
                Assert.Ignore("no /conformance/cases above " + Application.dataPath +
                              " — the corpus ships with the repository, not with the UPM package");

            var c = JObject.Parse(File.ReadAllText(Path.Combine(CorpusDir(), file)));
            var id = (string)c["id"] ?? file;
            var runtimes = (c["runtimes"] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
            if (!runtimes.Contains("csharp"))
                Assert.Ignore(id + ": not declared for the csharp runtime");

            var run = Play(id, c);
            var expect = c["expect"] as JObject ?? new JObject();

            if (expect["stops"] is JArray stops) AssertStops(id, stops, run.Stops);
            if (expect["vars"] is JObject vars) AssertVars(id, vars, run.Player.Vars);
            if (expect["stage"] is JArray stage) AssertStage(id, stage, run.Stage.Staged);
            if (expect["scene"] is JObject scene) AssertScene(id, scene, run.Stage);
            if (expect["labels"] is JArray labels)
                CollectionAssert.AreEqual(labels.Select(t => (string)t).ToList(), run.Labels,
                    id + ": the cursor took a different route through the labels");
            foreach (var e in expect["expr_true"] as JArray ?? new JArray())
                Assert.IsTrue(LvnExpression.EvaluateBool((string)e, run.Player.Vars),
                    id + ": expected «" + (string)e + "» to hold over the final variables");
            foreach (var e in expect["expr_false"] as JArray ?? new JArray())
                Assert.IsFalse(LvnExpression.EvaluateBool((string)e, run.Player.Vars),
                    id + ": expected «" + (string)e + "» NOT to hold over the final variables");
        }

        // ── the driver ──────────────────────────────────────────────────────
        // Advance to the next stop, react by kind, record what was observed —
        // the same loop the JS runner implements (conformance/README.md §Driving).

        sealed class RunResult
        {
            public LvnPlayer Player;
            public RecordingStage Stage;
            public List<Stop> Stops;
            public List<string> Labels;
        }

        static readonly Regex LabelTrace = new Regex(@"^#\d+ label id=(.+)$");

        RunResult Play(string id, JObject c)
        {
            var stage = new RecordingStage();
            var player = new LvnPlayer(LvnDocument.Parse(c["doc"].ToString()), stage);
            stage.Player = player;

            // Which labels the cursor actually entered is only observable through the
            // player's trace hook — the route, as opposed to the destination.
            var labels = new List<string>();
            var prevLog = LvnPlayer.Log;
            LvnPlayer.Log = line =>
            {
                var m = LabelTrace.Match(line ?? "");
                if (m.Success) labels.Add(m.Groups[1].Value);
            };

            var picks = new Queue<JToken>((c["picks"] as JArray ?? new JArray()).ToList());
            var inputs = new Queue<string>((c["inputs"] as JArray ?? new JArray()).Select(t => (string)t));
            int maxSteps = (int?)c["max_steps"] ?? DefaultMaxSteps;

            try
            {
                player.Advance();
                for (int step = 0; step < maxSteps; step++)
                {
                    Assert.IsNotEmpty(stage.Stops,
                        id + ": the player advanced without reaching any stop (no say/choice/input/wait/end)");
                    var stop = stage.Stops[stage.Stops.Count - 1];
                    switch (stop.Kind)
                    {
                        case StopKind.Say:
                        case StopKind.Wait:
                            player.Advance();
                            break;

                        case StopKind.Input:
                            Assert.IsNotEmpty(inputs, id + ": an input stop is open but `inputs` ran out");
                            player.SetVar(stop.InputVar, inputs.Dequeue());
                            player.Advance();
                            break;

                        case StopKind.Choice:
                            Assert.IsNotEmpty(picks, id + ": a choice is open but `picks` ran out");
                            var pick = picks.Dequeue();
                            if (pick is JObject po && (bool?)po["timeout"] == true)
                            {
                                Assert.IsTrue(player.ResolveChoiceTimeout(),
                                    id + ": pick says timeout, but the choice has no timeout_goto to take");
                            }
                            else
                            {
                                int nth = (int)pick;
                                Assert.Less(nth, stop.Presented.Count,
                                    id + ": pick " + nth + " is out of range of the " + stop.Presented.Count +
                                    " PRESENTED options (picks index the presented list, so a hidden option shifts it)");
                                player.Choose(stop.Presented[nth].Index);
                                player.Advance();
                            }
                            break;

                        case StopKind.End:
                            return new RunResult { Player = player, Stage = stage, Stops = stage.Stops, Labels = labels };
                    }
                }
                Assert.Fail(id + ": ran past max_steps (" + maxSteps + ") without ending");
                return null;
            }
            finally
            {
                LvnPlayer.Log = prevLog;
            }
        }

        // ── the recording stage ─────────────────────────────────────────────

        enum StopKind { Say, Choice, Input, Wait, End }

        sealed class Stop
        {
            public StopKind Kind;
            public string Who, Text, Style;                 // say
            public IReadOnlyList<LvnOption> Presented;      // choice
            public float Timeout;
            public string InputVar, Prompt, Default;        // input
            public int Max;
            public int Ms;                                  // wait

            public override string ToString()
            {
                switch (Kind)
                {
                    case StopKind.Say: return "say(" + Text + ")";
                    case StopKind.Choice: return "choice[" + string.Join(" | ", Presented.Select(o => o.Text)) + "]";
                    case StopKind.Input: return "input(" + Prompt + ")";
                    case StopKind.Wait: return "wait(" + Ms + ")";
                    default: return "end";
                }
            }
        }

        /// <summary>The observable surface of a run: every stop in order, every
        /// staging command in order, and the scene those commands reduce to.</summary>
        sealed class RecordingStage : ILvnStage
        {
            public LvnPlayer Player; // for the countdown seconds of the open choice
            public readonly List<Stop> Stops = new List<Stop>();
            public readonly List<JObject> Staged = new List<JObject>();
            public string Bg;
            public readonly Dictionary<string, JObject> Actors = new Dictionary<string, JObject>();

            public void ShowSay(string who, string text, string style)
                => Stops.Add(new Stop { Kind = StopKind.Say, Who = who, Text = text, Style = style });

            public void ShowChoice(IReadOnlyList<LvnOption> options)
                => Stops.Add(new Stop
                {
                    Kind = StopKind.Choice,
                    Presented = options,
                    Timeout = Player != null ? Player.CurrentChoiceTimeout : 0f,
                });

            public void OnEnd() => Stops.Add(new Stop { Kind = StopKind.End });

            // Подписанная дверь: заглушке различать отправителей незачем —
            // она просто записывает команду, как и раньше.
            public void ApplyStage(JObject c, Lvn.LvnSender sender) => ApplyStage(c);

            public void ApplyStage(JObject c)
            {
                Staged.Add(c);
                var op = (string)c["op"];
                switch (op)
                {
                    // The two staging commands that are also STOPS: the story waits
                    // here for the host, so they belong in the stop trace too.
                    case "input":
                        Stops.Add(new Stop
                        {
                            Kind = StopKind.Input,
                            InputVar = (string)c["var"],
                            Prompt = (string)c["prompt"],
                            Default = (string)c["default"],
                            Max = (int?)c["max"] ?? 0,
                        });
                        break;
                    case "wait":
                        Stops.Add(new Stop { Kind = StopKind.Wait, Ms = (int?)c["ms"] ?? 0 });
                        break;

                    // The scene reduction: latest backdrop, and per actor the last
                    // command's own fields with placement sticky (the live rule).
                    case "bg":
                        Bg = (string)c["sprite_url"];
                        break;
                    // clear takes everyone off stage at once. It must leave the
                    // SAME residue a per-actor `show=false` leaves — placement
                    // remembered, actor invisible — because the live stage keeps
                    // _placements through a hide, so a later `actor id=…` with no
                    // position returns her to the slot she left.
                    case "clear":
                        foreach (var kv in Actors)
                        {
                            var keptPl = new JObject();
                            foreach (var keep in new[] { "position", "x", "y" })
                                if (kv.Value[keep] != null) keptPl[keep] = kv.Value[keep].DeepClone();
                            kv.Value.RemoveAll();
                            foreach (var p in keptPl.Properties()) kv.Value[p.Name] = p.Value.DeepClone();
                            Видимость.Снять(kv.Value);
                        }
                        break;
                    // obj is a placeable sprite and shares the actor pipeline on
                    // the live stage (VnStage.ApplyStage routes both to
                    // ApplyActorAsync), so the reduction has to treat it the same
                    // — otherwise "clear also removes objects" is untestable.
                    case "obj":
                    case "actor":
                        var id = (string)c["id"];
                        if (string.IsNullOrEmpty(id)) break;
                        if (!Actors.TryGetValue(id, out var st)) { st = new JObject(); Actors[id] = st; }
                        var sticky = new JObject();
                        foreach (var keep in new[] { "position", "x", "y" })
                            if (st[keep] != null) sticky[keep] = st[keep].DeepClone();
                        st.RemoveAll();
                        foreach (var p in sticky.Properties()) st[p.Name] = p.Value.DeepClone();
                        foreach (var p in c.Properties())
                            if (p.Name != "op") st[p.Name] = p.Value.DeepClone();
                        // «Скрыт ли» — вопрос к ДОМУ (Lvn.LvnBool), а не приведение
                        // типа. Здесь это особенно дорого: корпус СЕРТИФИЦИРУЕТ
                        // поведение, и приведение заставляло его сертифицировать
                        // не то, что делает движок, — `show=no` оставался видимым.
                        Видимость.Отметить(st, c);
                        break;
                }
            }

            public HashSet<string> Visible() => Видимость.Видимые(Actors);
        }

        // ── expectation matching ────────────────────────────────────────────

        static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? "" : t.ToString();
        static string Str(string s) => s ?? "";

        static void AssertStops(string id, JArray want, List<Stop> got)
        {
            // Two phases. First the SHAPE — which kinds of stop, in which order —
            // because a trace that diverged structurally is unreadable as a
            // per-field failure halfway down. Only then the details, which the case
            // may pin selectively.
            var wantKinds = want.Select(w => ((JObject)w).Properties().First().Name).ToList();
            var gotKinds = got.Select(s => s.Kind.ToString().ToLowerInvariant()).ToList();
            if (!wantKinds.SequenceEqual(gotKinds))
                Assert.Fail(id + ": stop trace diverged\n  expected: " +
                            string.Join(" → ", want.Select(w => ShapeOf(id, (JObject)w))) +
                            "\n  actual:   " + string.Join(" → ", got.Select(s => s.ToString())));

            for (int i = 0; i < want.Count; i++)
            {
                var w = (JObject)want[i];
                var kind = w.Properties().First().Name;
                var body = w[kind];
                var g = got[i];
                var at = id + ": stop #" + i + " (" + kind + ")";
                switch (kind)
                {
                    case "say":
                        if (body.Type == JTokenType.String)
                        {
                            Assert.AreEqual((string)body, Str(g.Text), at + ": line");
                            break;
                        }
                        var so = (JObject)body;
                        if (so["who"] != null) Assert.AreEqual(Str(so["who"]), Str(g.Who), at + ": speaker");
                        if (so["text"] != null) Assert.AreEqual(Str(so["text"]), Str(g.Text), at + ": line");
                        if (so["style"] != null) Assert.AreEqual(Str(so["style"]), Str(g.Style), at + ": style");
                        break;

                    case "choice":
                        var opts = body is JArray arr ? arr : (JArray)body["options"];
                        if (opts != null)
                            CollectionAssert.AreEqual(opts.Select(t => (string)t).ToList(),
                                g.Presented.Select(o => o.Text).ToList(),
                                at + ": the PRESENTED options (a gated-out option must be absent, not disabled)");
                        if (body is JObject co && co["timeout"] != null)
                            Assert.AreEqual((float)co["timeout"], g.Timeout, 0.0001f,
                                at + ": countdown seconds never reached the presentation layer");
                        break;

                    case "input":
                        var io = (JObject)body;
                        if (io["prompt"] != null) Assert.AreEqual(Str(io["prompt"]), Str(g.Prompt), at + ": prompt");
                        if (io["default"] != null) Assert.AreEqual(Str(io["default"]), Str(g.Default), at + ": default");
                        if (io["max"] != null) Assert.AreEqual((int)io["max"], g.Max, at + ": max");
                        break;

                    case "wait":
                        if (body["ms"] != null) Assert.AreEqual((int)body["ms"], g.Ms, at + ": ms");
                        break;
                }
            }
        }

        // The one-line shape of a declared stop, so the trace diff reads like the
        // trace itself. Mirrors Stop.ToString().
        static string ShapeOf(string id, JObject stop)
        {
            var kind = stop.Properties().First().Name;
            var body = stop[kind];
            switch (kind)
            {
                case "say":
                    return "say(" + (body.Type == JTokenType.String ? (string)body : Str(body["text"])) + ")";
                case "choice":
                    var opts = body is JArray arr ? arr : (JArray)body["options"];
                    return "choice[" + string.Join(" | ", (opts ?? new JArray()).Select(t => (string)t)) + "]";
                case "input":
                    return "input(" + Str(body["prompt"]) + ")";
                case "wait":
                    return "wait(" + Str(body["ms"]) + ")";
                case "end":
                    return "end";
                default:
                    Assert.Fail(id + ": unknown stop kind " + kind);
                    return "";
            }
        }

        static void AssertVars(string id, JObject want, IDictionary<string, JToken> got)
        {
            foreach (var p in want.Properties())
            {
                Assert.IsTrue(got.TryGetValue(p.Name, out var actual),
                    id + ": variable " + p.Name + " was never set");
                switch (p.Value.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        // 2 and 2.0 are the same story state; only the value matters.
                        Assert.AreEqual((double)p.Value, ToDouble(actual), 0.000001,
                            id + ": variable " + p.Name);
                        break;
                    case JTokenType.Boolean:
                        Assert.AreEqual((bool)p.Value, (bool)actual, id + ": variable " + p.Name);
                        break;
                    default:
                        Assert.AreEqual(Str(p.Value), Str(actual), id + ": variable " + p.Name);
                        break;
                }
            }
        }

        static double ToDouble(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            return double.TryParse(Str(t), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        // Each expected staging command is matched as a SUBSET of the real one, so a
        // case pins the fields it cares about without freezing every unrelated key.
        static void AssertStage(string id, JArray want, List<JObject> got)
        {
            Assert.AreEqual(want.Count, got.Count,
                id + ": " + want.Count + " staging commands expected, " + got.Count + " reached the stage (" +
                string.Join(", ", got.Select(c => (string)c["op"])) + ")");
            for (int i = 0; i < want.Count; i++)
                foreach (var p in ((JObject)want[i]).Properties())
                    Assert.AreEqual(Str(p.Value), Str(got[i][p.Name]),
                        id + ": stage #" + i + " field " + p.Name + " diverged");
        }

        static void AssertScene(string id, JObject want, RecordingStage stage)
        {
            if (want["bg"] != null)
                Assert.AreEqual(Str(want["bg"]), Str(stage.Bg), id + ": backdrop");
            if (want["visible"] is JArray vis)
            {
                var expected = new HashSet<string>(vis.Select(t => (string)t));
                var actual = stage.Visible();
                Assert.IsTrue(expected.SetEquals(actual),
                    id + ": visible cast diverged (expected [" + string.Join(",", expected) +
                    "], scene has [" + string.Join(",", actual) + "])");
            }
            if (want["actors"] is JObject actors)
                foreach (var a in actors.Properties())
                {
                    Assert.IsTrue(stage.Actors.TryGetValue(a.Name, out var st),
                        id + ": actor " + a.Name + " never reached the scene");
                    foreach (var f in ((JObject)a.Value).Properties())
                        Assert.AreEqual(Str(f.Value), Str(st[f.Name]),
                            id + ": actor " + a.Name + "." + f.Name + " diverged");
                }
        }
    }
}
