using Lvn.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВЫЧИСЛЯЕМАЯ ЦЕЛЬ АНИМАЦИИ — <c>anim … to="{выражение}"</c>.
    ///
    /// <para>Цель СЧИТАЕТСЯ, а не задана числом: полоса здоровья тянется к доле,
    /// которую ещё предстоит вычислить. Компилятору считать её нечем —
    /// переменные появятся только во время игры, — поэтому он переносит
    /// исходную строку в трек полем <c>to_expr</c>, а число подставляет игрок
    /// перед самым запуском (<c>LvnPlayer.ResolveAnimTargets</c>).</para>
    ///
    /// <para>Редакторный путь этого не умел ВОВСЕ: число не разбиралось,
    /// управление уходило в ветку ключей, ключей там не было — и глава ПАДАЛА
    /// с «no keyframes». Та же глава через CLI собиралась. Автор, работающий в
    /// Unity, видел не «так писать нельзя», а сборку, которая ломается на
    /// строке, законной в языке.</para>
    ///
    /// <para>Ключи стоят в значении ПОКОЯ свойства — оба, начальный и
    /// конечный. Это не заглушка: движение по такому треку появляется только
    /// после подстановки числа, а до неё анимация обязана быть неподвижной, а
    /// не прыгать в ноль. Golden-фикстура <c>anim-to-expr</c> сверяет форму
    /// вывода с Go; здесь закреплены сами правила.</para>
    /// </summary>
    public sealed class LvnsCompilerAnimToExprTests
    {
        [Test]
        public void ВычисляемаяЦельПереезжаетВТрекИсходнойСтрокой()
        {
            var трек = Трек("scene t\nanim id=bar prop=alpha to=\"{hp / hp_max}\" dur=0.6");

            Assert.AreEqual("{hp / hp_max}", (string)трек["to_expr"],
                "выражение не доехало до трека — игроку нечего подставлять, "
                + "и полоса встанет там, где стояла");
            Assert.AreEqual("alpha", (string)трек["prop"]);
        }

        [Test]
        public void ГлаваСВычисляемойЦельюСобираетсяВРедакторе()
        {
            // Собственно регрессия: раньше эта строка роняла сборку с
            // «no keyframes», хотя через lvnconv та же глава собиралась.
            Assert.DoesNotThrow(
                () => LvnsCompiler.Compile("scene t\nanim id=bar prop=alpha to=\"{hp / hp_max}\" dur=0.6"),
                "глава с вычисляемой целью снова не собирается в редакторе — "
                + "автор в Unity не может собрать то, что собирает CLI");
        }

        [Test]
        public void КлючиВычисляемойЦелиСтоятВЗначенииПокоя()
        {
            // alpha: покой = 1. Оба ключа в покое — трек неподвижен, пока
            // игрок не подставил число.
            var прозрачность = Трек("scene t\nanim id=bar prop=alpha to=\"{hp / hp_max}\" dur=0.6");
            var kA = (JArray)прозрачность["keys"];
            Assert.AreEqual(2, kA.Count, "у вычисляемой цели ровно два ключа: начало и конец");
            Assert.AreEqual(0d, (double)kA[0][0], 1e-9);
            Assert.AreEqual(1d, (double)kA[0][1], 1e-9, "начало — значение покоя alpha");
            Assert.AreEqual(0.6d, (double)kA[1][0], 1e-9);
            Assert.AreEqual(1d, (double)kA[1][1], 1e-9,
                "конец тоже в покое: движение появится, только когда игрок подставит число");

            // x: покой = 0. Значение покоя берётся у СВОЙСТВА, а не константой.
            var смещение = Трек("scene t\nanim id=bar prop=x to=\"{hp}\" dur=2");
            var kX = (JArray)смещение["keys"];
            Assert.AreEqual(0d, (double)kX[0][1], 1e-9, "покой x — ноль");
            Assert.AreEqual(0d, (double)kX[1][1], 1e-9, "покой x — ноль в обоих ключах");
        }

        [Test]
        public void ДлительностьВычисляемойЦелиАвторская_АБезНеёСекунда()
        {
            var сЧислом = Анимация("scene t\nanim id=bar prop=alpha to=\"{hp}\" dur=0.6");
            Assert.AreEqual(0.6d, (double)сЧислом["anim"]["duration"], 1e-9);

            var безДлительности = Анимация("scene t\nanim id=bar prop=alpha to=\"{hp}\"");
            Assert.AreEqual(1d, (double)безДлительности["anim"]["duration"], 1e-9,
                "без dur анимация длится секунду — как и у цели числом");
            var ключи = (JArray)((JArray)безДлительности["anim"]["tracks"])[0]["keys"];
            Assert.AreEqual(1d, (double)ключи[1][0], 1e-9, "последний ключ стоит в конце длительности");
        }

        [Test]
        public void ПробелыВокругВыраженияСрезаются()
        {
            var трек = Трек("scene t\nanim id=bar prop=x to=\"  {hp}  \" dur=2");

            Assert.AreEqual("{hp}", (string)трек["to_expr"],
                "выражение с краевыми пробелами разойдётся с Go: там strings.TrimSpace");
        }

        [Test]
        public void ЦельЧисломПоПрежнемуРаботает()
        {
            var трек = Трек("scene t\nanim id=bar prop=alpha to=0.25 dur=0.4");

            Assert.IsNull(трек["to_expr"], "у цели числом считать нечего — поля быть не должно");
            var ключи = (JArray)трек["keys"];
            Assert.AreEqual(0d, (double)ключи[0][0], 1e-9);
            Assert.AreEqual(1d, (double)ключи[0][1], 1e-9, "тянем от значения покоя");
            Assert.AreEqual(0.4d, (double)ключи[1][0], 1e-9);
            Assert.AreEqual(0.25d, (double)ключи[1][1], 1e-9, "и до числа автора");
        }

        [Test]
        public void ЦельНеЧислоИБезФигурныхСкобокУходитВВеткуКлючей()
        {
            // Фигурных скобок нет и числом не читается — значит, это не цель, а
            // ветка ключей, и ключей автор не дал. Так было до правки, так и
            // осталось: правка расширила язык, а не смягчила отказ.
            var отказ = Assert.Throws<LvnsCompileException>(
                () => LvnsCompiler.Compile("scene t\nanim id=bar prop=alpha to=left dur=0.4"));

            StringAssert.Contains("no keyframes", отказ.Message);
        }

        // ── помощники ───────────────────────────────────────────────────────

        private static JObject Анимация(string src)
        {
            var script = (JArray)JToken.Parse(LvnsCompiler.Compile(src))["script"];
            foreach (var cmd in script)
                if ((string)cmd["op"] == "anim") return (JObject)cmd;
            Assert.Fail("в сценарии нет команды анимации");
            return null;
        }

        private static JObject Трек(string src)
            => (JObject)((JArray)Анимация(src)["anim"]["tracks"])[0];
    }
}
