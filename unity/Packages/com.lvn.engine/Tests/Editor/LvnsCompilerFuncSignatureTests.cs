using System.Collections.Generic;
using System.IO;
using Lvn.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОДПИСЬ ФУНКЦИИ СВЕРЯЕТСЯ — число доводов и единственность имени.
    ///
    /// <para>Функция в .lvns лоуэрится в метку и <c>call</c>, а доводы — в
    /// присваивания параметрам ПЕРЕД переходом. Отсюда цена обеих ошибок:
    /// промах даёт не отказ сборки, а НЕ ТУ ИГРУ.</para>
    ///
    /// <para>Доводов меньше, чем параметров, — недостающий параметр молча
    /// сохранял значение от ПРОШЛОГО вызова: вторая сцена играла на цифрах
    /// первой. Больше — лишние молча пропадали. Оба имени в одном файле —
    /// второе определение молча затирало первое, и половина вызовов уходила не
    /// туда. Ни одно из трёх не было видно ни в логе, ни на экране.</para>
    ///
    /// <para>Go отвечает на всё это ошибкой с номером строки; здесь закреплена
    /// та же формулировка — расхождение текстов означало бы, что автор,
    /// собравший главу в Unity, и автор, собравший её через CLI, читают разные
    /// диагнозы одной и той же опечатки.</para>
    /// </summary>
    public sealed class LvnsCompilerFuncSignatureTests
    {
        // Функция из двух параметров и вызов, который подставляется в конец.
        private static string ГлаваСВызовом(string вызов) =>
            "scene t\n"
            + "func greet(a, b) {\n"
            + "Кто: {a} и {b}\n"
            + "}\n"
            + вызов;

        [Test]
        public void ПравильноеЧислоДоводовСобирается()
        {
            var script = Сценарий(ГлаваСВызовом("greet(1, 2)"));

            int call = ИндексВызова(script, "__fn_greet");
            Assert.GreaterOrEqual(call, 2, "перед переходом обязаны стоять два присваивания параметрам");

            Assert.AreEqual("set", (string)script[call - 2]["op"]);
            Assert.AreEqual("a", (string)script[call - 2]["key"]);
            Assert.AreEqual("1", (string)script[call - 2]["expr"], "первый довод сел в первый параметр");

            Assert.AreEqual("set", (string)script[call - 1]["op"]);
            Assert.AreEqual("b", (string)script[call - 1]["key"]);
            Assert.AreEqual("2", (string)script[call - 1]["expr"], "второй довод сел во второй параметр");
        }

        [Test]
        public void ДоводовМеньшеЧемПараметров_Ошибка()
        {
            var отказ = Assert.Throws<LvnsCompileException>(
                () => LvnsCompiler.Compile(ГлаваСВызовом("greet(1)")));

            StringAssert.Contains("greet()", отказ.Message, "ошибка обязана назвать функцию");
            StringAssert.Contains("takes 2 argument(s)", отказ.Message, "и сколько она берёт");
            StringAssert.Contains("got 1", отказ.Message, "и сколько ей дали");
        }

        [Test]
        public void ДоводовБольшеЧемПараметров_Ошибка()
        {
            var отказ = Assert.Throws<LvnsCompileException>(
                () => LvnsCompiler.Compile(ГлаваСВызовом("greet(1, 2, 3)")));

            StringAssert.Contains("greet()", отказ.Message);
            StringAssert.Contains("takes 2 argument(s)", отказ.Message);
            StringAssert.Contains("got 3", отказ.Message, "лишний довод больше не пропадает молча");
        }

        [Test]
        public void ТекстОшибкиОДоводахСловоВСловоКакВGo()
        {
            // lvnconv на том же исходнике:
            //   line 7: greet() takes 2 argument(s), got 1
            // Номер — строка РАЗВЁРНУТОГО текста (функция уже опущена в
            // метку/goto), и он тоже обязан совпасть: по нему автор ищет место.
            var отказ = Assert.Throws<LvnsCompileException>(
                () => LvnsCompiler.Compile(ГлаваСВызовом("greet(1)")));

            Assert.AreEqual("line 7: greet() takes 2 argument(s), got 1", отказ.Message);
        }

        [Test]
        public void ДваОпределенияОдногоИмени_Ошибка()
        {
            const string src = "scene t\n"
                             + "func greet(a) {\n"
                             + "Кто: {a}\n"
                             + "}\n"
                             + "func greet(b) {\n"
                             + "Кто: снова {b}\n"
                             + "}\n"
                             + "greet(1)";

            var отказ = Assert.Throws<LvnsCompileException>(() => LvnsCompiler.Compile(src));

            // lvnconv на том же исходнике:
            //   line 5: func greet: already declared on line 2
            Assert.AreEqual("line 5: func greet: already declared on line 2", отказ.Message,
                "ошибка обязана назвать ОБЕ строки: без первой автор ищет затёртое определение вслепую");
        }

        [Test]
        public void ОдноимённыеФункцииИзРазныхПодключаемыхФайлов_ТожеДубль()
        {
            // Включения разворачиваются ДО сбора функций, поэтому два файла с
            // одним именем — ровно тот же дубль. Иначе `include` был бы дырой в
            // проверке: подключил две библиотеки — и половина вызовов ушла не туда.
            ВоВременномКаталоге(dir =>
            {
                File.WriteAllText(Path.Combine(dir, "a.inc"), "func greet(a) {\nКто: {a}\n}\n");
                File.WriteAllText(Path.Combine(dir, "b.inc"), "func greet(b) {\nКто: снова {b}\n}\n");
                string глава = Path.Combine(dir, "main.lvns");
                File.WriteAllText(глава,
                    "scene t\ninclude \"a.inc\"\ninclude \"b.inc\"\ngreet(1)\n");

                var отказ = Assert.Throws<LvnsCompileException>(() => LvnsCompiler.CompileFile(глава));

                StringAssert.Contains("func greet", отказ.Message);
                StringAssert.Contains("already declared", отказ.Message,
                    "дубль через include прошёл молча — второе определение затёрло первое");
            });
        }

        [Test]
        public void ФункцииИзРазныхПодключаемыхФайловСРазнымиИменамиНеМешают()
        {
            ВоВременномКаталоге(dir =>
            {
                File.WriteAllText(Path.Combine(dir, "a.inc"), "func greet(a) {\nКто: {a}\n}\n");
                File.WriteAllText(Path.Combine(dir, "b.inc"), "func farewell(b) {\nКто: пока, {b}\n}\n");
                string глава = Path.Combine(dir, "main.lvns");
                File.WriteAllText(глава,
                    "scene t\ninclude \"a.inc\"\ninclude \"b.inc\"\ngreet(1)\nfarewell(2)\n");

                JArray script = null;
                Assert.DoesNotThrow(
                    () => script = (JArray)JToken.Parse(LvnsCompiler.CompileFile(глава))["script"],
                    "проверка дублей поймала РАЗНЫЕ имена — библиотеку теперь не подключить");

                Assert.GreaterOrEqual(ИндексВызова(script, "__fn_greet"), 0);
                Assert.GreaterOrEqual(ИндексВызова(script, "__fn_farewell"), 0);
            });
        }

        [Test]
        public void РазныеИменаВОдномФайлеНеМешаютДругДругу()
        {
            const string src = "scene t\n"
                             + "func greet(a) {\n"
                             + "Кто: {a}\n"
                             + "}\n"
                             + "func farewell(b) {\n"
                             + "Кто: пока, {b}\n"
                             + "}\n"
                             + "greet(1)\n"
                             + "farewell(2)";

            var script = Сценарий(src);

            Assert.GreaterOrEqual(ИндексВызова(script, "__fn_greet"), 0, "вызов greet потерялся");
            Assert.GreaterOrEqual(ИндексВызова(script, "__fn_farewell"), 0, "вызов farewell потерялся");
        }

        // ── помощники ───────────────────────────────────────────────────────

        private static JArray Сценарий(string src)
            => (JArray)JToken.Parse(LvnsCompiler.Compile(src))["script"];

        private static int ИндексВызова(IList<JToken> script, string метка)
        {
            for (int i = 0; i < script.Count; i++)
                if ((string)script[i]["op"] == "call" && (string)script[i]["label"] == метка)
                    return i;
            return -1;
        }

        private static void ВоВременномКаталоге(System.Action<string> тело)
        {
            string dir = Path.Combine(Path.GetTempPath(), "lvns-funcs-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { тело(dir); }
            finally { try { Directory.Delete(dir, true); } catch { /* мусор во временном каталоге безобиден */ } }
        }
    }
}
