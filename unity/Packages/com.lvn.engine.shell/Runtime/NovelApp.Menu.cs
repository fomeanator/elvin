using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВИТРИНА МЕНЮ — часть <see cref="NovelApp"/>, отвечающая за сцену ЗА
    /// интерфейсом: широкое полотно, кукла героини перед ним и сторож, который
    /// сверяет всё это с фактом на экране.
    ///
    /// <para>Отдельным файлом, потому что NovelApp вырос до трёх тысяч строк и
    /// держит в себе всё сразу — загрузку контента, главу, кошелёк, квик-меню.
    /// Витрина — самостоятельная тема со своим состоянием и своими граблями
    /// (три бага 26–27.08 про белый прямоугольник, чёрный фон и уехавшую
    /// куклу), и искать её среди трёх тысяч строк не должно быть работой.</para>
    ///
    /// <para>Числа витрины живут в <see cref="LvnMenuStage"/>; здесь —
    /// поведение.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // ── МЕНЮ ВНУТРИ ИГРЫ (решение Ильи 26.08) ── меню рисуется НАСТОЯЩЕЙ
        // сценой: полотно — команда bg, фаворит — сценический актёр (живые
        // слои, наши fx, смена наряда обновляет его штатно). UITK-шелл поверх
        // держит только панели.
        private string _menuSceneActor;
        /// <summary>Фаворит, на котором сцену меню собирали в прошлый раз —
        /// по нему событие настроек отличает «сменили героиню» от «двигают
        /// ползунок громкости».</summary>
        private string _lastMenuFavorite;
        // Канвас меню ставится ОДИН РАЗ за менюшную сессию: повторные bg-команды
        // (страж наряда стрелял на каждую примерку) конкурировали с пан-командой
        // вкладок — фон «елозил туда-сюда» и пан сбивался (живой репорт 27.08).
        // Фоном меню рулят только: первая постановка здесь и PanMenuScene.
        // Знаем ли, на какой вкладке стоит полотно (был хоть один переезд).
        private bool _menuPanSet;

        /// <summary>Чем объясняется последний ответ <see cref="MenuCanvasUrl"/>.
        /// Пишется туда же, читается логом сцены: без этого откат на авторское
        /// полотно происходил молча.</summary>
        private string _canvasWhy = "—";

        /// <summary>ПОЛОТНО МЕНЮ — ОДНО МЕСТО РЕШЕНИЯ. Адрес читался в шести
        /// местах напрямую из манифеста, и выбор игрока пришлось бы вписывать
        /// в каждое: забыть одно — и меню в одном из путей (возврат из главы,
        /// первый показ, створ, прогрев) показало бы чужой фон.
        ///
        /// <para>Купленное важнее авторского, но ВЛАДЕНИЕ ПРОВЕРЯЕТСЯ: выбор
        /// живёт на устройстве, а покупка — в кошельке. Иначе достаточно было
        /// бы переставить ключ в настройках устройства, чтобы получить платный
        /// фон даром. Бесплатный (price = 0) ставится без вопросов.</para></summary>
        private string MenuCanvasUrl()
        {
            var b = _manifest?.ui?.browse;
            // ФОН ПРИНАДЛЕЖИТ ГЕРОЮ, КОТОРЫЙ СТОИТ НА ГЛАВНОЙ. Илья: «поставить
            // каждому свой можно будет» и «когда перса выбираешь — фон меняться
            // будет». Поэтому спрашиваем не «что игрок выбрал вообще», а «что
            // надето на том, кого он поставил»; смена фаворита меняет полотно
            // сама собой, без второго переключателя.
            var who = LvnFavorite.Entity(_manifest);
            string picked = null;
            bool trying = false;        // идёт примерка, а не показ купленного
            if (!string.IsNullOrEmpty(who))
            {
                // ПРИМЕРКА ВЫШЕ НАДЕТОГО. Полотно — такой же скин, и меряют его
                // до покупки, как платье: пока лист открыт, сцена показывает
                // выбранное. Здесь спрашивали только надетое, поэтому карточка
                // нажималась, а фон не менялся до самой оплаты.
                if (Lvn.UI.LvnWardrobe.Previewed(who)
                        .TryGetValue(WardrobeSheet.BackdropAxis, out picked))
                    trying = !string.IsNullOrEmpty(picked);
                if (!trying)
                    Lvn.UI.LvnWardrobe.Equipped(who)
                        .TryGetValue(WardrobeSheet.BackdropAxis, out picked);
            }
            _canvasWhy = "авторское";   // объяснение решения — на случай молчания
            // Хвост: героя ещё нет (первый запуск, пустой каталог обликов) —
            // берём выбор игрока как таковой, иначе фон нельзя было бы
            // поставить вовсе.
            if (string.IsNullOrEmpty(picked)) picked = Lvn.UI.LvnPrefs.MenuBackdrop;
            if (!string.IsNullOrEmpty(picked) && b?.canvas_options != null)
                foreach (var o in b.canvas_options)
                    if (o != null && o.id == picked && !string.IsNullOrEmpty(o.url))
                    {
                        // ПОЧЕМУ выбор игрока не поехал — вслух. Молчаливый
                        // откат на авторское полотно стоил половины вечера:
                        // выбор стоял, кнопка нажималась, а сцена показывала
                        // старое и не говорила ни слова.
                        // За примерку не платят: показываем, что меряют.
                        bool owned = trying || o.price <= 0
                                  || WardrobeSheet.OwnsBackdrop(who, o.id);
                        _canvasWhy = trying ? $"примерка «{o.id}»"
                                   : owned  ? $"надет «{o.id}» (герой {who})"
                                            : $"выбран «{o.id}», но НЕ КУПЛЕН — откат на авторское";
                        if (owned) return o.url;
                        break;
                    }
            else if (!string.IsNullOrEmpty(picked))
                _canvasWhy = $"выбран «{picked}», но его нет в ui.browse.canvas_options";
            return b?.canvas;
        }

        /// <param name="fade">null — растворять как фон внутри главы (тема
        /// <c>ui.stage.bg_fade</c>); число — своя длительность, 0 — мгновенно.</param>
        /// <summary>
        /// КОМАНДА ПОЛОТНА ВИТРИНЫ — где оно стоит и как проступает.
        ///
        /// <para>Собиралась в трёх местах руками (возврат из главы, первый показ
        /// меню, створ портала), и в каждом повторялось правило «какую точку
        /// показывать»: переезды уже были — берём их точку, не было — начальную.
        /// Правило одно, записей три; добавь в него условие (скажем, вкладку —
        /// а вкладки тут и есть смысл переездов) — и забыть его в одном месте
        /// значит получить прыжок полотна ровно на одном из трёх путей.</para>
        ///
        /// <para>Фон СЦЕНЫ (гардероб показывает последний фон главы) сюда не
        /// относится: у него нет переездов, и точка ему не нужна.</para>
        /// </summary>
        private Newtonsoft.Json.Linq.JObject MenuCanvasCmd(string canvas, float? fade)
        {
            if (string.IsNullOrEmpty(canvas)) return null;
            var cmd = new Newtonsoft.Json.Linq.JObject
            {
                ["op"] = "bg",
                ["sprite_url"] = canvas,
                ["pan"] = MenuPoint().x,
                ["pan_y"] = MenuPoint().y,          // комнаты стоят и по высоте — см. LvnTabs.Room
                ["zoom"] = LvnMenuStage.PanZoom,   // запас, по которому едет переезд
            };
            if (fade.HasValue) cmd["fade"] = fade.Value;
            return cmd;
        }

        /// <summary>Настройки ВИТРИН из манифеста: рост куклы и переезд
        /// полотна в меню (<c>ui.browse</c>), кадр плиток гардероба
        /// (<c>ui.wardrobe.framing</c>). Отдельным методом, потому что зовётся
        /// из двух мест — при загрузке и при живом обновлении, — и «поменять на
        /// лету» должно означать именно это, а не перезапуск.</summary>
        private static void ApplyMenuStaging(LvnManifest manifest)
        {
            var b = manifest?.ui?.browse;
            LvnMenuStage.Apply(b);
            LvnWardrobeStage.Apply(manifest?.ui?.wardrobe);
        }

        /// <summary>Полотно витрины греется, как только известен манифест.
        /// Меню открывается всегда — им начинается запуск и им кончается каждая
        /// глава, — а его фон качался и декодился по месту показа: витрина
        /// стояла чёрной, героиня в ней уже была, картинка доезжала «позже»
        /// (Илья 27.08). Одна известная картинка, прогретая заранее, снимает
        /// это целиком.</summary>
        private void WarmMenuCanvas()
        {
            if (Stage == null) return;
            var canvas = MenuCanvasUrl();
            if (!string.IsNullOrEmpty(canvas))
                LvnAsync.Fire(Stage.WarmMenuCanvasAsync(canvas), "WarmMenuCanvas");

            // ГЕРОИНЯ ГРЕЕТСЯ ВМЕСТЕ С ПОЛОТНОМ, А НЕ ПОСЛЕ НЕГО. Полотно
            // прогревали заранее, куклу — нет: её слои начинали качаться и
            // распаковываться в тот миг, когда витрина уже открыта. Живой
            // запуск 01.09 дал пять секунд ожидания, и не из-за сети: три
            // места в декодере занимали полотно (2000×1500) и ядро створа, а
            // пять слоёв героини стояли к ним в очередь по 1,2–5,0 с каждый.
            //
            // Кукла — не «про запас», она первый кадр витрины наравне с фоном.
            var fav = MenuFavoriteEntity();
            if (!string.IsNullOrEmpty(fav))
                LvnAsync.Fire(Stage.WarmActorAsync(fav), "WarmMenuHeroine");

            // И РАМКИ ВИТРИНЫ — ТОЙ ЖЕ ПАЧКОЙ. Панель, карточка, нижнее меню,
            // лого и значки качались и декодились уже ПОСЛЕ снятия вуали и
            // всплывали по одной, а через секунду живое обновление
            // пересобирало витрину и декодировало их второй раз («картинки
            // с главного меню надо при старте прогревать, а то мелькают» —
            // Илья 08.09). Задача остаётся у нас: бут-вуаль ждёт её вместе
            // с полотном.
            var art = MenuArtUrls(_manifest?.ui?.browse);
            if (art.Count > 0)
            {
                _menuArtWarm = Stage.WarmMenuArtAsync(art);
                LvnAsync.Fire(_menuArtWarm, "WarmMenuArt");
            }
        }

        /// <summary>
        /// ДОЖДАТЬСЯ, ПОКА ВИТРИНА НАРИСУЕТСЯ, и додержать затемнение.
        ///
        /// <para>До первой раскладки у хаба нулевая ширина: снятая в этот миг
        /// вуаль открывает пустоту, куда через кадр-другой въезжают панели.
        /// Ждём НЕНУЛЕВОЙ размер — это факт, а не таймер, — и добираем ещё
        /// пару кадров: на первой раскладке элементы рождаются, на следующей
        /// рисуются.</para>
        ///
        /// <para>Затем выдержка <see cref="LvnMenuStage.VeilHoldMs"/> от начала
        /// ожидания: секунда чёрного между заставкой и витриной — вход, а не
        /// задержка, и она же прячет остаток сборки на медленном телефоне.</para>
        /// </summary>
        private async Task MenuPaintedAsync(System.Threading.CancellationToken ct,
                                            System.Diagnostics.Stopwatch wait)
        {
            var hub = _shell?.Hub;
            if (hub != null)
            {
                while (!ct.IsCancellationRequested
                       && hub.resolvedStyle.width <= 1f
                       && wait.ElapsedMilliseconds < LvnMenuStage.VeilWaitMs)
                    await Task.Yield();
                for (int i = 0; i < 2 && !ct.IsCancellationRequested; i++)
                    await Task.Yield();
            }
            while (!ct.IsCancellationRequested
                   && wait.ElapsedMilliseconds < LvnMenuStage.VeilHoldMs)
                await Task.Yield();
        }

        /// <summary>Прогрев арта витрины — задача, которую бут-вуаль ждёт
        /// вместе с полотном, в тот же бюджет времени.</summary>
        private Task _menuArtWarm;
        private bool MenuArtReady => _menuArtWarm == null || _menuArtWarm.IsCompleted;

        /// <summary>АДРЕСА АРТА ВИТРИНЫ по манифесту: рамки облика «сцена» из
        /// папки skin (тот же список, что кладут главная и шапка), лого,
        /// аватар и значки валют. Пусто — витрина без облика, греть нечего.
        /// Без повторов: один адрес — один декод.</summary>
        internal static List<string> MenuArtUrls(BrowseConfig b)
        {
            var urls = new List<string>();
            if (b == null) return urls;
            void Add(string u) { if (!string.IsNullOrEmpty(u) && !urls.Contains(u)) urls.Add(u); }
            if (!string.IsNullOrEmpty(b.skin))
                foreach (var file in LvnStageKit.SkinFiles) Add(LvnStageKit.SkinUrl(b.skin, file));
            Add(b.logo);
            Add(b.avatar);
            if (b.currency_icons != null)
                foreach (var kv in b.currency_icons) Add(kv.Value);
            return urls;
        }

        /// <summary>
        /// ГЛАВА КОНЧИЛАСЬ — КАДР ПЕРЕХОДИТ МЕНЮ.
        ///
        /// <para>Раньше здесь сцена стиралась в ноль, и меню собирало её
        /// заново: белый кадр на месте полотна, перезагрузка слоёв героини,
        /// костыли вроде «держать арт куклы живым». Меню — не другой экран, а
        /// состояние этой же сцены, поэтому переход к нему — смена фона и
        /// уход лишних, а не уборка.</para>
        ///
        /// <para>Героиня меню и героиня главы — ОДНА И ТА ЖЕ: она уходит на
        /// миссию и возвращается. Поэтому она не ставится заново, а
        /// ПЕРЕСТАВЛЯЕТСЯ: место берётся у витрины меню, а наряд и эмоция
        /// остаются те, с которыми кончилась глава.</para>
        /// </summary>
        private void HandOverToMenu()
        {
            if (Stage == null) return;
            var canvas = MenuCanvasUrl();
            var fav = MenuFavoriteEntity();
            // ВОЗВРАЩЕНИЕ — ДЛИННЫЙ ВЫДОХ, а не переключение. Полторы секунды
            // кроссфейда: мир главы отпускает, полотно меню проступает.
            // Короткий фейд читался как сбой картинки.
            var bg = MenuCanvasCmd(canvas, MenuReturnFadeSeconds);
            LvnLog.Trace($"[lvn-menu] передача кадра: полотно={(bg != null ? "меню" : "прежнее")}, "
                       + $"остаётся={fav ?? "-"}, облик известен={(!string.IsNullOrEmpty(fav) && Stage.RememberedByScript(fav))}");
            Stage.HandOver(bg, fav);
            // Героиня выходит ПОСЛЕ фона, а не вместе с ним: сначала мир, потом
            // тот, кто в него вернулся. Появиться одновременно значит смазать
            // оба события в одно мельтешение.
            LvnAsync.Fire(PlaceMenuHeroineSoonAsync(), "MenuHeroine");
        }

        /// <summary>Сколько длится возвращение: кроссфейд полотна меню.</summary>
        private const float MenuReturnFadeSeconds = 0.5f;

        /// <summary>Пауза перед выходом героини — доля фейда фона. Мир должен
        /// успеть проступить.</summary>
        private const float MenuHeroineDelay = 0.55f;

        private async System.Threading.Tasks.Task PlaceMenuHeroineSoonAsync()
        {
            await System.Threading.Tasks.Task.Delay(
                (int)(MenuReturnFadeSeconds * MenuHeroineDelay * 1000f));
            if (InChapter) return;   // успели уйти обратно в главу
            // КАТСЦЕНА УЖЕ МОГЛА ЕЁ ПОСТАВИТЬ. Возвращение из главы ставит куклу
            // трижды: портал (по центру, перед всеми), эта отложенная
            // перестановка и витрина меню. Каждая двигала её заново — героиня
            // прыгала на месте, и это читалось как «их снова две». Стоит на
            // своём месте — не трогаем.
            var fav = MenuFavoriteEntity();
            // «УЖЕ СТОИТ» — ЭТО ЕЩЁ НЕ «СТОИТ ЦЕЛОЙ». Пропуск смотрел только на
            // то, видна ли она; неполная фигура (слой не доехал в первой
            // сессии) под это условие подходит — и остаётся на главном экране
            // безликой, потому что перестановку, которая её пересобрала бы, мы
            // же и пропустили. Признак целости у Примы был написан и не
            // спрошен ни разу — вот его место.
            if (!string.IsNullOrEmpty(fav) && fav == _menuSceneActor
                && Stage != null && Stage.ActorVisibleOrPending(fav)
                && Stage.Prima.Whole)
            {
                LvnLog.Trace("[lvn-menu] героиня уже стоит и цела — отложенную перестановку пропускаем");
                return;
            }
            PlaceMenuHeroine();
        }

        /// <summary>Героиня встаёт так, как стоит в меню: место — витрины,
        /// облик — тот, с которым она пришла (Restage не переодевает).
        /// <paramref name="z"/> задаётся только катсценами: там она обязана
        /// стоять перед всеми, а не по старшинству рождения.</summary>
        private bool PlaceMenuHeroine(int? z = null, LvnSender sender = LvnSender.Menu)
        {
            var fav = MenuFavoriteEntity();
            if (Stage == null || string.IsNullOrEmpty(fav)) return false;
            // ФИГУРА ОДНА, И НАСТРОЙКИ ЕЙ ШЛЮТ, А НЕ СОБИРАЮТ КОМАНДУ НА МЕСТЕ.
            // Здесь стоял десяток полей — место, рамка витрины, порядок слоя, —
            // и ровно такой же набор лежал ещё в трёх местах (две катсцены и
            // гардероб). Разница в одном поле у одного из четырёх вызовов
            // означала другого человека на экране; из этого и состояла неделя
            // дефектов «героинь две / встаёт по-менюшному / рост скачет».
            Stage.Prima.Cast(fav);
            // МЕСТО — СЛОТ ТЕКУЩЕЙ ВКЛАДКИ. Фигуру ставят заново на каждую
            // смену наряда и на каждый возврат из главы; ставь её всегда в
            // слот главной — и она прыгала бы влево посреди гардероба, где её
            // только что увели в центр.
            if (!Stage.Prima.Stand(sender, z, MenuDollSlot(),
                                   nudge: LvnMenuStage.DollNudge(LvnTabs.RoomOf(_shell?.Tab ?? LvnTabs.Home))))
                return false;
            // План и дыхание полотна — тоже свойства вкладки, не картинки.
            if (sender == LvnSender.Menu) RestoreMenuComposition();
            _menuSceneActor = fav;
            return true;
        }

        /// <summary>
        /// СЦЕНА ГЛАВНОЙ: полотно, героиня и — по случаю — врата.
        ///
        /// <para><paramref name="withPortal"/> отделяет ПОЯВЛЕНИЕ МЕНЮ от его
        /// ПЕРЕСБОРКИ. Пересобирают сцену часто: сменил наряд в гардеробе,
        /// сменил фаворита — кукла встаёт заново. Врата к этому отношения не
        /// имеют: они — событие ухода на миссию, и вспыхивать на каждую юбку им
        /// незачем («смена одежды в гардеробе портал тригерит, там не надо» —
        /// Илья 28.08).</para>
        /// </summary>
        private void ShowMenuScene() => ShowMenuScene(withPortal: true);

        private void ShowMenuScene(bool withPortal)
        {
            // ВЕРНУЛИСЬ ИЗ ГЛАВЫ — героиня это замечает (TR-66). Только настоящий
            // приход: лечение полотна зовёт нас же и событием не считается.
            if (withPortal) Raise("on_return", act: true);
            if (Stage == null || InChapter)
            {
                LvnLog.Trace($"[lvn-menu] сцена меню ПРОПУЩЕНА: stage={(Stage != null)}, играется глава={InChapter}");
                return;
            }
            var canvas = MenuCanvasUrl();
            // «Стоит ли уже полотно» спрашиваем У СЦЕНЫ. Здесь жил свой флажок,
            // и он врал ровно тогда, когда это было важнее всего: картинка со
            // сцены пропадала, а флажок держал «стоит».
            bool already = Stage.ShowsBackdrop(canvas);
            LvnLog.Trace($"[lvn-menu] сцена меню: canvas={(string.IsNullOrEmpty(canvas) ? "НЕТ" : canvas)} "
                      + $"[{_canvasWhy}], уже стоит={already} → полотно "
                      + $"{(!string.IsNullOrEmpty(canvas) && !already ? "СТАВИМ" : "не трогаем")}");
            if (!string.IsNullOrEmpty(canvas) && !already)
            {
                // Точку выбирает MenuCanvasCmd: стартовая четверть — вкладка
                // «Главная» (меню всегда открывается с неё), а если переезды уже
                // были — полотно встаёт СРАЗУ на их точку, иначе первый же тик
                // дёрнул бы его через полкадра.
                // ПЕРВОЕ ПОЛОТНО ВСТАЁТ МГНОВЕННО (растворять не из чего), а
                // ЗАМЕНА — кроссфейдом темы, тем же, что у фона внутри главы:
                // примерка фонов подменяет картину под ногами, и рывок читался
                // как сбой, а не как выбор.
                Stage.ApplyStage(MenuCanvasCmd(canvas, Stage.HasBackdrop ? (float?)null : 0f),
                                 LvnSender.Menu);
            }
            var fav = MenuFavoriteEntity();
            // ГЕРОИНЯ ОДНА, И РИСУЕТ ЕЁ СЦЕНА. Рисовать её умели двое — сцена
            // (актёр на канвасе, тот же, что в главе) и оболочка (своя кукла из
            // тех же слоёв в UI-элементе), — и первым вопросом о любой её
            // странности было «а кто её сейчас рисует». Второй реализации
            // больше нет; здесь остаётся один хозяин и один путь.
            // Кукла меню живёт между главами — её арт не отпускаем на уборке
            // сцены, иначе выход из главы каждый раз ждёт перезагрузку слоёв.
            Stage.Prima.Cast(fav);
            Stage.Prima.Keep();
            // Тот же фаворит уже стоит ИЛИ его показ в полёте — ничего не
            // слать: смену наряда сцена применяет сама (LvnWardrobe.Changed),
            // а повторная actor-команда только передёргивала куклу. Но если
            // кукла ПРОПАЛА (оборванная загрузка, сеть) — страж самолечится
            // и шлёт показ заново.
            LvnLog.Trace($"[lvn-menu] кукла: фаворит={fav ?? "-"}, стоявший={_menuSceneActor ?? "-"}, "
                      + $"на сцене={(string.IsNullOrEmpty(fav) ? false : Stage.ActorVisibleOrPending(fav))}");
            // Через 1.5с (после входа куклы) перечислить сплошные светлые
            // поверхности сцены — охота на белый прямоугольник (26.08).
            LvnAsync.Fire(DumpSceneSoonAsync(), "DumpScene");
            // ПОТОК КОМАНД — первое, что стоит спросить, когда «на экране не
            // то»: кто просил, что приняли, что отклонили и кем занят предмет.
            LvnLog.Trace(Stage.Commands.Journal());
            // ЧТО ЧИНИЛОСЬ САМО — второе. Каждое лечение это дефект, которого
            // игрок не увидел: пустой журнал значит, что сцена собралась как
            // задумана, непустой — список настоящих поломок со счётчиками.
            LvnLog.Trace(Stage.Healer.Journal());
            if (withPortal) ShowMenuPortal();   // врата — событие, а не пересборка
            if (fav == _menuSceneActor
                && (string.IsNullOrEmpty(fav) || Stage.ActorVisibleOrPending(fav))) return;
            // Самолечение того же фаворита не прячет его перед повтором show.
            // НАРОЧНО команда, а не Прима: Прима — это ТЕКУЩАЯ постоянная
            // фигура, а здесь уводят ПРЕЖНЮЮ, которая ею быть перестала.
            // Постановка идёт ниже, через PlaceMenuHeroine → Stage.Prima.
            if (!string.IsNullOrEmpty(_menuSceneActor) && _menuSceneActor != fav)
                Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                { ["op"] = "actor", ["id"] = _menuSceneActor, ["show"] = false }, LvnSender.Menu);
            // Тем же путём, что и штатная постановка: рост и место куклы —
            // настройки витрины, и знать их обязано ОДНО место. Здесь стояла
            // вторая копия тех же полей, и расходились они молча.
            if (!PlaceMenuHeroine()) _menuSceneActor = null;   // игра без героини
        }

        // Пан полотна по вкладкам: полотно ведёт ТИК UI-анимации
        // (OnTabTravelTick) — кадр в кадр и той же кривой, что переезд
        // страниц. Собственный пан-таймер фона (bg-команда, 0.30с smoothstep)
        // стартовал позже async-тракта и ехал иначе — «рассинхрон в глаза
        // бросается» (Илья 28.08). Здесь запоминаются только конечные точки.
        private Vector2 _menuPanFrom, _menuPanTo;
        // Куда уходит ГЕРОИНЯ на этих же вкладках: на главной она стоит там,
        // где её поставил автор, на боковых возвращается в центр кадра.
        private string _menuDollSlot;     // куда едет героиня в этом переезде
        private float _menuDollNudge;     // …и на сколько правее слота там стоит
        private bool _menuDollSent;         // …и послана ли она уже (первым тиком)
        // …и насколько она отодвинута: на главной свой план, на боковых — вблизи.
        private float _menuCastZoomFrom = 1f, _menuCastZoomTo = 1f;

        /// <summary>Точка полотна ДЛЯ ТЕКУЩЕГО МЕСТА игрока: переезды уже были —
        /// их конец, не было — вкладка, на которой он стоит. Сцену меню
        /// пересобирают и из гардероба, и вставать полотну надо туда, где оно
        /// и стояло, а не «как на главной».</summary>
        private Vector2 MenuPoint()
            => _menuPanSet ? _menuPanTo : MenuPointFor(_shell?.Tab ?? LvnTabs.Home);

        /// <summary>ТОЧКА ПОЛОТНА ДЛЯ ВКЛАДКИ — её комната на карте
        /// (<see cref="LvnTabs.Room"/>), пропущенная через правило витрины.</summary>
        private Vector2 MenuPointFor(int tab)
        {
            var room = LvnTabs.Room(tab);
            return LvnMenuStage.CanvasPointFor(room.x, room.y);
        }

        private void PanMenuScene(int fromTab, int toTab)
        {
            if (Stage == null || InChapter) return;
            // ГЕРОИНЯ ТРОГАЕТСЯ С ПЕРВЫМ КАДРОМ ПЕРЕЕЗДА, а не здесь: между
            // объявлением переезда и его первым кадром страница ещё
            // раскладывается (до 160 мс), и фигура, посланная сейчас, приехала
            // бы раньше кадра. Тик посылает её один раз — по этому флагу.
            _menuDollSlot = LvnMenuStage.DollSlot(LvnTabs.RoomOf(toTab));
            _menuDollNudge = LvnMenuStage.DollNudge(LvnTabs.RoomOf(toTab));
            _menuDollSent = false;
            // УХОДИМ ИЗ ГАРДЕРОБА — ОБЩИЙ ПЛАН ВОЗВРАЩАЕТСЯ ВМЕСТЕ С ПЕРЕЛЁТОМ.
            // Наезд гардероба (камера 1.07 на разделе «Моё») снимался при
            // закрытии листа, а лист закрывается ПОСЛЕ приезда: героиня
            // прилетала на главную, и уже там отдельным движением
            // «уменьшалась» — «чуть больше делает, потом меньше, анимация
            // кривит; с других вкладок нет» (Илья 08.09). Камера — один полёт:
            // сброс наезда идёт тем же временем, что и переезд. Повторный
            // сброс при закрытии листа найдёт камеру на месте и ничего не
            // сдвинет.
            if (fromTab == LvnTabs.Wardrobe)
                Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                { ["op"] = "camera", ["action"] = "reset",
                  ["duration"] = LvnMenuStage.TravelMs / 1000f }, LvnSender.Menu);
            var canvas = MenuCanvasUrl();
            if (string.IsNullOrEmpty(canvas)) return;
            // Здесь только откуда и куда: сам переезд ведёт тик анимации
            // вкладок, а правило «место кнопки → точка кадра» — у витрины.
            _menuPanFrom = MenuPointFor(fromTab);
            _menuPanTo = MenuPointFor(toTab);
            _menuCastZoomFrom = LvnMenuStage.CastZoomFor(LvnTabs.RoomOf(fromTab));
            _menuCastZoomTo = LvnMenuStage.CastZoomFor(LvnTabs.RoomOf(toTab));
            _menuPanSet = true;
            LvnLog.Trace($"[lvn-pan] полотно {fromTab} → {toTab}: "
                       + $"кадр ({_menuPanFrom.x:0.000}, {_menuPanFrom.y:0.000}) → "
                       + $"({_menuPanTo.x:0.000}, {_menuPanTo.y:0.000}); "
                       + $"героиня → слот «{_menuDollSlot}», "
                       + $"план {_menuCastZoomFrom:0.00} → {_menuCastZoomTo:0.00}");
            // ФЛАГ «канвас стоит» ЗДЕСЬ НЕ ВЫСТАВЛЯЕТСЯ. Пока пан жил
            // собственной bg-командой, этот метод сам ставил полотно и имел
            // право на такое заявление. Теперь он только запоминает точки — а
            // флаг заставлял ShowMenuScene пропустить постановку канваса, и
            // после выхода из главы (где флаг сбрасывается) меню оставалось
            // с пустым полотном: Image без спрайта — тот самый белый квадрат
            // на месте героини (Илья 26.08).
        }

        // ── камера гардероба: наезд на зону выбираемого скина ────────────────
        // Кукла меню: ноги у низа, рост 0.91 высоты сцены → голова ~0.82H от
        // низа, шея ~0.72H, корпус ~0.45H. Скейл GameRoot идёт вокруг центра,
        // пан возвращает точку интереса чуть выше центра кадра (+0.10H).
        private void OnWardrobeSection(Lvn.UI.Screens.WardrobeSheet from, string axis)
        {
            if (Stage == null) return;
            // Камера тут МЕНЮШНАЯ. Сюжетный лист стоит поверх сцены главы, и
            // его разделы к ней отношения не имеют: пусти их сюда — игрок
            // вернётся в меню и найдёт куклу в зуме, которого не выбирал.
            if (_storySheet != null && ReferenceEquals(from, _storySheet)) return;
            if (axis == null)
            {
                Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                { ["op"] = "camera", ["action"] = "reset", ["duration"] = 0.5 }, LvnSender.Menu);
                // «Общий план» — это про НАЕЗД гардероба, а не про композицию
                // витрины: сброс камеры возвращает фигурам их рост и место,
                // и без этой строки героиня после закрытия листа вставала на
                // главной в полный рост, забыв, что там она отодвинута.
                RestoreMenuComposition(0.5f);
                return;
            }
            // target — куда в кадре кладём точку интереса (доли высоты экрана
            // от центра, + = выше): цифры Ильи 28.08 — украшения на 10% ниже,
            // причёска на 30% ниже, платье на 7% выше и зум −10%.
            // ЧТО ЗА ОСЬ — спрашиваем у витрины (LvnWardrobeStage.KindOf), а не
            // угадываем заново. Здесь стояла ТРЕТЬЯ копия правила, и она успела
            // отстать: дом нормализует «ё» → «е», а копия нет — ось с именем
            // «Причёска» получала кадр НА КОРПУС, хотя лист показывал её
            // причёской. Числа кадра остаются здесь: они про камеру, а не про
            // смысл оси.
            float z, focus, target;
            if (axis == Lvn.UI.Screens.WardrobeSheet.AllTab)
            { z = 1.07f; focus = 0.5f; target = 0f; } // «Моё»: лёгкий наезд по центру
            // ПОЛОТНО СМОТРЯТ, А НЕ ПЛАТЬЕ: отводим камеру на общий план, иначе
            // фон закрыт героиней (ось попадала в «одежду» и давала наезд 1.31).
            // Дальше 1.0 не отводим: камера тянет GameRoot целиком (фон + куклу),
            // и зум <1 ужал бы САМ ФОН, оголив края кадра.
            else if (Lvn.UI.LvnWardrobeStage.KindOf(axis) == Lvn.UI.LvnWardrobeAxisKind.Backdrop)
            { z = 1.00f; focus = 0.5f; target = 0f; }
            else switch (Lvn.UI.LvnWardrobeStage.KindOf(axis))
            {
                case Lvn.UI.LvnWardrobeAxisKind.Hair:
                    z = 1.91f; focus = 0.82f; target = 0.30f; break; // Илья 28.08: 2×5% выше, зум −7%
                case Lvn.UI.LvnWardrobeAxisKind.Decor:
                    z = 1.90f; focus = 0.72f; target = 0.20f; break;
                default:
                    z = 1.31f; focus = 0.45f; target = 0.03f; break; // платье/наряд — корпус
            }
            // Канвас сцены width-match к 1080 — его высота в юнитах канваса.
            float H = 1080f * Screen.height / Mathf.Max(1, Screen.width);
            float panY = (target - (focus - 0.5f) * z) * H;
            Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
            { ["op"] = "camera", ["action"] = "zoom", ["factor"] = z, ["duration"] = 0.55 }, LvnSender.Menu);
            Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
            { ["op"] = "camera", ["action"] = "pan", ["y"] = panY, ["duration"] = 0.55 }, LvnSender.Menu);
            // ФИГУРЫ ОТЪЕЗЖАЮТ, ПОЛОТНО ОСТАЁТСЯ. Зумом камеры так нельзя: он
            // тянет фон вместе с героиней. На вкладке фона смотрят картину —
            // героиня уходит вглубь и не закрывает её собой.
            // НА ВКЛАДКЕ ФОНА ГЕРОИНИ НЕТ. Выбирают картину — она и должна быть
            // видна целиком; уменьшать фигуру бессмысленно (мелкая читается как
            // сбой), а снимать со сцены дорого — облик и примерка живут на ней.
            float cast = Lvn.UI.LvnWardrobeStage.KindOf(axis) == Lvn.UI.LvnWardrobeAxisKind.Backdrop
                       ? 0f : 1f;
            LvnLog.Trace($"[lvn-menu] раздел «{axis}» ({Lvn.UI.LvnWardrobeStage.KindOf(axis)}): "
                       + $"зум {z:0.00}, фигуры {(cast <= 0f ? "УБИРАЕМ" : "показываем")}");
            Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
            { ["op"] = "camera", ["action"] = "cast", ["alpha"] = cast, ["duration"] = 0.35 }, LvnSender.Menu);
        }

        /// <summary>Слот героини для текущей вкладки — по роду её комнаты:
        /// главная и магазин слева, боковые — центр. Одно место решения на
        /// всех, кто её ставит.</summary>
        private string MenuDollSlot()
            => LvnMenuStage.DollSlot(LvnTabs.RoomOf(_shell?.Tab ?? LvnTabs.Home));

        /// <summary>КОМПОЗИЦИЯ ТЕКУЩЕЙ ВКЛАДКИ — план героини и дыхание
        /// полотна. Место сюда НЕ входит: его фигура получает позой
        /// (<see cref="MenuDollSlot"/>), а не сдвигом слоя. Одно место решения
        /// на всех, кто может сбить план: сброс камеры после гардероба,
        /// пересборка сцены, возврат из главы.</summary>
        private void RestoreMenuComposition(float seconds = 0f)
        {
            if (Stage == null) return;
            var room = LvnTabs.RoomOf(_shell?.Tab ?? LvnTabs.Home);
            float zoom = LvnMenuStage.CastZoomFor(room);
            LvnLog.Trace($"[lvn-pan] композиция вкладки {_shell?.Tab ?? LvnTabs.Home} "
                       + $"({room}): слот «{MenuDollSlot()}», "
                       + $"план {zoom:0.00}, за {seconds:0.00}с");
            // Слой фигур больше не сдвигается — место у героини своё, слотом.
            // Ноль здесь — на случай, если слой остался сдвинутым с прежних
            // сборок (камера сбрасывает его сама, но не на всех путях).
            Stage.SetCastShift(0f, seconds);
            Stage.SetCastZoom(zoom, seconds);
            // ВИТРИНА ДЫШИТ. Блуждание — её свойство, а не картинки: новый фон
            // приходит неподвижным (SetSprite гасит его), и включать гуляние
            // надо там же, где встаёт композиция вкладки.
            Stage.SetBackgroundDrift(LvnMenuStage.Drift, LvnMenuStage.Drift,
                                     LvnMenuStage.DriftSeconds);
        }

        /// <summary>
        /// НЕДУГ ВИТРИНЫ — «мы в меню, а полотна нет».
        ///
        /// <para>Постановка полотна и куклы — последовательность шагов, и любой
        /// из них может не доехать: оборвалась загрузка, уборка главы пришла
        /// следом, промахнулся флаг. Держать инвариант шагами хрупко («как-то
        /// хлипко» — Илья 26.08), поэтому смотрим на ФАКТ КАРТИНКИ, а не на
        /// флаг: флаг «фон стоит» врал, когда команда приходила до рождения
        /// рендерера, и страж молчал вместе с ним.</para>
        ///
        /// <para>Свой таймер и своё терпение здесь больше не живут: и то и
        /// другое — работа Лекаря, у которого этот недуг стоит рядом с
        /// остальными и попадает в общий журнал. Терпение обязательно: крупный
        /// канвас декодится ~0.6с, и лечить живую загрузку значит перебивать
        /// её.</para>
        /// </summary>
        private void WatchMenuBackdrop()
        {
            if (Stage == null) return;
            Stage.Healer.Watch("полотно витрины",
                () => !InChapter
                      && !string.IsNullOrEmpty(MenuCanvasUrl())
                      && !Stage.BackdropHasArt,
                () =>
                {
                    Debug.LogWarning("[lvn-menu] полотна нет, хотя мы в меню — ставим заново");
                    ShowMenuScene(withPortal: false);   // лечение полотна — не приход в меню
                },
                period: LvnMenuStage.GuardPeriodSeconds,
                patience: LvnMenuStage.GuardPatienceSeconds,
                // ТЕРПЕНИЕ — ДОГАДКА, ПОГРУЗКА — ФАКТ. Два оговорённых
                // секунды хватало на декод крупного канваса тут, на этой
                // машине; на слабом телефоне картинку везут дольше, и лечение
                // забирало у фона поколение, начиная лестницу повторов заново.
                working: () => Stage.BringingBackdrop(MenuCanvasUrl()));
        }

        // Перечисление сплошных светлых поверхностей сцены — снасть охоты на
        // «белый прямоугольник вместо героини» (26.08). Держится в коде,
        // потому что баг был не один и тракт тот же; но обходить иерархию на
        // каждый показ меню в живой игре незачем — только при включённой
        // подробной диагностике.
        private async System.Threading.Tasks.Task DumpSceneSoonAsync()
        {
            if (!LvnLog.Verbose) return;
            await System.Threading.Tasks.Task.Delay(1500);
            if (!InChapter) Stage?.DumpOpaqueGraphics();
        }

        /// <summary>Витрина уходит с экрана — снимает свой слой. Не «прячет
        /// куклу командой»: команда осталась бы в кадре главы чужой записью, а
        /// снятый слой не оставляет следов вовсе.</summary>
        private void HideMenuSceneActor()
        {
            if (Stage == null) return;
            // Реакция витрины не идёт в главу: клип обрывается, наложенное
            // лицо снимается — иначе «сон» после покупки доиграл бы на героине
            // уже в сцене истории, где лицом командует сценарий.
            _moodClip?.Stop();
            ReleaseFace(refresh: false);
            Stage.CloseMenuLayer();
            _menuSceneActor = null;
        }

        private string MenuFavoriteEntity() => LvnFavorite.Entity(_manifest);
    }
}
