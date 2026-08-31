using System;
using System.Collections.Generic;
using System.Reflection;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧЕГО СЦЕНА ЖДЁТ — два названных ответа вместо четырёх конъюнкций.
    ///
    /// <para>Пара флагов ожидания (`wait` идёт / открыта форма ввода)
    /// складывалась в четырёх местах, и в каждом чуть по-своему. Теперь у
    /// вопроса два имени: «сцена занята сама собой» (ни листание, ни авточтение
    /// работать не должны) и «тап сейчас не наш».</para>
    ///
    /// <para>Второе имя несёт ОГОВОРКУ, ради которой всё и затевалось: `wait`
    /// глотает касание, КРОМЕ экрана с горячими точками. Там щелчок обязан
    /// дойти до точки и снять таймер — иначе поиск предмета замирает навсегда,
    /// и это не «мелкое неудобство», а глухой экран без выхода.</para>
    ///
    /// <para>ЧЕМ МЕРЯЕМ. Правила проверяются через ЖИВЫЕ ворота — настоящие
    /// <c>HandleTap</c> и <c>SkipTick</c> на настоящей сцене с настоящим чтецом,
    /// а не через чтение свойства. Сцена собирается ВЫКЛЮЧЕННОЙ (SetActive(false)
    /// до AddComponent): жизненный цикл MonoBehaviour в EditMode не запускается,
    /// панель UITK не строится — а обоим воротам панель и не нужна.</para>
    ///
    /// <para>ЧЕГО МЕРЯТЬ НЕЛЬЗЯ. Попадание по горячей точке — можно только в
    /// PlayMode: <c>StagePoint</c> делит на размер панели, а у отвязанного корня
    /// он нулевой, и точка попадания не считается вовсе. Поэтому оговорка
    /// закреплена по достижимому следствию: касание ДОХОДИТ до слоя точек
    /// (маршрутизатор пишет свою строку в журнал) — вместо того, чтобы быть
    /// проглоченным этажом выше. Промах при этом историю не двигает, и это тоже
    /// правда экрана поиска: `wait` тикает дальше.</para>
    /// </summary>
    public class StageWaitTapTests
    {
        private GameObject _go;
        private VnStage _stage;
        private RecordingStage _seen;
        private LvnPlayer _player;
        private Action<string> _prevLog;
        private List<string> _log;

        /// <summary>Чтец ведёт СВОЮ заглушку — так видно, продвинулась история
        /// или нет, не поднимая ни одной живой поверхности.</summary>
        private sealed class RecordingStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command, LvnSender sender) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        [SetUp]
        public void SetUp()
        {
            // ВЫКЛЮЧЕННОЙ — иначе OnEnable полезет строить панель, которой в
            // EditMode нет, и тест упал бы не на том, что проверяет.
            _go = new GameObject("wait-tap-stage");
            _go.SetActive(false);
            _stage = _go.AddComponent<VnStage>();

            _seen = new RecordingStage();
            _player = new LvnPlayer(LvnDocument.Parse(
                "{\"script\":[" +
                "{\"op\":\"say\",\"text\":\"первая\"}," +
                "{\"op\":\"say\",\"text\":\"вторая\"}," +
                "{\"op\":\"say\",\"text\":\"третья\"}]}"), _seen);
            _player.Advance();                       // первая реплика на экране
            Set("_player", _player);
            Set("_awaitingTapFlag", true);           // такт ждёт касания

            _prevLog = LvnPlayer.Log;
            _log = new List<string>();
            LvnPlayer.Log = s => _log.Add(s);
        }

        [TearDown]
        public void TearDown()
        {
            LvnPlayer.Log = _prevLog;
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
        }

        // ── правила ─────────────────────────────────────────────────────────

        /// <summary>Пауза автора не проматывается касанием.</summary>
        [Test]
        public void ВоВремяWaitБезГорячихТочекТапИсториюНеДвигает()
        {
            Assert.IsFalse(_stage.InputBlocked, "sanity: ввод никто не держит");
            Set("_awaitingWait", true);

            Tap();

            Assert.AreEqual(1, _seen.Lines.Count,
                "касание проскочило паузу, которую поставил автор");
        }

        /// <summary>ОГОВОРКА. На экране с горячими точками `wait` касание НЕ
        /// глотает: щелчок обязан дойти до точки и снять таймер — иначе поиск
        /// предмета замирает навсегда.</summary>
        [Test]
        public void ВоВремяWaitСГорячейТочкойЩелчокДоНеёДоходит()
        {
            Set("_awaitingWait", true);
            AddHotspot("сундук");
            Set("_uiRoot", new UnityEngine.UIElements.VisualElement());

            Assert.IsFalse(TapNotOurs,
                "экран с горячими точками остаётся кликабельным сквозь `wait`");

            Tap();

            Assert.IsTrue(_log.Exists(l => l.Contains("[click")),
                "касание не дошло до слоя горячих точек — его проглотили этажом выше, " +
                "и поиск предмета замер бы навсегда");
            Assert.AreEqual(1, _seen.Lines.Count,
                "промах мимо точки историю всё равно не двигает: `wait` тикает дальше");
        }

        /// <summary>Форма ввода забирает касание ЦЕЛИКОМ — и на обычном экране,
        /// и на экране с точками. Иначе тап съел бы строку, которую игрок ещё
        /// печатает.</summary>
        [Test]
        public void ПриОткрытойФормеВводаТапНеДвигаетНиНаКакомЭкране()
        {
            Set("_awaitingInput", true);
            Tap();
            Assert.AreEqual(1, _seen.Lines.Count, "форма ввода: касание не наше");

            AddHotspot("сундук");
            Set("_uiRoot", new UnityEngine.UIElements.VisualElement());
            Assert.IsTrue(TapNotOurs,
                "горячие точки НЕ отменяют форму ввода — иначе щелчок мимо поля " +
                "увёл бы игрока со строки, которую он набирает");
            Tap();
            Assert.AreEqual(1, _seen.Lines.Count, "и с точками тоже");
        }

        /// <summary>Опора для трёх правил выше: без ожидания те же ворота тап
        /// ПРОПУСКАЮТ. Без неё «не продвинулось» доказывало бы лишь то, что
        /// сцена не продвигается никогда.</summary>
        [Test]
        public void БезОжиданияТотЖеТапИсториюДвигает()
        {
            Tap();
            Assert.AreEqual(2, _seen.Lines.Count, "обычное касание обязано листать");
        }

        /// <summary>«Сцена занята сама собой» — это ПАУЗА пропуска, а не его
        /// отмена: перемотка обязана продолжиться сама, когда `wait` истечёт.</summary>
        [Test]
        public void ПропускНаВремяWaitВстаётНоНеОтменяется()
        {
            _stage.StartSkip();
            Assert.IsTrue(_stage.Skipping, "sanity: перемотка пошла");

            Set("_awaitingWait", true);
            Call("SkipTick");
            Assert.AreEqual(1, _seen.Lines.Count, "перемотка проскочила паузу автора");
            Assert.IsTrue(_stage.Skipping, "пауза не должна ГАСИТЬ перемотку — только держать");

            Set("_awaitingWait", false);
            Call("SkipTick");
            Assert.AreEqual(2, _seen.Lines.Count, "пауза кончилась — перемотка идёт дальше");
        }

        /// <summary>Форма ввода держит перемотку так же, как `wait`: иначе она
        /// пролистала бы строку, которую игрок ещё печатает.</summary>
        [Test]
        public void ПропускПриОткрытойФормеВводаНеИдёт()
        {
            _stage.StartSkip();
            Set("_awaitingInput", true);

            Call("SkipTick");

            Assert.AreEqual(1, _seen.Lines.Count, "перемотка съела строку, которую набирают");
            Assert.IsTrue(_stage.Skipping, "и здесь пауза, а не отмена");
        }

        /// <summary>Горячие точки — оговорка ТОЛЬКО про касание. «Сцена занята
        /// сама собой» их не знает: перемотка на экране поиска обязана стоять
        /// ровно так же, иначе она проскочит и таймер, и сам предмет.</summary>
        [Test]
        public void ЗанятостьСценыГорячимиТочкамиНеОтменяется()
        {
            AddHotspot("сундук");
            Set("_awaitingWait", true);

            Assert.IsTrue(StageBusy,
                "сцена занята `wait` независимо от точек — это про листание, не про тап");
            Assert.IsFalse(TapNotOurs,
                "а тап при тех же флагах наш: два вопроса, два разных ответа");

            _stage.StartSkip();
            Call("SkipTick");
            Assert.AreEqual(1, _seen.Lines.Count,
                "перемотка на экране поиска обязана стоять — точки её не отпирают");
        }

        // ── помощники ───────────────────────────────────────────────────────

        private void Tap() => Call("HandleTap", Vector2.zero);

        private void AddHotspot(string id)
        {
            var list = (List<(string id, Action onClick)>)Field("_hotspots").GetValue(_stage);
            list.Add((id, () => { }));
        }

        private bool StageBusy => (bool)Prop("StageBusy").GetValue(_stage);
        private bool TapNotOurs => (bool)Prop("TapNotOurs").GetValue(_stage);

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo Field(string name)
        {
            for (Type t = typeof(VnStage); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any);
                if (f != null) return f;
            }
            Assert.Fail($"поле {name} у VnStage пропало — поправь якорь теста");
            return null;
        }

        private static PropertyInfo Prop(string name)
        {
            PropertyInfo p = typeof(VnStage).GetProperty(name, Any);
            if (p == null) Assert.Fail($"свойство {name} у VnStage пропало — поправь якорь теста");
            return p;
        }

        private void Set(string field, object value) => Field(field).SetValue(_stage, value);

        private void Call(string method, params object[] args)
        {
            MethodInfo m = typeof(VnStage).GetMethod(method, Any);
            if (m == null) Assert.Fail($"метод {method} у VnStage пропал — поправь якорь теста");
            m.Invoke(_stage, args);
        }
    }
}
