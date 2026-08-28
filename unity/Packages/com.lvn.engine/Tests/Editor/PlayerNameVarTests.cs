using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>Имя, которым игрок назвался в истории, знает и оболочка.</summary>
    public class PlayerNameVarTests
    {
        private string _var;

        [SetUp]
        public void Save() { _var = LvnPlayerName.Var; PlayerPrefs.DeleteKey("lvn_pref_player_name"); }

        [TearDown]
        public void Restore() { LvnPlayerName.Var = _var; LvnPlayerName.Set(""); }

        [Test]
        public void NovellaNamesItsOwnVariable()
        {
            LvnPlayerName.Var = "name";     // как в Time Romance

            Assert.IsTrue(LvnPlayerName.IsNameVar("name"));
            Assert.IsTrue(LvnPlayerName.IsNameVar("Name"), "регистр не должен решать");
            Assert.IsFalse(LvnPlayerName.IsNameVar("player"),
                "объявила своё — умолчание движка больше не в счёт");
        }

        [Test]
        public void DefaultStaysPlayerWhenTheNovellaSaidNothing()
        {
            LvnPlayerName.Var = LvnPlayerName.DefaultVar;

            Assert.IsTrue(LvnPlayerName.IsNameVar("player"));
            Assert.IsFalse(LvnPlayerName.IsNameVar("name"));
        }

        [Test]
        public void EmptyVariableIsNeverTheNameVariable()
        {
            Assert.IsFalse(LvnPlayerName.IsNameVar(null));
            Assert.IsFalse(LvnPlayerName.IsNameVar(""));
        }
    }
}
