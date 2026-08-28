using System.Collections;
using System.Collections.Generic;
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
    /// ПАРА «РЕПЛИКА + ВЫБОР» ПОКАЗЫВАЕТ ИМЕННО СВОЮ РЕПЛИКУ — ВИЗУАЛЬНО.
    ///
    /// <para>Плеерный порядок событий давно закреплён (ChoiceBeatPairingTests),
    /// но живой скрин показал дыру уровнем ниже: ShowSay при видимом окне
    /// откладывает новый текст до конца анимации падения карточки, а ShowChoice
    /// того же такта перебивал поколение — отложенная реплика погибала, и
    /// карточка возвращалась со СТАРЫМ текстом («варианты повисли под
    /// предыдущей репликой»). Этот тест гоняет НАСТОЯЩУЮ карточку.</para>
    /// </summary>
    public class SayChoicePairVisualTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private const string Script = @"{
          ""scene"": ""pair"",
          ""script"": [
            { ""op"": ""say"", ""who"": ""A"", ""text"": ""line one"" },
            { ""op"": ""say"", ""who"": ""A"", ""text"": ""line two"" },
            { ""op"": ""choice"", ""options"": [
              { ""text"": ""left"", ""goto"": ""L"" },
              { ""text"": ""right"", ""goto"": ""R"" } ] },
            { ""op"": ""label"", ""id"": ""L"" },
            { ""op"": ""label"", ""id"": ""R"" },
            { ""op"": ""say"", ""text"": ""tail"" }
          ]
        }";

        [UnitySetUp]
        public IEnumerator Boot()
        {
            _stage = TestStage.Panel("pair-stage", out _go, out _panel);
            yield return null;
            _stage.Play(Script);
            yield return null;
        }

        [TearDown]
        public void Cleanup()
        {
            if (_go != null) Object.Destroy(_go);
            if (_panel != null) Object.Destroy(_panel);
        }

        private T Field<T>(string name) where T : class
            => typeof(VnStage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(_stage) as T;

        private string CardText()
        {
            var box = Field<VisualElement>("_dialogue");
            if (box == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var l in box.Query<Label>().ToList())
                if (!string.IsNullOrEmpty(l.text)) sb.Append(l.text).Append('\n');
            return sb.ToString();
        }

        [UnityTest]
        public IEnumerator ChoicePromptLine_IsShown_NotThePreviousLine()
        {
            // Реплика 1 на экране; продвигаем — реплика 2 и выбор идут одним тактом.
            var player = Field<LvnPlayer>("_player");
            Assert.IsNotNull(player, "стейдж играет скрипт");
            player.Advance();

            // Падение старой карточки + пауза + подъём: даём хореографии дожить,
            // но не завязываемся на точные длительности — опрос с потолком.
            float deadline = Time.realtimeSinceStartup + 3f;
            IReadOnlyList<LvnOption> choices = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                choices = Field<IReadOnlyList<LvnOption>>("_curChoices");
                if (choices != null && choices.Count > 0 && CardText().Contains("line two")) break;
                yield return null;
            }

            Assert.IsNotNull(choices, "варианты вышли на экран");
            Assert.AreEqual(2, choices.Count, "оба варианта на месте");
            StringAssert.Contains("line two", CardText(),
                "карточка обязана показать реплику выбора — не предыдущую");
            StringAssert.DoesNotContain("line one", CardText(),
                "старая реплика не смеет вернуться под вариантами");
        }
    }
}
