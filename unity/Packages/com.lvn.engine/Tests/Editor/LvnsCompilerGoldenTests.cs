using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using Lvn.Editor;

namespace Lvn.Tests
{
    /// <summary>
    /// Anti-drift guard for the C# LVNScript compiler (<see cref="LvnsCompiler"/>).
    ///
    /// The Go transcoder is the single source of truth. For each fixture we ship the
    /// `.lvns` source and the `.lvn` the Go transcoder produced; this test compiles
    /// the source with the C# port and asserts the result is structurally identical
    /// (numbers compared by value, object key order ignored). If the two
    /// implementations ever diverge, the failing fixture + JSON path points right at
    /// the offending command.
    ///
    /// Fixtures live in Tests/Editor/Fixtures as `<name>.lvns.txt` (source) +
    /// `<name>.lvn.txt` (Go golden). Regenerate goldens with:
    ///   lvnconv convert -i <name>.lvns -o <name>.lvn
    /// </summary>
    public class LvnsCompilerGoldenTests
    {
        const string FixturesDir = "Packages/com.lvn.engine/Tests/Editor/Fixtures";

        static IEnumerable<string> FixtureNames()
        {
            var names = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { FixturesDir });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".lvns.txt"))
                {
                    string n = System.IO.Path.GetFileName(path);
                    names.Add(n.Substring(0, n.Length - ".lvns.txt".Length));
                }
            }
            names.Sort();
            if (names.Count == 0) names.Add("(no fixtures found)");
            return names;
        }

        [Test]
        public void CSharpCompilerMatchesGoGolden([ValueSource(nameof(FixtureNames))] string name)
        {
            if (name == "(no fixtures found)")
                Assert.Ignore("No .lvns.txt fixtures under " + FixturesDir);

            var srcAsset = AssetDatabase.LoadAssetAtPath<TextAsset>($"{FixturesDir}/{name}.lvns.txt");
            var goldAsset = AssetDatabase.LoadAssetAtPath<TextAsset>($"{FixturesDir}/{name}.lvn.txt");
            Assert.IsNotNull(srcAsset, $"missing source fixture {name}.lvns.txt");
            Assert.IsNotNull(goldAsset, $"missing golden fixture {name}.lvn.txt");

            string producedJson = LvnsCompiler.Compile(srcAsset.text);
            JToken produced = JToken.Parse(producedJson);
            JToken golden = JToken.Parse(goldAsset.text);

            string path = "";
            Assert.IsTrue(JsonEquivalent(produced, golden, ref path),
                $"C# compiler output diverges from Go golden for '{name}' at: {path}\n" +
                $"--- produced (C#) ---\n{Truncate(producedJson)}\n");
        }

        // ── a few inline primitive checks (fast, self-documenting) ───────────

        [Test]
        public void Narration_BecomesSay()
        {
            JObject c = FirstCmd("scene t\nHello there.");
            Assert.AreEqual("say", (string)c["op"]);
            Assert.AreEqual("Hello there.", (string)c["text"]);
            Assert.IsNull(c["who"]);
        }

        [Test]
        public void DialogueWithEmotion_EmitsActorThenSay()
        {
            var script = (JArray)JToken.Parse(LvnsCompiler.Compile("actor_map Mara=mara\nMara [smile]: Hi."))["script"];
            Assert.AreEqual("actor", (string)script[0]["op"]);
            Assert.AreEqual("mara", (string)script[0]["id"]);
            Assert.AreEqual("smile", (string)script[0]["emotion"]);
            Assert.AreEqual("say", (string)script[1]["op"]);
            Assert.AreEqual("Mara", (string)script[1]["who"]);
        }

        [Test]
        public void SceneHeader_IsExtracted()
        {
            JToken doc = JToken.Parse(LvnsCompiler.Compile("scene chapter-1\nHi."));
            Assert.AreEqual("chapter-1", (string)doc["scene"]);
        }

        [Test]
        public void SaveAndLoad_ArePrimitiveOps()
        {
            var script = (JArray)JToken.Parse(LvnsCompiler.Compile("scene t\nsave\nload"))["script"];
            Assert.AreEqual("save", (string)script[0]["op"]);
            Assert.AreEqual("load", (string)script[1]["op"]);
        }

        [Test]
        public void Assignment_BecomesSet()
        {
            JObject c = FirstCmd("scene t\ngold = gold + 1");
            Assert.AreEqual("set", (string)c["op"]);
            Assert.AreEqual("gold", (string)c["key"]);
            Assert.AreEqual("gold + 1", (string)c["expr"]);
        }

        [Test]
        public void UnknownNothing_NarrationFallback()
        {
            // A bare sentence with no colon is narration, never an error.
            JObject c = FirstCmd("scene t\nThe rain kept falling.");
            Assert.AreEqual("say", (string)c["op"]);
        }

        [Test]
        public void MalformedFor_Throws()
        {
            Assert.Throws<LvnsCompileException>(() => LvnsCompiler.Compile("scene t\nfor x { }"));
        }

        // ── helpers ──────────────────────────────────────────────────────────

        static JObject FirstCmd(string src)
        {
            var script = (JArray)JToken.Parse(LvnsCompiler.Compile(src))["script"];
            return (JObject)script[0];
        }

        static string Truncate(string s) => s.Length > 4000 ? s.Substring(0, 4000) + "…" : s;

        // Structural equivalence: numbers compared by value (so int 3 == float 3.0),
        // object keys order-independent, arrays positional.
        static bool JsonEquivalent(JToken a, JToken b, ref string path)
        {
            if (a is JObject oa && b is JObject ob)
            {
                foreach (var prop in oa.Properties())
                {
                    JToken bv = ob[prop.Name];
                    if (bv == null) { path = path + "." + prop.Name + " (missing in golden)"; return false; }
                    string sub = path + "." + prop.Name;
                    if (!JsonEquivalent(prop.Value, bv, ref sub)) { path = sub; return false; }
                }
                foreach (var prop in ob.Properties())
                {
                    if (oa[prop.Name] == null) { path = path + "." + prop.Name + " (extra in golden)"; return false; }
                }
                return true;
            }
            if (a is JArray aa && b is JArray ba)
            {
                if (aa.Count != ba.Count) { path = path + $" (array len {aa.Count} != {ba.Count})"; return false; }
                for (int i = 0; i < aa.Count; i++)
                {
                    string sub = path + "[" + i + "]";
                    if (!JsonEquivalent(aa[i], ba[i], ref sub)) { path = sub; return false; }
                }
                return true;
            }
            if (a is JValue va && b is JValue vb)
            {
                if (IsNum(va) && IsNum(vb))
                {
                    double da = va.Value<double>(), db = vb.Value<double>();
                    if (System.Math.Abs(da - db) > 1e-9) { path = path + $" ({da} != {db})"; return false; }
                    return true;
                }
                bool eq = JToken.DeepEquals(va, vb);
                if (!eq) path = path + $" ('{va}' != '{vb}')";
                return eq;
            }
            if (a.Type == JTokenType.Null && b.Type == JTokenType.Null) return true;
            bool deep = JToken.DeepEquals(a, b);
            if (!deep) path = path + " (type/leaf mismatch)";
            return deep;
        }

        static bool IsNum(JValue v) => v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
    }
}
