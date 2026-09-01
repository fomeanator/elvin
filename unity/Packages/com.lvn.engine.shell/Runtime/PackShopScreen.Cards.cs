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
            title.style.color = LvnTokens.Text;
            title.style.fontSize = LvnTokens.TextBase;
            title.style.whiteSpace = WhiteSpace.Normal;
            card.Add(title);

            var hint = new Label();
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = LvnTokens.TextXs;
            hint.style.whiteSpace = WhiteSpace.Normal;
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
            bool wide = pack.Best || pack.Grants != null; // герой и наборы — во всю ширину
            var card = new VisualElement();
            card.style.width = Length.Percent(wide ? 100f : 48.5f);
            card.style.marginBottom = LvnTokens.Space2;
            LvnChrome.Card(card, pack.Best ? LvnTokens.SurfaceHi : LvnTokens.Surface, LvnTokens.Radius);
            card.style.overflow = Overflow.Hidden;
            if (pack.Best)
            {
                // Верх акцентный, остальные три — тот же тихий тон, что у
                // обычной карточки: выделяется одна сторона, а не рамка целиком.
                var quietEdge = LvnChrome.BorderTone(0.64f);
                LvnChrome.Border(card, quietEdge, 1f);
                LvnChrome.EdgeOn(card, LvnSide.Top, LvnTokens.Accent, 2f);
            }

            // Арт-сцена: не фиолетовая шапка, а тихий стол витрины. Реальная
            // иконка каталога может заполнить её целиком; без неё остаётся
            // аккуратный знак валюты и подпись категории.
            var art = new VisualElement();
            art.style.height = wide ? 112 : 82;
            art.style.alignItems = Align.Center;
            art.style.justifyContent = Justify.Center;
            art.style.backgroundColor = UiColor.WithAlpha(pack.Tint, 0.88f);
            LvnChrome.RoundTop(art, LvnTokens.Radius);
            art.style.overflow = Overflow.Hidden;
            LvnPicture.Fit(art);
            var halo = new VisualElement { pickingMode = PickingMode.Ignore };
            halo.style.position = Position.Absolute;
            halo.style.width = wide ? 78 : 60; halo.style.height = wide ? 78 : 60;
            halo.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f);
            LvnChrome.Round(halo, wide ? 39f : 30f);
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
            float sum = wide ? 30 : 25;
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
                // Состав набора — пилюлями: читается с одного взгляда.
                var chips = new VisualElement();
                LvnFlow.Wrap(chips);
                chips.style.marginTop = LvnTokens.Space1;
                foreach (var kv in pack.Grants)
                {
                    var chip = LvnStyler.Chip(ScreenUi.Row(), LvnTokens.Faint);
                    chip.style.marginBottom = LvnTokens.Space1;
                    chip.style.marginRight = LvnTokens.Space1;
                    // РЯД СОБИРАЕТ ЦЕННИК. Здесь он складывался руками —
                    // значок акцентным, сумма цветом текста, — и та же валюта
                    // в хабе и в гардеробе выглядела иначе. Заодно уходит
                    // повторённое здесь решение дома: слова рядом со значком
                    // нет, он уже сказал, какая это валюта.
                    chip.Add(LvnPriceTag.Tag(kv.Key, kv.Value,
                        new LvnPriceTag.Row { FontSize = 20f, IconSize = 18f, Gap = 6f }));
                    chips.Add(chip);
                }
                body.Add(chips);
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

            var buy = new Button { text = pack.Price };
            buy.style.fontSize = LvnTokens.TextSm;
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
                bool gold = pack.Badge == Ribbon.Value || pack.Badge == Ribbon.BestPrice;
                string txt = pack.Badge == Ribbon.Popular ? LvnWords.Of("shop.popular", "POPULAR")
                           : pack.Badge == Ribbon.Value ? LvnWords.Of("shop.value", "BEST VALUE")
                           : LvnWords.Of("shop.best_price", "BEST PRICE");
                var ribbon = new Label(txt) { pickingMode = PickingMode.Ignore };
                ribbon.style.position = Position.Absolute;
                ribbon.style.top = 10;
                ribbon.style.left = 12;
                ribbon.style.fontSize = LvnTokens.TextMicro;
                ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
                ribbon.style.letterSpacing = 1.5f;
                ribbon.style.color = gold ? LvnTokens.Bg : LvnTokens.OnAccent;
                ribbon.style.backgroundColor = gold ? LvnTokens.Gold : LvnTokens.Accent;
                LvnAir.Pad(ribbon, LvnTokens.Space2, LvnTokens.Hair);
                LvnChrome.Round(ribbon, LvnTokens.RadiusXs);
                card.Add(ribbon);
            }

            return card;
        }
    }
}
