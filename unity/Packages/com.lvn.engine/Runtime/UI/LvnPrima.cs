using Newtonsoft.Json.Linq;

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
        public bool Stand(LvnSender sender, int? z = null)
        {
            if (_stage == null || !Exists) return false;
            var pose = Pose(Id, Place, LvnMenuStage.DollWidth, LvnMenuStage.DollHeight, z ?? 0);
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
        public static JObject Pose(string id, string place, float width, float height, int z)
            => new JObject
            {
                ["op"] = "actor",
                ["id"] = id,
                ["show"] = true,
                ["position"] = string.IsNullOrEmpty(place) ? "center" : place,
                ["width"] = width,
                ["height"] = height,
                ["z"] = z,
            };
    }
}
