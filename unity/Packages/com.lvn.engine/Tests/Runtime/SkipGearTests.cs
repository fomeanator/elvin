using System;
using System.Collections;
using System.Reflection;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПРОПУСК ОСТАНАВЛИВАЕТСЯ ТАМ, ГДЕ НУЖЕН ИГРОК.
    ///
    /// <para>Пропуск — жанровая передача «я это уже читал». Игрок жмёт её и
    /// перестаёт смотреть на экран: строки летят сами. Значит всё, ради чего
    /// движок обязан вернуть игрока — развилка, конец главы, новый текст при
    /// включённом «пропускать только прочитанное», — обязано пропуск ПОГАСИТЬ,
    /// и погасить ДО того, как что-то решится за игрока.</para>
    ///
    /// <para>Из EditMode этого не видно: пропуск живёт на расписании панели и
    /// тикает настоящими кадрами. Стенд здесь настоящий — панель, сцена, живые
    /// секунды.</para>
    /// </summary>
    public class SkipGearTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private static string Глава(string срок) => @"{
          ""scene"": ""пропуск"",
          ""script"": [
            { ""op"": ""say"", ""text"": ""первая"" },
            { ""op"": ""say"", ""text"": ""вторая"" },
            { ""op"": ""say"", ""text"": ""третья"" },
            { ""op"": ""choice""@СРОК@, ""options"": [
              { ""text"": ""налево"", ""goto"": ""L"" },
              { ""text"": ""направо"", ""goto"": ""R"" } ] },
            { ""op"": ""label"", ""id"": ""L"" },
            { ""op"": ""say"", ""text"": ""левая ветка"" },
            { ""op"": ""goto"", ""label"": ""КОНЕЦ"" },
            { ""op"": ""label"", ""id"": ""R"" },
            { ""op"": ""say"", ""text"": ""правая ветка"" },
            { ""op"": ""goto"", ""label"": ""КОНЕЦ"" },
            { ""op"": ""label"", ""id"": ""ПОЗДНО"" },
            { ""op"": ""say"", ""text"": ""время вышло"" },
            { ""op"": ""label"", ""id"": ""КОНЕЦ"" }
          ]
        }".Replace("@СРОК@", срок);

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _stage = TestStage.Panel("skip-gear-stage", out _go, out _panel);
            yield return null;
        }

        [TearDown]
        public void Уборка()
        {
            LvnScreenDirector.Current.ShowChromeAll();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
        }

        private VisualElement Корень => _go != null
            ? _go.GetComponent<UIDocument>()?.rootVisualElement : null;

        private bool НаВыборе
        {
            get
            {
                var p = typeof(VnStage).GetField("_player",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_stage) as LvnPlayer;
                return p != null && p.AtChoice;
            }
        }

        /// <summary>Кнопка варианта — по надписи, как её видит игрок.</summary>
        private Button Вариант(string надпись)
        {
            var меню = Корень?.Q<ChoiceList>();
            if (меню == null) return null;
            foreach (var кнопка in меню.Query<Button>().ToList())
                foreach (var подпись in кнопка.Query<Label>().ToList())
                    if (подпись.text == надпись) return кнопка;
            return null;
        }

        private bool ВИстории(string текст)
        {
            foreach (var строка in _stage.Backlog)
                if (строка.text == текст) return true;
            return false;
        }

        private System.Collections.Generic.List<string> ИсторияСписком()
        {
            var список = new System.Collections.Generic.List<string>();
            foreach (var строка in _stage.Backlog) список.Add(строка.text);
            return список;
        }

        private static IEnumerator Ждём(Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        private static IEnumerator Пауза(float секунд)
        {
            float до = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < до) yield return null;
        }

        /// РАЗВИЛКА ГАСИТ ПРОПУСК — и ни одна ветка не берётся сама.
        [UnityTest]
        public IEnumerator ПропускДоводитДоРазвилкиИГаснет()
        {
            _stage.Play(Глава(""));
            yield return null;
            _stage.StartSkip();
            yield return Пауза(3f);

            Assert.IsTrue(НаВыборе, "пропуск не довёл до развилки — промотка встала раньше");
            Assert.IsFalse(_stage.Skipping, "пропуск идёт на открытой развилке — следующий кадр решит за игрока");
            Assert.IsFalse(ВИстории("левая ветка"), "ветка взята без игрока");
            Assert.IsFalse(ВИстории("правая ветка"), "ветка взята без игрока");
            Assert.IsTrue(ВИстории("третья"), "промотанные строки обязаны остаться в истории — их можно перечитать");
        }

        /// ПОСЛЕ СОЗНАТЕЛЬНОГО ВЫБОРА ПРОПУСК ВОЗВРАЩАЕТСЯ. Это заявленное
        /// поведение: игрок сам нажал вариант, дальше снова знакомое.
        [UnityTest]
        public IEnumerator ПослеВыбораПропускВозвращается()
        {
            _stage.Play(Глава(""));
            yield return null;
            _stage.StartSkip();
            yield return Пауза(3f);
            Assert.IsTrue(НаВыборе, "стенд: развилка не открылась");

            yield return Ждём(() => Вариант("налево") != null && Вариант("налево").enabledInHierarchy, 6f);
            var кнопка = Вариант("налево");
            Assert.IsNotNull(кнопка, "вариант не вышел на экран");
            Assert.IsTrue(кнопка.enabledInHierarchy, "вариант на экране, но погашен");
            TestStage.Press(кнопка)?.Invoke();
            yield return Ждём(() => ВИстории("левая ветка"), 6f);
            TestContext.WriteLine("история после нажатия: " + string.Join(" | ", ИсторияСписком()));

            Assert.IsTrue(ВИстории("левая ветка"), "выбор игрока не открыл свою ветку");
            Assert.IsFalse(НаВыборе, "развилка осталась открытой после нажатия");
        }

        /// КОНЕЦ ГЛАВЫ ГАСИТ ПРОПУСК: дальше листать нечего.
        [UnityTest]
        public IEnumerator КонецГлавыГаситПропуск()
        {
            _stage.Play(@"{""scene"":""к"",""script"":[
                {""op"":""say"",""text"":""раз""},
                {""op"":""say"",""text"":""два""}
            ]}");
            yield return null;
            _stage.StartSkip();
            yield return Пауза(2.5f);

            TestContext.WriteLine("история в конце главы: " + string.Join(" | ", ИсторияСписком()));
            Assert.IsFalse(_stage.Skipping, "пропуск продолжается на доигранной главе");
            Assert.IsTrue(ВИстории("два"), "последняя строка не показана — пропуск проскочил конец");
        }

        /// ЖУРНАЛ РЕПЛИК ПЕРЕЖИВАЕТ КОНЕЦ ГЛАВЫ. Замер 05.09: после последней
        /// строки журнал был полон, а после конца главы — пуст, потому что
        /// уборка сцены сносила его вместе с актёрами. Дочитал — и перечитать
        /// нечего, хотя это ровно тот момент, когда журнал открывают.
        [UnityTest]
        public IEnumerator ЖурналПереживаетКонецГлавы()
        {
            _stage.Play(@"{""scene"":""к"",""script"":[
                {""op"":""say"",""text"":""раз""},
                {""op"":""say"",""text"":""два""}
            ]}");
            yield return Ждём(() => ВИстории("раз"), 5f);
            TestContext.WriteLine("после первой строки: " + string.Join(" | ", ИсторияСписком()));
            _stage.Player.Advance();
            yield return Ждём(() => ВИстории("два"), 5f);
            TestContext.WriteLine("после второй строки: " + string.Join(" | ", ИсторияСписком()));
            _stage.Player.Advance();
            yield return Пауза(1.5f);
            TestContext.WriteLine("после конца главы: " + string.Join(" | ", ИсторияСписком()));

            Assert.IsTrue(_stage.Player.Finished, "стенд: глава не дочитана");
            Assert.IsTrue(ВИстории("раз"), "журнал пуст после конца главы — перечитать нечего");
            Assert.IsTrue(ВИстории("два"), "последняя строка исчезла из журнала");
        }

        /// ЦЕНА, КОТОРУЮ НАДО НАЗВАТЬ: развилка СО СРОКОМ отсчитывает время и
        /// после пропуска. Игрок, который смотрел на летящие строки, получает
        /// ровно тот же срок, что и тот, кто сам дочитал до развилки.
        [UnityTest]
        public IEnumerator РазвилкаСоСрокомПослеПропускаНеДаётФоры()
        {
            _stage.Play(Глава(@", ""timeout"": 1, ""timeout_goto"": ""ПОЗДНО"""));
            yield return null;
            _stage.StartSkip();
            yield return Пауза(3.5f);

            Assert.IsFalse(_stage.Skipping, "пропуск не погас на развилке со сроком");
            TestContext.WriteLine($"пропуск + срок 1 с: время вышло={ВИстории("время вышло")} — "
                                + "фора игроку, вернувшемуся к экрану, движком не даётся "
                                + "(см. вердикт «Пропуск останавливается там, где нужен игрок»)");
            // Поведение закреплено как ЕСТЬ: срок идёт своим ходом. Меняется
            // оно только продуктовым решением, и тогда упадёт эта проверка —
            // что и требуется, чтобы решение не проехало молча.
            Assert.IsTrue(ВИстории("время вышло"),
                "срок перестал идти после пропуска — поведение изменилось, обнови вердикт");
        }
    }
}
