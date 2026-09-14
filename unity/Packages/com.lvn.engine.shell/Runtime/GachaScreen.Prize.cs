using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    public sealed partial class GachaScreen
    {
        private VisualElement _reward, _rewardArt;
        private Label _rewardName;
        private int _prizeVersion;
        private readonly CancellationTokenSource _artCancel = new CancellationTokenSource();

        private void BuildReward(VisualElement content)
        {
            _reward = new VisualElement { name = "gacha-reward", pickingMode = PickingMode.Ignore };
            _reward.style.display = DisplayStyle.None;
            _reward.style.flexShrink = 0;
            _reward.style.marginTop = LvnTokens.Space3;
            _reward.style.alignItems = Align.Center;
            _rewardArt = new VisualElement { name = "gacha-reward-art", pickingMode = PickingMode.Ignore };
            _rewardArt.style.height = 240f;
            _rewardArt.style.width = Length.Percent(100f);
            _rewardArt.style.alignItems = Align.Center;
            _rewardArt.style.justifyContent = Justify.Center;
            LvnPicture.Fit(_rewardArt, cover: false);
            _reward.Add(_rewardArt);
            _rewardName = new Label { name = "gacha-reward-name", pickingMode = PickingMode.Ignore };
            LvnFonts.Apply(_rewardName, LvnFonts.Display);
            _rewardName.style.fontSize = LvnTokens.TextXl;
            _rewardName.style.color = LvnTokens.Gold;
            _rewardName.style.whiteSpace = WhiteSpace.Normal;
            _rewardName.style.unityTextAlign = TextAnchor.MiddleCenter;
            _rewardName.style.alignSelf = Align.Stretch;
            _rewardName.style.marginTop = LvnTokens.Space2;
            _reward.Add(_rewardName);
            content.Add(_reward);
        }

        internal async Task RevealPrizeAsync(LvnGacha.Spin spin)
        {
            int version = ++_prizeVersion;
            var prize = spin.Super ? DescribePrize(spin.Prize) : null;
            _rewardArt.Clear();
            _rewardArt.style.backgroundImage = StyleKeyword.None;
            _rewardArt.Add(spin.Super ? LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(96f), LvnTokens.Gold)
                : LvnIcons.MakeCurrency(spin.Currency, LvnStageKit.D(96f)));
            _rewardName.text = spin.Super
                ? prize.Label ?? prize.Sku
                : "+" + LvnPriceTag.Amount(spin.Amount);
            _status.text = spin.Super
                ? LvnWords.Of("gacha.won_prize", "You got: {0}", prize.Label ?? prize.Sku)
                : LvnWords.Of("gacha.won_currency", "You got: {0}", LvnPriceTag.Full(spin.Currency, spin.Amount));
            _window.style.display = DisplayStyle.None;
            _content.scrollOffset = Vector2.zero;
            float available = _content.contentViewport.resolvedStyle.height;
            _rewardArt.style.height = float.IsNaN(available) ? LvnStageKit.D(280f)
                : Mathf.Clamp(available * 0.6f, LvnStageKit.D(100f), LvnStageKit.D(360f));
            _reward.style.display = DisplayStyle.Flex;
            _reward.style.opacity = 0f;
            // Artwork loading cannot hold the reward hostage. The gift icon
            // stays readable on a failed load, and late art never revives a closed screen.
            if (spin.Super && !string.IsNullOrEmpty(prize.Art))
                LvnAsync.Fire(LoadPrizeArtAsync(prize, version), "GachaPrizeArt");
            await LvnMotion.PlayAsync(_reward, LvnMotion.Calm * 2, (element, progress) =>
            {
                float k = LvnMotion.Settle(progress);
                element.style.opacity = k;
                float scale = LvnPrefs.ReduceMotion ? 1f : Mathf.Lerp(0.88f, 1f, k);
                element.style.scale = new Scale(new Vector2(scale, scale));
            });
            if (_closed) return;
            _reward.style.opacity = 1f;
            _reward.style.scale = new Scale(Vector2.one);
            ActionButton("gacha-take", () => LvnWords.Of("gacha.take", "Take the prize"), () =>
            {
                // The prize is already owned. This tap only dismisses its reveal.
                if (!spin.WalletSynced) LvnAsync.Fire(LvnWallet.RefreshAsync(), "GachaWalletRetry");
                _prizeVersion++;
                BuildStrip();
                PaintIdle();
            });
        }

        // Published gacha prizes may carry only a SKU and a generic label.
        // Resolve their actual name/art from the same catalog as the wardrobe.
        internal LvnGacha.Prize DescribePrize(LvnGacha.Prize prize)
        {
            var result = new LvnGacha.Prize { Sku = prize.Sku, Label = prize.Label, Art = prize.Art };
            var parts = prize.Sku?.Split(':');
            if (parts == null || parts.Length != 4 || parts[0] != "wardrobe") return result;
            if (parts[2] == WardrobeSheet.BackdropAxis)
            {
                var options = _manifest?.ui?.browse?.canvas_options;
                if (options != null) foreach (var option in options)
                    if (option.id == parts[3])
                    {
                        result.Label = LvnWords.Name("skin", option.id, option.title ?? result.Label);
                        if (string.IsNullOrEmpty(result.Art)) result.Art = string.IsNullOrEmpty(option.preview) ? option.url : option.preview;
                        break;
                    }
            }
            else if (_manifest?.sprites != null && _manifest.sprites.TryGetValue(parts[1], out var entity)
                && entity?.wardrobe != null && entity.wardrobe.TryGetValue(parts[2], out var slot) && slot?.items != null)
                foreach (var item in slot.items)
                    if (item.value == parts[3])
                    {
                        result.Label = LvnWords.Name("skin", item.value, item.name);
                        if (string.IsNullOrEmpty(result.Art)) result.Art = item.icon;
                        break;
                    }
            return result;
        }

        private async Task LoadPrizeArtAsync(LvnGacha.Prize prize, int version)
        {
            if (_assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(prize.Art, _artCancel.Token);
                if (_closed || version != _prizeVersion || sprite == null) return;
                _rewardArt.Clear();
                LvnPicture.Paint(_rewardArt, sprite, slice: 0);
            }
            catch (System.OperationCanceledException) { /* the reward screen was closed */ }
            catch (System.Exception ex) { LvnLog.Warn("[lvn-gacha] prize art unavailable: " + ex.Message); }
        }
    }
}
