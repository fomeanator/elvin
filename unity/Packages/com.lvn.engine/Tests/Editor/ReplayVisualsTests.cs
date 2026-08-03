using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // ReplayVisuals rebuilds the scene a save/rollback landed in. Structural ops
    // (bg/actor/obj/anim/text) re-run in order; FX/audio collapse to the LAST
    // value per state key so a load doesn't flash through every fade of the
    // chapter or restart the soundtrack N times.
    public class ReplayVisualsTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<JObject> Applied = new List<JObject>();
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) => Applied.Add(command);
            public void OnEnd() { }
        }

        private static (LvnPlayer p, RecStage s) Make(string json)
        {
            var s = new RecStage();
            return (new LvnPlayer(LvnDocument.Parse(json), s), s);
        }

        private List<JObject> Ops(RecStage s, string op)
            => s.Applied.Where(c => (string)c["op"] == op).ToList();

        [Test]
        public void FxCollapsesToLastValuePerKind()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""fade"",""to"":""black""},
                {""op"":""say"",""text"":""a""},
                {""op"":""fade"",""to"":""clear""},
                {""op"":""dim"",""alpha"":0.2},
                {""op"":""dim"",""alpha"":0.7},
                {""op"":""tint"",""color"":""warm""},
                {""op"":""say"",""text"":""b""}
            ]}");
            p.ReplayVisuals(7);

            var fades = Ops(s, "fade");
            Assert.AreEqual(1, fades.Count, "only the LAST fade replays");
            Assert.AreEqual("clear", (string)fades[0]["to"]);

            var dims = Ops(s, "dim");
            Assert.AreEqual(1, dims.Count);
            Assert.AreEqual(0.7f, (float)dims[0]["alpha"], 0.001f);

            Assert.AreEqual(1, Ops(s, "tint").Count);
        }

        [Test]
        public void ParticlesKeyedPerType()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""particles"",""type"":""rain"",""on"":true},
                {""op"":""particles"",""type"":""snow"",""on"":true},
                {""op"":""particles"",""type"":""rain"",""on"":false},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);

            var parts = Ops(s, "particles");
            Assert.AreEqual(2, parts.Count, "one final state per particle type");
            var rain = parts.First(c => (string)c["type"] == "rain");
            Assert.IsFalse((bool)rain["on"], "rain ended OFF");
            var snow = parts.First(c => (string)c["type"] == "snow");
            Assert.IsTrue((bool)snow["on"], "snow stayed ON");
        }

        [Test]
        public void CameraZoomPanPersistShakeAndResetDoNot()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""camera"",""action"":""shake"",""amplitude"":10},
                {""op"":""camera"",""action"":""zoom"",""factor"":1.5},
                {""op"":""camera"",""action"":""pan"",""x"":0.2,""y"":0},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);

            var cams = Ops(s, "camera");
            Assert.AreEqual(2, cams.Count, "zoom + pan replay; shake is transient");
            Assert.IsFalse(cams.Any(c => (string)c["action"] == "shake"));
        }

        [Test]
        public void CameraResetClearsAccumulatedZoomAndPan()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""camera"",""action"":""zoom"",""factor"":2},
                {""op"":""camera"",""action"":""pan"",""x"":0.5,""y"":0.5},
                {""op"":""camera"",""action"":""reset""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);
            Assert.AreEqual(0, Ops(s, "camera").Count, "reset returns camera to default — nothing to replay");
        }

        [Test]
        public void AudioResumesLastTrackPerChannelSfxSkipped()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""audio"",""channel"":""music"",""url"":""/m1.ogg""},
                {""op"":""audio"",""channel"":""sfx"",""url"":""/boom.ogg""},
                {""op"":""audio"",""channel"":""music"",""url"":""/m2.ogg""},
                {""op"":""audio"",""channel"":""ambient"",""url"":""/wind.ogg""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(5);

            var audio = Ops(s, "audio");
            Assert.AreEqual(2, audio.Count, "one per looping channel; sfx one-shots don't replay");
            Assert.AreEqual("/m2.ogg", (string)audio.First(c => (string)c["channel"] == "music")["url"]);
            Assert.AreEqual("/wind.ogg", (string)audio.First(c => (string)c["channel"] == "ambient")["url"]);
        }

        [Test]
        public void AudioStopIsTheFinalStateToo()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""audio"",""channel"":""music"",""url"":""/m1.ogg""},
                {""op"":""audio"",""channel"":""music"",""action"":""stop""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(3);

            var audio = Ops(s, "audio");
            Assert.AreEqual(1, audio.Count);
            Assert.AreEqual("stop", (string)audio[0]["action"], "a stopped channel replays as stopped");
        }

        [Test]
        public void StructuralOpsStillReplayInOrderAndFxComesAfter()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""fade"",""to"":""black""},
                {""op"":""bg"",""sprite_url"":""/bg/a.jpg""},
                {""op"":""actor"",""id"":""hero"",""show"":true},
                {""op"":""bg"",""sprite_url"":""/bg/b.jpg""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(5);

            var ops = s.Applied.Select(c => (string)c["op"]).ToList();
            Assert.AreEqual(new[] { "bg", "actor", "bg", "fade" }, ops,
                "structural ops in order, collapsed FX after");
        }

        // Найдено вживую: бой шёл на 3D-наборе, игрок продолжил с сохранения и
        // получил ЧЁРНЫЙ экран. Набор — такой же постоянный задник, как `bg`,
        // но в списке переигрываемых op его не было, и вернуть его было нечему:
        // следующая команда `bg3d` в скрипте могла не встретиться уже никогда.
        [Test]
        public void Bg3dSetAndItsFramingReplayInOrder()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""bg3d"",""id"":""duelwood"",""z"":-7},
                {""op"":""say"",""text"":""a""},
                {""op"":""bg3d"",""z"":-4,""fov"":60},
                {""op"":""say"",""text"":""b""}
            ]}");
            p.ReplayVisuals(4);

            var sets = Ops(s, "bg3d");
            Assert.AreEqual(2, sets.Count, "и постановка набора, и последующее кадрирование");
            Assert.AreEqual("duelwood", (string)sets[0]["id"], "сначала встаёт сам набор");
            Assert.AreEqual(60f, (float)sets[1]["fov"], 0.001f,
                "последним — тот ракурс, в котором игрок сохранился");
        }

        // Полосы hp прыгали через всю историю боя при загрузке: каждая `anim`
        // за сцену переигрывалась подряд, со своей длительностью.
        [Test]
        public void AnimCollapsesPerChannelAndSnapsToEnd()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""obj"",""id"":""bar"",""sprite_url"":""/ui/bar.png""},
                {""op"":""anim"",""id"":""bar"",""anim"":{""duration"":0.4,""tracks"":[
                    {""prop"":""fill"",""keys"":[[0,1.0],[0.4,0.75]]}]}},
                {""op"":""say"",""text"":""удар""},
                {""op"":""anim"",""id"":""bar"",""anim"":{""duration"":0.4,""tracks"":[
                    {""prop"":""fill"",""keys"":[[0,0.75],[0.4,0.5]]}]}},
                {""op"":""say"",""text"":""ещё""}
            ]}");
            p.ReplayVisuals(5);

            var anims = Ops(s, "anim");
            Assert.AreEqual(1, anims.Count, "одна дорожка — одна команда, не вся история");
            var track = anims[0]["anim"]["tracks"][0];
            Assert.AreEqual(1, ((JArray)track["keys"]).Count, "один ключ: переходить уже неоткуда");
            Assert.AreEqual(0.5f, (float)track["keys"][0][1], 0.001f, "конечное значение последней анимации");
            Assert.Less((float)anims[0]["anim"]["duration"], 0.01f, "без кадра перехода");
        }

        // Зацикленное (дыхание, мерцание) — это состояние, а не переход:
        // оборвав его, сцена замерла бы в случайной фазе.
        [Test]
        public void LoopingAnimReplaysIntact()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""obj"",""id"":""torch"",""sprite_url"":""/ui/t.png""},
                {""op"":""anim"",""id"":""torch"",""anim"":{""loop"":true,""duration"":1.2,""tracks"":[
                    {""prop"":""alpha"",""keys"":[[0,1.0],[1.2,0.6]]}]}},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(3);

            var anims = Ops(s, "anim");
            Assert.AreEqual(1, anims.Count);
            Assert.AreEqual(1.2f, (float)anims[0]["anim"]["duration"], 0.001f, "длительность цикла сохранена");
            Assert.AreEqual(2, ((JArray)anims[0]["anim"]["tracks"][0]["keys"]).Count, "ключи цикла не тронуты");
        }

        // Остановленная анимация не должна воскресать при каждой загрузке.
        [Test]
        public void StoppedAnimDoesNotComeBack()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""obj"",""id"":""bar"",""sprite_url"":""/ui/bar.png""},
                {""op"":""anim"",""id"":""bar"",""anim"":{""duration"":0.4,""tracks"":[
                    {""prop"":""fill"",""keys"":[[0,1.0],[0.4,0.3]]}]}},
                {""op"":""anim"",""id"":""bar"",""stop"":""all""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);
            Assert.AreEqual(0, Ops(s, "anim").Count, "stop снимает накопленное, а не добавляет команду");
        }

        // Разные свойства идут параллельно — схлопывать их в одну нельзя.
        [Test]
        public void DifferentPropsSurviveSideBySide()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""obj"",""id"":""bar"",""sprite_url"":""/ui/bar.png""},
                {""op"":""anim"",""id"":""bar"",""anim"":{""duration"":0.3,""tracks"":[
                    {""prop"":""fill"",""keys"":[[0,1.0],[0.3,0.4]]}]}},
                {""op"":""anim"",""id"":""bar"",""anim"":{""duration"":0.3,""tracks"":[
                    {""prop"":""alpha"",""keys"":[[0,1.0],[0.3,0.8]]}]}},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);
            Assert.AreEqual(2, Ops(s, "anim").Count, "fill и alpha — разные дорожки");
        }

        [Test]
        public void SayChoiceSetWaitNeverReplay()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""set"",""key"":""x"",""value"":1},
                {""op"":""say"",""text"":""a""},
                {""op"":""wait"",""ms"":500},
                {""op"":""choice"",""options"":[{""text"":""go"",""goto"":""L""}]},
                {""op"":""label"",""id"":""L""}
            ]}");
            p.ReplayVisuals(5);
            Assert.AreEqual(0, s.Applied.Count, "no data/pause/dialogue ops in a visual replay");
        }
    }
}
