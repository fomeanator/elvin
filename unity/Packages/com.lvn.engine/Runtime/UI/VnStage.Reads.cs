using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЧТЕЦ КОМАНД — как поле команды превращается в значение.
    ///
    /// <para>Автор пишет `dur=0.5`, `alpha=40%`, `show=yes`, `to=black`, и
    /// каждое из этих написаний должно значить одно и то же в любом опе. Пока
    /// читалки лежали посреди самих опов, «число» и «правда» тихо расходились
    /// от места к месту: где-то проценты понимались, где-то нет.</para>
    ///
    /// <para>Здесь они собраны и ничего не делают со сценой — только читают.
    /// Что СЧИТАЕТСЯ числом, решает <see cref="LvnNum"/>: там же разбор
    /// процентов и там же он покрыт тестом.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private static float NumOr(JToken t, float dflt) => NumOrNull(t) ?? dflt;

        // Что считается числом — решает Lvn.LvnNum: там же живёт разбор
        // процентов, и там же он покрыт тестом.
        private static float? NumOrNull(JToken t) => LvnNum.Parse(t);

        private static int? IntOrNull(JToken t)
        {
            var f = NumOrNull(t);
            return f == null ? (int?)null : (int)Mathf.Round(f.Value);
        }

        // Tolerant boolean read: absent → dflt, and true/false/1/0 written as a
        // string or number are all accepted rather than throwing an invalid cast.
        // Словарь согласия — у ЧТЕЦА «ДА-НЕТ» (Lvn.LvnBool): здесь он знал
        // true/1/yes, звук не знал и этого, а UI-слой знал ещё on/off и «нет».
        private static bool BoolOr(JToken t, bool dflt) => Lvn.LvnBool.Of(t, dflt);


        internal static TransitionType ParseTransition(string name)
        {
            if (string.IsNullOrEmpty(name)) return TransitionType.None;
            switch (name.ToLowerInvariant())
            {
                case "fade": return TransitionType.Fade;
                case "slide_left": return TransitionType.SlideLeft;
                case "slide_right": return TransitionType.SlideRight;
                case "pop": return TransitionType.Pop;
                // Виды из общего набора движка (LvnAppear): персонаж всплывает
                // из-под стекла и утопает обратно, как и любая панель.
                case "rise": case "sink": return TransitionType.Rise;
                case "drop": return TransitionType.Drop;
                case "unfold": return TransitionType.Unfold;
                case "dissolve": case "burn": return TransitionType.Dissolve;
                case "drift": case "side": return TransitionType.Drift;
                default: return TransitionType.None;
            }
        }

        internal static Color ParseColor(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.ToLowerInvariant())
            {
                case "white": return Color.white;
                case "black": return Color.black;
                case "red": return Color.red;
                case "blue": return Color.blue;
                case "green": return Color.green;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "cold":
                case "tint_cold": return new Color(0.6f, 0.7f, 1f, 1f);
                case "warm":
                case "tint_warm": return new Color(1f, 0.85f, 0.7f, 1f);
                case "sepia": return new Color(0.76f, 0.6f, 0.42f, 1f);
                default: return fallback;
            }
        }
    }
}
