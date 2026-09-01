using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// НАЖАТЬ КНОПКУ В ТЕСТЕ — без панели, указателя и кадров.
    ///
    /// <para>В EditMode настоящий тап до кнопки не доходит: события разносит
    /// панель, а панели нет, и раскладки нет. Поэтому спрашивают саму кнопку —
    /// её <c>Clickable</c>, у которого подписка приватна.</para>
    ///
    /// <para>Приём был расписан ЧЕТЫРЬМЯ копиями в четырёх файлах, и три из них
    /// умеют только одно: найти поле подписки. Четвёртая знает запасной путь —
    /// приватный <c>Invoke</c>. Разница не косметическая: переименуй Unity поле
    /// <c>clicked</c>, и три копии вернут «обработчика нет» вместо «до него не
    /// дотянуться». Тест, проверяющий «кнопка мертва», при этом ПРОЙДЁТ — на
    /// живой кнопке. Зелёное на неправде, и заметит это игрок.</para>
    ///
    /// <para>Два способа тут намеренно разные, и путать их нельзя:
    /// <see cref="Обработчик"/> отвечает на вопрос «подписан ли кто-нибудь»
    /// (его <c>null</c> — законный ответ теста), а <see cref="Жать"/> обязан
    /// нажать и падает, если не смог.</para>
    /// </summary>
    internal static class Нажатие
    {
        private const BindingFlags Любое =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>Что случится при нажатии. <c>null</c> — не подписан никто,
        /// и это ответ, а не сбой.</summary>
        public static Action Обработчик(Button b)
        {
            Assert.NotNull(b, "кнопки нет");
            foreach (var f in typeof(Clickable).GetFields(Любое))
                if (f.GetValue(b.clickable) is Action a) return a;
            return null;
        }

        /// <summary>Нажать. Сперва подписка, потом запасной путь через
        /// приватный <c>Invoke</c>: он зовёт ВСЕХ подписчиков, а поле отдаёт
        /// одного, и на кнопке с двумя обработчиками это разные вещи.</summary>
        public static void Жать(Button b)
        {
            var действие = Обработчик(b);
            if (действие != null) { действие(); return; }

            var invoke = typeof(Clickable).GetMethod("Invoke",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(EventBase) }, null);
            Assert.NotNull(invoke, "до обработчика кнопки не дотянуться — нажатие проверять нечем");
            invoke.Invoke(b.clickable, new object[] { null });
        }
    }
}
