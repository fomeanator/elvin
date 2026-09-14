using System.Collections.Generic;
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
            // РЕДКИЙ ПРИЗ — С ЦЕРЕМОНИЕЙ (TR-102, Илья 15.09): экран уходит в
            // чёрное, в центре за 1,7 с проявляется награда, ещё через 2 с
            // ниже появляются название и «Забрать». Валюта — как раньше.
            if (spin.Super)
            {
                LvnLog.Info($"[lvn-gacha] редкое: {prize.Sku} «{prize.Label}», арт {(string.IsNullOrEmpty(prize.Art) ? "—" : prize.Art)}");
                if (!string.IsNullOrEmpty(prize.Art)) LvnAsync.Fire(LoadPrizeArtAsync(prize, version), "GachaPrizeArt");
                await CelebrateAsync(spin, version);
                return;
            }
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
            TakeButton(spin);
        }

        private Button TakeButton(LvnGacha.Spin spin)
            => ActionButton("gacha-take", () => LvnWords.Of("gacha.take", "Take the prize"), () => TakeNow(spin));

        /// <summary>Приз уже выдан сервером — этот тап только закрывает показ.</summary>
        private void TakeNow(LvnGacha.Spin spin)
        {
            LvnLog.Info("[lvn-gacha] «Забрать» — закрываю показ приза");
            if (!spin.WalletSynced) LvnAsync.Fire(LvnWallet.RefreshAsync(), "GachaWalletRetry");
            _prizeVersion++;
            DismissCeremony();
            PaintIdle();   // лента остаётся где стояла (TR-108)
            _taken?.TrySetResult(true);
        }

        private VisualElement _blackout;

        /// <summary>Церемония редкого приза: чёрный экран поверх круток, награда
        /// проявляется в центре 1,7 с, через 2 с ниже — название и «Забрать».
        /// При «уменьшить движение» — те же паузы без плавности.</summary>
        private async Task CelebrateAsync(LvnGacha.Spin spin, int version)
        {
            DismissCeremony();
            var veil = new VisualElement { name = "gacha-blackout" };
            LvnChrome.Stretch(veil);   // во весь экран круток
            veil.style.backgroundColor = Color.black;
            veil.style.alignItems = Align.Center;
            veil.style.justifyContent = Justify.Center;
            veil.style.opacity = 0f;
            // Чёрный экран глушит ВСЁ под собой (касания и клики), и после
            // появления кнопки тап по любому месту экрана — тоже «Забрать»:
            // кнопка не имеет права запереть игрока (Илья: «забрать не работает»).
            bool ready = false;
            veil.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            veil.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            veil.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); if (ready) TakeNow(spin); });
            Add(veil);
            veil.BringToFront();
            _blackout = veil;
            LvnLog.Info("[lvn-gacha] церемония: чёрный экран");
            // Награда переезжает в центр чёрного экрана; кнопка — под ней, а не в нижнем ряду.
            var column = new VisualElement { name = "gacha-ceremony" };
            column.style.alignItems = Align.Center;
            column.style.width = Length.Percent(88f);
            _reward.RemoveFromHierarchy();
            column.Add(_reward);
            veil.Add(column);
            _reward.style.display = DisplayStyle.Flex;
            _reward.style.opacity = 1f;
            _reward.style.scale = new Scale(Vector2.one);
            _reward.style.marginTop = 0;
            _rewardArt.style.opacity = 0f;
            _rewardArt.style.height = LvnStageKit.D(280f);
            _rewardName.style.opacity = 0f;
            var take = TakeButton(spin);
            take.RemoveFromHierarchy();
            take.style.marginTop = LvnTokens.Space3;
            take.style.opacity = 0f;
            take.SetEnabled(false);
            column.Add(take);
            bool reduce = LvnPrefs.ReduceMotion;
            await FadeIn(veil, 350, reduce);
            if (_closed || version != _prizeVersion) return;
            // ЗОЛОТЫЕ ИСКРЫ (Илья: «с шейдером, который золотые искры
            // разбрасывает»): шейдер на элемент интерфейса не навесить — рой
            // частиц разлетается от приза двумя волнами, пока он проявляется.
            if (!reduce) LvnAsync.Fire(SparklesAsync(veil, version), "GachaSparkles");
            await FadeIn(_rewardArt, 1700, reduce);          // награда проявляется 1,7 с
            if (_closed || version != _prizeVersion) return;
            await Task.Delay(2000);                           // тишина: только приз на чёрном
            if (_closed || version != _prizeVersion) return;
            take.SetEnabled(true);
            ready = true;
            LvnLog.Info("[lvn-gacha] церемония: название и «Забрать» показаны");
            await Task.WhenAll(FadeIn(_rewardName, 400, reduce), FadeIn(take, 400, reduce));
        }

        /// <summary>Две волны золотых искр из центра экрана: каждая частица летит
        /// по своему лучу, гаснет и исчезает. Только частицы интерфейса, без
        /// шейдеров — работает на любом телефоне.</summary>
        private async Task SparklesAsync(VisualElement host, int version)
        {
            var rnd = new System.Random();
            for (int wave = 0; wave < 2 && !_closed && version == _prizeVersion; wave++)
            {
                var burst = new List<(VisualElement dot, float ang, float dist, float size)>();
                for (int i = 0; i < 28; i++)
                {
                    float size = LvnStageKit.D(3f + (float)rnd.NextDouble() * 5f);
                    var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                    dot.style.position = Position.Absolute;
                    dot.style.left = Length.Percent(50f); dot.style.top = Length.Percent(45f);
                    dot.style.width = size; dot.style.height = size;
                    LvnChrome.Circle(dot, size);
                    dot.style.backgroundColor = i % 3 == 0 ? Color.white : LvnTokens.Gold;
                    dot.style.opacity = 0f;
                    host.Add(dot);
                    burst.Add((dot, (float)(rnd.NextDouble() * Mathf.PI * 2), LvnStageKit.D(120f + (float)rnd.NextDouble() * 220f), size));
                }
                await LvnMotion.PlayAsync(host, 1100, (el, p) =>
                {
                    float k = LvnMotion.Settle(p);
                    foreach (var b in burst)
                    {
                        float x = Mathf.Cos(b.ang) * b.dist * k, y = Mathf.Sin(b.ang) * b.dist * k - LvnStageKit.D(40f) * p;
                        b.dot.style.translate = new Translate(x, y);
                        b.dot.style.opacity = p < 0.15f ? p / 0.15f : 1f - (p - 0.15f) / 0.85f;
                    }
                });
                foreach (var b in burst) b.dot.RemoveFromHierarchy();
                if (wave == 0) await Task.Delay(250);
            }
        }

        private static Task FadeIn(VisualElement el, int ms, bool instant)
        {
            if (instant) { el.style.opacity = 1f; return Task.CompletedTask; }
            return LvnMotion.PlayAsync(el, ms, (e, p) => e.style.opacity = LvnMotion.Settle(p));
        }

        /// <summary>Снять чёрный экран и вернуть блок награды на его место в списке.</summary>
        private void DismissCeremony()
        {
            if (_blackout == null) return;
            _reward.RemoveFromHierarchy();
            _reward.style.marginTop = LvnTokens.Space3;
            _rewardArt.style.opacity = 1f;
            _rewardName.style.opacity = 1f;
            _content.Add(_reward);
            _blackout.RemoveFromHierarchy();
            _blackout = null;
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
                if (_closed || version != _prizeVersion || sprite == null)
                {
                    LvnLog.Warn($"[lvn-gacha] арт приза не лёг: closed={_closed} version={version}/{_prizeVersion} sprite={(sprite == null ? "null" : "ok")}");
                    return;
                }
                _rewardArt.Clear();
                LvnPicture.Paint(_rewardArt, sprite, slice: 0);
                LvnLog.Info($"[lvn-gacha] арт приза показан: {prize.Art} ({sprite.rect.width:0}×{sprite.rect.height:0})");
            }
            catch (System.OperationCanceledException) { /* the reward screen was closed */ }
            catch (System.Exception ex) { LvnLog.Warn("[lvn-gacha] prize art unavailable: " + ex.Message); }
        }
    }
}
