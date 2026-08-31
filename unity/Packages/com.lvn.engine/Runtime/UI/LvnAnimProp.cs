using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЧТО МОЖНО АНИМИРОВАТЬ — перечень свойств и жалоба на незнакомое.
    ///
    /// <para>Список свойств живёт в двух исполнителях: плоская сцена
    /// (<see cref="LvnAnimSampler"/>) и трёхмерный мир (<c>WorldActor</c>).
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
        /// <summary>Свойства ФИГУРЫ ЦЕЛИКОМ — трек без слоя.</summary>
        private static readonly HashSet<string> Whole = new HashSet<string>
        {
            "x", "y",                 // смещение от места, в долях кадра
            "screen_x", "screen_y",   // движение самого места по экрану
            "scale", "scalex", "scaley",
            "rotation",
            "alpha",
        };

        /// <summary>Свойства ОДНОГО СЛОЯ куклы — трек со слоем.
        ///
        /// <para>Экранного места у слоя нет: по экрану ходит фигура, а не её
        /// рукав. Зато у слоя есть КАДР — им и подменяют картинку.</para></summary>
        private static readonly HashSet<string> Layered = new HashSet<string>
        {
            "x", "y",
            "scale", "scalex", "scaley",
            "rotation",
            "alpha",
            "frame",                  // подмена кадра слоя (кукла, спрайтовый лист)
        };

        /// <summary>Все имена, какие бывают. Для тех, кто спрашивает про
        /// словарь вообще, — валидатор, подсказки редактора, сторожа.</summary>
        public static readonly IReadOnlyCollection<string> Known = Union();

        private static HashSet<string> Union()
        {
            var all = new HashSet<string>(Whole);
            all.UnionWith(Layered);
            return all;
        }

        private static readonly HashSet<string> _complained = new HashSet<string>();

        /// <summary>Знакомо ли имя. Пустое — не жалоба: трек без свойства
        /// отбрасывают раньше.</summary>
        public static bool IsKnown(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return false;
            foreach (var k in Known) if (k == prop) return true;
            return false;
        }

        /// <summary>
        /// Сказать об этом имени один раз. Возвращает <c>false</c> для
        /// незнакомого — чтобы вызывающий мог и пожаловаться, и пропустить трек
        /// одним условием.
        /// </summary>
        public static bool Check(string prop, string where = null)
        {
            if (string.IsNullOrEmpty(prop)) return true;
            bool layered = !string.IsNullOrEmpty(where);
            var here = layered ? Layered : Whole;
            if (here.Contains(prop)) return true;

            // ЗНАКОМОЕ ИМЯ НЕ В СВОЁМ МЕСТЕ — отдельная жалоба, и это главное,
            // что тут можно сказать. Набор был ПЛОСКИМ, а исполнитель нет:
            // `screen_x` со слоем проходил проверку и молча отбрасывался, а
            // `frame` без слоя — наоборот. Проверка говорила «всё в порядке»
            // ровно там, где ничего не игралось.
            if (_complained.Add((layered ? "L:" : "W:") + prop))
            {
                if (Whole.Contains(prop) || Layered.Contains(prop))
                    Debug.LogWarning($"[lvn-anim] свойство \"{prop}\" здесь не играет: "
                        + (layered
                            ? $"у слоя «{where}» экранного места нет — уберите layer= или анимируйте x/y"
                            : "оно принадлежит СЛОЮ — добавьте layer=")
                        + ". Здесь можно: " + string.Join(", ", Sorted(here)));
                else
                    Debug.LogWarning($"[lvn-anim] свойство \"{prop}\" движку неизвестно"
                        + (layered ? $" (слой «{where}»)" : "")
                        + " — анимация по нему не сыграет. Здесь можно: "
                        + string.Join(", ", Sorted(here)));
            }
            return false;
        }

        private static List<string> Sorted(IEnumerable<string> of = null)
        {
            var list = new List<string>(of ?? Known);
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }
    }
}
