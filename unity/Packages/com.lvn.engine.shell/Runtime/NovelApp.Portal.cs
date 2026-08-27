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
            Stage.ApplyStage(PortalCmd(portal, portal.idle ?? 0.34f, 0.6f));
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

            Stage.ApplyStage(PortalCmd(portal, 1f, 0.75f));
            await Task.Delay(280);
            // РАСТВОРЯЕМ ТОЛЬКО ТОГО, КТО В КАДРЕ: команда для отсутствующего
            // актёра не теряется — она ждёт его рождения и срабатывает на том,
            // кто появится позже, уже в другом месте.
            if (inFrame)
                Stage.ApplyStage(new JObject
                {
                    ["op"] = "sfx", ["id"] = fav, ["dissolve"] = 1f, ["dur"] = 0.45f,
                });
            await Task.Delay(520);
        }

        /// <summary>Глава поставила первый кадр — створ закрывается: героиня из
        /// него вышла. Своей же командой, а не сбросом всего стека эффектов:
        /// чужие эффекты новой главы гасить незачем.</summary>
        private void ArriveInChapter()
        {
            var portal = Portal;
            if (Stage == null || portal == null) return;
            Stage.ApplyStage(PortalCmd(portal, 1f, 0f));
            Stage.ApplyStage(PortalCmd(portal, 0f, 0.7f));
        }

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
