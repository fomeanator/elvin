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
        private Newtonsoft.Json.Linq.JObject MenuCanvasCmd(string canvas, float fade)
            => string.IsNullOrEmpty(canvas) ? null : new Newtonsoft.Json.Linq.JObject
            {
                ["op"] = "bg",
                ["sprite_url"] = canvas,
                ["pan"] = _menuPanSet ? _menuPanTo : LvnMenuStage.PanStart,
                ["fade"] = fade,
            };

        /// <summary>Настройки ВИТРИН из манифеста: рост куклы и переезд
        /// полотна в меню (<c>ui.browse</c>), кадр плиток гардероба
        /// (<c>ui.wardrobe.framing</c>). Отдельным методом, потому что зовётся
        /// из двух мест — при загрузке и при живом обновлении, — и «поменять на
        /// лету» должно означать именно это, а не перезапуск.</summary>
        private static void ApplyMenuStaging(LvnManifest manifest)
        {
            var b = manifest?.ui?.browse;
            if (b != null)
                LvnMenuStage.Apply(b.doll_height, b.doll_width, b.canvas_pan, b.canvas_pan_step);
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
            var canvas = _manifest?.ui?.browse?.canvas;
            if (Stage == null || string.IsNullOrEmpty(canvas)) return;
            LvnAsync.Fire(Stage.WarmMenuCanvasAsync(canvas), "WarmMenuCanvas");
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
            var canvas = _manifest?.ui?.browse?.canvas;
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
            if (_chapterPlaying) return;   // успели уйти обратно в главу
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
            if (!Stage.Prima.Stand(sender, z)) return false;
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
            if (Stage == null || _chapterPlaying)
            {
                LvnLog.Trace($"[lvn-menu] сцена меню ПРОПУЩЕНА: stage={(Stage != null)}, играется глава={_chapterPlaying}");
                return;
            }
            var canvas = _manifest?.ui?.browse?.canvas;
            // «Стоит ли уже полотно» спрашиваем У СЦЕНЫ. Здесь жил свой флажок,
            // и он врал ровно тогда, когда это было важнее всего: картинка со
            // сцены пропадала, а флажок держал «стоит».
            bool already = Stage.ShowsBackdrop(canvas);
            LvnLog.Trace($"[lvn-menu] сцена меню: canvas={(string.IsNullOrEmpty(canvas) ? "НЕТ" : "есть")}, "
                      + $"уже стоит={already} → полотно {(!string.IsNullOrEmpty(canvas) && !already ? "СТАВИМ" : "не трогаем")}");
            if (!string.IsNullOrEmpty(canvas) && !already)
            {
                // Точку выбирает MenuCanvasCmd: стартовая четверть — вкладка
                // «Главная» (меню всегда открывается с неё), а если переезды уже
                // были — полотно встаёт СРАЗУ на их точку, иначе первый же тик
                // дёрнул бы его через полкадра.
                Stage.ApplyStage(MenuCanvasCmd(canvas, 0f), LvnSender.Menu);
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
        private float _menuPanFrom, _menuPanTo;
        private void PanMenuScene(int fromTab, int toTab)
        {
            if (Stage == null || _chapterPlaying) return;
            var canvas = _manifest?.ui?.browse?.canvas;
            if (string.IsNullOrEmpty(canvas)) return;
            // Куда едет камера полотна — знает витрина (LvnMenuStage.PanFor;
            // ui.browse.canvas_pan / canvas_pan_step). Здесь только откуда и
            // куда: сам переезд ведёт тик анимации вкладок.
            _menuPanFrom = LvnMenuStage.PanFor(fromTab);
            _menuPanTo = LvnMenuStage.PanFor(toTab);
            _menuPanSet = true;
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
        private void OnWardrobeSection(string axis)
        {
            if (Stage == null) return;
            if (axis == null)
            {
                Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                { ["op"] = "camera", ["action"] = "reset", ["duration"] = 0.5 }, LvnSender.Menu);
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
                () => !_chapterPlaying
                      && !string.IsNullOrEmpty(_manifest?.ui?.browse?.canvas)
                      && !Stage.BackdropHasArt,
                () =>
                {
                    Debug.LogWarning("[lvn-menu] полотна нет, хотя мы в меню — ставим заново");
                    ShowMenuScene(withPortal: false);   // лечение полотна — не приход в меню
                },
                period: LvnMenuStage.GuardPeriodSeconds,
                patience: LvnMenuStage.GuardPatienceSeconds);
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
            if (!_chapterPlaying) Stage?.DumpOpaqueGraphics();
        }

        /// <summary>Витрина уходит с экрана — снимает свой слой. Не «прячет
        /// куклу командой»: команда осталась бы в кадре главы чужой записью, а
        /// снятый слой не оставляет следов вовсе.</summary>
        private void HideMenuSceneActor()
        {
            if (Stage == null) return;
            Stage.CloseMenuLayer();
            _menuSceneActor = null;
        }

        private string MenuFavoriteEntity()
        {
            var fav = Lvn.UI.LvnPrefs.MenuFavorite;
            if (!string.IsNullOrEmpty(fav) && _manifest?.sprites != null
                && _manifest.sprites.ContainsKey(fav)) return fav;
            var def = _manifest?.ui?.wardrobe?.entity;
            return !string.IsNullOrEmpty(def) && _manifest?.sprites != null
                && _manifest.sprites.ContainsKey(def) ? def : null;
        }
    }
}
