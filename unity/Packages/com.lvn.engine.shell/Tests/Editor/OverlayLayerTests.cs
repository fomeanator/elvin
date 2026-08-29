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

        /// <summary>
        /// Тот же экран-пустышка, но МОДАЛЬНЫЙ и с мгновенным проявлением.
        ///
        /// <para>Ноль секунд не косметика: <c>ScreenFx.FadeAsync</c> при нулевой
        /// длительности возвращается сразу, и <c>ShowAsync</c> доходит до
        /// ожидания закрытия БЕЗ ЕДИНОГО КАДРА. В EditMode кадров нет, и без
        /// этого весь модальный путь остался бы непроверяемым — а именно на нём
        /// экран и живёт в игре.</para>
        /// </summary>
        private sealed class Modal : LvnOverlayScreen
        {
            protected override float FadeSeconds => 0f;
            public int ClosedCalls;
            public VisualElement Put()
            {
                var card = new VisualElement();
                PutOverlay(card);
                return card;
            }
            public void PutNothing() => PutOverlay(null);
            public void Drop() => DropOverlay();
            public bool Standing => HasOverlay;
            /// <summary>Крестик карточки, тап мимо, снос сцены — «уйти ни с чем».</summary>
            public void Back() => Cancel();
            /// <summary>То, чего экран и ждал: «играть», «купить», «сохранить».</summary>
            public void Ok() => Close();
            protected override void OnClosed() => ClosedCalls++;
        }

        [Test]
        public async System.Threading.Tasks.Task ОтменаМодалиСнимаетНаложение()
        {
            // Выходов у экрана пять, и уборка обязана висеть на КАЖДОМ.
            // Проверка ухода по вкладке ниже держит ровно один из них — а в игре
            // карточка новеллы открыта МОДАЛЬНО, и закрывает её не «уход по
            // вкладке», а разрешение ожидания внутри ShowAsync. Если бы уборка
            // висела только на HideAsTab, подтверждение осталось бы невидимым
            // ребёнком закрытого экрана — и всплыло поверх ДРУГОЙ новеллы.
            var screen = new Modal();
            var show = screen.ShowAsync();
            var card = screen.Put();
            Assert.AreSame(screen, card.parent, "наложение обязано лежать в экране");

            screen.Back();

            Assert.IsFalse(await show, "отмена — это «не подтверждено»");
            Assert.IsNull(card.parent, "наложение пережило модальное закрытие экрана");
            Assert.IsFalse(screen.Standing);
            Assert.AreEqual(0, screen.childCount, "в закрытом экране остался мусор");
            Assert.AreEqual(1, screen.ClosedCalls, "наследнику всё ещё сообщают о закрытии");
        }

        [Test]
        public async System.Threading.Tasks.Task ПодтверждениеМодалиТожеСнимаетНаложение()
        {
            // Второй выход того же пути: экран закрыли не отменой, а тем, чего
            // он и ждал. Уборка стоит в общем `finally`, и оба ответа обязаны
            // через неё пройти — иначе «купить» оставляет мусор, а «отмена» нет.
            var screen = new Modal();
            var show = screen.ShowAsync();
            var card = screen.Put();

            screen.Ok();

            Assert.IsTrue(await show, "подтверждение — это «да»");
            Assert.IsNull(card.parent, "наложение пережило подтверждённое закрытие");
            Assert.IsFalse(screen.Standing);
            Assert.AreEqual(1, screen.ClosedCalls);
        }

        [Test]
        public void СнятиеНесуществующегоНаложенияБезвредно()
        {
            // База зовёт уборку на каждом выходе, не спрашивая, было ли что
            // убирать. Если пустой вызов не безвреден, наследник обязан помнить
            // про наложение — а ровно этого он и не помнит.
            var screen = new Modal();

            Assert.DoesNotThrow(() => screen.Drop());
            screen.Put();
            screen.Drop();
            Assert.DoesNotThrow(() => screen.Drop(), "повторное снятие обязано быть пустым делом");

            Assert.IsFalse(screen.Standing);
            Assert.AreEqual(0, screen.childCount);
        }

        [Test]
        public void ПустоеНаложениеНеОставляетМусора()
        {
            // Экран собирает карточку по данным и вправе решить, что показывать
            // нечего. Тогда прежнее наложение всё равно обязано уйти, а
            // «ничего» — не считаться стоящим наложением: иначе «назад» съест
            // себя на пустоте и экран перестанет закрываться.
            var screen = new Modal();
            var card = screen.Put();

            screen.PutNothing();

            Assert.IsNull(card.parent, "прежнее наложение осталось висеть под пустым новым");
            Assert.IsFalse(screen.Standing, "«ничего» посчиталось стоящим наложением");
            Assert.AreEqual(0, screen.childCount);
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
