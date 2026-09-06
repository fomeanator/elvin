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
            // Подписанная дверь: заглушке различать отправителей незачем —
            // она просто записывает команду, как и раньше.
            public void ApplyStage(JObject command, Lvn.LvnSender sender) => ApplyStage(command);

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

        /// <summary>
        /// ПОЛОТНО СТАВИТСЯ ОДИН РАЗ, И ПЕРВЫМ.
        ///
        /// <para>Прежний договор гнал каждый <c>bg</c> пути через сцену.
        /// Замер на живой главе: 11 команд полотна, 10 разных картинок — и
        /// возврат в её конец тянул через загрузчик все десять, из которых
        /// игрок увидит одну. На устройстве, где свободно 80 МБ из 976, это
        /// десятки мегабайт декода ради кадра, который тут же перекроется.</para>
        ///
        /// <para>Схлопывается СЛИЯНИЕМ полей, а не «берём последнюю команду»:
        /// картинку и переезд камеры автор задаёт врозь, и последняя команда
        /// вполне может быть без адреса.</para>
        /// </summary>
        /// <summary>
        /// ПРЕДМЕТ СТРОИТСЯ ОДИН РАЗ — и когда следа нет тоже.
        ///
        /// <para>Реплей идёт по следу исполненных команд, а след сжимается по
        /// предмету. У старого сейва и правленого скрипта следа нет — путь
        /// линейный, и сжимать его было некому: двадцать команд об одной лампе
        /// давали двадцать применений. На живой главе это 226 применений вместо
        /// 107, и каждое тянет свой спрайт.</para>
        ///
        /// <para>Теперь `obj` идёт тем же трактом, что актёр: срабатывает на
        /// последнем своём вхождении, с накопленным размещением. Ровно так же
        /// он устроен и на живой сцене.</para>
        /// </summary>
        [Test]
        public void ПредметСтроитсяОдинРаз_ДажеБезСледа()
        {
            var sb = new System.Text.StringBuilder("{\"script\":[");
            for (int i = 0; i < 20; i++)   // один и тот же предмет двадцать раз
                sb.Append(i > 0 ? "," : "").Append("{\"op\":\"obj\",\"id\":\"лампа\",\"sprite_url\":\"o/")
                  .Append(i).Append(".png\"}");
            sb.Append(",{\"op\":\"say\",\"text\":\"стоп\"}]}");
            var (p, s) = Make(sb.ToString());
            p.ReplayVisuals(21);          // следа нет — линейный проход
            int applied = s.Applied.Count(c => (string)c["op"] == "obj");
            TestContext.WriteLine($"двадцать команд об одном предмете → применено {applied}");
            Assert.AreEqual(1, applied,
                "перестройка кадра применила предмет несколько раз: на линейном пути "
              + "(старый сейв, правленый скрипт) сжимать след некому, и возврат в тяжёлую "
              + "главу платит за каждую промежуточную команду");
        }

        /// <summary>
        /// НАДПИСЬ И ДЕРЕВО ИНТЕРФЕЙСА — ПО СМЫСЛУ, А НЕ ПО ОБЩЕМУ КЛЮЧУ.
        ///
        /// <para>У `text` смысл актёрский: побеждает последняя команда про id,
        /// а `hide` снимает надпись совсем — значит ставить её при перестройке
        /// не нужно вовсе.</para>
        ///
        /// <para>У `ui` схлопывание общим ключом СЛОМАЛО БЫ кадр: `action=hide`
        /// не создаёт дерево, а прячет существующее. Путь «объявили → спрятали»
        /// свернулся бы в одно «спрятали», дерево не появилось бы, и следующая
        /// команда `action=show` показывать было бы нечего. Поэтому помнятся
        /// два слоя: последнее объявление и последнее действие.</para>
        /// </summary>
        [Test]
        public void НадписиИДеревьяСхлопываютсяПоСмыслу()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""text"",""id"":""очки"",""text"":""1""},
                {""op"":""text"",""id"":""очки"",""text"":""2""},
                {""op"":""text"",""id"":""очки"",""text"":""3""},
                {""op"":""text"",""id"":""подсказка"",""text"":""жми""},
                {""op"":""text"",""id"":""подсказка"",""hide"":true},
                {""op"":""ui"",""id"":""панель"",""tree"":{""kind"":""box""}},
                {""op"":""ui"",""id"":""панель"",""action"":""hide""},
                {""op"":""ui"",""id"":""мусор"",""tree"":{""kind"":""box""}},
                {""op"":""ui"",""id"":""мусор"",""action"":""drop""},
                {""op"":""say"",""text"":""стоп""}
            ]}");
            p.ReplayVisuals(10);

            var texts = Ops(s, "text");
            Assert.AreEqual(1, texts.Count,
                "надписей применено " + texts.Count + " — должна остаться одна, последняя");
            Assert.AreEqual("3", (string)texts[0]["text"], "взята не последняя надпись");
            Assert.AreEqual("очки", (string)texts[0]["id"],
                "снятая надпись всё-таки поставлена — игрок увидит то, что автор убрал");

            var uis = Ops(s, "ui");
            var ids = uis.Select(c => (string)c["id"]).ToList();
            CollectionAssert.DoesNotContain(ids, "мусор",
                "дерево, которое в итоге сброшено, строится зря");
            Assert.AreEqual(2, uis.Count(c => (string)c["id"] == "панель"),
                "у спрятанного дерева обязаны остаться ОБА шага: объявление и сокрытие — "
              + "иначе следующая команда show показывать будет нечего");
        }

        [Test]
        public void ПолотноСтавитсяОдинРазИПервым()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""bg"",""sprite_url"":""/bg/a.jpg"",""fade"":1.0},
                {""op"":""actor"",""id"":""hero"",""show"":true},
                {""op"":""bg"",""sprite_url"":""/bg/b.jpg""},
                {""op"":""bg"",""pan"":0.7},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(5);

            var ops = s.Applied.Select(c => (string)c["op"]).ToList();
            CollectionAssert.AreEqual(new[] { "bg", "actor" }, ops,
                "полотно обязано ставиться один раз и до людей");

            var bg = s.Applied.First(c => (string)c["op"] == "bg");
            Assert.AreEqual("/bg/b.jpg", (string)bg["sprite_url"],
                "взята не последняя картинка полотна");
            Assert.AreEqual(0.7, (double)bg["pan"], 0.0001,
                "переезд камеры, заданный отдельной командой, потерян при слиянии");
            Assert.IsNull(bg["fade"],
                "перестройка кадра обязана вставать на место, а не проступать");
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
            // Полотно теперь одно и первое (см. соседнюю проверку); остальные
            // структурные по-прежнему идут по порядку пути, эффекты — после.
            Assert.AreEqual(new[] { "bg", "actor", "fade" }, ops,
                "structural ops in order, backdrop once up front, collapsed FX after");
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
