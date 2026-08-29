using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// НАЛОЖЕНИЕ НЕ ПЕРЕЖИВАЕТ СВОЙ ЭКРАН.
    ///
    /// <para>Живой сценарий, ради которого тесты и написаны: игрок открывает
    /// карточку новеллы, жмёт «Начать заново», видит подтверждение — и вместо
    /// кнопки «Отмена» жмёт системную «назад». Подтверждение ставил экран сам,
    /// снимала его ровно одна кнопка внутри карточки, а «назад» закрывала весь
    /// экран мимо неё. Карточка оставалась невидимым ребёнком закрытого экрана
    /// и всплывала при следующем открытии — уже поверх ДРУГОЙ новеллы, потому
    /// что пересборка страницы чистит ленту, а не корень.</para>
    ///
    /// <para>Оба обещания проверяются здесь, потому что оба принадлежат базе:
    /// наследников с наложением станет больше, а помнить про пять выходов из
    /// экрана — работа не для того, кто пишет очередное подтверждение.</para>
    /// </summary>
    public sealed class OverlayLayerTests
    {
        /// <summary>Экран-пустышка: тело не нужно, проверяем жизненный цикл.</summary>
        private sealed class Probe : LvnOverlayScreen
        {
            public int ClosedCalls;
            public VisualElement Put()
            {
                var card = new VisualElement();
                PutOverlay(card);
                return card;
            }
            public void Drop() => DropOverlay();
            public bool Standing => HasOverlay;
            public void LeaveAsTab() => HideAsTab();
            protected override void OnClosed() => ClosedCalls++;
        }

        [Test]
        public void УходСоЭкранаСнимаетНаложение()
        {
            var screen = new Probe();
            var card = screen.Put();
            Assert.AreSame(screen, card.parent, "наложение обязано лежать в экране");

            screen.LeaveAsTab();

            Assert.IsNull(card.parent, "наложение осталось висеть в закрытом экране");
            Assert.IsFalse(screen.Standing);
            Assert.AreEqual(1, screen.ClosedCalls, "наследнику всё ещё сообщают о закрытии");
        }

        [Test]
        public void НазадСнимаетТолькоВерхнийСлой()
        {
            var screen = new Probe();
            var card = screen.Put();

            screen.RequestCancel();

            Assert.IsNull(card.parent, "«назад» обязана снять подтверждение");
            Assert.AreEqual(0, screen.ClosedCalls,
                "«назад» под открытым подтверждением закрыла весь экран — " +
                "игрок ответил не на тот вопрос, который видел");
        }

        [Test]
        public void БезНаложенияНазадЗакрываетЭкран()
        {
            var screen = new Probe();

            screen.RequestCancel();

            // Экран не открывали, поэтому до OnClosed дело не доходит — важно
            // ровно то, что отмена НЕ проглочена: наложения не было.
            Assert.IsFalse(screen.Standing);
        }

        [Test]
        public void ВторойПоказНеПоказываетЧужогоНаложения()
        {
            var screen = new Probe();
            screen.Put();
            screen.LeaveAsTab();

            var second = screen.Put();

            Assert.AreEqual(1, screen.childCount,
                "в экране больше одного наложения — прежнее не убрали");
            Assert.AreSame(screen, second.parent);
        }

        [Test]
        public void НовоеНаложениеВытесняетПрежнее()
        {
            var screen = new Probe();
            var first = screen.Put();
            var second = screen.Put();

            Assert.IsNull(first.parent, "два подтверждения одновременно не бывают");
            Assert.AreSame(screen, second.parent);
        }
    }
}
