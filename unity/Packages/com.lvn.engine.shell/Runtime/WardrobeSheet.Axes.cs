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
        private bool IsSubAxis(string axis) =>
            axis != null && _def?.wardrobe != null
            && _def.wardrobe.TryGetValue(axis, out var s)
            && !string.IsNullOrEmpty(s?.subOf) && _def.wardrobe.ContainsKey(s.subOf);

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
                if (it.value == value) return it.name ?? it.value;
            return value;
        }

        // Подпись под каруселью описывает ВЕСЬ образ раздела — «Рыжая:
        // Голливудские волны»: сначала выбранные поднастройки (цвет волос),
        // затем предмет, который листают стрелки. Раньше тап по свотчу писал
        // туда одно своё имя, и «Рыжая» читалась как то, что сейчас мотается
        // стрелками — а мотались причёски (Илья 26.08).
    }
}
