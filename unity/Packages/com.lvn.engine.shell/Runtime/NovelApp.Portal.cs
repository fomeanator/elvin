using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СТВОР НА ГЛАВНОЙ — врата, в которые героиня уходит на миссию.
    ///
    /// <para>Отдельного экрана перехода нет: игрок уже нажал «играть», и ещё
    /// одна остановка между его решением и историей — это задержка, а не
    /// подготовка. Створ живёт прямо в витрине меню, рядом с героиней, и
    /// открывается там же, где она стоит.</para>
    ///
    /// <para>Он — СЛОЙ СЦЕНЫ (<c>op portal</c>), а не полноэкранный эффект.
    /// Постэффект живёт на камере: без неё он молча не рисуется, уборка сцены
    /// сбрасывает его посреди перехода, и лечь ПОД героиню он не может —
    /// работает с кадром, где она уже нарисована. Слой рисуется всегда, стоит
    /// за актёрами и ничьей уборки не боится.</para>
    /// </summary>
    public partial class NovelApp
    {
        private PortalConfig Portal => _shell?.Portal;

        /// <summary>Створ виден на главной постоянно — тускло, вполсилы: это
        /// часть мира, а не всплывающий эффект.</summary>
        private void ShowMenuPortal()
        {
            var portal = Portal;
            if (Stage == null || portal == null) return;
            Stage.ApplyStage(PortalCmd(portal, portal.idle ?? 0.34f, 0.6f), LvnSender.Cutscene);
        }

        /// <summary>
        /// КАТСЦЕНА УХОДА С МИССИИ: створ забирает героиню, и кадр гаснет.
        ///
        /// <para>Как в гардеробе, сцена сначала расталкивает всех, кроме неё, и
        /// ЖДЁТ, пока они уйдут: створ не должен открываться поверх уходящих.
        /// Потом она встаёт по центру — где бы её ни оставила глава, — портал
        /// раскрывается и забирает её. Дальше кадр уходит в фейд, и по ту
        /// сторону меню закрывает створ за ней.</para>
        ///
        /// <para>Створ НЕ закрывается здесь: он остаётся открытым, и меню
        /// принимает его открытым — оттуда она и выходит. Закрыть его тут
        /// значило бы дважды показать одно и то же движение.</para>
        /// </summary>
        private async Task LeaveToMenuAsync()
        {
            var portal = Portal;
            if (Stage == null || portal == null) return;
            var fav = MenuFavoriteEntity();
            LvnLog.Trace($"[lvn-portal] уход с миссии: героиня={fav ?? "-"}");

            // 1. Кадр расчищает РАСПОРЯДИТЕЛЬ: остаётся она одна, и вместе с
            //    людьми уходят следы главы — окно реплики, вуали, чужой грим.
            await Stage.BeginSoloAsync(fav);
            EnsureCutsceneBackdrop();

            // 2. Она встаёт по центру и ПЕРЕД всеми: где её оставила глава,
            //    значения не имеет. Если её в главе не было вовсе — ставим,
            //    иначе створу некого забирать.
            PlaceMenuHeroine(VnStage.SoloFrontZ, LvnSender.Cutscene);
            await Task.Delay(340);

            // 3. Створ раскрывается и забирает её.
            Stage.ApplyStage(PortalCmd(portal, 1f, 0.7f), LvnSender.Cutscene);
            await Task.Delay(300);
            if (!string.IsNullOrEmpty(fav))
            {
                Stage.ApplyStage(Hidden2(fav, 0.45f), LvnSender.Cutscene);          // растворилась в створе
            }
            await Task.Delay(520);

            // 4. Кадр уходит в фейд — по ту сторону будет уже меню.
            Stage.ApplyStage(new JObject
            {
                ["op"] = "fade", ["to"] = "black", ["duration"] = 0.45f,
            }, LvnSender.Cutscene);
            await Task.Delay(480);
            // Возвращать уведённых некому: глава закончилась вместе с кадром.
            Stage.DropSolo();
        }

        /// <summary>
        /// КАТСЦЕНЕ НУЖЕН МИР, А НЕ ПУСТОТА.
        ///
        /// <para>Выход может застать главу в любой момент — в том числе в
        /// первые секунды, когда её собственный фон ещё грузится по сети или
        /// когда сцена держит чистый кадр под вступление. Тогда героиня уходила
        /// в портал с серой пустоты: полотна за ней просто не было (живой
        /// репорт — выход с начальной катсцены главы 0).</para>
        ///
        /// <para>Если полотна нет — ставим полотно меню сразу, без фейда: она и
        /// так уходит домой, и по ту сторону портала будет ровно этот же кадр.
        /// Если фон главы стоит — не трогаем, уход играет в её мире.</para>
        /// </summary>
        private void EnsureCutsceneBackdrop()
        {
            if (Stage == null || Stage.BackdropHasArt) return;
            var canvas = _manifest?.ui?.browse?.canvas;
            LvnLog.Trace($"[lvn-portal] в кадре нет полотна — подставляем меню: "
                       + $"{(string.IsNullOrEmpty(canvas) ? "НЕЧЕМ" : canvas)}");
            if (string.IsNullOrEmpty(canvas)) return;
            Stage.ApplyStage(new JObject
            {
                ["op"] = "bg", ["sprite_url"] = canvas,
                ["pan"] = _menuPanSet ? _menuPanTo : LvnMenuStage.PanStart,
                ["fade"] = 0f,
            }, LvnSender.Cutscene);
            _menuBgSet = true;
        }

        /// <summary>
        /// ВОЗВРАЩЕНИЕ ЦЕЛИКОМ: уход с миссии, смена кадра и приход в меню.
        ///
        /// <para>Одна точка на все пути выхода — и по кнопке, и по концу
        /// новеллы. Раньше выход был просто сменой кадра, и игрока «кидало» в
        /// меню без единого движения.</para>
        /// </summary>
        private async Task ReturnToMenuAsync()
        {
            var portal = Portal;
            if (Stage == null) return;
            if (portal == null) { HandOverToMenu(); _shell?.ShowMenuChrome(); return; }

            await LeaveToMenuAsync();      // расталкивание, створ, шейдер, фейд

            // Кадр меню ставится ПОД ЧЁРНЫМ — смены никто не видит.
            HandOverToMenu();
            var fav = MenuFavoriteEntity();
            if (!string.IsNullOrEmpty(fav)) Stage.ApplyStage(Hidden(fav), LvnSender.Cutscene);
            Stage.ApplyStage(PortalCmd(portal, 1f, 0f), LvnSender.Cutscene);   // створ ЗДЕСЬ ещё открыт
            await Task.Delay(120);

            // И меню проявляется: свет возвращается, героиня выходит из створа,
            // створ закрывается за ней.
            Stage.ApplyStage(new JObject
            {
                ["op"] = "fade", ["to"] = "clear", ["duration"] = 0.5f,
            }, LvnSender.Cutscene);
            if (!string.IsNullOrEmpty(fav)) Stage.ApplyStage(Revealed(fav, 0.6f), LvnSender.Cutscene);
            Stage.ApplyStage(PortalCmd(portal, 0f, 0.8f), LvnSender.Cutscene);
            _shell?.ShowMenuChrome();
            LvnLog.Trace("[lvn-portal] возвращение доиграно");
        }

        /// <summary>
        /// УХОД В ГЛАВУ. Створ раскрывается во весь свой рост, героиня уходит в
        /// него растворением — и только потом кадр забирает глава.
        /// </summary>
        private async Task EnterPortalAsync()
        {
            var portal = Portal;
            if (Stage == null || portal == null) return;

            var fav = MenuFavoriteEntity();
            bool inFrame = !string.IsNullOrEmpty(fav) && Stage.ActorVisibleOrPending(fav);
            LvnLog.Trace($"[lvn-portal] уход в главу: героиня={fav ?? "-"}, в кадре={inFrame}");
            _shell?.HideMenuChrome();   // кадр остаётся сценой, а не витриной с кнопками

            Stage.ApplyStage(PortalCmd(portal, 1f, 0.75f), LvnSender.Cutscene);
            await Task.Delay(280);
            // РАСТВОРЯЕМ ТОЛЬКО ТОГО, КТО В КАДРЕ: команда для отсутствующего
            // актёра не теряется — она ждёт его рождения и срабатывает на том,
            // кто появится позже, уже в другом месте.
            if (inFrame)
                Stage.ApplyStage(new JObject
                {
                    ["op"] = "sfx", ["id"] = fav, ["dissolve"] = 1f, ["dur"] = 0.45f,
                }, LvnSender.Cutscene);
            await Task.Delay(520);
        }

        /// <summary>
        /// КАТСЦЕНА ПРИБЫТИЯ: героиня выходит из портала в новеллу.
        ///
        /// <para>Глава уже поставила свой первый кадр, но играть ещё не начала —
        /// её держат ворота входа. Здесь разыгрывается приезд: сцена
        /// расталкивает всех, кого успела выставить, героиня встаёт по центру,
        /// створ за её спиной закрывается — она прошла насквозь. Потом она
        /// уходит обычным уходом актёра, и глава продолжает своим чередом: если
        /// первая реплика её, история сама выведет её снова.</para>
        ///
        /// <para>Порядок здесь не украшение. Расталкивание ДО постановки —
        /// иначе героиня появляется в толпе, которую сцена уже успела
        /// выставить; закрытие створа ПОСЛЕ её появления — иначе она выходит из
        /// пустоты; уход ПОСЛЕ закрытия — иначе кадр пустеет раньше, чем
        /// зритель понял, что произошло.</para>
        /// </summary>
        private async Task ArriveInChapterAsync()
        {
            var portal = Portal;
            if (Stage == null || portal == null) return;
            var fav = MenuFavoriteEntity();
            LvnLog.Trace($"[lvn-portal] прибытие в главу: героиня={fav ?? "-"}");

            // 1. Кадр расчищает РАСПОРЯДИТЕЛЬ — и запоминает, кого глава успела
            //    выставить: после катсцены он вернёт их на место.
            await Stage.BeginSoloAsync(fav);

            // 2. Створ РАСКРЫТ — она пришла через него; и героиня по центру,
            //    ПЕРЕД всеми, в том облике, с каким уходила из меню.
            Stage.ApplyStage(PortalCmd(portal, 1f, 0f), LvnSender.Cutscene);
            if (!string.IsNullOrEmpty(fav))
            {
                PlaceMenuHeroine(VnStage.SoloFrontZ, LvnSender.Cutscene);
                Stage.ApplyStage(Hidden(fav), LvnSender.Cutscene);                 // ещё внутри створа
                // АРТ ДОЛЖЕН ПРИЕХАТЬ ДО ПРИХОДА. Растворение, наложенное на
                // ещё пустую куклу, шумит по всему кадру, а когда кончается,
                // остаётся белый прямоугольник: Image без спрайта заливает себя
                // сплошняком. Ждём слои и только потом играем появление.
                await Stage.WaitForActorArtAsync(fav);
                await Task.Delay(120);
                Stage.ApplyStage(Revealed(fav, 0.5f), LvnSender.Cutscene);         // проступает
                // ПАУЗА ПОСЛЕ ПОЯВЛЕНИЯ. Героиня должна побыть в кадре, прежде
                // чем створ начнёт закрываться: без неё приход и уход
                // сливаются в одно смазанное движение, и зритель не успевает
                // понять, что она пришла.
                await Task.Delay(300);
            }

            // 3. Створ закрывается за её спиной.
            Stage.ApplyStage(PortalCmd(portal, 0f, 0.8f), LvnSender.Cutscene);
            await Task.Delay(900);

            // 4. И она уходит обычным уходом — дальше история сама решает, кому
            //    быть в кадре. НО ЕСЛИ ИСТОРИЯ ЕЁ ЖДЁТ (сцена показывала её до
            //    катсцены — так бывает при старте с сохранения, когда реплей
            //    выставил кадр и следующая реплика её), уводить незачем: шаг 5
            //    вернёт её авторской командой, и уход был бы миганием.
            if (!string.IsNullOrEmpty(fav) && !Stage.SoloReturns(fav))
            {
                Stage.HideActor(fav);
                await Stage.WaitForExitsAsync();
            }

            // 5. КАДР ВОЗВРАЩАЕТСЯ ИСТОРИИ. Те, кого глава выставила до
            //    катсцены, встают обратно своими же командами — иначе они
            //    пропадали до следующей авторской команды о них, то есть на
            //    несколько ходов, а собеседник в неподвижной сцене — насовсем.
            Stage.EndSolo();
            LvnLog.Trace("[lvn-portal] прибытие доиграно — глава продолжает");
        }

        // ── КАТСЦЕНЫ СОБИРАЮТСЯ ИЗ КОМАНД ДВИЖКА ────────────────────────────
        // Не из полей и вызовов, а из тех же команд, которыми пишут сценарий:
        // показать актёра в центре, растворить, открыть створ. Вход и уход
        // актёра движок анимирует САМ — своё дело он знает, и подменять это
        // ручными треками значит спорить с ним из-за уже решённого.

        /// <summary>Актёр есть, но его ещё не видно — он внутри створа.</summary>
        private static JObject Hidden(string id) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["dissolve"] = 1f, ["dur"] = 0f,
        };

        /// <summary>Растворяется за отведённое время.</summary>
        private static JObject Hidden2(string id, float seconds) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["dissolve"] = 1f, ["dur"] = seconds,
        };

        /// <summary>Проступает.</summary>
        private static JObject Revealed(string id, float seconds) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["dissolve"] = 0f, ["dur"] = seconds,
        };

        private static JObject PortalCmd(PortalConfig p, float open, float dur)
        {
            var cmd = new JObject
            {
                ["op"] = "portal",
                ["open"] = open,
                ["x"] = p.x ?? 0.72f,
                ["y"] = p.y ?? 0.52f,
                ["radius"] = p.radius ?? 0.30f,
                ["dur"] = dur,
            };
            if (!string.IsNullOrEmpty(p.color)) cmd["color"] = p.color;
            return cmd;
        }
    }
}
