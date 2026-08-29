using Lvn.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПРАВИЛО ПЛЕТЕНИЯ: что имеет право ехать рантайм-телом опции, а что
    /// ОБЯЗАНО уехать в скрипт за минтованную метку.
    ///
    /// <para>Тело опции исполняется БЕЗ СОБСТВЕННОГО ИНДЕКСА в скрипте, а
    /// картинку сцены игра восстанавливает по следу исполнения — списку
    /// индексов. У чего нет индекса, того нет и в следе: команда отработает
    /// один раз и исчезнет при первом же сохранении.</para>
    ///
    /// <para>Партнёрский отчёт «вышел в меню, вернулся — персонажа нет» ровно
    /// об этом: <c>actor</c> в теле опции показал героиню, автосохранение её не
    /// запомнило, и возврат собрал сцену без неё — фон на месте, реплики идут,
    /// фигуры нет.</para>
    ///
    /// <para>Список разрешённого ЗАКРЫТЫЙ (<c>set</c>/<c>inc</c>/<c>goto</c>) —
    /// и это главное, что здесь проверяется. Пока он был обратным (перечислял
    /// запрещённое), вся постановка — <c>actor</c>, <c>bg</c>, <c>fade</c>,
    /// <c>audio</c>, <c>camera</c>, <c>fx</c> — молча ехала телом просто потому,
    /// что её забыли назвать. Новая команда языка обязана плестись в скрипт по
    /// умолчанию, а не теряться.</para>
    ///
    /// <para>Пара фикстур <c>weave-staging.lvns/.lvn</c> держит тот же рубеж
    /// целиком, до последнего поля JSON, но только на ОДНОМ примере. Здесь —
    /// правило по каждой команде отдельно, чтобы падение называло виновника.</para>
    /// </summary>
    public sealed class WeaveRuleTests
    {
        // Хвост со схождением: у опции есть куда прыгать, и это не влияет на
        // разбор самого блока.
        private const string Tail = "\n:дальше\nВсё.\n-> __end\n";

        private static JArray Script(string body)
            => (JArray)JToken.Parse(LvnsCompiler.Compile("scene t\n" + body + Tail))["script"];

        private static JObject FirstOption(JArray script)
        {
            foreach (JToken t in script)
                if ((string)t["op"] == "choice")
                    return (JObject)((JArray)t["options"])[0];
            Assert.Fail("в скрипте нет выбора");
            return null;
        }

        private static bool Has(JArray script, string op)
        {
            foreach (JToken t in script)
                if ((string)t["op"] == op) return true;
            return false;
        }

        [Test]
        public void БлокИзОднихПеременныхОстаётсяТелом()
        {
            // Для переменных отсутствие индекса безобидно: их значения снимок
            // несёт сам, и восстанавливать их по следу не нужно.
            var script = Script("- Взять -> дальше {\n  gold = gold + 1\n  inc key=fame by=2\n}");
            var opt = FirstOption(script);

            var body = (JArray)opt["body"];
            Assert.IsNotNull(body, "блок из одних переменных уехал в скрипт — платим меткой ни за что");
            Assert.AreEqual("set", (string)body[0]["op"]);
            Assert.AreEqual("inc", (string)body[1]["op"]);
            Assert.AreEqual("goto", (string)body[2]["op"],
                "переход из шапки опции обязан стать последней командой тела");
            Assert.AreEqual("дальше", (string)body[2]["label"]);
            Assert.IsNull(opt["goto"], "переход остался и в шапке, и в теле — прыгнем дважды");
        }

        [Test]
        public void ЧистоеТелоНеПлодитМеток()
        {
            var script = Script("- Взять -> дальше {\n  gold = gold + 1\n}");

            foreach (JToken t in script)
                if ((string)t["op"] == "label")
                    Assert.IsFalse(((string)t["id"]).StartsWith("__weave"),
                        "обычный выбор оплатил плетение, которого не просил");
        }

        // Каждая команда постановки — своим случаем: падение обязано назвать
        // виновника, а не «где-то в блоке».
        [TestCase("actor id=heroine show=true", "actor", TestName = "ПлетётсяActor")]
        [TestCase("bg url=\"street.png\"", "bg", TestName = "ПлетётсяBg")]
        [TestCase("hint text=\"Осмотрись\"", "hint", TestName = "ПлетётсяHint")]
        [TestCase("Нихарис: Иду.", "say", TestName = "ПлетётсяSay")]
        [TestCase("fade to=black duration=0.5", "fade", TestName = "ПлетётсяFade")]
        [TestCase("camera action=shake", "camera", TestName = "ПлетётсяCamera")]
        public void ПостановкаВБлокеУезжаетВСкрипт(string command, string op)
        {
            var script = Script("- Позвать -> дальше {\n  позвал = true\n  " + command + "\n}");
            var opt = FirstOption(script);

            Assert.IsNull(opt["body"],
                $"«{op}» уехал рантайм-телом: у команды тела нет индекса, и сцена, " +
                "собранная по следу после сохранения, её потеряет");
            var target = (string)opt["goto"];
            Assert.IsNotNull(target, "у опции не осталось ни тела, ни перехода");
            StringAssert.StartsWith("__weave", target,
                "опция обязана вести на минтованную метку сплетённой ветки");

            // И сама ветка обязана лежать в скрипте — с индексом, который
            // попадёт в след исполнения.
            Assert.IsTrue(Has(script, op), $"команда «{op}» не доехала до скрипта вовсе");
            Assert.IsTrue(Has(script, "label"), "метка сплетённой ветки не выписана");
        }

        [Test]
        public void ОдинНеПодходящийОпЗабираетВесьБлок()
        {
            // Смешанный блок нельзя расщепить: порядок команд — часть смысла.
            // Уезжает ЦЕЛИКОМ, вместе с переменными.
            var script = Script("- Позвать -> дальше {\n  позвал = true\n  actor id=heroine show=true\n}");
            var opt = FirstOption(script);

            Assert.IsNull(opt["body"]);

            int label = -1, set = -1, actor = -1;
            for (int i = 0; i < script.Count; i++)
            {
                var t = script[i];
                if ((string)t["op"] == "label" && (string)t["id"] == (string)opt["goto"]) label = i;
                else if ((string)t["op"] == "set" && (string)t["key"] == "позвал") set = i;
                else if ((string)t["op"] == "actor") actor = i;
            }

            Assert.Greater(set, label, "переменная осталась в теле — блок расщепили");
            Assert.Greater(actor, set, "порядок команд блока переставлен");
        }

        [Test]
        public void СплетённаяВеткаНеПротекаетВСоседнюю()
        {
            // Ветка, дописанная в скрипт, стоит СРАЗУ за выбором — и без
            // схождения выполнение из неё утекло бы в следующую ветку.
            var script = Script(
                "- Позвать -> дальше {\n  actor id=heroine show=true\n}\n" +
                "- Промолчать -> дальше {\n  позвал = false\n}");

            JArray options = null;
            int choiceAt = -1;
            for (int i = 0; i < script.Count; i++)
                if ((string)script[i]["op"] == "choice") { options = (JArray)script[i]["options"]; choiceAt = i; }

            Assert.GreaterOrEqual(choiceAt, 0, "в скрипте нет выбора");
            Assert.AreEqual("goto", (string)script[choiceAt + 1]["op"],
                "сразу за выбором нет прыжка на схождение — ветка выбора, ничего не сплётшая, " +
                "провалится в чужую");
            StringAssert.StartsWith("__wend", (string)script[choiceAt + 1]["label"]);

            // Вторая опция осталась обычным телом: плетётся только та, что этого требует.
            Assert.IsNotNull(((JObject)options[1])["body"],
                "блок из одних переменных уехал в скрипт за компанию");
        }
    }
}
