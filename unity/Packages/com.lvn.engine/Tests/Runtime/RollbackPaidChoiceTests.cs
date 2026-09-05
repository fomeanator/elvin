using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗА ОДНУ ВЕТКУ ПЛАТЯТ ОДИН РАЗ.
    ///
    /// <para>Платный выбор — настоящие деньги игрока. Рядом с ним живёт откат
    /// («назад»), который возвращает на развилку вместе с деньгами на счету, но
    /// НЕ вместе с купленной веткой. Замер 05.09: игрок оплатил вариант,
    /// откатился, ткнул тот же — кошелёк списал второй раз.</para>
    ///
    /// <para>Отметка об оплате живёт вне снимков отката (иначе он унёс бы и
    /// её) и до конца главы: новую главу начинают с чистого счёта.</para>
    /// </summary>
    public class RollbackPaidChoiceTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private int _списаний;

        private const string Глава = @"{
          ""scene"": ""откат"",
          ""script"": [
            { ""op"": ""say"", ""text"": ""перед развилкой"" },
            { ""op"": ""choice"", ""options"": [
              { ""text"": ""платный"", ""goto"": ""P"",
                ""wallet_cost"": { ""currency"": ""тесткоин"", ""amount"": 25 } },
              { ""text"": ""даром"", ""goto"": ""F"" } ] },
            { ""op"": ""label"", ""id"": ""P"" },
            { ""op"": ""say"", ""text"": ""платная ветка"" },
            { ""op"": ""goto"", ""label"": ""КОНЕЦ"" },
            { ""op"": ""label"", ""id"": ""F"" },
            { ""op"": ""say"", ""text"": ""даровая ветка"" },
            { ""op"": ""label"", ""id"": ""КОНЕЦ"" }
          ]
        }";

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _списаний = 0;
            _stage = TestStage.Panel("rollback-paid-stage", out _go, out _panel);
            _stage.ChoiceSpend = (валюта, сумма) => { _списаний++; return Task.FromResult(true); };
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

        private static IEnumerator Ждём(Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        private IEnumerator ДоЖивогоВыбора(string надпись)
        {
            yield return Ждём(() => Вариант(надпись) != null && Вариант(надпись).enabledInHierarchy, 8f);
        }

        /// Откатился и выбрал ТОТ ЖЕ платный вариант — платит один раз.
        [UnityTest]
        public IEnumerator ОткатИПовторныйВыборНеПлатятДважды()
        {
            _stage.Play(Глава);
            yield return ДоЖивогоВыбора("платный");
            Assert.IsNotNull(Вариант("платный"), "стенд: развилка не открылась");

            TestStage.Press(Вариант("платный"))?.Invoke();
            yield return Ждём(() => ВИстории("платная ветка"), 6f);
            TestContext.WriteLine($"после первой оплаты: списаний {_списаний}, "
                                + $"платная ветка={ВИстории("платная ветка")}");

            bool можно = _stage.CanRollback;
            bool откатили = _stage.RollbackStep();
            yield return Ждём(() => Вариант("платный") != null && Вариант("платный").enabledInHierarchy, 6f);
            TestContext.WriteLine($"откат: можно={можно}, получилось={откатили}, "
                                + $"развилка снова на экране={Вариант("платный") != null}");

            if (Вариант("платный") != null && Вариант("платный").enabledInHierarchy)
            {
                TestStage.Press(Вариант("платный"))?.Invoke();
                yield return Ждём(() => _списаний > 1, 6f);
            }
            TestContext.WriteLine($"после повторного выбора того же варианта: списаний {_списаний}");

            Assert.IsTrue(откатили, "откат не сработал — проверять нечего");
            Assert.AreEqual(1, _списаний,
                "за одну и ту же ветку заплатили дважды: откат вернул развилку, а память об оплате — нет");
            yield return Ждём(() => ВИстории("платная ветка"), 6f);
            Assert.IsTrue(ВИстории("платная ветка"), "повторный выбор не открыл оплаченную ветку");
        }

        /// Ушёл в даровую ветку, откатился, выбрал платную — платит один раз
        /// (первая оплата уже была).
        [UnityTest]
        public IEnumerator ВозвратКПлатнойВеткеНеПлатитСнова()
        {
            _stage.Play(Глава);
            yield return ДоЖивогоВыбора("платный");
            TestStage.Press(Вариант("платный"))?.Invoke();
            yield return Ждём(() => ВИстории("платная ветка"), 6f);
            Assert.AreEqual(1, _списаний, "стенд: первая оплата не прошла");

            _stage.RollbackStep();
            yield return ДоЖивогоВыбора("даром");
            TestStage.Press(Вариант("даром"))?.Invoke();
            yield return Ждём(() => ВИстории("даровая ветка"), 6f);

            _stage.RollbackStep();
            yield return ДоЖивогоВыбора("платный");
            TestStage.Press(Вариант("платный"))?.Invoke();
            yield return Ждём(() => _списаний > 1, 3f);

            Assert.AreEqual(1, _списаний,
                "вернулся к уже оплаченной ветке — и заплатил за неё второй раз");
        }

        /// Отказ кошелька оплаченным не считается: денег не хватило — ветка
        /// закрыта, и следующая попытка снова идёт в кассу.
        [UnityTest]
        public IEnumerator ОтказКошелькаНеСчитаетсяОплатой()
        {
            _stage.ChoiceSpend = (валюта, сумма) => { _списаний++; return Task.FromResult(false); };
            _stage.Play(Глава);
            yield return ДоЖивогоВыбора("платный");

            TestStage.Press(Вариант("платный"))?.Invoke();
            yield return Ждём(() => _списаний >= 1, 6f);
            yield return ДоЖивогоВыбора("платный");
            TestStage.Press(Вариант("платный"))?.Invoke();
            yield return Ждём(() => _списаний >= 2, 6f);

            Assert.AreEqual(2, _списаний,
                "после отказа кошелька вторая попытка не дошла до кассы — ветка стала бесплатной");
            Assert.IsFalse(ВИстории("платная ветка"), "неоплаченная ветка всё-таки открылась");
        }
    }
}
