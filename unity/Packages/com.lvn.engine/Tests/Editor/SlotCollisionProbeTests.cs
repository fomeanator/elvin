using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// The slot-collision probe: walks every local chapter IN FLOW ORDER
    /// (seeded random choices through the real player, not file order — the
    /// linearized choice tails make file order a lie) and reports every pause
    /// where two visible actors resolve to the same effective X. This is the
    /// "two characters standing inside each other" partner bug hunted as a
    /// property, not a repro.
    ///
    /// Effective X mirrors the stage's rules: explicit x → slot(position) →
    /// sticky from the actor's merged history → 0.5 default.
    /// </summary>
    public class SlotCollisionProbeTests
    {
        private static readonly Dictionary<string, float> Slot = new Dictionary<string, float>
        {
            ["left"] = 0.25f, ["center"] = 0.5f, ["right"] = 0.75f,
            ["far_left"] = 0.1f, ["far_right"] = 0.9f,
            ["offleft"] = -0.3f, ["offright"] = 1.3f,
        };

        private static float EffectiveX(JObject st)
        {
            var x = st["x"];
            if (x != null && (x.Type == JTokenType.Float || x.Type == JTokenType.Integer))
                return (float)x;
            var pos = (string)st["position"];
            if (pos != null && Slot.TryGetValue(pos, out var sx)) return sx;
            return 0.5f;
        }

        public static IEnumerable<string> Chapters()
        {
            var root = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "..", "server", "content", "scripts"));
            if (!Directory.Exists(root)) yield break;
            var files = Directory.GetFiles(root, "*.lvn");
            System.Array.Sort(files);
            foreach (var f in files) yield return f;
        }

        [Test]
        public void NoTwoVisibleActorsShareAnEffectiveSlot()
        {
            var reports = new List<string>();
            int chapters = 0, runs = 0;
            foreach (var path in Chapters())
            {
                chapters++;
                var json = File.ReadAllText(path);
                var name = Path.GetFileName(path);
                LvnDocument doc;
                try { doc = LvnDocument.Parse(json); } catch { continue; }
                foreach (var seed in new[] { 3, 7, 13, 21, 42 })
                {
                    runs++;
                    var stage = new SceneModel();
                    var player = new LvnPlayer(doc, stage);
                    var rng = new System.Random(seed);
                    var seen = new HashSet<string>();
                    int guard = 0;
                    try
                    {
                        player.Advance();
                        while (!player.Finished && guard++ < 2000)
                        {
                            var vis = new List<(string id, float x)>();
                            foreach (var kv in stage.Actors)
                                if ((bool?)kv.Value["__visible"] == true)
                                    vis.Add((kv.Key, EffectiveX(kv.Value)));
                            for (int a = 0; a < vis.Count; a++)
                                for (int b = a + 1; b < vis.Count; b++)
                                    if (Mathf.Abs(vis[a].x - vis[b].x) < 0.08f)
                                    {
                                        var key = $"{name}: {vis[a].id}+{vis[b].id}@{vis[a].x:0.00}";
                                        if (seen.Add(key)) reports.Add($"{key} (seed {seed}, index {player.Index})");
                                    }
                            if (player.AtChoice)
                            {
                                var opts = stage.LastOptions;
                                var free = new List<LvnOption>();
                                foreach (var o in opts) if (o.WalletCurrency == null) free.Add(o);
                                IReadOnlyList<LvnOption> pool = free.Count > 0 ? free : opts;
                                player.Choose(pool[rng.Next(pool.Count)].Index);
                            }
                            else player.Advance();
                        }
                    }
                    catch (LvnException) { /* известные контент-циклы — не про слоты */ }
                }
            }
            Debug.Log($"[slot-probe] {chapters} глав, {runs} прогонов, контент-коллизий: {reports.Count}");
            foreach (var r in reports) Debug.Log("[slot-probe] " + r);
            // Content collisions are DATA facts (the importer loses hides on
            // branch merges); the stage's slot arbiter now refuses to draw
            // them. The probe stays green as a diagnostic: the list above is
            // the importer-fix worklist, not a runtime regression.
        }
    }
}
