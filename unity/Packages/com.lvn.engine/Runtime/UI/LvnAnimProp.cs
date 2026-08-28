using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЧТО МОЖНО АНИМИРОВАТЬ — перечень свойств и жалоба на незнакомое.
    ///
    /// <para>Список свойств живёт в двух исполнителях: плоская сцена
    /// (<see cref="ActorAnimator"/>) и трёхмерный мир (<c>WorldActor</c>).
    /// Тела у них разные и такими должны остаться — одно двигает элемент
    /// интерфейса, другое узел сцены. Общее у них ровно одно: НАБОР имён.</para>
    ///
    /// <para>Пока набор нигде не назван, незнакомое имя проваливается в
    /// <c>switch</c> без ветки и исчезает. Автор пишет <c>prop="opacity"</c>
    /// вместо <c>alpha</c> или <c>rot</c> вместо <c>rotation</c> — анимация
    /// молча не играет. Ни компилятор, ни валидатор внутрь треков не смотрят,
    /// так что сказать об этом больше некому.</para>
    ///
    /// <para>Жалоба одна на имя за запуск: трек сэмплируется каждый кадр, и
    /// повтор превратил бы лог в шум ровно там, где его читают.</para>
    /// </summary>
    public static class LvnAnimProp
    {
        /// <summary>Имена, которые понимают ОБА исполнителя.</summary>
        private static readonly HashSet<string> Known = new HashSet<string>
        {
            "x", "y",                 // смещение от места, в долях кадра
            "screen_x", "screen_y",   // движение самого места по экрану
            "scale", "scalex", "scaley",
            "rotation",
            "alpha",
            "frame",                  // подмена кадра слоя (кукла, спрайтовый лист)
        };

        private static readonly HashSet<string> _complained = new HashSet<string>();

        /// <summary>Знакомо ли имя. Пустое — не жалоба: трек без свойства
        /// отбрасывают раньше.</summary>
        public static bool IsKnown(string prop) => !string.IsNullOrEmpty(prop) && Known.Contains(prop);

        /// <summary>
        /// Сказать об этом имени один раз. Возвращает <c>false</c> для
        /// незнакомого — чтобы вызывающий мог и пожаловаться, и пропустить трек
        /// одним условием.
        /// </summary>
        public static bool Check(string prop, string where = null)
        {
            if (string.IsNullOrEmpty(prop) || Known.Contains(prop)) return true;
            if (_complained.Add(prop))
                Debug.LogWarning($"[lvn-anim] свойство \"{prop}\" движку неизвестно"
                                 + (string.IsNullOrEmpty(where) ? "" : $" (слой «{where}»)")
                                 + " — анимация по нему не сыграет. Известны: "
                                 + string.Join(", ", Sorted()));
            return false;
        }

        private static List<string> Sorted()
        {
            var list = new List<string>(Known);
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }
    }
}
