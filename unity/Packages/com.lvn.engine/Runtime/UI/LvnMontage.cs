using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// МОНТАЖЁР — решает, КОГДА обновлять кадр и ЧТО в нём менять: поправить
    /// на месте или пересобрать.
    ///
    /// <para>Правила не было вообще. Каждый экран решал сам, и решал одинаково
    /// грубо: пришло событие → <c>Clear()</c> → собрать всё заново. Игрок видел
    /// это своими глазами. Шапка хаба перерисовывалась через секунду после
    /// входа, потому что ответ кошелька приходил по сети и сносил пилюли вместе
    /// со значками. Вкладки нижнего меню МИГАЛИ намеренно — «полфейда вниз,
    /// перекраска, полфейда вверх», — потому что иконку нельзя было перекрасить,
    /// её пересоздавали, и подмену пришлось прикрыть гашением.</para>
    ///
    /// <para>Отсюда три правила, и все три — про «не трогай того, что не
    /// изменилось»:</para>
    /// <list type="bullet">
    ///   <item><see cref="Sync{T}"/> — список сверяется поштучно: что было,
    ///   остаётся тем же элементом и лишь обновляется. Сносится только то, чего
    ///   в модели больше нет. Экземпляр переживает обновление — а с ним
    ///   переживают фокус, позиция скролла, начатая анимация и загруженная
    ///   картинка.</item>
    ///   <item><see cref="RevealWhenLaidOut"/> — пересобранное показывается,
    ///   когда у него посчитана геометрия. Показать раньше — значит показать
    ///   кадр, где всё в нуле; это и есть «моргнуло».</item>
    ///   <item><see cref="Coalesce"/> — три события за кадр стоят одной
    ///   перерисовки, а не трёх.</item>
    /// </list>
    ///
    /// <para>Монтажёр НЕ решает, как элемент выглядит (это <see cref="LvnStyler"/>)
    /// и кто сейчас на экране (это <see cref="LvnScreenDirector"/>). Только —
    /// что и когда обновить.</para>
    /// </summary>
    public static class LvnMontage
    {
        // Ключ живёт в имени элемента: своё поле для этого в UITK не заведено,
        // а name свободен и переживает пересадку в дереве. Префикс — чтобы не
        // спутать с именами, которые экраны дают элементам для Q<>().
        private const string KeyPrefix = "mtg:";

        /// <summary>
        /// СВЕРИТЬ СПИСОК С МОДЕЛЬЮ, не пересобирая его.
        ///
        /// <para>Каждому элементу модели нужен устойчивый <paramref name="key"/>
        /// — по нему узнают «это тот же самый». Что уже есть — остаётся тем же
        /// экземпляром и получает <paramref name="update"/>; чего не было —
        /// создаётся; чего в модели больше нет — уходит. Порядок приводится к
        /// модели, но элемент двигают, только если он реально стоит не там.</para>
        ///
        /// <para>Дети без ключа (воздух, разделители, служебные слои) не
        /// трогаются вовсе — их ставит сам экран, и монтажёру они не
        /// принадлежат.</para>
        /// </summary>
        public static void Sync<T>(VisualElement host, IReadOnlyList<T> model,
            Func<T, string> key, Func<T, VisualElement> create,
            Action<VisualElement, T> update = null)
        {
            if (host == null || key == null || create == null) return;
            model ??= Array.Empty<T>();

            // Что уже стоит, по ключам.
            Dictionary<string, VisualElement> mine = null;
            int strangers = 0;
            foreach (var child in host.Children())
            {
                var name = child.name;
                if (string.IsNullOrEmpty(name) || !name.StartsWith(KeyPrefix, StringComparison.Ordinal))
                {
                    // ЧУЖОЙ ЖИЛЕЦ. Монтажёр убирает только свои элементы — это
                    // нарочно: в теле могут стоять служебные соседи, и сносить
                    // их он не вправе. Но если чужой лежит СРЕДИ карточек, то
                    // тело наполняют двумя способами разом, и второй способ
                    // невидим для сверки: его элементы не уйдут никогда.
                    // Именно так украшения оставались в ленте причёсок
                    // (гардероб, витрина «Моё» клала карточки напрямую).
                    strangers++;
                    continue;
                }
                (mine ??= new Dictionary<string, VisualElement>())[name] = child;
            }

            var kept = new HashSet<string>(StringComparer.Ordinal);
            int at = 0;
            foreach (var item in model)
            {
                var k = key(item);
                if (string.IsNullOrEmpty(k)) continue;
                var name = KeyPrefix + k;
                if (!kept.Add(name)) continue;   // дубль ключа: первый победил

                VisualElement el = null;
                mine?.TryGetValue(name, out el);
                if (el == null)
                {
                    el = create(item);
                    if (el == null) continue;
                    el.name = name;
                }
                else
                {
                    update?.Invoke(el, item);
                }

                // Двигаем, только если элемент стоит не на своём месте: лишняя
                // пересадка в дереве — это тоже перерисовка.
                int now = host.IndexOf(el);
                if (now != at)
                {
                    if (now >= 0) el.RemoveFromHierarchy();
                    at = Math.Min(at, host.childCount);
                    host.Insert(at, el);
                }
                at++;
            }

            if (strangers > 0 && kept.Count > 0)
                LvnLog.Warn($"[lvn-montage] в теле {strangers} элемент(ов) не от монтажёра рядом с " +
                                $"{kept.Count} карточками: тело наполняют двумя способами, и чужие " +
                                "не уйдут при следующей сверке — наполняйте через Sync");

            if (mine == null) return;
            foreach (var pair in mine)
                if (!kept.Contains(pair.Key))
                    pair.Value.RemoveFromHierarchy();
        }

        /// <summary>
        /// ПОКАЗАТЬ, КОГДА ПОСЧИТАНА ГЕОМЕТРИЯ.
        ///
        /// <para>Только что пересобранное тело в первом кадре ещё не измерено —
        /// UITK рисует его в нуле, и переход моргает. Ждём кадр, где раскладка
        /// готова.</para>
        ///
        /// <para><paramref name="safetyMs"/> — страховка: у пустого тела
        /// геометрию считать не на чем, события может не быть вовсе, и без неё
        /// элемент остался бы невидимым навсегда.</para>
        /// </summary>
        public static void RevealWhenLaidOut(VisualElement el, long safetyMs = 64)
        {
            if (el == null) return;
            el.style.opacity = 0f;
            EventCallback<GeometryChangedEvent> shown = null;
            shown = _ => { el.style.opacity = 1f; el.UnregisterCallback(shown); };
            el.RegisterCallback(shown);
            el.schedule.Execute(() =>
            {
                if (el.style.display != DisplayStyle.None) el.style.opacity = 1f;
            }).ExecuteLater(safetyMs);
        }

        /// <summary>
        /// ОДНА ПЕРЕРИСОВКА НА КАДР. Кошелёк, гардероб и настройки способны
        /// прислать три события подряд; каждое из них по отдельности честно
        /// просит обновиться, и без склейки экран пересобирается трижды за
        /// кадр. <paramref name="tag"/> различает разные работы на одном
        /// элементе.
        /// </summary>
        public static void Coalesce(VisualElement host, string tag, Action work)
        {
            if (host == null || work == null) return;
            var k = (host, tag ?? "");
            if (!Queued.Add(k)) return;   // уже назначено — второй раз не надо
            host.schedule.Execute(() =>
            {
                Queued.Remove(k);
                work();
            }).ExecuteLater(0);
        }

        private static readonly HashSet<(VisualElement, string)> Queued
            = new HashSet<(VisualElement, string)>();
    }
}
