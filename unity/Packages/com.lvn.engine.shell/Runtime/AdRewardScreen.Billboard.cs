using System.Threading.Tasks;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// БИЛБОРД НАД ВИТРИНОЙ (TR-64, пункт 4) — постановка вокруг ролика.
    ///
    /// <para>Просьба Ильи дословно: «экран рекламы, где героиня около огромного
    /// билборда встаёт и потом к нему переезжает, и запускается реклама». То
    /// есть показ — это не всплывашка поверх меню, а СЦЕНА: героиня уже стоит
    /// на витрине за нашим листом, и к ней добавляется щит.</para>
    ///
    /// <para>Щит рисуется рамкой облика (<c>adv.png</c> — тот же арт, что у
    /// кнопки награды) с тёмным экраном внутри и бегущим по нему бликом.
    /// Картинку внутрь можно положить манифестом (<c>ui.store.ad_art</c>), и
    /// тогда щит показывает её — но и без арта он выглядит щитом, а не
    /// заглушкой: ждать картинку, которой может не быть, значит не сделать
    /// пункт вовсе.</para>
    ///
    /// <para>КАМЕРУ ВЕДЁТ ХОЗЯИН. Экран говорит «начинается показ», а двигает
    /// сцену тот, кто ею владеет (<see cref="OnApproach"/>): своим доступом к
    /// сцене экран завёл бы вторую власть над камерой — ту самую, из-за
    /// которой героиня в меню «ходила ходуном».</para>
    /// </summary>
    public sealed partial class AdRewardScreen
    {
        /// <summary>Наезд на щит: экран просит хозяина подвинуть сцену (true —
        /// подъехать к щиту, false — вернуть общий план). Не подписан — сцена
        /// стоит, и остаётся только въезд самого щита.</summary>
        public System.Action<bool> OnApproach;

        private VisualElement _board, _boardGlare;
        private const float BoardW = 250f, BoardH = 150f;   // dp макета облика

        /// <summary>Щит на сцене: стоит ЗА листом разговора и живёт всё время
        /// экрана — сначала вдали, потом, на «Смотреть», наезжает.</summary>
        private void BuildBillboard()
        {
            if (_board != null || !StageDressed) return;
            var host = this;                      // слой экрана, а не лист: лист уезжает
            var board = new VisualElement { pickingMode = PickingMode.Ignore };
            board.style.position = Position.Absolute;
            board.style.width = LvnStageKit.D(BoardW);
            board.style.height = LvnStageKit.D(BoardH);
            board.style.left = Length.Percent(50f);
            board.style.marginLeft = -LvnStageKit.D(BoardW) * 0.5f;
            board.style.top = Length.Percent(18f);
            board.style.opacity = 0f;             // проявится въездом
            board.style.scale = new Scale(new Vector2(0.82f, 0.82f));

            // Экран щита: тёмное стекло под рамкой. Рисуем ПЕРВЫМ, чтобы
            // рамка облика легла поверх краёв, как она и нарисована.
            var glass = LvnChrome.Stretch(new VisualElement());
            glass.pickingMode = PickingMode.Ignore;
            glass.style.backgroundColor = LvnTokens.Veil(0.92f);   // тёмное стекло щита — цвет темы
            LvnChrome.Round(glass, LvnTokens.RadiusSm);
            board.Add(glass);

            var art = _manifest?.ui?.store?.ad_art;
            if (!string.IsNullOrEmpty(art)) LvnPicture.Photo(glass, art, _assets, cover: true);

            // Блик, бегущий по стеклу: щит читается включённым, а не выключенным.
            var glare = new VisualElement { pickingMode = PickingMode.Ignore };
            glare.style.position = Position.Absolute;
            glare.style.top = 0; glare.style.bottom = 0;
            glare.style.width = Length.Percent(22f);
            glare.style.backgroundColor = UiColor.WithAlpha(LvnTokens.Text, 0.10f);
            glass.Add(glare);
            _boardGlare = glare;
            glass.style.overflow = Overflow.Hidden;

            board.Add(LvnStageKit.Art(LvnStageKit.SkinUrl(_skin, "adv.png"), _assets,
                                      0f, 0f, LvnStageKit.D(BoardW), LvnStageKit.D(BoardH)));

            var mark = LvnStageKit.Text(() => Lvn.Content.LvnWords.Of("ads.board", "AD"),
                                        LvnTokens.TextXs, LvnTokens.TextDim);
            mark.style.position = Position.Absolute;
            mark.style.right = LvnTokens.Space2;
            mark.style.bottom = LvnTokens.Hair;
            board.Add(mark);

            host.Insert(0, board);                // за листом разговора
            _board = board;
            LvnAsync.Fire(BoardArriveAsync(), "AdBoardArrive");
            RunGlare();
        }

        /// <summary>Щит въезжает: издалека и снизу, как проезжающий мимо.</summary>
        private async Task BoardArriveAsync()
        {
            var board = _board;
            if (board == null) return;
            await LvnMotion.PlayAsync(board, 420, (el, p) =>
            {
                float k = LvnMotion.Settle(p);
                el.style.opacity = k;
                float s = Mathf.Lerp(0.82f, 0.94f, k);
                el.style.scale = new Scale(new Vector2(s, s));
                el.style.translate = new Translate(0f, Mathf.Lerp(26f, 0f, k));
            });
        }

        private void RunGlare()
        {
            var glare = _boardGlare;
            if (glare == null) return;
            float x = -30f;
            glare.schedule.Execute(() =>
            {
                x += 1.4f;
                if (x > 130f) x = -30f;
                glare.style.left = Length.Percent(x);
            }).Every(32);
        }

        /// <summary>
        /// ПЕРЕЕЗД К ЩИТУ — то, что происходит между «Смотреть» и роликом.
        ///
        /// <para>Лист разговора уезжает вниз, щит вырастает во весь кадр, сцена
        /// подаётся ближе. Полсекунды: дольше — игрок ждёт не рекламу, а нас.</para>
        /// </summary>
        private async Task ApproachAsync()
        {
            OnApproach?.Invoke(true);
            var sheet = _sheet;
            var board = _board;
            if (sheet != null) LvnAsync.Fire(LvnMotion.PlayAsync(sheet, 320, (el, p) =>
            {
                float k = LvnMotion.Leave(p);
                el.style.opacity = 1f - k;
                el.style.translate = new Translate(0f, 40f * k);
            }), "AdSheetLeave");
            if (board != null)
                await LvnMotion.PlayAsync(board, 520, (el, p) =>
                {
                    float k = LvnMotion.Settle(p);
                    float s = Mathf.Lerp(0.94f, 1.34f, k);
                    el.style.scale = new Scale(new Vector2(s, s));
                    el.style.translate = new Translate(0f, Mathf.Lerp(0f, 18f, k));
                });
        }

        /// <summary>Вернуть разговор: ролик кончился, щит отходит, лист
        /// возвращается с итогом.</summary>
        private void Depart()
        {
            OnApproach?.Invoke(false);
            var sheet = _sheet;
            if (sheet != null)
            {
                sheet.style.opacity = 1f;
                sheet.style.translate = new Translate(0f, 0f);
            }
            var board = _board;
            if (board == null) return;
            LvnAsync.Fire(LvnMotion.PlayAsync(board, 380, (el, p) =>
            {
                float k = LvnMotion.Settle(p);
                float s = Mathf.Lerp(1.34f, 0.94f, k);
                el.style.scale = new Scale(new Vector2(s, s));
                el.style.translate = new Translate(0f, Mathf.Lerp(18f, 0f, k));
            }), "AdBoardDepart");
        }
    }
}
