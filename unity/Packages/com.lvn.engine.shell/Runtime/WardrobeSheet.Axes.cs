using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОСИ И ЗНАЧЕНИЯ — из чего собран облик и что из этого игроку доступно.
    ///
    /// <para>Ось это «причёска», «платье», «украшения»; значение — конкретный
    /// предмет. Здесь только вопросы к каталогу и гардеробу: какие оси
    /// подчинены другой, что надето сейчас, как это называется, что игрок уже
    /// встречал в игре, чем оно рисуется. Ответы на них нужны и ленте карточек,
    /// и свотчам, и покупке — поэтому они живут отдельно от вёрстки.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        /// <summary>
        /// Ось-поднастройка: живёт не своей вкладкой, а рядом свотчей под
        /// лентой родителя (цвет волос под причёской).
        ///
        /// <para>Родителем может быть и сборная вкладка «Моё»
        /// (<see cref="AllTab"/>) — так на неё попадает ОСНОВА фигуры: выбор
        /// запад/север не наряд, отдельной вкладки не заслуживает, но под общей
        /// витриной ему самое место (просьба Ильи 28.08).</para>
        /// </summary>
        private bool IsSubAxis(string axis) =>
            axis != null && _def?.wardrobe != null
            && _def.wardrobe.TryGetValue(axis, out var s)
            && !string.IsNullOrEmpty(s?.subOf)
            && (s.subOf == AllTab || _def.wardrobe.ContainsKey(s.subOf));

        /// <summary>
        /// Чем листает карусель вкладки «Моё».
        ///
        /// <para>Собственных предметов у неё нет — она собирает покупки других
        /// осей, поэтому стрелки на ней всегда стояли мёртвыми, а подпись
        /// говорила «Мои скины», то есть ничего. Зато на этой вкладке живёт
        /// ОСНОВА фигуры: её и отдаём рулю — стрелки листают основы, подпись
        /// называет выбранную (просьба Ильи 28.08).</para>
        /// </summary>
        private string AllTabAxis
        {
            get
            {
                foreach (var sub in SubAxesOf(AllTab)) return sub;
                return null;
            }
        }

        private IEnumerable<string> SubAxesOf(string parent)
        {
            if (parent == null || _def?.wardrobe == null) yield break;
            foreach (var kv in _def.wardrobe)
                if (kv.Value?.subOf == parent && IsSubAxis(kv.Key)) yield return kv.Key;
        }

        // Что на оси надето прямо сейчас — спрашиваем Костюмера; здесь только
        // витринный хвост: пустой слот показывает первый предмет, иначе
        // шаблонной иконке и подписи было бы нечего показать.
        private string CurrentValueOf(string axis)
        {
            var v = LvnCostumer.Chosen(_entity, axis, _def?.defaults);
            if (LvnCostumer.Bare(v))
            {
                var items = Items(axis);
                v = items.Count > 0 ? items[0].value : "";
            }
            return v;
        }

        private string NameOfValue(string axis, string value)
        {
            foreach (var it in Items(axis))
                if (it.value == value) return Lvn.Content.LvnWords.Name("skin", it.value, it.name);
            return value;
        }

        // Подпись под каруселью описывает ВЕСЬ образ раздела — «Рыжая:
        // Голливудские волны»: сначала выбранные поднастройки (цвет волос),
        // затем предмет, который листают стрелки. Раньше тап по свотчу писал
        // туда одно своё имя, и «Рыжая» читалась как то, что сейчас мотается
        // стрелками — а мотались причёски (Илья 26.08).
    }
}
