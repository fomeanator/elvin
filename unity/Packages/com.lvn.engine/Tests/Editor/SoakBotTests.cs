using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// The soak bot (ladder A1+A2): every local .lvn script gets played
    /// through headlessly with seeded random choices. The run must raise no
    /// exceptions and no error logs, and at EVERY pause a snapshot restored
    /// into a fresh player must rebuild the very scene the live run shows —
    /// the resume-truth property enforced across the whole content library,
    /// not just hand-written minimal repros.
    ///
    /// Partner content (cold-*) lives only on dev machines and stays out of
    /// git — a missing scripts dir skips, never fails, so CI stays green on
    /// clean clones while dev machines soak everything they have.
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

        public static IEnumerable<TestCaseData> AllScripts()
        {
            if (!Directory.Exists(ScriptsRoot))
            {
                yield return new TestCaseData(null).SetName("Soak(no-local-content)");
                yield break;
            }
            var files = Directory.GetFiles(ScriptsRoot, "*.lvn");
            System.Array.Sort(files);
            foreach (var f in files)
                yield return new TestCaseData(f).SetName("Soak(" + Path.GetFileName(f) + ")");
        }

        // Real content bugs the soak caught on 2026-07-19: an UNCONDITIONAL
        // `goto` back into the wardrobe loop (the exit condition lived on an
        // articy pin the importer dropped), leaving the rest of the chapter
        // dead code. The player's infinite-loop guard fires — correctly.
        // Kept out of the red bar so ENGINE regressions stay visible; the
        // moment the importer is fixed and a chapter passes, the test demands
        // its removal from this list.
        private static readonly HashSet<string> KnownContentLoops = new HashSet<string>
        {
            "cold-ch14.lvn", "cold-ch17.lvn", "cold-ch18.lvn",
            "cold-ch21.lvn", "cold-ch23.lvn", "cold-ch24.lvn",
        };

        // The real client applies the title declaration (vars_url: game +
        // chapter defaults) before every chapter, and imported chapters are
        // STRIPPED of the default-set boilerplate — soaking without the
        // declaration would walk branches no player can ever reach.
        private static JObject LoadDeclaration(string path)
        {
            var name = Path.GetFileName(path);
            int cut = name.LastIndexOf("-ch", System.StringComparison.Ordinal);
            if (cut <= 0) return null;
            var vars = Path.Combine(Path.GetDirectoryName(path), name.Substring(0, cut) + "-vars.json");
            return File.Exists(vars) ? JObject.Parse(File.ReadAllText(vars)) : null;
        }

        [TestCaseSource(nameof(AllScripts))]
        public void RandomPlaythroughIsCleanAndResumable(string path)
        {
            if (path == null)
                Assert.Ignore("server/content/scripts отсутствует на этой машине — соук пропущен");
            var json = File.ReadAllText(path);
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
    }
}
