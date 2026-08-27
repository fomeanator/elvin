using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СЦЕНА ПЕРЕХОДА со стороны хоста: створ и героиня у него.
    ///
    /// <para>Панель с описанием миссии рисует <see cref="PortalScreen"/>, а всё,
    /// что происходит НА СЦЕНЕ, — здесь: это одна и та же непрерывная сцена, на
    /// которой только что было меню (см. <c>VnStage.HandOver</c>), и относиться
    /// к ней надо как к сцене, а не как к заставке.</para>
    ///
    /// <para>Героиня у створа не ставится заново — она ПЕРЕСТАВЛЯЕТСЯ
    /// (<c>VnStage.Restage</c>): к порталу она приходит в том же наряде и с той
    /// же эмоцией, в которых стояла в меню. Мельче — потому что рядом со
    /// створом важен масштаб перехода, а не её лицо.</para>
    /// </summary>
    public partial class NovelApp
    {
        /// <summary>Створ открылся: ставим его на сцену тусклым и подводим к
        /// нему героиню. Готовность (и яркость) дальше ведёт экран.</summary>
        private void OpenPortalScene(LvnTitle title, LvnChapter chapter)
        {
            var portal = _shell?.Portal;
            if (Stage == null || portal == null) return;

            var fav = MenuFavoriteEntity();
            if (!string.IsNullOrEmpty(fav))
                Stage.Restage(fav, new JObject
                {
                    ["x"] = portal.DollX,
                    ["height"] = portal.DollHeight,
                });

            Stage.ApplyStage(PortalFx(portal, portal.Idle, 0.5f));
            // Экран двигает готовность — створ разгорается вместе с ней.
            portal.Readiness = r => Stage.ApplyStage(PortalFx(portal, r, 0.35f));
            LvnLog.Trace($"[lvn-portal] створ открыт: миссия={title?.id}, глава={chapter?.id}, "
                       + $"героиня={fav ?? "-"}");
        }

        /// <summary>Игрок шагнул в створ: портал раскрывается во весь кадр, а
        /// героиня уходит в него растворением. Ждём, пока это доиграет, —
        /// глава начнётся ПОСЛЕ перехода, а не поверх него.</summary>
        private async Task EnterPortalAsync()
        {
            var portal = _shell?.Portal;
            if (Stage == null || portal == null) return;

            var fav = MenuFavoriteEntity();
            if (!string.IsNullOrEmpty(fav))
                Stage.ApplyStage(new JObject
                {
                    ["op"] = "sfx", ["id"] = fav, ["dissolve"] = 1f, ["dur"] = 0.55f,
                });
            Stage.ApplyStage(PortalFx(portal, 1f, 0.55f));
            portal.Readiness = null;   // экран уходит — двигать больше нечего
            await Task.Delay(620);
            // Створ гасится ЗДЕСЬ, до первого кадра главы: эффект живёт в
            // стеке, а стек переживает смену сцены — иначе глава открылась бы
            // под воронкой предыдущего перехода.
            Stage.ApplyStage(new JObject { ["op"] = "fx", ["off"] = true, ["dur"] = 0.25f });
        }

        private static JObject PortalFx(PortalScreen p, float open, float dur)
        {
            var cmd = new JObject
            {
                ["op"] = "fx",
                ["portal"] = open,
                ["portal_x"] = p.CenterX,
                ["portal_y"] = p.CenterY,
                ["portal_radius"] = p.Radius,
                ["dur"] = dur,
            };
            if (!string.IsNullOrEmpty(p.Color)) cmd["portal_color"] = p.Color;
            return cmd;
        }
    }
}
