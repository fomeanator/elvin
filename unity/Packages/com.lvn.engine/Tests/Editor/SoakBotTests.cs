using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// The soak bot (ladder A1+A2): every script we can reach gets played
    /// through headlessly with seeded random choices. The run must raise no
    /// exceptions and no error logs, and at EVERY pause a snapshot restored
    /// into a fresh player must rebuild the very scene the live run shows —
    /// the resume-truth property enforced across whole scripts, not just
    /// hand-written minimal repros.
    ///
    /// TWO fixture sources, and the difference matters:
    ///
    ///  * <see cref="SynthDir"/> — synthetic fixtures that SHIP IN GIT, so the
    ///    soak actually runs on a clean clone and in CI. Partner content can
    ///    never live in a public repo, and a gate that only fires on the two
    ///    dev machines that happen to hold that content is not a gate. Each
    ///    fixture reproduces a shape that has bitten us (see SoakFixtures/*).
    ///  * server/content/scripts — partner content (cold-*) on dev machines
    ///    only; a missing dir simply adds no cases, it never fails.
    /// </summary>
    public class SoakBotTests
    {
        // LVN_SOAK_SEED_BASE varies the random walks per run — the stability
        // harness (qa/stability.sh) sets a fresh base per iteration so ten
        // runs shake 30 different paths, not the same three ten times.
        private static int[] Seeds
        {
            get
            {
                var raw = System.Environment.GetEnvironmentVariable("LVN_SOAK_SEED_BASE");
                var b = int.TryParse(raw, out var v) ? v : 11;
                return new[] { b, b + 18, b + 36 };
            }
        }

        private const int PauseBudget = 2000; // runaway-loop guard per seed

        // The resume-truth check replays the whole prefix (O(index)), so doing
        // it on EVERY pause makes loop-heavy scripts quadratic — a single
        // fixture ate half an hour. Full density early (where most shipped
        // bugs live), then a deterministic stride that still samples deep
        // loop states without the blowup.
        private const int TruthDenseUntil = 200;
        private const int TruthStride = 17;

        private static string ScriptsRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", "..", "server", "content", "scripts"));

        /// <summary>Committed synthetic fixtures — the soak's CI floor.</summary>
        private const string SynthDir = "Packages/com.lvn.engine/Tests/Editor/SoakFixtures";

        // A fixture named `*.expect-loop.lvn.txt` is a NEGATIVE fixture: it is
        // supposed to trip the runaway guard, so it stays out of the clean-run
        // sweep and is asserted by LoopGuardFiresOnUnexitableCycle instead.
        private const string ExpectLoopSuffix = ".expect-loop.lvn.txt";

        private static List<string> SynthFixtures(bool expectLoop)
        {
            var found = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { SynthDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.EndsWith(".lvn.txt")) continue;
                if (p.EndsWith(ExpectLoopSuffix) == expectLoop) found.Add(p);
            }
            found.Sort();
            return found;
        }

        // Reads either an in-project fixture (package path → AssetDatabase) or
        // a partner script sitting outside the Unity project (plain file IO).
        private static string ReadScript(string path)
        {
            if (!path.StartsWith("Packages/")) return File.ReadAllText(path);
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            Assert.IsNotNull(asset, "фикстура не загрузилась: " + path);
            return asset.text;
        }

        private static string CaseName(string path)
        {
            var n = Path.GetFileName(path);
            return n.EndsWith(".lvn.txt") ? n.Substring(0, n.Length - ".txt".Length) : n;
        }

        public static IEnumerable<TestCaseData> AllScripts()
        {
            // Synthetic first — these run everywhere, including a clean clone.
            foreach (var p in SynthFixtures(expectLoop: false))
                yield return new TestCaseData(p).SetName("Soak(" + CaseName(p) + ")");

            if (!Directory.Exists(ScriptsRoot)) yield break;
            var files = Directory.GetFiles(ScriptsRoot, "*.lvn");
            System.Array.Sort(files);
            foreach (var f in files)
                yield return new TestCaseData(f).SetName("Soak(" + Path.GetFileName(f) + ")");
        }

        public static IEnumerable<TestCaseData> LoopFixtures()
        {
            foreach (var p in SynthFixtures(expectLoop: true))
                yield return new TestCaseData(p).SetName("LoopGuard(" + CaseName(p) + ")");
        }

        // Content bugs the soak caught, quarantined so ENGINE regressions stay
        // visible in the red bar while the IMPORTER is being fixed. The moment
        // a quarantined chapter passes, the test below demands its removal —
        // which is exactly how this list emptied out: the 2026-07-19 entries
        // (cold-ch14/17/18/21/23/24, an UNCONDITIONAL `goto` back into the
        // wardrobe loop whose exit condition lived on an articy pin the
        // importer dropped) all pass on content rebuilt 2026-07-25. The shape
        // itself is now pinned forever by SoakFixtures/wardrobe-hostop-*.
        private static readonly HashSet<string> KnownContentLoops = new HashSet<string>();

        // The real client applies the title declaration (vars_url: game +
        // chapter defaults) before every chapter, and imported chapters are
        // STRIPPED of the default-set boilerplate — soaking without the
        // declaration would walk branches no player can ever reach.
        private static JObject LoadDeclaration(string path)
        {
            if (path.StartsWith("Packages/")) return null; // synthetic fixtures are self-contained
            var name = Path.GetFileName(path);
            int cut = name.LastIndexOf("-ch", System.StringComparison.Ordinal);
            if (cut <= 0) return null;
            var vars = Path.Combine(Path.GetDirectoryName(path), name.Substring(0, cut) + "-vars.json");
            return File.Exists(vars) ? JObject.Parse(File.ReadAllText(vars)) : null;
        }

        /// <summary>
        /// The fixtures are the whole point of the clean-clone story, so their
        /// absence must be a red bar, not a quietly shrunken test list.
        /// </summary>
        [Test]
        public void SyntheticFixturesAreShipped()
        {
            Assert.Greater(SynthFixtures(expectLoop: false).Count, 0,
                "нет коммитируемых соук-фикстур в " + SynthDir + " — на чистом клоне соук ничего не гоняет");
            Assert.Greater(SynthFixtures(expectLoop: true).Count, 0,
                "нет негативных фикстур (*" + ExpectLoopSuffix + ") — сторож бесконечного цикла ничем не проверен");
        }

        [TestCaseSource(nameof(AllScripts))]
        public void RandomPlaythroughIsCleanAndResumable(string path)
        {
            var json = ReadScript(path);
            var name = Path.GetFileName(path);
            var decl = LoadDeclaration(path);
            var known = KnownContentLoops.Contains(name);
            try
            {
                foreach (var seed in Seeds)
                    SoakOne(json, seed, name, decl);
            }
            catch (LvnException e) when (known && e.Message.Contains("infinite loop"))
            {
                Assert.Ignore("известный импорт-баг (гардеробный goto-цикл без выхода): " + e.Message);
            }
            if (known)
                Assert.Fail(name + " прошёл соук — импортёр починен? Убери главу из KnownContentLoops.");
        }

        /// <summary>
        /// The other half of the guard: a cycle with NO pause between jumps
        /// must fail loudly instead of spinning the main thread forever. This
        /// is the 2026-07-19 wardrobe shape — a host op (served by the shell
        /// package, absent on a bare engine) followed by an unconditional
        /// `goto` back to the label above it. Raise the budget, drop the
        /// guard, or start treating unregistered ops as pauses, and this test
        /// is the one that notices.
        /// </summary>
        [TestCaseSource(nameof(LoopFixtures))]
        public void LoopGuardFiresOnUnexitableCycle(string path)
        {
            var json = ReadScript(path);
            var name = Path.GetFileName(path);
            foreach (var seed in Seeds)
            {
                var e = Assert.Throws<LvnException>(
                    () => SoakOne(json, seed, name, null),
                    $"{name} seed {seed}: цикл без паузы НЕ поймал сторож — плеер завис бы навсегда");
                StringAssert.Contains("infinite loop", e.Message);
            }
        }

        private static void SoakOne(string json, int seed, string name, JObject decl)
        {
            // ONE parse per run: LvnDocument is immutable and players only
            // read the script array, so live and resumed players share it —
            // re-parsing per truth check made long chapters quadratic-slow.
            var doc = LvnDocument.Parse(json);
            var live = new SceneModel();
            var player = new LvnPlayer(doc, live);
            if (decl != null)
            {
                player.ApplyDefaults(decl["game"] as JObject);
                player.ApplyDefaults(decl["chapter"] as JObject);
            }
            var rng = new System.Random(seed);

            // СИД ДОЛЖЕН РЕШАТЬ ВСЁ, ИНАЧЕ ЭТО НЕ СИД.
            //
            // Бот выбирал варианты по сиду, а броски САМОГО КОНТЕНТА
            // (rand()/chance() в выражениях) шли из общего потока движка,
            // который никто не сеял. Один и тот же сид давал разные прогоны:
            // упавший тест переигрывался «примерно похожим» проходом, и
            // таблица флейков не отличала «иногда падает» от «контент выбросил
            // другое число».
            //
            // Об этом прямо написано в qa/stability.sh — как о том, чего НЕТ.
            // Соук, находящий баг и теряющий путь к нему, стоит меньше, чем
            // кажется: он сообщает, что беда есть, и не даёт её повторить.
            var contentRng = Lvn.LvnExpression.Random;
            Lvn.LvnExpression.Random = new Lvn.LvnRandom((ulong)seed);
            try
            {
            int pauses = 0;

            player.Advance();
            while (!player.Finished && pauses++ < PauseBudget)
            {
                // A2: the resume-truth property — dense early, strided deep.
                if (pauses <= TruthDenseUntil || pauses % TruthStride == 0)
                {
                    var snap = player.Save();
                    var replayed = new SceneModel();
                    var resumed = new LvnPlayer(doc, replayed);
                    resumed.Restore(snap);
                    // Production resume re-applies defaults after the restore
                    // (fill-if-unset — must be a no-op on a complete snapshot).
                    if (decl != null)
                    {
                        resumed.ApplyDefaults(decl["game"] as JObject);
                        resumed.ApplyDefaults(decl["chapter"] as JObject);
                    }
                    resumed.ReplayVisuals(resumed.Index);
                    SceneModel.AssertSameScene(live, replayed, $"{name} seed {seed} @index {snap.Index}");
                }

                if (player.AtChoice)
                {
                    var opts = live.LastOptions;
                    Assert.IsNotNull(opts, $"{name} seed {seed}: AtChoice без ShowChoice");
                    Assert.Greater(opts.Count, 0, $"{name} seed {seed}: выбор без опций");
                    // Paid options need the host's wallet-spend hook — the bot
                    // plays a walletless install, so it picks among free ones.
                    var free = new List<LvnOption>();
                    foreach (var o in opts)
                        if (o.WalletCurrency == null) free.Add(o);
                    IReadOnlyList<LvnOption> pool = free.Count > 0 ? free : opts;
                    player.Choose(pool[rng.Next(pool.Count)].Index);
                }
                else
                {
                    player.Advance();
                }
            }

            Assert.Greater(pauses, 0, $"{name} seed {seed}: скрипт не дал ни одной паузы");
            Debug.Log(player.Finished
                ? $"[soak] {name} seed {seed}: finished after {pauses} pauses"
                : $"[soak] {name} seed {seed}: pause budget {PauseBudget} exhausted (loops are legal — run stayed clean)");
            }
            finally { Lvn.LvnExpression.Random = contentRng; }
        }
    }
}
