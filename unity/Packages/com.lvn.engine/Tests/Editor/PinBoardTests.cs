using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Lvn.UI;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ДОСКА ЗАКРЕПЛЕНИЯ — <see cref="LvnPinBoard{TKey}"/>.
    ///
    /// <para>Проверяется ПОРЯДОК, а не бухгалтерия. Наборы пересекаются:
    /// пересборка облика оставляет те же слои, перестройка скелета — те же
    /// страницы атласа. Если отпустить прежний набор раньше, чем прикрепить
    /// новый, счётчик общего спрайта на мгновение доходит до нуля — и
    /// стриминговое окно вправе забрать текстуру именно там. Сцена это знала,
    /// скелеты делали наоборот; проверка нужна ровно потому, что разница
    /// невидима глазом и проявляется белым прямоугольником раз в сто
    /// показов.</para>
    /// </summary>
    public class PinBoardTests
    {
        /// <summary>Подставной загрузчик: считает пины по спрайту и, главное,
        /// ЗАПОМИНАЕТ, доходил ли счётчик до нуля хоть на мгновение.</summary>
        private sealed class Counting : Lvn.Content.ILvnPinLedger
        {
            public readonly Dictionary<Sprite, int> Pins = new Dictionary<Sprite, int>();
            public readonly HashSet<Sprite> TouchedZero = new HashSet<Sprite>();

            public void PinSprite(Sprite s, bool pinned)
            {
                Pins.TryGetValue(s, out var n);
                n += pinned ? 1 : -1;
                if (n < 0) n = 0;
                Pins[s] = n;
                if (n == 0) TouchedZero.Add(s);
            }
        }

        private static Sprite Made(string name)
        {
            var tex = new Texture2D(2, 2);
            var s = Sprite.Create(tex, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            s.name = name;
            return s;
        }

        private Sprite _a, _b, _c;
        private Counting _led;
        private LvnPinBoard<string> _board;

        [SetUp]
        public void Setup()
        {
            _a = Made("a"); _b = Made("b"); _c = Made("c");
            _led = new Counting();
            _board = new LvnPinBoard<string>(0f);
        }

        [TearDown]
        public void Teardown()
        {
            foreach (var s in new[] { _a, _b, _c })
                if (s != null) { Object.DestroyImmediate(s.texture); Object.DestroyImmediate(s); }
        }

        [Test]
        public void Общий_спрайт_переживает_замену_набора()
        {
            _board.Hold("облик", _led, new[] { _a, _b });
            _board.Hold("облик", _led, new[] { _a, _c });   // слой «a» остался тот же

            Assert.IsFalse(_led.TouchedZero.Contains(_a),
                "общий спрайт на мгновение остался без держателей — окно вправе "
                + "забрать текстуру именно в этот миг, и актёр встанет белым");
            Assert.AreEqual(1, _led.Pins[_a], "у общего слоя должен остаться ровно один держатель");
            Assert.AreEqual(0, _led.Pins[_b], "ушедший слой не отпущен");
            Assert.AreEqual(1, _led.Pins[_c]);
        }

        [Test]
        public void Пустой_набор_равен_отпусканию()
        {
            _board.Hold("облик", _led, new[] { _a });
            _board.Hold("облик", _led, new Sprite[0]);
            Assert.AreEqual(0, _led.Pins[_a]);
            Assert.IsFalse(_board.Holds("облик"), "пустой набор оставил ключ на доске");
        }

        [Test]
        public void Отпускание_чужого_ключа_ничего_не_делает()
        {
            _board.Hold("облик", _led, new[] { _a });
            _board.Release("другой");
            Assert.AreEqual(1, _led.Pins[_a], "отпустили не тот набор");
        }

        [Test]
        public void Ключи_отдаются_копией()
        {
            _board.Hold("bg", _led, new[] { _a });
            _board.Hold("actor:hill", _led, new[] { _b });
            // Обходя доску, как раз и отпускают — правка словаря во время его
            // же обхода была бы исключением на ровном месте.
            foreach (var k in _board.Keys())
                if (k != "bg") _board.Release(k);
            Assert.AreEqual(new[] { "bg" }, _board.Keys().ToArray());
            Assert.AreEqual(1, _led.Pins[_a]);
            Assert.AreEqual(0, _led.Pins[_b]);
        }

        [Test]
        public void Отпускает_тот_кто_держал()
        {
            // Смена содержимого меняет загрузчика под ногами. Снятие через
            // ТЕКУЩИЙ вернуло бы счётчик не туда: прежний держал бы текстуру
            // вечно, а новому пришёл бы минус за то, чего он не давал.
            var first = new Counting();
            var second = new Counting();
            var board = new LvnPinBoard<string>(0f);
            board.Hold("bg", first, new[] { _a });
            board.Hold("bg", second, new[] { _b });

            Assert.AreEqual(0, first.Pins[_a], "прежний держатель не получил своего минуса");
            Assert.IsFalse(second.Pins.ContainsKey(_a),
                "минус за чужой спрайт ушёл новому загрузчику");
            Assert.AreEqual(1, second.Pins[_b]);
        }

        [Test]
        public void Задержка_откладывает_только_отпускание()
        {
            var slow = new LvnPinBoard<string>(5f);
            slow.Hold("облик", _led, new[] { _a });
            slow.Hold("облик", _led, new[] { _b });
            Assert.AreEqual(1, _led.Pins[_a],
                "прежний набор отпущен сразу — прокси кроссфейда ещё показывает его слои");
            Assert.AreEqual(1, _led.Pins[_b], "новый набор должен прикрепляться БЕЗ задержки");
        }
    }
}
