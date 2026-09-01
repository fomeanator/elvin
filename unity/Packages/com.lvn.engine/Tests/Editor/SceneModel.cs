using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The reference scene a player "sees": the latest backdrop plus each
    /// actor's latest full command (visibility + fields), mirroring the live
    /// sticky rules. Shared by the resume-truth fixtures and the soak bot so
    /// test land has ONE definition of scene equality.
    /// </summary>
    /// <summary>
    /// ВИДИМОСТЬ В МОДЕЛИ СЦЕНЫ — ключ и правило, один на всех читателей.
    ///
    /// <para>Модель сцены сводит поток команд к «кто на экране». Признак
    /// видимости она хранит служебным полем внутри самой команды, и это
    /// ПРОТОКОЛ: имя поля знали три файла — заглушка сцены, модель корпуса
    /// соответствия и проба расстановки, — каждый своей строкой.</para>
    ///
    /// <para>Опасность тут особая: два из трёх — код, который СЕРТИФИЦИРУЕТ
    /// поведение движка. Разойдись правило — и корпус подтвердит согласие
    /// рантаймов ровно там, где они расходятся. Так уже было: `show` читали
    /// приведением типа, и `show=no` оставался «видимым».</para>
    /// </summary>
    internal static class Видимость
    {
        /// <summary>Служебное поле внутри команды. Начинается с подчёркиваний,
        /// чтобы не столкнуться с полем автора.</summary>
        public const string Ключ = "__visible";

        /// <summary>Отметить по команде: не сказано — видно. Слово читает ДОМ
        /// (<see cref="Lvn.LvnBool"/>), а не приведение: `show=no` доезжает
        /// строкой.</summary>
        public static void Отметить(JObject состояние, JObject команда) =>
            состояние[Ключ] = Lvn.LvnBool.Of(команда["show"], true);

        /// <summary>Скрыть — для `clear`, который уводит всех разом.</summary>
        public static void Снять(JObject состояние) => состояние[Ключ] = false;

        /// <summary>Кто на экране.</summary>
        public static HashSet<string> Видимые(Dictionary<string, JObject> актёры)
        {
            var v = new HashSet<string>();
            foreach (var kv in актёры)
                if ((bool?)kv.Value[Ключ] == true) v.Add(kv.Key);
            return v;
        }
    }

    internal sealed class SceneModel : ILvnStage
    {
        public string Bg;
        public readonly Dictionary<string, JObject> Actors = new Dictionary<string, JObject>();

        /// <summary>The options of the most recent choice pause — what a bot
        /// picks from (SceneModel itself renders nothing).</summary>
        public IReadOnlyList<LvnOption> LastOptions;

        public void ShowSay(string who, string text, string style) { }
        public void ShowChoice(IReadOnlyList<LvnOption> options) => LastOptions = options;
        public void OnEnd() { }

        // Подписанная дверь: заглушке различать отправителей незачем —
        // она просто записывает команду, как и раньше.
        public void ApplyStage(JObject c, Lvn.LvnSender sender) => ApplyStage(c);

        public void ApplyStage(JObject c)
        {
            switch ((string)c["op"])
            {
                case "bg":
                    Bg = (string)c["sprite_url"];
                    break;
                case "actor":
                    var id = (string)c["id"];
                    if (string.IsNullOrEmpty(id)) return;
                    if (!Actors.TryGetValue(id, out var st)) { st = new JObject(); Actors[id] = st; }
                    // mirror the live sticky rule: placement fields persist,
                    // everything else is the current command's word
                    var sticky = new JObject();
                    foreach (var keep in new[] { "position", "x", "y" })
                        if (st[keep] != null) sticky[keep] = st[keep];
                    st.RemoveAll();
                    foreach (var p in sticky.Properties()) st[p.Name] = p.Value;
                    foreach (var p in c.Properties())
                        if (p.Name != "op") st[p.Name] = p.Value.DeepClone();
                    // «Скрыт ли» — вопрос к ДОМУ (Lvn.LvnBool), а не приведение типа:
                    // компилятор булевых не приводит, и `show=no` доезжает сюда
                    // строкой. Приведение видело только настоящий bool, и заглушка
                    // считала скрытую героиню видимой — сертифицируя не то, что
                    // делает движок.
                    Видимость.Отметить(st, c);
                    break;
            }
        }

        public HashSet<string> Visible() => Видимость.Видимые(Actors);

        public static void AssertSameScene(SceneModel live, SceneModel replayed, string when)
        {
            Assert.AreEqual(live.Bg, replayed.Bg, when + ": backdrop diverged");
            var lv = live.Visible();
            var rv = replayed.Visible();
            Assert.IsTrue(lv.SetEquals(rv),
                when + $": visible actors diverged (live [{string.Join(",", lv)}] vs replay [{string.Join(",", rv)}])");
            foreach (var id in lv)
            {
                // the fields the player SEES: emotion/outfit resolve from the
                // final command — a replay must land on the same values
                foreach (var field in new[] { "emotion", "outfit", "position" })
                {
                    var a = (string)live.Actors[id][field];
                    var b = (string)replayed.Actors[id][field];
                    Assert.AreEqual(a, b, when + $": {id}.{field} diverged");
                }
            }
        }
    }
}
