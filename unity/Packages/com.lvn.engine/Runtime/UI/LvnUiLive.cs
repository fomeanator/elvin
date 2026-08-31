using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using static Lvn.UI.LvnUiValues;

namespace Lvn.UI
{
    /// <summary>
    /// ЖИВОЕ ЗНАЧЕНИЕ ДЕРЕВА `ui` — «{hp}/{hp_max}» в подписи, доля в полосе,
    /// цвет по переменной.
    ///
    /// <para>МЕХАНИЗМ, а не содержание: он не знает, что нарисовано, — только
    /// что этот элемент показывает вот это выражение и обязан меняться следом
    /// за переменными. Три правила, и все три уже стоили дефекта: значение
    /// кладётся СРАЗУ (первый кадр игрок видит до первого опроса), не трогается
    /// зря (лишняя запись в стиль стоит перерисовки всего поддерева), а
    /// сломанное выражение автора не имеет права уронить экран.</para>
    ///
    /// <para>Обновление ОПРОСОМ, а не по сигналу: сигнала об изменении
    /// переменной в движке нет, а заводить его значило бы менять каждое место
    /// записи. Такт держит слой; здесь — только что делать на такте.</para>
    /// </summary>
    internal sealed class LvnUiLive
    {
        private sealed class Binding
        {
            public VisualElement El;
            public string Field;   // text | value | color | bg | w | h | …
            public string Expr;    // исходная строка с {…}
            public string Last;    // что показано сейчас — чтобы не трогать зря
        }

        private readonly List<Binding> _bindings = new List<Binding>();

        /// <summary>Есть ли что опрашивать. Дерево без живых значений опрос
        /// пропускает целиком.</summary>
        public bool Any => _bindings.Count > 0;

        /// <summary>Кладёт значение СРАЗУ и, если в нём есть {…}, заводит
        /// привязку — тогда оно будет пересчитываться само. Сразу — потому что
        /// первый кадр игрок видит до первого опроса.</summary>
        public void Bind(VisualElement el, string field, JToken raw)
        {
            string str = raw.Type == JTokenType.String ? (string)raw : raw.ToString();
            if (str != null && str.Contains("{"))
                _bindings.Add(new Binding { El = el, Field = field, Expr = str });
            Set(el, field, str);
        }

        /// <summary>Пересчитать всё живое под текущими переменными.</summary>
        /// <param name="force">поставить значение, даже если оно не менялось —
        /// нужно после пересборки узла, где на экране стоит уже другое</param>
        public void Refresh(IReadOnlyDictionary<string, JToken> vars, bool force)
        {
            if (_bindings.Count == 0 || vars == null) return;
            foreach (var b in _bindings)
            {
                string now;
                try { now = TextInterpolation.Apply(b.Expr, vars); }
                catch { continue; }   // сломанное выражение не должно ронять экран
                if (!force && now == b.Last) continue;   // не трогаем зря
                b.Last = now;
                Set(b.El, b.Field, now);
            }
        }

        /// <summary>Поставить значение элементу — по имени поля.</summary>
        public static void Set(VisualElement el, string field, string value)
        {
            if (el == null || value == null) return;
            switch (field)
            {
                case "text":
                    if (el is Label l) l.text = value;
                    else if (el is Button bt) bt.text = value;
                    break;
                case "color":
                    var c = Color(value, LvnTokens.Text);
                    el.style.color = c;
                    if (el.ClassListContains(LvnUiLayer.BarClass) && el.childCount > 0) el[0].style.backgroundColor = c;
                    break;
                case "bg":
                    el.style.backgroundColor = Color(value, Color32Clear);
                    break;
                case "hide":
                    el.style.display = Truthy(value) ? DisplayStyle.None : DisplayStyle.Flex;
                    break;
                case "opacity":
                    el.style.opacity = Num(value, 1f);
                    break;
                case "w":
                    SetLen(v => el.style.width = v, Len(value, out var wu), wu);
                    break;
                case "h":
                    SetLen(v => el.style.height = v, Len(value, out var hu), hu);
                    break;
                case "value":
                    // Полоса: доля 0…1 в ширину заливки. Ради этого одного и
                    // затевалось — раньше это были семнадцать веток с
                    // литеральными ширинами.
                    //
                    // Заливка ЕДЕТ, а не прыгает: мгновенный скачок здоровья
                    // читается как сбой отрисовки, и глаз не успевает связать
                    // удар с потерей.
                    var fv = Lvn.LvnNum.Parse((JToken)value);
                    if (el.childCount > 0 && fv != null)
                    {
                        float f = fv.Value;
                        var fill = el[0];
                        if (fill.style.transitionProperty.keyword == StyleKeyword.Null
                            && (fill.style.transitionDuration.value == null
                                || fill.style.transitionDuration.value.Count == 0))
                        {
                            fill.style.transitionProperty = new List<StylePropertyName> { "width" };
                            fill.style.transitionDuration =
                                new List<TimeValue> { new TimeValue(0.22f, TimeUnit.Second) };
                            fill.style.transitionTimingFunction =
                                new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };
                        }
                        fill.style.width = Length.Percent(Mathf.Clamp01(f) * 100f);
                    }
                    break;
            }
        }

    }
}
