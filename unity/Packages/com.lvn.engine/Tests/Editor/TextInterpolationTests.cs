using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class TextInterpolationTests
    {
        private static Dictionary<string, JToken> Vars(params (string k, JToken v)[] kv)
        {
            var d = new Dictionary<string, JToken>();
            foreach (var p in kv) d[p.k] = p.v;
            return d;
        }

        [Test]
        public void Replaces_Single_Var()
        {
            Assert.AreEqual("Hi, Charlie!",
                TextInterpolation.Apply("Hi, {name}!", Vars(("name", "Charlie"))));
        }

        [Test]
        public void Replaces_Multiple_Vars()
        {
            Assert.AreEqual("score=42 hp=99",
                TextInterpolation.Apply("score={s} hp={h}", Vars(("s", 42), ("h", 99))));
        }

        [Test]
        public void Missing_Var_Renders_As_Literal_Placeholder()
        {
            Assert.AreEqual("hello {who}",
                TextInterpolation.Apply("hello {who}", Vars()));
        }

        [Test]
        public void Doubled_Braces_Escape()
        {
            Assert.AreEqual("{name} = Charlie",
                TextInterpolation.Apply("{{name}} = {name}", Vars(("name", "Charlie"))));
        }

        [Test]
        public void No_Braces_Returns_Original()
        {
            Assert.AreEqual("plain text", TextInterpolation.Apply("plain text", Vars(("name", "x"))));
        }

        [Test]
        public void Null_Or_Empty_Input_Roundtrips()
        {
            Assert.IsNull(TextInterpolation.Apply(null, Vars()));
            Assert.AreEqual("", TextInterpolation.Apply("", Vars()));
        }

        // ── вложенный путь и точка внутри выражения ─────────────────────────

        // Точка в ключе читается как ПУТЬ к вложенной переменной: кросс-новелльные
        // статы игрока лежат под общим корнем (`global`), гардероб — под своим
        // (`Wardrobe`). Плоского ключа с таким именем в наборе нет вовсе, и не
        // умей подстановка ходить по пути — автор получил бы в реплике
        // «{global.rep}» вместо числа, которое сам же копил всю главу.
        [Test]
        public void ВложенныйПутьЧитаетсяПоТочке()
        {
            var перем = Vars(("global", new JObject { ["rep"] = 3 }),
                             ("Wardrobe", new JObject { ["mainCh_Clothes"] = "hill" }));

            Assert.AreEqual("Репутация: 3",
                TextInterpolation.Apply("Репутация: {global.rep}", перем),
                "путь к вложенной переменной не прочитан — вместо числа игрок прочтёт служебные скобки");
            Assert.AreEqual("наряд hill",
                TextInterpolation.Apply("наряд {Wardrobe.mainCh_Clothes}", перем),
                "путь гардероба не прочитан — подпись наряда останется скобками");
        }

        // ТОЧКА В КЛЮЧЕ — ЕЩЁ НЕ ПУТЬ. `{global.rep + 1}` это ВЫРАЖЕНИЕ, и разбор
        // пути на нём бросал чужое исключение — мимо перехвата, который ловит
        // только свои. Оно уходило наверх из шага чтеца и РОНЯЛО ГЛАВУ ПОСРЕДИ
        // СЦЕНЫ. Тот же счёт без пробелов, `{global.rep+1}`, работал: игра
        // падала от пробела вокруг плюса, и связать одно с другим автор не мог
        // никак — он же написал одно и то же.
        [Test]
        public void ТочкаВнутриВыраженияНеРоняетГлаву()
        {
            var перем = Vars(("global", new JObject { ["rep"] = 3 }));

            string сПробелами = null;
            Assert.DoesNotThrow(
                () => сПробелами = TextInterpolation.Apply("{global.rep + 1}", перем),
                "пробел вокруг плюса уронил подстановку — глава оборвётся посреди сцены");

            Assert.AreEqual("4", сПробелами,
                "выражение по вложенному пути посчитано неверно");
            Assert.AreEqual(TextInterpolation.Apply("{global.rep+1}", перем), сПробелами,
                "пробелы вокруг знака поменяли смысл выражения — одна и та же запись даёт разное");
        }

        // Сломанное выражение С ТОЧКОЙ ведёт себя как всякое другое сломанное:
        // видно как есть. Уронить на нём главу нельзя тем более — опечатка в
        // подписи не стоит оборванной сцены, а показать её надо, иначе автор
        // будет искать пропавшее число до вечера.
        [Test]
        public void СломанноеВыражениеСТочкой_ВидноАвтору_АНеРоняет()
        {
            var перем = Vars(("global", new JObject { ["rep"] = 3 }));

            string вышло = null;
            Assert.DoesNotThrow(
                () => вышло = TextInterpolation.Apply("итог {global.rep +}", перем),
                "опечатка в выражении с точкой уронила главу");
            Assert.AreEqual("итог {global.rep +}", вышло,
                "сломанное выражение с точкой не показано автору — он не узнает, где опечатался");
        }

        // ── что видно на месте непонятого ───────────────────────────────────

        // Непонятое выражение показывается КАК ЕСТЬ, а не стирается в пустоту.
        // Пустота на месте подстановки читается как «так и задумано»: фраза
        // выходит обрубком («осталось  ходов»), и на неё никто не жалуется,
        // пока её не заметит игрок.
        [Test]
        public void НепонятоеВыражениеВидноАвтору_АНеПустота()
        {
            Assert.AreEqual("осталось {hp +}",
                TextInterpolation.Apply("осталось {hp +}", Vars(("hp", 7))),
                "сломанная подстановка стёрлась в пустоту — автор об опечатке не узнает, "
                + "а игрок прочтёт обрубленную фразу");
        }

        // ПЛОСКИЙ ключ со значением «ничего» — это ПУСТАЯ СТРОКА, а не скобки.
        // Переменная объявлена, автор сам решил не давать ей значения (имя
        // героя до знакомства, титул до его получения); показать здесь «{name}»
        // значит вывести игроку служебный текст там, где задумана пауза.
        [Test]
        public void ПлоскийКлючСоЗначениемNull_ДаётПустуюСтроку()
        {
            Assert.AreEqual("имя: ",
                TextInterpolation.Apply("имя: {name}", Vars(("name", JValue.CreateNull()))),
                "объявленная пустая переменная показана скобками — игрок прочтёт служебный текст");
        }

        // …А ПО ВЛОЖЕННОМУ ПУТИ — СКОБКИ, и это не небрежность, а решение.
        //
        // Ни один из двух рантаймов не умеет отличить «такого пути нет» от
        // «путь есть, значение пусто»: оба спрашивают значение и получают
        // «ничего». Раз отличить нельзя, ответ должен быть ОДИН — и выбран тот,
        // что полезнее автору: скобки. Опечатка в namespaced-имени (`Wai.Moral`
        // вместо `Way.Moral`) куда вероятнее намеренной пустоты — такие имена
        // приходят из импорта сотнями и всегда числовые.
        //
        // Раньше C# отвечал здесь пустотой, а браузер — скобками: один вопрос,
        // два ответа, и молчал именно тот рантайм, в котором играет игрок.
        // Правило закреплено сверкой (conformance/34-say-unset-dotted-name).
        [Test]
        public void ПустоеЗначениеПоПути_ПоказываетсяСкобками_КакИВБраузере()
        {
            Assert.AreEqual("репутация: {global.rep}",
                TextInterpolation.Apply("репутация: {global.rep}",
                    Vars(("global", new JObject { ["rep"] = JValue.CreateNull() }))),
                "по вложенному пути пустота стёрлась молча — автор не отличит опечатку от пустого значения");
        }
    }
}
