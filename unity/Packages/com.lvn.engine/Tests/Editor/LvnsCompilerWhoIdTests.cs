using System.Collections.Generic;
using Lvn.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ID ГОВОРЯЩЕГО В РЕПЛИКЕ — редакторный компилятор кладёт его так же, как Go.
    ///
    /// <para>Явный <c>actor_map</c> значит ровно одно: экранное имя и id спрайта
    /// РАСХОДЯТСЯ («Ash» говорит через спрайт «hill»). Пока имя совпадает с id,
    /// сцена находит говорящего по имени и так; как только автор их развёл,
    /// нужен <c>who_id</c> — иначе подсветка говорящего и синхрон губ уходят
    /// НИКОМУ.</para>
    ///
    /// <para>Go клал это поле с самого начала, редакторный путь его терял: одна
    /// и та же глава, собранная в Unity и собранная через CLI, играла ПО-РАЗНОМУ.
    /// Тихо — потому что реплика на экране одинаковая, расходится только то, кого
    /// сцена считает говорящим.</para>
    ///
    /// <para>Обратное правило тоже закреплено: говорящий ВНЕ карты поля не несёт.
    /// Свободное соответствие имени и id — норма языка, и лишнее поле в каждой
    /// реплике только раздувало бы главу и расходилось бы с эталонами Go.</para>
    /// </summary>
    public sealed class LvnsCompilerWhoIdTests
    {
        [Test]
        public void ГоворящийИзКартыАктёровНесётИдСпрайта()
        {
            var реплика = ПерваяРеплика("scene t\nactor_map Ash=hill\nAsh: Держись рядом.");

            Assert.AreEqual("say", (string)реплика["op"]);
            Assert.AreEqual("Ash", (string)реплика["who"], "экранное имя — авторское");
            Assert.AreEqual("hill", (string)реплика["who_id"],
                "реплика не донесла id спрайта: сцена подсветит говорящего НИКОМУ, "
                + "а глава, собранная через CLI, сыграет иначе");
        }

        [Test]
        public void ГоворящийВнеКартыИдНеНесёт()
        {
            var реплика = ПерваяРеплика("scene t\nactor_map Ash=hill\nМара: Я тут никто.");

            Assert.AreEqual("Мара", (string)реплика["who"]);
            Assert.IsNull(реплика["who_id"],
                "имя вне actor_map соответствует id свободно — лишнее поле здесь "
                + "не нужно и разойдётся с эталоном Go");
        }

        [Test]
        public void ПовествованиеБезГоворящегоИдНеНесёт()
        {
            var реплика = ПерваяРеплика("scene t\nactor_map Ash=hill\nДождь не переставал.");

            Assert.AreEqual("say", (string)реплика["op"]);
            Assert.IsNull(реплика["who"], "у повествования нет говорящего");
            Assert.IsNull(реплика["who_id"], "у повествования нет и id говорящего");
        }

        [Test]
        public void РепликаСЭмоциейСтавитАктёраПоИдИНесётТотЖеИдВРеплике()
        {
            var script = Сценарий("scene t\nactor_map Ash=hill\nAsh [smile]: Держись рядом.");

            Assert.AreEqual("actor", (string)script[0]["op"]);
            Assert.AreEqual("hill", (string)script[0]["id"], "актёр ставится по id, не по имени");
            Assert.AreEqual("smile", (string)script[0]["emotion"]);

            Assert.AreEqual("say", (string)script[1]["op"]);
            Assert.AreEqual("Ash", (string)script[1]["who"]);
            Assert.AreEqual("hill", (string)script[1]["who_id"],
                "актёр встал по id, а реплика о нём не знает — сцена подсвечивает "
                + "не того, кого только что вывела");
        }

        [Test]
        public void РепликаВнутриТелаВыбораНеТеряетИдГоворящего()
        {
            // Тело опции — часть ТОЙ ЖЕ главы и наследует всё, что глава
            // объявила выше. Реплика внутри `{ … }` переезжает в отдельный
            // сплетённый блок, и вместе с ней должна переехать карта актёров.
            const string src = "scene t\nactor_map Ash=hill\nAsh: Ну?\n"
                             + "- Остаться {\n    Ash: Тогда останемся.\n}\n"
                             + "- Уйти -> __end";

            var внутри = РепликаПоТексту(Сценарий(src), "Тогда останемся.");
            Assert.IsNotNull(внутри, "реплика из тела опции не доехала до сценария");
            Assert.AreEqual("Ash", (string)внутри["who"]);
            Assert.AreEqual("hill", (string)внутри["who_id"],
                "внутри тела опции карта актёров потерялась — говорящий в ветке "
                + "выбора остался без id");
        }

        // ── помощники ───────────────────────────────────────────────────────

        private static JArray Сценарий(string src)
            => (JArray)JToken.Parse(LvnsCompiler.Compile(src))["script"];

        private static JObject ПерваяРеплика(string src)
        {
            foreach (var cmd in Сценарий(src))
                if ((string)cmd["op"] == "say") return (JObject)cmd;
            Assert.Fail("в сценарии нет ни одной реплики");
            return null;
        }

        private static JObject РепликаПоТексту(IEnumerable<JToken> script, string text)
        {
            foreach (var cmd in script)
                if ((string)cmd["op"] == "say" && (string)cmd["text"] == text) return (JObject)cmd;
            return null;
        }
    }
}
