using System.Threading;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>ОКНО в дом движения: гашение, которого можно дождаться.
    ///
    /// <para>Работа переехала к <see cref="LvnMotion"/> — там живёт время
    /// движения, и там же гашение наконец спрашивает темп. Имя осталось: его
    /// знают девять экранов, и переписывать их ради переезда дороже, чем
    /// оставить дверь.</para></summary>
    public static class ScreenFx
    {
        public static Task FadeAsync(VisualElement el, float from, float to, float seconds, CancellationToken ct)
            => LvnMotion.FadeAsync(el, from, to, seconds, ct);

        /// <summary>
        /// УБРАТЬ ПОВЕРХНОСТЬ — И ОСТАВИТЬ ЕЁ ПРИГОДНОЙ К ВОЗВРАТУ.
        ///
        /// <para>Уйти с экрана — это не одно присваивание, а отмена всего, чем
        /// уход был обставлен: <c>display</c> убирает из раскладки,
        /// <c>opacity</c> и <c>translate</c> возвращают на место то, что двигал
        /// и гасил показ. Правило открывали ТРИЖДЫ и каждый раз наполовину:
        /// накладной экран помнил смещение (раздел открывался за кромкой),
        /// панель истории — прозрачность рамки, бут и загрузка — свою
        /// прозрачность. Ни один не знал всего набора.</para>
        ///
        /// <para>Опасность несимметрична. Забыть <c>display</c> — видно сразу:
        /// экран остался на глазах. Забыть прозрачность или смещение — не видно
        /// никогда: следующий показ ставит <c>display</c>, поверхность честно в
        /// дереве, ловит тапы, ждёт игрока — и невидима. Ровно это и есть
        /// ловушка, из-за которой правило приходится держать домом, а не
        /// привычкой.</para>
        ///
        /// <para>Кто гаснет перед уходом, зовёт <see cref="FadeAwayAsync"/>:
        /// пара «погасить, потом убрать» стояла семью отдельными парами, и
        /// вторая половина каждой писалась заново.</para>
        /// </summary>
        public static void PutAway(VisualElement el)
        {
            if (el == null) return;
            el.style.display = DisplayStyle.None;
            el.style.opacity = 1f;
            el.style.translate = new Translate(0f, 0f, 0f);
        }

        /// <summary>ПОГАСНУТЬ И УЙТИ — одним поступком. Отмена не оставляет
        /// поверхность полупрозрачной: <see cref="FadeAsync"/> досаживает её в
        /// конечное значение, а убирает всё равно <see cref="PutAway"/>.</summary>
        public static async Task FadeAwayAsync(VisualElement el, float seconds, CancellationToken ct)
        {
            if (el == null) return;
            await FadeAsync(el, 1f, 0f, seconds, ct);
            PutAway(el);
        }
    }
}
