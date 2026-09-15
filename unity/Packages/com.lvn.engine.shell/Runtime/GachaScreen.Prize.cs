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
        private Label _rewardName, _rewardRarity;
        private IReadOnlyDictionary<string, string> RarityPalette => _manifest?.ui?.wardrobe?.rarity_colors;
        private int _prizeVersion;
        private CancellationTokenSource _artCancel = new CancellationTokenSource();   // вкладка заводит новый на каждый показ

        private void BuildReward(VisualElement content)
        {
            _reward = new VisualElement { name = "gacha-reward", pickingMode = PickingMode.Ignore };
            _reward.style.display = DisplayStyle.None;
            _reward.style.flexShrink = 0;
            _reward.style.marginTop = LvnTokens.Space3;
            _reward.style.alignItems = Align.Center;
            // ШИРИНА — СВОЯ. В церемонии награда стоит в центрирующей колонке,
            // и без растяжки блок ужимался до ширины подписи: картинка на 100 %
            // от «ничего» — ноль, приз не показывался (Илья 15.09).
            _reward.style.alignSelf = Align.Stretch;
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
            _rewardRarity = new Label { name = "gacha-reward-rarity", pickingMode = PickingMode.Ignore };
            _rewardRarity.style.fontSize = LvnTokens.TextBase;
            _rewardRarity.style.unityTextAlign = TextAnchor.MiddleCenter;
            _rewardRarity.style.alignSelf = Align.Stretch;
            _rewardRarity.style.marginTop = LvnTokens.Hair;
            _rewardRarity.style.display = DisplayStyle.None;
            _reward.Add(_rewardRarity);
            content.Add(_reward);
        }

        internal async Task RevealPrizeAsync(LvnGacha.Spin spin)
        {
            int version = ++_prizeVersion;
            var prize = spin.Super ? DescribePrize(spin.Prize) : null;
            _rewardArt.Clear();
            _rewardArt.style.backgroundImage = StyleKeyword.None;
            _rewardArt.Add(spin.Super ? LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(96f), LvnTokens.Gold)
                : LvnPriceTag.Icon(spin.Currency, LvnStageKit.D(96f)));
            _rewardName.text = spin.Super
                ? prize.Label ?? prize.Sku
                : "+" + LvnPriceTag.Amount(spin.Amount);
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
            _reward.style.display = DisplayStyle.None;
            _poolDirty = true;   // в пуле приз станет «есть»
            _taken?.TrySetResult(true);   // ход продолжается: покой или следующий ход рисует он
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
            // До «Забрать» тап торопит церемонию (протапать можно всё), после — забирает.
            veil.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); if (ready) TakeNow(spin); else _skipAsked = true; });
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
            // РЕДКОСТЬ В ЦЕРЕМОНИИ (TR-109): имя в цвете ступени, под ним —
            // её слово; чем выше ступень, тем громче эйфория (волны искр).
            var prize = DescribePrize(spin.Prize);
            var rarityColor = LvnRarity.ColorOf(prize.Rarity, RarityPalette);
            int rank = LvnRarity.Rank(prize.Rarity);
            _rewardName.style.color = rank >= 0 ? rarityColor : LvnTokens.Gold;
            // Цена рядом с именем (Илья): «Мифический · 350 кристаллов».
            // КОПИЯ (Илья 15.09): скин уже есть — копия продана за цену скина,
            // и это сказано прямо под именем, вместе с кристаллами.
            _rewardRarity.text = LvnRarity.Word(prize.Rarity)
                + (prize.Price > 0 ? " · " + LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.Price) : "")
                + (spin.Copy > 0
                    ? " · " + (spin.SoldAmount > 0
                        ? LvnWords.Of("gacha.copy_sold", "a copy — sold for {0}", LvnPriceTag.Full(spin.SoldCurrency, spin.SoldAmount))
                        : LvnWords.Of("gacha.copy", "a copy"))
                    : "");
            _rewardRarity.style.color = rarityColor;
            _rewardRarity.style.opacity = 0f;
            _rewardRarity.style.display = rank >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // ЗАБРАТЬ ИЛИ ПРОДАТЬ (TR-124, Илья 15.09): новый скин можно тут же
            // продать за цену копии — вещь уходит, кристаллы приходят, скин
            // снова может выпасть. Копия уже продана сервером — второй кнопки
            // у неё нет. В авто приз забирается сам, продажа — только тапом.
            var take = TakeButton(spin);
            take.RemoveFromHierarchy();
            var actions = ScreenUi.Row();
            actions.style.marginTop = LvnTokens.Space3;
            actions.style.opacity = 0f;
            actions.style.alignSelf = Align.Stretch;
            actions.style.justifyContent = Justify.Center;
            take.style.flexGrow = 1; take.style.flexBasis = 0;
            actions.Add(take);
            long sale = prize.SellPrice > 0 ? prize.SellPrice : prize.Price;
            string saleCurrency = prize.Currency ?? _state?.SpinCurrency;
            Button sell = null;
            if (spin.Copy == 0 && sale > 0 && !string.IsNullOrEmpty(saleCurrency))
            {
                sell = ActionButton("gacha-sell", () => LvnWords.Of("gacha.sell", "Sell for {0}", LvnPriceTag.Full(saleCurrency, sale)),
                    () => LvnAsync.Fire(SellPrizeAsync(spin, take, version), "GachaSell"));
                sell.RemoveFromHierarchy();
                LvnStageKit.PlateButton(sell, primary: false);
                sell.style.flexGrow = 1; sell.style.flexBasis = 0;
                sell.style.marginLeft = LvnTokens.Space2;
                actions.Add(sell);
            }
            column.Add(actions);
            take.SetEnabled(false);
            sell?.SetEnabled(false);
            bool reduce = LvnPrefs.ReduceMotion;
            await FadeIn(veil, 350, reduce);
            if (_closed || version != _prizeVersion) return;
            await RevealAsync(_rewardArt, 1700, reduce);     // награда проявляется 1,7 с (тап — сразу)
            if (_closed || version != _prizeVersion) return;
            await WaitOrTapAsync(2000);                       // тишина: только приз на чёрном (тап — дальше)
            if (_closed || version != _prizeVersion) return;
            take.SetEnabled(true);
            sell?.SetEnabled(true);
            ready = true;
            LvnLog.Info("[lvn-gacha] церемония: название и «Забрать» показаны");
            await Task.WhenAll(FadeIn(_rewardName, 400, reduce), FadeIn(_rewardRarity, 400, reduce), FadeIn(actions, 400, reduce));
        }

        /// <summary>Продажа из церемонии: пока сервер отвечает, кнопки заперты и
        /// авто-забор не торопит; итог — строкой под именем, затем показ
        /// закрывается как по «Забрать». Отказ — строкой же, приз остаётся.</summary>
        private async Task SellPrizeAsync(LvnGacha.Spin spin, Button take, int version)
        {
            var sku = spin.Prize?.Sku;
            if (string.IsNullOrEmpty(sku) || _closed || version != _prizeVersion) return;
            var buttons = _blackout?.Q<Button>("gacha-sell");
            take.SetEnabled(false);
            buttons?.SetEnabled(false);
            _sellingPrize = true;
            try
            {
                LvnGacha.Sale sale = await LvnGacha.SellAsync(sku);
                if (_closed || version != _prizeVersion) return;
                if (!string.IsNullOrEmpty(sale.Error))
                {
                    LvnLog.Warn($"[lvn-gacha] продажа {sku} не прошла: {sale.Error}");
                    _rewardRarity.text += " · " + LvnWords.Of("gacha.sell_failed", "could not sell");
                    take.SetEnabled(true);
                    buttons?.SetEnabled(true);
                    return;
                }
                LvnLog.Info($"[lvn-gacha] продано: {sku} за {sale.Amount} {sale.Currency}");
                // Проданный приз снова «не получен»: пул судит по списку ещё не
                // выбитых, а свежий список придёт только со следующей круткой.
                if (_state != null && spin.Prize != null && _state.PrizesLeft.TrueForAll(p => p.Sku != sku))
                    _state.PrizesLeft.Add(spin.Prize);
                _rewardRarity.text += " · " + LvnWords.Of("gacha.sold", "sold for {0}", LvnPriceTag.Full(sale.Currency, sale.Amount));
                await WaitOrTapAsync(900);   // итог виден, потом показ закрывается
                if (_closed || version != _prizeVersion) return;
                TakeNow(spin);
            }
            finally { _sellingPrize = false; }
        }

        /// <summary>Идёт продажа приза — авто-забор ждёт её конца.</summary>
        private bool _sellingPrize;

        /// <summary>Проявление, которое можно протапать: тап ставит конечную
        /// прозрачность, и оставшиеся кадры прежнего хода её уже не трогают.</summary>
        private async Task RevealAsync(VisualElement el, int ms, bool instant)
        {
            if (instant) { el.style.opacity = 1f; return; }
            bool done = false;
            var play = LvnMotion.PlayAsync(el, ms, (e, p) => { if (!done) e.style.opacity = LvnMotion.Settle(p); });
            await Task.WhenAny(play, WaitOrTapAsync(ms));
            done = true;
            el.style.opacity = 1f;
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
            _rewardArt.Q<LvnPrizeGlow>()?.RemoveFromHierarchy();   // сияние не тикает под спрятанным блоком
            _reward.style.marginTop = LvnTokens.Space3;
            _rewardArt.style.opacity = 1f;
            _rewardName.style.opacity = 1f;
            _rewardRarity.style.display = DisplayStyle.None;
            _content.Add(_reward);
            _blackout.RemoveFromHierarchy();
            _blackout = null;
        }

        // Published gacha prizes may carry only a SKU and a generic label.
        // Resolve their actual name/art from the same catalog as the wardrobe.
        internal LvnGacha.Prize DescribePrize(LvnGacha.Prize prize)
        {
            var result = new LvnGacha.Prize { Sku = prize.Sku, Label = prize.Label, Art = prize.Art, Rarity = prize.Rarity, Weight = prize.Weight,
                Price = prize.Price, Currency = prize.Currency, SellPrice = prize.SellPrice };
            var parts = prize.Sku?.Split(':');
            // КАТАЛОГ — ПЕРВЫМ (TR-114): одна запись на приз любого домена, включая
            // аватарки, которых в манифесте по sku не найти. Манифест — запасной
            // путь: офлайн или старый сервер.
            var known = LvnSkins.Find(prize.Sku);
            if (known != null)
            {
                string value = parts != null && parts.Length == 4 ? parts[3] : known.Sku;
                result.Label = LvnWords.Name("skin", value, known.Name ?? result.Label);
                if (string.IsNullOrEmpty(result.Art)) result.Art = string.IsNullOrEmpty(known.Preview) && known.Kind != "backdrop" ? known.Art : (known.Kind == "backdrop" && !string.IsNullOrEmpty(known.Preview) ? known.Preview : known.Art);
                if (!string.IsNullOrEmpty(known.Rarity)) result.Rarity = known.Rarity;
                result.Price = known.Price; result.Currency = known.Currency;
                result.GachaOnly = known.GachaOnly; result.Kind = known.Kind;
                result.Description = known.Description;
                if (known.SellPrice > 0) result.SellPrice = known.SellPrice;
            }
            if (parts == null || parts.Length != 4 || parts[0] != "wardrobe") return result;
            if (parts[2] == WardrobeSheet.BackdropAxis)
            {
                var options = _manifest?.ui?.browse?.canvas_options;
                if (options != null) foreach (var option in options)
                    if (option.id == parts[3])
                    {
                        result.Label = LvnWords.Name("skin", option.id, option.title ?? result.Label);
                        if (string.IsNullOrEmpty(result.Art)) result.Art = string.IsNullOrEmpty(option.preview) ? option.url : option.preview;
                        if (!string.IsNullOrEmpty(option.rarity)) result.Rarity = option.rarity;
                        result.Price = option.price; result.Currency = option.currency;
                        if (string.IsNullOrEmpty(result.Description)) result.Description = option.description;
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
                        if (!string.IsNullOrEmpty(item.rarity)) result.Rarity = item.rarity;
                        result.Price = item.price; result.Currency = item.currency;
                        result.GachaOnly = item.gacha;
                        if (string.IsNullOrEmpty(result.Description)) result.Description = item.description;
                        break;
                    }
            // МЕТКИ СОСЕДНЕЙ ОСИ В АДРЕСЕ (Илья 15.09: «картинка не показывается»):
            // арт причёски — hair_orchid_{hair}.png, цвет подставляет гардероб;
            // крутки брали адрес как есть и не грузили ничего. Подставляем то,
            // что надето, иначе первый цвет из манифеста.
            if (!string.IsNullOrEmpty(result.Art) && result.Art.IndexOf('{') >= 0 && _manifest?.sprites != null
                && _manifest.sprites.TryGetValue(parts[1], out var owner) && owner?.wardrobe != null)
            {
                var worn = LvnWardrobe.Equipped(parts[1]);
                foreach (var kv in owner.wardrobe)
                {
                    var token = "{" + kv.Key + "}";
                    if (!result.Art.Contains(token)) continue;
                    string value = null;
                    if (worn != null && worn.TryGetValue(kv.Key, out var w) && !string.IsNullOrEmpty(w) && w != LvnWardrobe.NoneValue) value = w;
                    else if (kv.Value?.items != null)
                        foreach (var it in kv.Value.items)
                            if (!string.IsNullOrEmpty(it.value) && it.value != LvnWardrobe.NoneValue) { value = it.value; break; }
                    if (value != null) result.Art = result.Art.Replace(token, value);
                }
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
                // СИЯНИЕ ЗА КАРТИНКОЙ (Илья 15.09): лучи и горящая рамка в цвет
                // ступени — под картинкой, картинка — отдельным слоем сверху.
                _rewardArt.Clear();
                int rank = LvnRarity.Rank(prize.Rarity);
                var glow = new LvnPrizeGlow
                {
                    name = "gacha-glow",
                    Tint = rank >= 0 ? LvnRarity.ColorOf(prize.Rarity, RarityPalette) : LvnTokens.Gold,
                };
                LvnChrome.Stretch(glow);
                _rewardArt.Add(glow);
                // КАДР КАК НА ПЛИТКЕ, ТОЛЬКО КРУПНЕЕ (Илья 15.09 со скрином:
                // причёска терялась точкой сверху — слой куклы вписывался
                // целиком): причёска — к голове, платье — к корпусу, фон —
                // заливкой; окно обрезает излишек, лучи остаются снаружи.
                var look = InfoFor(prize, RarityPalette, null, owned: false);
                float zoom = look.Cover ? 1f : Mathf.Max(1f, look.Frame * LvnSkinDetail.DetailZoom);
                var frame = new VisualElement { name = "gacha-frame", pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(frame);
                frame.style.overflow = Overflow.Hidden;
                LvnChrome.Round(frame, LvnTokens.RadiusSm);
                var picture = new VisualElement { name = "gacha-picture", pickingMode = PickingMode.Ignore };
                picture.style.position = Position.Absolute;
                picture.style.width = Length.Percent(zoom * 100f);
                picture.style.height = Length.Percent(zoom * 100f);
                picture.style.left = Length.Percent(50f - zoom * 100f * 0.5f);
                picture.style.top = Length.Percent(50f - zoom * 100f * look.FrameY);
                LvnPicture.Fit(picture, cover: look.Cover);
                LvnPicture.Paint(picture, sprite, slice: 0);
                frame.Add(picture);
                _rewardArt.Add(frame);
                LvnLog.Info($"[lvn-gacha] арт приза показан: {prize.Art} ({sprite.rect.width:0}×{sprite.rect.height:0})");
                _rewardArt.schedule.Execute(() => LvnLog.Info(
                    $"[lvn-gacha] арт приза: окно {_rewardArt.resolvedStyle.width:0}×{_rewardArt.resolvedStyle.height:0}, "
                    + $"opacity {_rewardArt.resolvedStyle.opacity:0.00}, блок {_reward.resolvedStyle.width:0}×{_reward.resolvedStyle.height:0}")).StartingIn(60);
            }
            catch (System.OperationCanceledException) { /* the reward screen was closed */ }
            catch (System.Exception ex) { LvnLog.Warn("[lvn-gacha] prize art unavailable: " + ex.Message); }
        }
    }
}
