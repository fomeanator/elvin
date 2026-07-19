using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Title-level variable declarations (vars_url): ONE block per game
    /// replaces the ~250-op default-set boilerplate at the top of every
    /// chapter. Contracts: defaults never overwrite progress; chapter scope
    /// resets on a fresh entry and re-fills from its declaration; nested
    /// (global.*) keys go through the path setter.
    /// </summary>
    public class TitleVarsTests
    {
        private static LvnPlayer NewPlayer()
        {
            var json = @"{""script"":[{""op"":""say"",""text"":""x""}]}";
            return new LvnPlayer(LvnDocument.Parse(json), new SceneModel());
        }

        [Test]
        public void DefaultsFillOnlyUnsetKeys()
        {
            var p = NewPlayer();
            p.SetVar("Way.Moral", 7); // player progress
            p.ApplyDefaults(JObject.Parse(@"{""Way.Moral"": 0, ""Remember.Lie"": false}"));
            Assert.AreEqual(7, (int)((JObject)p.Vars["Way"])["Moral"], "прогресс перетёрт декларацией");
            Assert.AreEqual(false, (bool)((JObject)p.Vars["Remember"])["Lie"], "отсутствующий ключ не заполнен");
        }

        [Test]
        public void ChapterScopeResetsThenRefills()
        {
            var p = NewPlayer();
            p.SetVar("Temp.flag", 42); // прошлая глава оставила мусор
            var chapter = JObject.Parse(@"{""Temp.flag"": 0}");
            p.ResetScope(new[] { "Temp.flag" });
            p.ApplyDefaults(chapter);
            Assert.AreEqual(0, (int)((JObject)p.Vars["Temp"])["flag"], "chapter-скоуп не сбросился к дефолту");
        }

        [Test]
        public void NestedGlobalKeysApplyThroughThePathSetter()
        {
            var p = NewPlayer();
            p.ApplyDefaults(JObject.Parse(@"{""global.reputation"": 5}"));
            var g = p.Vars["global"] as JObject;
            Assert.IsNotNull(g, "global-объект не создан");
            Assert.AreEqual(5, (int)g["reputation"]);
        }
    }
}
