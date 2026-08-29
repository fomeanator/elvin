using Lvn.Content;
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

        // Показать — это имя игрока, а без него подпись безымянного. Один
        // ответ на вопрос «что писать в этом месте», а не по слову на экран.
        [Test]
        public void DisplayIsTheNameOrTheGuestLabel()
        {
            var savedLabel = LvnPlayerName.GuestLabel;
            try
            {
                LvnPlayerName.GuestLabel = null;
                LvnPlayerName.Set("");
                Assert.AreEqual(LvnPlayerName.DefaultGuest, LvnPlayerName.Display,
                    "умолчание движка системно и английское");

                LvnPlayerName.Set("Майя");
                Assert.AreEqual("Майя", LvnPlayerName.Display);
                Assert.AreEqual("Майя", LvnPlayerName.Current);
            }
            finally { LvnPlayerName.GuestLabel = savedLabel; }
        }

        // Подпись безымянного принадлежит АВТОРУ: «Гость» русским словом в коде
        // навязывался любой другой игре.
        [Test]
        public void TheNovellaNamesItsOwnGuest()
        {
            var savedLabel = LvnPlayerName.GuestLabel;
            try
            {
                LvnPlayerName.Set("");
                LvnPlayerName.GuestLabel = "Странник";
                Assert.AreEqual("Странник", LvnPlayerName.Display);

                LvnPlayerName.GuestLabel = "";
                LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
                    { ["player.guest"] = "Гость" });
                Assert.AreEqual("Гость", LvnPlayerName.Display,
                    "пустое поле манифеста отдаёт слово словарю");
            }
            finally { LvnPlayerName.GuestLabel = savedLabel; LvnWords.Learn(null); }
        }

        [Test]
        public void SetNullIsAnEmptyNameNotACrash()
        {
            LvnPlayerName.Set(null);
            Assert.AreEqual("", LvnPlayerName.Current);
        }

        // Посев имени в историю: правило стояло четырьмя копиями, и ключ в
        // каждой был написан строкой — переименуй автор переменную, и три из
        // четырёх промолчали бы.
        [Test]
        public void SeedWritesTheNameUnderTheAuthorsVariable()
        {
            LvnPlayerName.Var = "name";
            LvnPlayerName.Set("Майя");
            var player = new LvnPlayer(LvnDocument.Parse("{\"script\":[]}"), new SceneModel());

            LvnPlayerName.Seed(player);

            Assert.AreEqual("Майя", (string)player.Vars["name"]);
            Assert.IsFalse(player.Vars.ContainsKey(LvnPlayerName.DefaultVar),
                "движковое «player» вместо авторского имени — тот самый промах");
        }

        [Test]
        public void SeedTakesAnExplicitNameOverTheStoredOne()
        {
            LvnPlayerName.Var = "player";
            LvnPlayerName.Set("Майя");
            var player = new LvnPlayer(LvnDocument.Parse("{\"script\":[]}"), new SceneModel());

            LvnPlayerName.Seed(player, "Алёна");

            Assert.AreEqual("Алёна", (string)player.Vars["player"]);
        }

        [Test]
        public void NamelessPlayerLeavesNoHoleInTheLine()
        {
            // Пустая переменная в тексте выглядела бы дырой посреди реплики —
            // пусть автор сам решает, что показать безымянному.
            LvnPlayerName.Var = "player";
            LvnPlayerName.Set("");
            var player = new LvnPlayer(LvnDocument.Parse("{\"script\":[]}"), new SceneModel());

            LvnPlayerName.Seed(player);

            Assert.IsFalse(player.Vars.ContainsKey("player"));
        }

        [Test]
        public void SeedingNobodyIsHarmless()
        {
            Assert.DoesNotThrow(() => LvnPlayerName.Seed(null, "Майя"));
        }
    }
}
