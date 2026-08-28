using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// ПОВОДОК ПОДПИСОК — то, на что подписались, отпускается разом.
    ///
    /// <para>Событие статическое, а обработчик — метод экземпляра: подписка
    /// переживает того, кто подписался. Пересозданная оболочка остаётся висеть
    /// на кошельке и на настройках, дёргая мёртвое дерево интерфейса на каждое
    /// движение денег. Урок этот в движке УЖЕ записан — прямо в комментарии к
    /// одной из отписок, — но применён был к двум подпискам из четырёх, а пятая
    /// и вовсе лямбда, которую отписать нечем: делегат никто не сохранил.</para>
    ///
    /// <para>Это ровно «список того, что надо не забыть» из канона. Дописать
    /// недостающие строки — значит согласиться, что список будет расти дальше;
    /// поводок делает отпускание СВОЙСТВОМ владельца, а не его памятью.</para>
    ///
    /// <para>Приём тот же, что у Рамки и у витка оболочки: когда работа парная,
    /// вторую половину нельзя доверять внимательности пишущего.</para>
    /// </summary>
    public sealed class LvnLeash
    {
        private readonly List<Action> _release = new List<Action>();

        /// <summary>Подписаться и запомнить, чем отписаться.</summary>
        public void Hold(Action subscribe, Action unsubscribe)
        {
            if (subscribe == null || unsubscribe == null) return;
            subscribe();
            _release.Add(unsubscribe);
        }

        /// <summary>
        /// Отпустить всё. Идемпотентно: снос бывает и двойным (сцена уходит
        /// вместе с хостом), а отписка дважды — не ошибка.
        /// </summary>
        public void Release()
        {
            for (int i = _release.Count - 1; i >= 0; i--)
            {
                // Одна упавшая отписка не должна оставить остальные висеть:
                // снос идёт по кускам, и половина отпущенного хуже целого.
                try { _release[i]?.Invoke(); }
                catch (Exception e) { Debug.LogWarning("[lvn] отписка не удалась: " + e.Message); }
            }
            _release.Clear();
        }

        /// <summary>Сколько подписок держим — для диагностики и тестов.</summary>
        public int Count => _release.Count;

        /// <summary>
        /// ПОДПИСКА ЖИВЁТ РОВНО СТОЛЬКО, СКОЛЬКО ЭЛЕМЕНТ НА ЭКРАНЕ.
        ///
        /// <para>Связку писали пятеро одинаково и вручную: подписаться на
        /// <c>AttachToPanelEvent</c>, отписаться на <c>DetachFromPanelEvent</c>,
        /// не забыть позвать обновление сразу — иначе экран открывается с
        /// прошлым балансом. Три строки, из которых забыть можно любую, и
        /// забытая отписка не проявляется никак: пересозданная оболочка просто
        /// продолжает дёргать мёртвое дерево на каждое движение денег.</para>
        ///
        /// <para>Поводок уже умел отпускать разом — не хватало ПОВОДА взять и
        /// отпустить. Теперь он тоже здесь: владельцу остаётся сказать, на что
        /// подписаться и чем обновиться.</para>
        /// </summary>
        public static void WhileOnScreen(UnityEngine.UIElements.VisualElement el,
                                         Action subscribe, Action unsubscribe, Action refresh = null)
        {
            if (el == null || subscribe == null || unsubscribe == null) return;
            var leash = new LvnLeash();
            el.RegisterCallback<UnityEngine.UIElements.AttachToPanelEvent>(_ =>
            {
                leash.Hold(subscribe, unsubscribe);
                refresh?.Invoke();
            });
            el.RegisterCallback<UnityEngine.UIElements.DetachFromPanelEvent>(_ => leash.Release());
            // Элемент, УЖЕ стоящий в панели, события привязки больше не увидит:
            // подписка тогда не случилась бы никогда.
            if (el.panel != null) { leash.Hold(subscribe, unsubscribe); refresh?.Invoke(); }
        }
    }
}
