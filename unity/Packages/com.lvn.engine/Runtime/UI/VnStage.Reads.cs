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

        /// <summary>
        /// ЦВЕТ ВСПЫШКИ И ВУАЛИ — окно в общий словарь (<see cref="UiColor.Named"/>).
        ///
        /// <para>Свой набор слов сцена держала до конца: имена движка и три
        /// мнемоники настроения, но НЕ токены темы. Автор писал
        /// <c>tint color="accent"</c> — и получал жалобу в журнале, хотя то же
        /// слово в дереве <c>ui</c> работало. Набор слов у цвета один, где бы
        /// его ни писали; какие именно — решает <see cref="UiColor"/>.</para>
        /// </summary>
        internal static Color ParseColor(string name, Color fallback)
            => UiColor.Named(name, fallback);
    }
}
