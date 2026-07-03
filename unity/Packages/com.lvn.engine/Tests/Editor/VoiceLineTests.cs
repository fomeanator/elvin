using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Voice-over plumbing at the player level: the say's voice url is visible
    /// to the stage exactly while its line shows, and silent lines clear it.
    public class VoiceLineTests
    {
        private sealed class FakeStage : ILvnStage
        {
            public LvnPlayer Player;
            public readonly List<string> VoiceAtShow = new List<string>();
            public void ShowSay(string who, string text, string style)
                => VoiceAtShow.Add(Player.CurrentVoiceUrl);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        [Test]
        public void VoiceUrl_TracksItsOwnLineOnly()
        {
            const string json = @"{""script"":[
                {""op"":""say"",""text"":""voiced"",""voice"":""/content/voice/a1.ogg""},
                {""op"":""say"",""text"":""silent""},
                {""op"":""say"",""text"":""voiced too"",""voice"":""/content/voice/a2.ogg""}
            ]}";
            var stage = new FakeStage();
            var p = new LvnPlayer(LvnDocument.Parse(json), stage);
            stage.Player = p;
            p.Advance(); p.Advance(); p.Advance();

            Assert.AreEqual(
                new List<string> { "/content/voice/a1.ogg", null, "/content/voice/a2.ogg" },
                stage.VoiceAtShow,
                "each line carries exactly its own voice url; silent lines clear it");
        }
    }
}
