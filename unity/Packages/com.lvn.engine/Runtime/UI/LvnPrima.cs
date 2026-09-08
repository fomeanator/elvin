using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ПРИМА — ПОСТОЯННАЯ ФИГУРА СЦЕНЫ, одна и неделимая.
    ///
    /// <para>У всех остальных на сцене жизнь короткая: их ставит глава, и с
    /// главой они уходят. Героиня — другая: она стоит в витрине меню, уходит с
    /// ней в главу, играет её, возвращается, переодевается в гардеробе и снова
    /// стоит в витрине. Между этими состояниями она обязана оставаться ОДНИМ
    /// человеком, а не собираться заново на каждом переходе.</para>
    ///
    /// <para>Пока дома у неё не было, её ставили четверо — витрина, катсцена
    /// ухода, катсцена прихода и гардероб, — каждый своей командой из десятка
    /// полей, собранной на месте. Отсюда весь список живых дефектов недели:
    /// «героинь опять две», «встаёт по-менюшному в главе», «рост скачет»,
    /// «шум, белое пятно и бац». Разница в одном поле у одного из четырёх
    /// вызовов — и человек другой.</para>
    ///
    /// <para>Здесь она принимает НАСТРОЙКИ, а не команды: кто она
    /// (<see cref="Cast"/>), где стоит и какого роста (<see cref="Place"/>,
    /// рамка витрины), перед всеми ли (<c>z</c>). Всё остальное — дело сцены:
    /// облик уже надет, и показать её значит включить, а не собрать
    /// (см. <c>VnStage.ActorArtAlive</c>).</para>
    /// </summary>
    public sealed class LvnPrima
    {
        private readonly VnStage _stage;

        public LvnPrima(VnStage stage) { _stage = stage; }

        /// <summary>Кто она. Ставится хостом (фаворит гардероба или героиня по
        /// умолчанию) и меняется, когда игрок выбрал другую.</summary>
        public string Id { get; private set; }

        /// <summary>Назначить фигуру. Смена — это смена ЧЕЛОВЕКА (другой
        /// фаворит), а не переодевание: наряд меняет Костюмер.</summary>
        public void Cast(string id) => Id = id;

        /// <summary>Есть ли вообще постоянная фигура: игра может обойтись без
        /// неё, и тогда витрина — просто полотно.</summary>
        public bool Exists => !string.IsNullOrEmpty(Id);

        /// <summary>Где она стоит в витрине. Настройка, а не поле команды.</summary>
        public string Place = "center";

        /// <summary>Она в кадре или её показ уже в полёте.</summary>
        public bool InFrame => _stage != null && _stage.ActorVisibleOrPending(Id);

        /// <summary>Фигура цела: слои на месте и каждому есть чем рисовать.
        /// Целую показывают включением, а не сборкой.</summary>
        public bool Whole => _stage != null && _stage.ActorArtAlive(Id);

        /// <summary>Сцена бережёт её арт при уборке: она понадобится через миг
        /// по ту сторону перехода, и отпустить его — значит купить пересборку
        /// на каждом выходе из главы.</summary>
        public void Keep()
        {
            if (_stage != null && Exists) _stage.KeepActorAlive = Id;
        }

        /// <summary>
        /// ВСТАТЬ В КАДРЕ ОТПРАВИТЕЛЯ.
        ///
        /// <para>Витрина ставит её своим слоем: пока он открыт, кадр
        /// принадлежит меню, а закроется — глава получит свой кадр нетронутым.
        /// Катсцена ведёт кадр сама и называет порядок слоя явно: там она
        /// обязана стоять перед всеми.</para>
        /// </summary>
        /// <param name="place">Стоячий слот сцены; молчание — слот главной.
        /// Место словом сцены, а не долей: так героиня меню и героиня главы
        /// стоят в одних и тех же точках.</param>
        /// <param name="seconds">За сколько ЭКРАННЫХ секунд дойти до слота,
        /// если фигура уже видна; 0 — встать сразу. Перевод в заявленное
        /// время знает сцена (<see cref="VnStage.DeclareMovement"/>).</param>
        /// <param name="nudge">Сдвиг от слота, доля ширины кадра (вправо
        /// положительный). Слот остаётся словом сцены — по нему фигура
        /// ездит и разводится; сдвиг — нюанс композиции витрины.</param>
        public bool Stand(LvnSender sender, int? z = null, string place = null, float seconds = 0f,
                          float nudge = 0f)
        {
            if (_stage == null || !Exists) return false;
            if (string.IsNullOrEmpty(place)) place = LvnMenuStage.HomeDollSlot;
            var pose = Pose(Id, place, LvnMenuStage.DollWidth, LvnMenuStage.DollHeight, z ?? 0, nudge);
            if (seconds > 0f) pose["transition_duration"] = VnStage.DeclareMovement(seconds);
            LvnLog.Trace($"[lvn-doll] {Id}: в слот «{place}» ({Placement.SlotX(place):0.000}"
                       + (nudge != 0f ? $" {(nudge > 0 ? "+" : "−")} {Mathf.Abs(nudge):0.000} = {(float)pose["x"]:0.000}" : "")
                       + " ширины)"
                       + (seconds > 0f ? $" за {seconds:0.00}с" : " сразу") + $" от {sender}");
            if (sender == LvnSender.Menu) _stage.ShowMenuDoll(Id, pose);
            else _stage.ApplyStage(pose, sender);
            return true;
        }

        /// <summary>Уйти из кадра отправителя.</summary>
        public void Leave(LvnSender sender)
        {
            if (_stage == null || !Exists) return;
            _stage.HideActor(Id, sender);
        }

        /// <summary>
        /// НАСТРОЙКИ ФИГУРЫ ОДНОЙ КОМАНДОЙ — единственное место, где они
        /// превращаются в поля.
        ///
        /// <para>Порядок слоя задаётся ВСЕГДА: явный <c>z</c> живёт у сцены до
        /// следующего явного значения, и «сотка» катсцены тащилась бы за куклой
        /// в меню и в следующую главу — она стояла бы поверх собеседников.</para>
        ///
        /// <para>Y НЕ ЗАДАЁТСЯ: у фигуры якорь ног, и число здесь уводило её за
        /// нижнюю кромку кадра.</para>
        /// </summary>
        public static JObject Pose(string id, string place, float width, float height, int z,
                                   float nudge = 0f)
        {
            var pose = new JObject
            {
                ["op"] = "actor",
                ["id"] = id,
                ["show"] = true,
                ["width"] = width,
                ["height"] = height,
                ["z"] = z,
                // ВИТРИНА — ПОРТРЕТ, И ОБРЕЗ ЗДЕСЬ НАМЕРЕННЫЙ. Кукла в 0.9 ширины
                // экрана «слева» стоит только краем за кадром; без этого
                // зажим сцены (фигура целиком на экране) молча возвращал её
                // почти в центр при любом слоте.
                ["crop"] = true,
            };
            // Место — словом («left», «center»…) или долей ширины кадра
            // («0.32»): доля идёт полем x, у слова свой словарь мест.
            if (float.TryParse(place, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
                pose["x"] = UnityEngine.Mathf.Clamp01(x + nudge);
            else
            {
                pose["position"] = string.IsNullOrEmpty(place) ? "center" : place;
                // СДВИГ ОТ СЛОТА: слово остаётся в позе (по нему фигура
                // ездит и разводится), а точное место идёт числом — у сцены
                // `x` сильнее `position`, поэтому оба поля живут вместе.
                if (nudge != 0f)
                    pose["x"] = UnityEngine.Mathf.Clamp01(Placement.SlotX(pose["position"].ToString()) + nudge);
            }
            return pose;
        }
    }
}
