using System.Linq;
using Lvn.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The Unity import path used to accept a dangling jump silently: the script
    /// compiled, the asset built, and the break only showed up as "the chapter
    /// just ended" in someone's hands. These pin the two checks that close it.
    /// </summary>
    public class LvnsStructureCheckTests
    {
        private static JArray Script(string json) => JArray.Parse(json);

        [Test]
        public void CleanScriptHasNoProblems()
        {
            var s = Script(@"[
                {'op':'say','text':'Hello.'},
                {'op':'goto','label':'next'},
                {'op':'label','id':'next'},
                {'op':'goto','label':'__end'}
            ]".Replace('\'', '"'));
            Assert.IsEmpty(LvnsStructureCheck.Run(s));
        }

        [Test]
        public void DanglingGotoIsReported()
        {
            var s = Script(@"[{'op':'goto','label':'nowhere'}]".Replace('\'', '"'));
            var p = LvnsStructureCheck.Run(s);
            Assert.AreEqual(1, p.Count, string.Join(" | ", p));
            StringAssert.Contains("nowhere", p[0]);
        }

        [Test]
        public void DuplicateLabelIsReported()
        {
            var s = Script(@"[
                {'op':'label','id':'twice'},
                {'op':'label','id':'twice'}
            ]".Replace('\'', '"'));
            Assert.IsTrue(LvnsStructureCheck.Run(s).Any(x => x.Contains("duplicate")));
        }

        [Test]
        public void BuiltinEndIsAlwaysValid()
        {
            var s = Script(@"[{'op':'goto','label':'__end'}]".Replace('\'', '"'));
            Assert.IsEmpty(LvnsStructureCheck.Run(s));
        }

        // The targets that hide inside structures — where a dangling jump
        // survives review because nothing on screen hints the branch is broken.
        [Test]
        public void HiddenJumpTargetsAreChecked()
        {
            var s = Script(@"[
                {'op':'if','expr':'x > 1','then':'ghostA','else':'ghostB'},
                {'op':'choice','options':[
                    {'text':'a','goto':'ghostC'},
                    {'text':'b','body':[{'op':'goto','label':'ghostD'}]}
                ]},
                {'op':'obj','id':'key','on_click':'ghostE'}
            ]".Replace('\'', '"'));
            var p = LvnsStructureCheck.Run(s);
            foreach (var ghost in new[] { "ghostA", "ghostB", "ghostC", "ghostD", "ghostE" })
                Assert.IsTrue(p.Any(x => x.Contains(ghost)), $"{ghost} not reported: {string.Join(" | ", p)}");
        }

        [Test]
        public void NullScriptIsTolerated()
        {
            Assert.IsEmpty(LvnsStructureCheck.Run(null));
        }
    }
}
