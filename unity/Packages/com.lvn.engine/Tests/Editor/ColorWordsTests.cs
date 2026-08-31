using System;
using System.Text.RegularExpressions;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// СЛОВА ЦВЕТА — ОДИН НАБОР, ГДЕ БЫ ЦВЕТ НИ ПИСАЛИ.
    ///
    /// <para>Словарей было три, и у каждого свой набор слов. Дерево <c>ui</c>
    /// знало токены темы и не знало ни <c>warm</c>, ни <c>sepia</c>. Команды
    /// кадра (<c>tint</c>, <c>flash</c>) знали настроения и не знали
    /// <c>accent</c>. Поля команд (<c>fx ink_color=</c>) не знали ни того, ни
    /// другого — только шестнадцать цифр. Автор писал одно слово в трёх местах
    /// и в двух из них получал молчание: «эффект не сработал», хотя сработал,
    /// цвета просто не нашли.</para>
    ///
    /// <para>Здесь закреплён СОСТАВ словаря и его три трети — токены темы,
    /// имена движка, мнемоники настроения, — а также то, что прежние двери
    /// (<see cref="UiColor.Token"/>, <see cref="VnStage.ParseColor"/>,
    /// <see cref="UiColor.FromCmd"/>) стали окнами в него, а не остались
    /// самостоятельными разборами. Проверять их порознь недостаточно: три
    /// зелёных набора и были тем состоянием, из которого словарь выделяли.</para>
    ///
    /// <para>Дверей на самом деле четыре, и последняя — ПОЛЕ МАНИФЕСТА. Сто
    /// три поля (<c>title_color</c>, <c>bg_color</c>, <c>prompt_color</c>…)
    /// читались разбором «шестнадцать цифр», и слово словаря в них молчало:
    /// в скрипте работало, в манифесте нет, хотя пишет их один человек.
    /// Закреплено это настоящими экранами и настоящим текстом манифеста (см.
    /// раздел ниже). Исключение ровно одно — сборка самой темы: оттуда словарь
    /// звать нельзя, он спрашивает цвет у действующей темы, а она в этот
    /// момент ещё строится.</para>
    ///
    /// <para>Числа тем в проверках не зашиты: значение сравнивается С ТЕМОЙ, а
    /// не с константой, — иначе перекраска палитры красила бы тест, а не
    /// интерфейс.</para>
    /// </summary>
    public sealed class ColorWordsTests
    {
        private string _темаБыла;

        [SetUp]
        public void Запомнить() => _темаБыла = LvnTheme.Current.Name;

        [TearDown]
        public void Вернуть() => LvnTheme.Use(_темаБыла);

        /// <summary>Токены темы: слово и то место темы, откуда оно берётся.
        /// <c>clear</c> сюда не входит — прозрачность теме не принадлежит.</summary>
        private static readonly (string Слово, Func<Color> УТемы)[] ТокеныТемы =
        {
            ("bg",         () => LvnTheme.Current.Bg),
            ("surface",    () => LvnTheme.Current.Surface),
            ("surface_hi", () => LvnTheme.Current.SurfaceHi),
            ("panel",      () => LvnTheme.Current.PanelBg),
            ("text",       () => LvnTheme.Current.Text),
            ("dim",        () => LvnTheme.Current.TextDim),
            ("accent",     () => LvnTheme.Current.Accent),
            ("on_accent",  () => LvnTheme.Current.OnAccent),
            ("gold",       () => LvnTheme.Current.Gold),
            ("warn",       () => LvnTheme.Current.Warn),
            ("border",     () => LvnTheme.Current.Border),
            ("veil",       () => LvnTheme.Current.Scrim),
        };

        // ── треть первая: токены темы ───────────────────────────────────────

        [Test]
        public void КаждыйТокенТемыБерётсяИменноУТемы()
        {
            // Не «цвет похож на нужный», а «взят из того самого поля»: словарь
            // с одной перепутанной строкой выглядит рабочим ровно до тех пор,
            // пока обе краски не разъедутся в следующей теме.
            foreach (var (слово, уТемы) in ТокеныТемы)
                Assert.AreEqual(уТемы(), UiColor.Named(слово, Color.magenta),
                    $"слово «{слово}» берёт цвет не из своего места темы");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ТокенТемыСледуетЗаТемойАНеЗашитВСловарь()
        {
            // ЭТО И ЕСТЬ СМЫСЛ ТОКЕНА. Зашитое значение выглядит правильным на
            // теме по умолчанию и остаётся прежним на всех остальных: экран
            // выходит наполовину перекрашенным — хуже, чем не перекрашенным.
            LvnTheme.Use("midnight");
            var полночь = Array.ConvertAll(ТокеныТемы, т => UiColor.Named(т.Слово, Color.magenta));

            LvnTheme.Use("cyber");
            for (var i = 0; i < ТокеныТемы.Length; i++)
            {
                var слово = ТокеныТемы[i].Слово;
                var кибер = UiColor.Named(слово, Color.magenta);

                Assert.AreEqual(ТокеныТемы[i].УТемы(), кибер,
                    $"после смены темы «{слово}» разошлось с самой темой");
                Assert.AreNotEqual(полночь[i], кибер,
                    $"«{слово}» не заметило смены темы — цвет зашит, а не спрошен");
            }
        }

        [Test]
        public void ПрозрачныйНеПринадлежитТеме()
        {
            // «clear» — это отсутствие краски, а не краска: тема на него не
            // влияет ни в одной из своих палитр.
            LvnTheme.Use("midnight");
            Assert.AreEqual(new Color(0, 0, 0, 0), UiColor.Named("clear", Color.white));

            LvnTheme.Use("cyber");
            Assert.AreEqual(new Color(0, 0, 0, 0), UiColor.Named("clear", Color.white));
        }

        // ── треть вторая: имена движка ──────────────────────────────────────

        [Test]
        public void ЗелёныйЭтоЯркийЗелёныйАНеТёмныйИзHTML()
        {
            // ГЛАВНОЕ ПРАВИЛО ЭТОГО ФАЙЛА, и оно про уже написанные главы.
            // «green» в HTML — тёмный #008000, в движке — яркий (0,1,0). Отдай
            // слово общему разбору — и каждая вспышка, каждая вуаль, написанная
            // словом, перекрасится задним числом, молча, без строчки в журнале.
            var зелёный = UiColor.Named("green", Color.magenta);

            Assert.AreEqual(new Color(0f, 1f, 0f, 1f), зелёный,
                "«green» перестал быть ярким зелёным");
            Assert.AreNotEqual(UiColor.Named("#008000", Color.magenta), зелёный,
                "движковый «green» съехал на HTML-овский тёмный — главы перекрашены задним числом");
        }

        [Test]
        public void ОстальныеСемьИмёнДвижкаНаМесте()
        {
            Assert.AreEqual(Color.white, UiColor.Named("white", Color.magenta));
            Assert.AreEqual(Color.black, UiColor.Named("black", Color.magenta));
            Assert.AreEqual(Color.red, UiColor.Named("red", Color.magenta));
            Assert.AreEqual(Color.blue, UiColor.Named("blue", Color.magenta));
            Assert.AreEqual(Color.yellow, UiColor.Named("yellow", Color.black));
            Assert.AreEqual(Color.cyan, UiColor.Named("cyan", Color.magenta));
            Assert.AreEqual(Color.magenta, UiColor.Named("magenta", Color.black));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ИменаДвижкаНеЗависятОтТемы()
        {
            // Красный остаётся красным в любой палитре: имя движка — это
            // краска, а не роль на экране. Иначе автор не смог бы назвать
            // конкретный цвет вообще ничем, кроме шестнадцати цифр.
            LvnTheme.Use("midnight");
            var было = UiColor.Named("red", Color.magenta);

            LvnTheme.Use("cyber");
            Assert.AreEqual(было, UiColor.Named("red", Color.magenta));
        }

        // ── треть третья: мнемоники настроения ──────────────────────────────

        [Test]
        public void МнемоникиНастроенияЗовутсяСловом()
        {
            var тепло = UiColor.Named("warm", Color.black);
            var холод = UiColor.Named("cold", Color.black);
            var сепия = UiColor.Named("sepia", Color.black);

            Assert.Greater(тепло.r, тепло.b, "«тепло» обязано быть тёплым");
            Assert.Greater(холод.b, холод.r, "«холодно» обязано быть холодным");
            Assert.Greater(сепия.r, сепия.b, "«сепия» обязана быть коричневатой");
            Assert.AreNotEqual(тепло, холод, "тепло и холод — разные настроения");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ПараСинонимовДаётОдинЦвет()
        {
            // «cold» и «tint_cold» — одно настроение, названное двумя словами.
            // Разойдись они хоть на единицу — автор получит два разных кадра за
            // то, что счёл одним и тем же приёмом.
            Assert.AreEqual(UiColor.Named("cold", Color.magenta),
                            UiColor.Named("tint_cold", Color.magenta));
            Assert.AreEqual(UiColor.Named("warm", Color.magenta),
                            UiColor.Named("tint_warm", Color.magenta));
        }

        // ── как слово написано ──────────────────────────────────────────────

        [Test]
        public void РегистрНаписанияНеВажен()
        {
            // Раньше словарь темы был регистрозависимым: «Accent» тихо уходил в
            // общий разбор, там не находился — и автор получал умолчание за
            // заглавную букву.
            Assert.AreEqual(UiColor.Named("accent", Color.magenta),
                            UiColor.Named("Accent", Color.magenta),
                            "«Accent» ушло мимо словаря темы");
            Assert.AreEqual(UiColor.Named("accent", Color.magenta),
                            UiColor.Named("ACCENT", Color.magenta));
            Assert.AreEqual(UiColor.Named("warm", Color.magenta),
                            UiColor.Named("WARM", Color.magenta));
            Assert.AreEqual(UiColor.Named("surface_hi", Color.magenta),
                            UiColor.Named("Surface_Hi", Color.magenta));
            Assert.AreEqual(Color.green, UiColor.Named("Green", Color.magenta),
                            "заглавная буква вернула «green» к HTML-овскому тёмному");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void РешёткаНеобязательнаИПрозрачностьЧитаетсяВосьмойЦифрой()
        {
            Assert.AreEqual(Color.red, UiColor.Named("#ff0000", Color.black));
            Assert.AreEqual(Color.red, UiColor.Named("ff0000", Color.black),
                            "решётку автор ставит не всегда");
            Assert.AreEqual(UiColor.Named("#ffffff80", Color.black),
                            UiColor.Named("ffffff80", Color.black),
                            "половина формы работает, а половина молча даёт умолчание");
            Assert.AreEqual(0.5f, UiColor.Named("#00ff0080", Color.black).a, 0.01f);
            Assert.AreEqual(UiColor.Named("#3a1c0d", Color.white),
                            UiColor.Named("#3A1C0D", Color.white));

            LogAssert.NoUnexpectedReceived();
        }

        // ── что бывает, когда слова нет ─────────────────────────────────────

        [Test]
        public void НеизвестноеСловоДаётУмолчаниеИЖалуется()
        {
            // Опечатку автор обязан УВИДЕТЬ: без жалобы «нарисовалось не то»
            // ищется глазами по всему скрипту. И умолчание берётся у
            // вызывающего, а не белое: не сработавший эффект не вправе залить
            // кадр.
            LogAssert.Expect(LogType.Warning, new Regex("verdigris"));
            Assert.AreEqual(Color.magenta, UiColor.Named("verdigris", Color.magenta));

            LogAssert.Expect(LogType.Warning, new Regex("lvn-ui"));
            Assert.AreEqual(Color.magenta, UiColor.Named("акцентт", Color.magenta));
        }

        [Test]
        public void НеподставленнаяПодстановкаНеОпечатка()
        {
            // «{skin.accent}» ещё не подставили — жаловаться на неё рано, иначе
            // журнал заполняется руганью на строки, которые вот-вот станут
            // цветом.
            Assert.AreEqual(Color.magenta, UiColor.Named("{skin.accent}", Color.magenta));
            Assert.AreEqual(Color.magenta, UiColor.Named("#{hex}", Color.magenta));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ПустотаЭтоОтсутствиеАНеМусор()
        {
            Assert.AreEqual(Color.magenta, UiColor.Named(null, Color.magenta));
            Assert.AreEqual(Color.magenta, UiColor.Named("", Color.magenta));

            LogAssert.NoUnexpectedReceived();
        }

        // ── прежние двери ведут в тот же словарь ────────────────────────────

        [Test]
        public void ПрежниеДвериВедутВТотЖеСловарь()
        {
            // По слову из каждой трети: токен темы, имя движка, мнемоника.
            // Раньше каждая дверь знала свою треть и молчала на две чужие.
            foreach (var слово in new[] { "accent", "green", "warm" })
            {
                var общее = UiColor.Named(слово, Color.magenta);

                Assert.AreEqual(общее, UiColor.Token(слово, Color.magenta),
                    $"дерево ui разошлось со словарём на слове «{слово}»");
                Assert.AreEqual(общее, VnStage.ParseColor(слово, Color.magenta),
                    $"команды кадра разошлись со словарём на слове «{слово}»");
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ДверьКомандКадраЗнаетТокеныТемы()
        {
            // До выделения словаря сцена держала свой набор до конца: автор
            // писал `tint color="accent"` и получал жалобу в журнал, хотя то же
            // слово в дереве ui работало.
            LvnTheme.Use("cyber");

            Assert.AreEqual(LvnTheme.Current.Accent, VnStage.ParseColor("accent", Color.magenta));
            Assert.AreEqual(LvnTheme.Current.PanelBg, VnStage.ParseColor("panel", Color.magenta));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ДверьДереваUiЗнаетМнемоникиНастроения()
        {
            // И встречное: дерево ui не знало ни warm, ни sepia.
            Assert.AreEqual(UiColor.Named("sepia", Color.magenta),
                            UiColor.Token("sepia", Color.magenta));
            Assert.AreEqual(UiColor.Named("tint_warm", Color.magenta),
                            UiColor.Token("tint_warm", Color.magenta));

            LogAssert.NoUnexpectedReceived();
        }

        // ── поле команды ────────────────────────────────────────────────────

        [Test]
        public void ПустоеПолеКомандыМолчаОставляетПрежнийЦвет()
        {
            // Отсутствие поля — это не мусор: команда просто не про цвет.
            Assert.AreEqual(Color.green, UiColor.FromCmd(new JObject { ["op"] = "fx" }, "ink_color", Color.green));
            Assert.AreEqual(Color.green, UiColor.FromCmd(new JObject { ["ink_color"] = "" }, "ink_color", Color.green));
            Assert.AreEqual(Color.green, UiColor.FromCmd(null, "ink_color", Color.green));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ПолеКомандыПонимаетСловоСловаря()
        {
            // РОВНО ТА ДЫРА, ради которой словарь и выделяли: «ink_color=warm»
            // молча оставляло прежний цвет, хотя соседняя команда то же слово
            // понимала. Молча — то есть автор видел «эффект не сработал».
            var тепло = new JObject { ["ink_color"] = "warm" };
            Assert.AreEqual(UiColor.Named("warm", Color.magenta),
                            UiColor.FromCmd(тепло, "ink_color", Color.magenta),
                            "поле команды снова не знает мнемоник настроения");

            LvnTheme.Use("cyber");
            var акцент = new JObject { ["aura_color"] = "accent" };
            Assert.AreEqual(LvnTheme.Current.Accent,
                            UiColor.FromCmd(акцент, "aura_color", Color.magenta),
                            "поле команды не знает токенов темы");

            var заглавное = new JObject { ["glow_color"] = "Gold" };
            Assert.AreEqual(LvnTheme.Current.Gold,
                            UiColor.FromCmd(заглавное, "glow_color", Color.magenta));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void МусорВПолеКомандыЖалуетсяИНеМеняетЦвет()
        {
            LogAssert.Expect(LogType.Warning, new Regex("lvn-ui"));
            var cmd = new JObject { ["burn_color"] = "зелёненький" };

            Assert.AreEqual(Color.green, UiColor.FromCmd(cmd, "burn_color", Color.green));
        }

        // ── третья дверь: ПОЛЕ МАНИФЕСТА ────────────────────────────────────
        //
        // Сто три поля манифеста (`title_color`, `bg_color`, `prompt_color`…)
        // читались разбором «шестнадцать цифр»: `title_color: "accent"` молча
        // не срабатывал — в скрипте то же слово работало, в манифесте нет,
        // хотя пишет их один человек. Молча — то есть автор видел не ошибку, а
        // «цвет по умолчанию», и искал причину в чём угодно, кроме слова.
        //
        // Проверяется это НАСТОЯЩИМИ экранами и настоящим текстом манифеста, а
        // не рефлексией по полям: поле, которое не доехало из JSON, выглядит
        // ровно так же, как поле, которого экран не прочитал.

        /// <summary>Блок <c>ui</c> из текста манифеста — тем же путём, каким
        /// его читает игра: JSON → <see cref="LvnManifest"/> → конфиг экрана.</summary>
        private static LvnUiConfig Манифест(string ui)
            => JsonConvert.DeserializeObject<LvnManifest>("{\"ui\": " + ui + "}").ui;

        private static Color Фон(VisualElement el) => el.style.backgroundColor.value;
        private static Color Чернила(VisualElement el) => el.style.color.value;

        [Test]
        public void СловоСловаряВПолеМанифестаКраситЭкран()
        {
            // РОВНО ТА ДЫРА. Четыре поля одного экрана, по слову из каждой
            // трети словаря: затемнение, лист, заголовок, текст.
            //
            // Слова выбраны так, чтобы НЕ СОВПАДАТЬ с умолчанием своего поля:
            // «scrim_color: veil» и без словаря дало бы тот же Scrim, и
            // проверка была бы зелёной на пустом месте.
            LvnTheme.Use("cyber");
            var ui = Манифест(@"{ ""popup"": {
                ""scrim_color"": ""bg"", ""panel_color"": ""surface"",
                ""title_color"": ""gold"", ""text_color"": ""warm"" } }");

            var попап = new PopupScreen(ui.popup);

            Assert.AreEqual(LvnTheme.Current.Bg, Фон(попап),
                "затемнение попапа не узнало слова словаря");
            Assert.AreEqual(LvnTheme.Current.Surface, Фон(попап.ElementAt(0)),
                "лист попапа не узнал слова словаря");
            Assert.AreEqual(LvnTheme.Current.Gold, Чернила(попап.Q<Label>("popup-title")),
                "заголовок попапа не узнал слова словаря");
            Assert.AreEqual(UiColor.Named("warm", Color.magenta),
                Чернила(попап.Q<Label>("popup-message")),
                "мнемоника настроения в манифесте по-прежнему молчит");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void СловоИзМанифестаОстаётсяРольюАНеЗастываетКраской()
        {
            // Слово в манифесте — это РОЛЬ («акцент этой игры»), а не краска.
            // Зашей его при сборке экрана — и смена темы перекрасит хаб, но не
            // попап поверх него: экран выйдет наполовину чужим.
            var ui = Манифест(@"{ ""popup"": { ""title_color"": ""accent"" } }");

            LvnTheme.Use("midnight");
            var подПолночью = Чернила(new PopupScreen(ui.popup).Q<Label>("popup-title"));
            Assert.AreEqual(LvnTheme.Current.Accent, подПолночью);

            LvnTheme.Use("cyber");
            var подКибером = Чернила(new PopupScreen(ui.popup).Q<Label>("popup-title"));
            Assert.AreEqual(LvnTheme.Current.Accent, подКибером);

            Assert.AreNotEqual(подПолночью, подКибером,
                "«accent» из манифеста оказался зашитой краской, а не ролью");
        }

        [Test]
        public void ЭкранЗагрузкиЧитаетСловоСловаряАНеОстаётсяЧёрным()
        {
            // Умолчание фона загрузки — ЧЁРНЫЙ. Пока поле читали hex-разбором,
            // «bg_color: surface» давал именно его: игрок видел чёрный экран и
            // считал это задумкой.
            LvnTheme.Use("romance");
            var ui = Манифест(@"{ ""loading"": { ""bg_color"": ""surface"" } }");

            var экран = new LoadingScreen(ui.loading, new TestAssets());

            Assert.AreEqual(LvnTheme.Current.Surface, Фон(экран));
            Assert.AreNotEqual(Color.black, Фон(экран),
                "слово в манифесте снова уходит в умолчание — экран загрузки чёрный");
        }

        [Test]
        public void ЭкранКонцаГлавыЧитаетСловоСловаряВоВсехСвоихПолях()
        {
            // Здесь тоже ни одно слово не совпадает с умолчанием своего поля:
            // «subtitle_color: dim» дало бы TextDim и без словаря.
            LvnTheme.Use("cyber");
            var ui = Манифест(@"{ ""chapter_end"": {
                ""bg_color"": ""panel"", ""title_color"": ""accent"",
                ""subtitle_color"": ""text"", ""button_color"": ""gold"" } }");

            var экран = new ChapterEndScreen(ui.chapter_end, new TestAssets());
            var подписи = экран.Query<Label>().ToList();
            var продолжить = экран.Query<Button>().ToList()[0];

            Assert.AreEqual(LvnTheme.Current.PanelBg, Фон(экран), "фон экрана");
            Assert.AreEqual(LvnTheme.Current.Accent, Чернила(подписи[0]), "заголовок");
            Assert.AreEqual(LvnTheme.Current.Text, Чернила(подписи[1]), "название главы");
            Assert.AreEqual(LvnTheme.Current.Gold, Фон(продолжить), "кнопка «дальше»");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ШестнадцатьЦифрВМанифестеПродолжаютРаботать()
        {
            // Словарь ДОБАВЛЕН, а не поставлен вместо: у всех уже написанных
            // новелл в этих полях стоит hex, и обе записи — с решёткой и без —
            // обязаны читаться как раньше.
            var ui = Манифест(@"{ ""popup"": {
                ""panel_color"": ""#8c3659"", ""title_color"": ""8c3659"" } }");
            var ожидание = UiColor.Named("#8c3659", Color.magenta);

            var попап = new PopupScreen(ui.popup);

            Assert.AreEqual(ожидание, Фон(попап.ElementAt(0)));
            Assert.AreEqual(ожидание, Чернила(попап.Q<Label>("popup-title")),
                "решётку автор ставит не всегда");

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ОпечаткаВМанифестеЖалуетсяИОставляетУмолчаниеЭкрана()
        {
            // Опечатку автор обязан УВИДЕТЬ, а экран — остаться собой: не
            // сработавшее поле не вправе стереть заголовок.
            LogAssert.Expect(LogType.Warning, new Regex("lvn-ui"));
            var ui = Манифест(@"{ ""chapter_end"": { ""title_color"": ""акцентт"" } }");

            var экран = new ChapterEndScreen(ui.chapter_end, new TestAssets());

            Assert.AreEqual(LvnTokens.Text, Чернила(экран.Query<Label>().ToList()[0]),
                "опечатка в манифесте прокрасила заголовок ничем");
        }

        // ── единственное исключение: СБОРКА САМОЙ ТЕМЫ ──────────────────────

        [Test]
        public void СборкаТемыНеСпрашиваетДействующуюТему()
        {
            // Тема — единственное место, откуда словарь звать НЕЛЬЗЯ: он
            // спрашивает цвет у действующей темы, а она в этот момент ещё
            // строится. Правило видно снаружи: тема, собранная при любой
            // другой действующей, обязана выйти одинаковой.
            LvnTheme.Use("midnight");
            var подПолночью = new[] { LvnTheme.Cyber(), LvnTheme.Romance(), LvnTheme.Midnight() };

            LvnTheme.Use("cyber");
            var подКибером = new[] { LvnTheme.Cyber(), LvnTheme.Romance(), LvnTheme.Midnight() };

            for (var i = 0; i < подПолночью.Length; i++)
            {
                Assert.AreEqual(подПолночью[i].Accent, подКибером[i].Accent,
                    $"тема «{подПолночью[i].Name}» собралась из ДЕЙСТВУЮЩЕЙ темы — круг замкнулся");
                Assert.AreEqual(подПолночью[i].Bg, подКибером[i].Bg);
                Assert.AreEqual(подПолночью[i].Text, подКибером[i].Text);
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ИмяТемыВМанифестеРазбираетсяКакИмяАНеКакЦвет()
        {
            // `ui.browse.theme` — имя темы, и слово словаря там не имя: «accent»
            // это не тема. Ответ — умолчание, а не пустой экран и не круг
            // «тема спрашивает цвет, цвет спрашивает тему».
            LvnTheme.Use("cyber");
            var ui = Манифест(@"{ ""browse"": { ""theme"": ""accent"" } }");

            var тема = LvnTheme.ByName(ui.browse.theme);

            Assert.AreEqual("midnight", тема.Name, "«accent» — слово цвета, а не имя темы");
            Assert.AreEqual(LvnTheme.Midnight().Accent, тема.Accent);
            Assert.AreNotEqual(LvnTheme.Current.Accent, тема.Accent,
                "тема собралась из действующей темы");
        }
    }
}
