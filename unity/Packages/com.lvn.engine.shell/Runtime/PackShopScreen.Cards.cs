using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КАРТОЧКИ МАГАЗИНА — как выглядит то, что продают.
    ///
    /// <para>Две карточки и обе про одно: чем игрок платит и что получает.
    /// Обычная — за деньги, «бесплатная» — за просмотр рекламы. Вторая живёт по
    /// тем же правилам, что первая, и потому стоит рядом: показывать её или нет
    /// (нет хука рекламы или площадки — карточки нет вовсе: кнопка, которая
    /// ничего не делает, хуже отсутствующей), сколько показов осталось и когда
    /// счётчик восстановится.</para>
    ///
    /// <para>Уехали из <c>PackShopScreen.cs</c> целиком: тот держал три
    /// разговора — откуда берётся каталог, как выглядят карточки и что
    /// происходит при покупке.</para>
    /// </summary>
    public sealed partial class PackShopScreen
    {
        /// <summary>
        /// КАРТОЧКА РЕКЛАМЫ — «+5 кристаллов за ролик», с зарядами и отсчётом.
        ///
        /// <para>Состояние ведёт СЕРВЕР (сколько показов осталось в цикле и
        /// когда он восстановится): свой счётчик на клиенте разошёлся бы с ним
        /// на первом перезапуске игры, и кнопка обещала бы показ, которого не
        /// будет.</para>
        ///
        /// <para>Нет хука показа рекламы (хост не подключил SDK) или нет
        /// площадки в каталоге — карточки нет вовсе: кнопка, которая ничего не
        /// делает, хуже отсутствующей.</para>
        /// </summary>
        private VisualElement AdCard()
        {
            if (!Lvn.Services.LvnAds.Available || string.IsNullOrEmpty(AdPlacement)) return null;
            var st = Lvn.Services.LvnAds.StateOf(AdPlacement);
            if (st == null) return null;

            var card = new VisualElement();
            LvnAir.Pad(card, LvnTokens.Space3);
            card.style.marginBottom = LvnTokens.Space2;
            LvnStyler.Card(card);

            // ЧИСЛО БЕЗ ВАЛЮТЫ НЕ ГОВОРИТ НИЧЕГО: «получите 5» — пять чего? В
            // игре две валюты, и путать их дороже всего именно в магазине.
            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("ads.free_title", "Watch an ad — get {0}",
                LvnPriceTag.Full(st.Currency, st.Amount)));
            ScreenUi.Quiet(title, LvnTokens.TextBase, LvnTokens.Text);
            card.Add(title);

            var hint = new Label();
            ScreenUi.Quiet(hint, LvnTokens.TextXs);
            hint.style.marginTop = LvnTokens.Space1;
            card.Add(hint);

            var btn = new Button();
            btn.style.marginTop = LvnTokens.Space2;
            card.Add(btn);

            void Paint()
            {
                var now = Lvn.Services.LvnAds.StateOf(AdPlacement) ?? st;
                long wait = now.WaitSeconds;
                bool ready = now.Ready;
                // Сколько показов осталось — словами игрока, а не «left=2».
                hint.text = now.Left < 0
                    ? LvnWords.Of("ads.unlimited", "Available now")
                    : ready
                        ? LvnWords.Of("ads.left", "{0} of {1} left", now.Left, now.Charges)
                        : LvnWords.Of("ads.recharging", "More in {0}", LvnTimeWords.Clock(wait));
                btn.text = ready
                    ? LvnWords.Of("ads.watch", "Watch")
                    : LvnTimeWords.Clock(wait);
                btn.SetEnabled(ready);
                LvnStyler.Primary(btn);
            }
            Paint();

            // Отсчёт идёт РЕАЛЬНЫМ временем: перезарядка тикает и в свёрнутой
            // игре, и подпись обязана это знать, иначе она врёт после возврата.
            card.schedule.Execute(Paint).Every(500);
            Lvn.UI.LvnBusy.OnClick(btn, async () =>
            {
                // ОТВЕТ ЗДЕСЬ ВЫБРАСЫВАЕТСЯ НАМЕРЕННО, и это стоит сказать
                // вслух: у соседних операций денег брошенный ответ оказался
                // дефектом, и без пометки этот выглядит таким же.
                //
                // «false» тут значит четыре разных вещи, и три из них — норма:
                // нет SDK у хоста, игрок закрыл ролик на середине, ролика не
                // дали. Награды в этих случаях и не должно быть, а говорить
                // игроку нечего.
                //
                // Четвёртая — досмотрел, а начисление не прошло — единственная
                // настоящая, и о ней докладывает сам дом рекламы: событие
                // ad_reward_fail уходит в аналитику оттуда. Здесь повторять
                // нечего, а показать попап — продуктовое решение, а не техника.
                await Lvn.Services.LvnAds.WatchAndRewardAsync(AdPlacement);
                Paint();   // перерисовка сама покажет: награда пришла или счётчик остался
            }, busyText: null, what: "WatchAd");
            return card;
        }

        // ── One pack card ─────────────────────────────────────────────────────
        private VisualElement Card(Pack pack)
        {
            if (Dressed) return StagePack(pack);
            bool wide = pack.Best;
            var card = new VisualElement { name = "shop-pack", userData = pack.Sku };
            card.style.width = Length.Percent(wide ? 100f : _column ? 86f : 48.5f);
            card.style.flexShrink = 0;
            card.style.marginBottom = LvnTokens.Space2;
            LvnChrome.Card(card, pack.Best ? LvnTokens.SurfaceHi : LvnTokens.Surface, LvnTokens.Radius);
            card.style.overflow = Overflow.Hidden;
            if (pack.Best)
            {
                card.AddToClassList("shop-recommended");
                LvnChrome.Border(card, LvnTokens.Accent, 2f);
                card.Add(Glow());
            }

            // Арт-сцена: не фиолетовая шапка, а тихий стол витрины. Реальная
            // иконка каталога может заполнить её целиком; без неё остаётся
            // аккуратный знак валюты и подпись категории.
            var art = new VisualElement { name = "shop-art" };
            art.style.height = wide ? LvnTokens.Space6 * 3 : LvnTokens.Space6 * 2;
            art.style.flexShrink = 0;
            art.style.alignItems = Align.Center;
            art.style.justifyContent = Justify.Center;
            art.style.backgroundColor = Color.Lerp(LvnTokens.Surface, pack.Tint, 0.16f);
            LvnChrome.RoundTop(art, LvnTokens.Radius);
            art.style.overflow = Overflow.Hidden;
            LvnPicture.Fit(art);
            var halo = new VisualElement { pickingMode = PickingMode.Ignore };
            halo.style.position = Position.Absolute;
            halo.style.backgroundColor = LvnTokens.Faint; // ореол под значком — тихая плашка темы
            LvnChrome.Circle(halo, wide ? 78f : 60f);
            art.Add(halo);
            var glyph = LvnIcons.Make(pack.Emblem, wide ? 46f : 36f, LvnTokens.Text, 0f, LvnTheme.Current.IconGlow * 0.55f);
            art.Add(glyph);
            var category = new Label(pack.Grants != null ? LvnWords.Of("shop.story_bundle", "STORY BUNDLE") : TabTitle(pack.Currency).ToUpperInvariant())
            { pickingMode = PickingMode.Ignore };
            category.style.position = Position.Absolute;
            category.style.left = 12; category.style.bottom = 9;
            category.style.color = UiColor.WithAlpha(LvnTokens.Text, 0.72f);
            category.style.fontSize = LvnTokens.TextMicro;
            category.style.letterSpacing = 1.4f;
            category.style.unityFontStyleAndWeight = FontStyle.Bold;
            art.Add(category);
            card.Add(art);
            if (!string.IsNullOrEmpty(pack.Card))
                LvnPicture.Photo(art, pack.Card, _assets);

            // Текстовый этаж: количество/название, бонус и состав набора.
            var body = new VisualElement();
            LvnAir.Pad(body, LvnTokens.Space2);
            body.style.alignItems = wide ? Align.FlexStart : Align.Center;
            card.Add(body);

            // СКОЛЬКО И ЧЕГО. У набора своё название («Набор новичка»), у пачки
            // валюты — сумма со значком: слово («500 кристаллов») занимало
            // полторы строки крупным кеглем и переносилось посреди числа.
            float sum = LvnTokens.TextBase;
            VisualElement amount;
            if (!string.IsNullOrEmpty(pack.Headline))
            {
                var head = new Label(pack.Headline);
                head.style.color = LvnTokens.Text;
                head.style.fontSize = sum;
                head.style.unityFontStyleAndWeight = FontStyle.Bold;
                head.style.whiteSpace = WhiteSpace.Normal;
                if (!wide) head.style.unityTextAlign = TextAnchor.MiddleCenter;
                amount = head;
            }
            else amount = Lvn.UI.LvnPriceTag.Tag(pack.Currency, pack.Amount,
                new Lvn.UI.LvnPriceTag.Row { FontSize = sum, TextColor = LvnTokens.Text, Gap = 6f });
            body.Add(amount);

            if (pack.Grants != null && pack.Grants.Count > 0)
            {
                body.Add(GrantChips(pack));
            }
            else if (pack.Bonus > 0)
            {
                var bonus = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("shop.bonus", "+{0} bonus", LvnPriceTag.Amount(pack.Bonus)));
                bonus.style.color = LvnTokens.Gold;
                bonus.style.fontSize = LvnTokens.TextXs;
                bonus.style.marginTop = LvnTokens.Tight;
                bonus.style.unityFontStyleAndWeight = FontStyle.Bold;
                body.Add(bonus);
            }

            var buy = new Button { name = "shop-price", text = pack.Price };
            buy.style.fontSize = LvnTokens.TextLg;
            buy.style.whiteSpace = WhiteSpace.Normal;
            buy.style.unityTextAlign = TextAnchor.MiddleCenter;
            buy.style.marginTop = LvnTokens.Space2;
            buy.style.alignSelf = Align.Stretch;
            LvnAir.PadY(buy, LvnTokens.Space2);
            buy.style.color = pack.Best ? LvnTokens.OnAccent : LvnTokens.Text;
            buy.style.backgroundColor = pack.Best
                ? LvnTokens.Accent
                : UiColor.WithAlpha(LvnTokens.Accent, 0.15f);
            buy.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Рекомендуемый пак заливкой и без рамки, остальные — обводкой.
            // Огранка называется целиком в обеих ветках: иначе скругление
            // живёт отдельно от решения про рамку и разъезжается с ним.
            if (pack.Best) LvnChrome.Frame(buy, LvnTokens.RadiusSm);
            else LvnChrome.Frame(buy, LvnTokens.RadiusSm,
                                 UiColor.WithAlpha(LvnTokens.Accent, 0.36f), 1f);
            buy.clicked += () => Buy(buy, pack);
            body.Add(buy);

            if (pack.Badge != Ribbon.None)
            {
                var ribbon = BadgeLabel(pack.Badge);
                ribbon.style.position = Position.Absolute;
                ribbon.style.top = LvnTokens.Space1;
                ribbon.style.left = LvnTokens.Space1;
                ribbon.style.right = LvnTokens.Space1;
                art.Add(ribbon);
            }

            return card;
        }

        private static VisualElement Glow()
        {
            var glow = new VisualElement { name = "shop-glow", pickingMode = PickingMode.Ignore };
            glow.style.position = Position.Absolute;
            glow.style.left = 0; glow.style.right = 0; glow.style.bottom = 0;
            glow.style.height = Length.Percent(45f);
            glow.style.backgroundImage = LvnBackdrop.Vertical(
                UiColor.WithAlpha(LvnTokens.Accent, 0f), UiColor.WithAlpha(LvnTokens.Accent, 0.32f), smooth: true);
            return glow;
        }

        private static Label BadgeLabel(Ribbon badge)
        {
            string text = badge == Ribbon.Popular ? LvnWords.Of("shop.popular", "POPULAR")
                : badge == Ribbon.Value ? LvnWords.Of("shop.value", "BEST VALUE")
                : LvnWords.Of("shop.best_price", "BEST PRICE");
            // Три роли различаются и словами, и тоном, включая две премиальные ленты.
            Color tone = badge == Ribbon.Popular ? LvnTokens.Accent
                : badge == Ribbon.Value ? LvnTokens.Bronze : LvnTokens.Gold;
            var ribbon = new Label(text) { name = "shop-ribbon", pickingMode = PickingMode.Ignore };
            ribbon.style.fontSize = LvnTokens.TextXs;
            ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
            ribbon.style.letterSpacing = LvnTokens.TextXs * 0.06f;
            ribbon.style.whiteSpace = WhiteSpace.Normal;
            ribbon.style.unityTextAlign = TextAnchor.MiddleCenter;
            ribbon.style.color = badge == Ribbon.Popular ? LvnTokens.OnAccent : LvnTokens.Bg;
            ribbon.style.backgroundColor = tone;
            LvnAir.Pad(ribbon, LvnTokens.Space2, LvnTokens.Hair);
            LvnChrome.Round(ribbon, LvnTokens.RadiusXs);
            return ribbon;
        }

        private static VisualElement GrantChips(Pack pack)
        {
            var chips = new VisualElement { name = "shop-grants", pickingMode = PickingMode.Ignore };
            LvnFlow.Wrap(chips, Justify.Center);
            chips.style.width = Length.Percent(100f);
            chips.style.marginTop = LvnTokens.Space1;
            foreach (var kv in pack.Grants)
            {
                var chip = LvnStyler.Chip(ScreenUi.Row(), LvnTokens.SurfaceHi);
                chip.style.marginBottom = LvnTokens.Space1;
                chip.style.marginRight = LvnTokens.Space1;
                chip.Add(LvnPriceTag.Tag(kv.Key, kv.Value,
                    new LvnPriceTag.Row { FontSize = LvnTokens.TextXs, IconSize = LvnTokens.TextXs, Gap = LvnTokens.Space1 }));
                chips.Add(chip);
            }
            return chips;
        }
    }
}
