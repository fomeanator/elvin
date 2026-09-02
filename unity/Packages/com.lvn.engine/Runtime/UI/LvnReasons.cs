using System;
using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ДЕРЖАТ ПО ПРИЧИНАМ — счётчик причин вместо булева флага.
    ///
    /// <para>Форма встречается везде, где одно и то же можно выключить по
    /// нескольким независимым поводам: интерфейс убирают катсцена, режим «во
    /// весь рост» и долгое нажатие; стопку выбора гасят обработка нажатия и
    /// незакончившаяся хореография актёров. Пока держатель один, флаг работает.
    /// Со вторым он ломается ОДИНАКОВО: ушла одна причина — снялись все.</para>
    ///
    /// <para>«Отпустил палец посреди катсцены, и хром вернулся» — это ровно
    /// оно. И заметьте: код при этом не падает, ничего не логируется, а на
    /// экране просто «иногда не так».</para>
    ///
    /// <para>Правило: держат, пока держит хоть ОДНА причина; кто попросил, тот
    /// и снимает, а чужую просьбу отменить нельзя. Повтор той же причины —
    /// не событие: причина одна, сколько бы раз о ней ни сказали.</para>
    ///
    /// <para>Возвращаемое <c>bool</c> у <see cref="Hold"/> и <see cref="Drop"/>
    /// значит «состояние ПЕРЕВЕРНУЛОСЬ», а не «просьба принята»: вызывающему
    /// нужно знать, когда перерисовываться, и именно на этом вопросе флаг
    /// экономил, заставляя каждого держать свою память о прошлом значении.</para>
    /// </summary>
    public sealed class LvnReasons
    {
        private readonly HashSet<string> _held = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Держит ли хоть кто-нибудь.</summary>
        public bool Any => _held.Count > 0;

        /// <summary>Сколько причин держит сейчас.</summary>
        public int Count => _held.Count;

        /// <summary>Держит ли именно эта причина.</summary>
        public bool Has(string reason) => reason != null && _held.Contains(reason);

        /// <summary>Взять. <c>true</c> — держать начали ТОЛЬКО ЧТО (был ноль).</summary>
        public bool Hold(string reason)
        {
            if (string.IsNullOrEmpty(reason) || !_held.Add(reason)) return false;
            return _held.Count == 1;
        }

        /// <summary>Отпустить своё. <c>true</c> — держать перестали совсем.
        /// Чужие причины остаются: катсцена не кончается оттого, что игрок
        /// отпустил палец.</summary>
        public bool Drop(string reason)
        {
            if (string.IsNullOrEmpty(reason) || !_held.Remove(reason)) return false;
            return _held.Count == 0;
        }

        /// <summary>Снять ВСЁ разом — сброс: причина не имеет права пережить
        /// то, ради чего она заводилась (главу, показ, выбор). <c>true</c> —
        /// что-то действительно сняли.</summary>
        public bool Clear()
        {
            if (_held.Count == 0) return false;
            _held.Clear();
            return true;
        }

        /// <summary>
        /// КТО ДЕРЖИТ — одной строкой в лог.
        ///
        /// <para>Ради этого причины и названы словами. Вопрос «почему оно до
        /// сих пор выключено» у флага ответа не имеет вовсе, а здесь на него
        /// отвечает сам предмет.</para>
        /// </summary>
        public string Journal()
        {
            if (_held.Count == 0) return "никто";
            var names = new List<string>(_held);
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }
    }
}
