using System.Collections.Generic;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Localization: a chapter carries language-independent `text_id` references;
    /// the player resolves them through a swappable string catalog. The same .lvn
    /// plays in any language by changing the catalog.
    public class LocalizationTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<string> Says = new List<string>();
            public readonly List<string> Options = new List<string>();
            public void ShowSay(string who, string text, string style) => Says.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options)
            {
                foreach (var o in options) Options.Add(o.Text);
            }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() { }
        }

        private const string Script = @"[
            {""op"":""say"",""who"":""Mara"",""text_id"":""g1""},
            {""op"":""choice"",""options"":[{""text_id"":""o1"",""goto"":""__end""}]},
            {""op"":""label"",""id"":""__end""}
        ]";

        private static RecStage Play(IReadOnlyDictionary<string, string> strings)
        {
            var doc = LvnDocument.Parse("{\"scene\":\"t\",\"script\":" + Script + "}");
            var stage = new RecStage();
            var p = new LvnPlayer(doc, stage) { Strings = strings };
            p.Advance();        // → say
            p.Advance();        // → choice
            return stage;
        }

        [Test]
        public void ResolvesTextIdFromCatalog()
        {
            var s = Play(new Dictionary<string, string> { { "g1", "Привет" }, { "o1", "Остаться" } });
            Assert.AreEqual("Привет", s.Says[0]);
            Assert.AreEqual("Остаться", s.Options[0]);
        }

        [Test]
        public void SwitchingCatalogSwitchesLanguage()
        {
            var en = Play(new Dictionary<string, string> { { "g1", "Hello" }, { "o1", "Stay" } });
            Assert.AreEqual("Hello", en.Says[0]);
            Assert.AreEqual("Stay", en.Options[0]);
        }

        [Test]
        public void MissingTranslationFallsBackToKey()
        {
            var s = Play(new Dictionary<string, string>()); // empty catalog
            Assert.AreEqual("g1", s.Says[0]);   // never crashes — shows the key
        }
    }
}
