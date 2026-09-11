using System;
using System.Globalization;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЭКРАН НАГРАДЫ ЗА РОЛИК — то, что стоит между нажатием и рекламой.
    ///
    /// <para>Раньше значок на главной вызывал показ напрямую, и игрок не знал
    /// ни что получит, ни сколько показов осталось, ни почему ничего не
    /// случилось, когда ролика не оказалось («в главном меню по нажатию
    /// переход», «щас вообще не срабатывает» — Илья 09.09; задача TR-64).</para>
    ///
    /// <para>Экран отвечает за РАЗГОВОР, а не за рекламу: сколько дают, сколько
    /// осталось, когда следующий показ, что вышло. Ролик показывает дом
    /// рекламы, награду начисляет сервер — здесь не считают ни того, ни
    /// другого.</para>
    /// </summary>
    public sealed partial class AdRewardScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly Label _reward;
        private readonly Label _note;
        private readonly VisualElement _actions;
        private string _placement;
        private string _skin;
        private bool _stageGlass;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        public AdRewardScreen(ILvnAssets assets)
        {
            _assets = assets;
            var sheet = _sheet = Sheet(sideInset: 8f, topInset: 22f);
            AdoptSheet(sheet);

            var header = ScreenUi.Row();
            header.style.marginBottom = LvnTokens.Space3;
            sheet.Add(header);
            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("ads.title", "Free crystals"));
            LvnChrome.Heading(_title);
            _title.style.color = LvnTokens.Text;
            _title.style.fontSize = LvnTokens.TextLg;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.flexGrow = 1;
            header.Add(_title);

            _reward = new Label();
            _reward.style.color = LvnTokens.Gold;
            _reward.style.fontSize = LvnTokens.TextXl;
            _reward.style.unityFontStyleAndWeight = FontStyle.Bold;
            _reward.style.unityTextAlign = TextAnchor.MiddleCenter;
            _reward.style.marginTop = LvnTokens.Space4;
            sheet.Add(_reward);

            _note = new Label();
            _note.style.color = LvnTokens.TextDim;
            _note.style.fontSize = LvnTokens.TextSm;
            _note.style.whiteSpace = WhiteSpace.Normal;
            _note.style.unityTextAlign = TextAnchor.MiddleCenter;
            _note.style.marginTop = LvnTokens.Space2;
            sheet.Add(_note);

            _actions = new VisualElement();
            _actions.style.marginTop = LvnTokens.Space5;
            sheet.Add(_actions);
        }

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);
        }

        private LvnManifest _manifest;

        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        /// <summary>Показать предложение и довести его до конца: ролик, награда
        /// и слово о том, чем всё кончилось.</summary>
        public async Task RunAsync(string placement)
        {
            _placement = placement;
            BuildBillboard();   // щит стоит на сцене всё время разговора (TR-64)
            Offer();
            await ShowAsync();
        }

        // ── ЧТО ПОКАЗЫВАЕМ ────────────────────────────────────────────────────

        private void Offer()
        {
            var st = Lvn.Services.LvnAds.StateOf(_placement);
            long amount = st?.Amount ?? 0;
            _reward.text = "+" + amount.ToString(CultureInfo.InvariantCulture);
            _note.text = Left(st);
            _actions.Clear();
            bool ready = st == null || st.Ready;
            if (ready) _actions.Add(Primary(() => LvnWords.Of("ads.watch", "Watch"), Watch));
            _actions.Add(Quiet(() => LvnWords.Of("ads.later", "Not now"), Cancel));
        }

        /// <summary>Сколько показов осталось — словом сервера, а не своим
        /// счётом: клиентский разошёлся бы с ним на первом перезапуске.</summary>
        private static string Left(Lvn.Services.LvnAds.Placement st)
        {
            if (st == null) return "";
            if (!st.Ready && st.WaitSeconds > 0)
            {
                var wait = TimeSpan.FromSeconds(st.WaitSeconds);
                return LvnWords.Of("ads.wait", "Available in {0}",
                                   wait.Minutes.ToString("0") + ":" + wait.Seconds.ToString("00"));
            }
            if (st.Left < 0) return "";
            // Слово ТО ЖЕ, что на карточке магазина: один ключ с двумя
            // формулировками — это две правды об одном остатке.
            return LvnWords.Of("ads.left", "{0} of {1} left",
                               st.Left.ToString(CultureInfo.InvariantCulture),
                               Math.Max(st.Left, st.Charges).ToString(CultureInfo.InvariantCulture));
        }

        private void Watch()
        {
            _note.text = LvnWords.Of("ads.loading", "Loading the ad…");
            _actions.Clear();
            LvnAsync.Fire(WatchAsync(), "AdWatch");
        }

        private async Task WatchAsync()
        {
            // СНАЧАЛА ПЕРЕЕЗД, ПОТОМ РОЛИК: щит вырастает во весь кадр, и
            // реклама начинается ИЗ сцены, а не поверх разговора.
            await ApproachAsync();
            bool granted = false;
            try { granted = await Lvn.Services.LvnAds.WatchAndRewardAsync(_placement); }
            catch (Exception e) { LvnLog.Trace("[lvn-ads] показ не удался: " + e.Message); }
            if (!IsOpen) return;
            Depart();
            Result(granted);
        }

        /// <summary>Итог показа. Отказ называем ПРИЧИНОЙ, а не молчанием: до
        /// этого экран просто ничего не делал, и игрок жал значок второй раз.</summary>
        private void Result(bool granted)
        {
            var st = Lvn.Services.LvnAds.StateOf(_placement);
            if (granted)
            {
                _reward.text = "+" + (st?.Amount ?? 0).ToString(CultureInfo.InvariantCulture);
                _note.text = LvnWords.Of("ads.got", "Crystals are in your wallet");
            }
            else
            {
                _reward.text = "";
                _note.text = st != null && !st.Ready && st.WaitSeconds > 0
                    ? Left(st)
                    : LvnWords.Of("ads.none", "No ad right now. Try again in a minute.");
            }
            _actions.Clear();
            _actions.Add(Primary(() => LvnWords.Of("ads.done", "Done"), Cancel));
        }

        // ── КНОПКИ ────────────────────────────────────────────────────────────

        private VisualElement Primary(Func<string> text, Action onTap)
        {
            if (StageDressed)
            {
                var plate = LvnStageKit.Button(text, onTap);
                plate.style.height = LvnStageKit.D(52f);
                plate.style.marginBottom = LvnTokens.Space2;
                LvnStageKit.HollowFrame(plate, LvnStageKit.SkinUrl(_skin, "card-back.png"), _assets,
                                        LvnStageKit.CardBackW, LvnStageKit.CardBackH,
                                        LvnStageKit.CardBackCornerPx, LvnStageKit.CardBackPxPerDp,
                                        index: 0, solid: true);
                return plate;
            }
            var btn = Lvn.UI.LvnRedress.Bind(new Button(() => onTap()), text);
            btn.style.fontSize = LvnTokens.TextBase;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnAir.PadY(btn, LvnTokens.Space3);
            btn.style.marginBottom = LvnTokens.Space2;
            LvnStyler.Primary(btn, LvnTokens.RadiusSm);
            return btn;
        }

        private static VisualElement Quiet(Func<string> text, Action onTap)
        {
            var btn = Lvn.UI.LvnRedress.Bind(new Button(() => onTap()), text);
            btn.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadY(btn, LvnTokens.Space2);
            LvnStyler.Quiet(btn, LvnTokens.RadiusSm);
            return btn;
        }
    }
}
