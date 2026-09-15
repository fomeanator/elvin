using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВИТРИНА ЛИСТА — часть <see cref="WardrobeSheet"/>: лента карточек, кадр
    /// плитки, колонка эмоций и вся мелкая моторика — перетаскивание, ползунок,
    /// въезд карточек.
    ///
    /// <para>Кадрирование по осям живёт в <see cref="LvnWardrobeStage"/>, здесь
    /// — сборка элементов и их поведение под пальцем.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        // Плавная смена стиля и набор свойств «карточка переезжает» живут в
        // LvnMotion: понятие общее для всей оболочки, а не для этого листа.
        private static void Smooth(VisualElement el, int ms, params string[] props)
            => LvnMotion.Smooth(el, ms, props);

        // Въезд элемента: лёгкий подъём + проявление, каскадом по позиции —
        // перестройка ленты «переезжает», а не мигает. Transition вешается
        // ВНУТРИ отложки: повешенный сразу, он анимировал бы сам старт в ноль.
        private static void EnterSoft(VisualElement el, int i)
        {
            el.style.opacity = 0f;
            el.style.translate = new Translate(0f, 12f);
            el.schedule.Execute(() =>
            {
                Smooth(el, LvnMotion.Calm, CardGlide);
                el.style.opacity = 1f;
                el.style.translate = new Translate(0f, 0f);
            }).ExecuteLater(16 + Mathf.Min(i, 10) * 26);
        }





        // animate=false — ЛЕНТА УЖЕ НА ЭКРАНЕ и просто пересобирается после
        // примерки (тап по свотчу цвета, тап по карточке в «Моё»): проигрывать
        // въезд карточек заново значит дёргать неподвижный список под пальцем
        // (Илья 26.08). Въезд принадлежит появлению ленты — смене раздела,
        // персонажа, открытию листа.
        /// <summary>Рост плитки — одно число на карточку и на пустую ленту.</summary>
        private const float StripCardH = LvnSkinCard.BaseHeight;

        private void RebuildStrip(bool animate = true)
        {
            if (_strip == null) return;
            if (_tab == AllTab)
            {
                // Сборная витрина: пары (ось, предмет) — тап примеряет в СВОЮ
                // ось; подсветка по надетому каждой оси.
                //
                // ЧЕРЕЗ ТОГО ЖЕ МОНТАЖЁРА, что и обычная лента, и это не
                // причёсывание кода. Раньше витрина чистила ленту сама и
                // клала карточки напрямую — без монтажной метки. Монтажёр
                // убирает только СВОИ элементы (чужих он не трогает нарочно),
                // поэтому при уходе с «Моё» на любой раздел его карточки
                // оставались в ленте, а монтажёр досыпал сверху свои: игрок
                // открывал «Причёску» и видел там украшения (живой репорт
                // Ильи 29.08 со скриншотом — «Мак» и «Орхидея» среди причёсок).
                // Одна лента, наполняемая двумя способами, обязана была
                // разъехаться — вопрос был только когда.
                var all = new List<(string axis, LvnWardrobeItem item)>();
                if (_slots != null)
                    foreach (var kv in _slots)
                    {
                        // Поднастройка — не самостоятельный скин: цвет волос
                        // выбирается только внутри «Причёски», и в витрине
                        // покупок ему делать нечего (Илья 26.08).
                        if (IsSubAxis(kv.Key)) continue;
                        foreach (var it in Items(kv.Key))
                        {
                            // Collection = purchases AND awarded wheel prizes.
                            // Zero-price gacha items are earned, unlike the free base look.
                            if (it.value == LvnWardrobe.NoneValue || (it.price <= 0 && !it.gacha)
                                || !IsOwnedIn(kv.Key, it)) continue;
                            all.Add((kv.Key, it));
                        }
                    }
                int shown = all.Count;
                Lvn.UI.LvnMontage.Sync(_strip.contentContainer, all,
                    key: p => p.axis + "/" + p.item.value,
                    create: p =>
                    {
                        var card = StripCard(p.axis, -1, p.item);
                        if (animate) EnterSoft(card, all.FindIndex(x => x.axis == p.axis && x.item == p.item));
                        return card;
                    },
                    update: (el, p) => RefreshCard(el, p.axis, p.item));
                AdoptStripCards();
                // Подпись отдана основе (RefreshLabel ниже); своё слово витрина
                // говорит, только когда основы нет и показывать нечего.
                if (AllTabAxis == null)
                    _itemName.text = shown > 0
                ? LvnWords.Of("wardrobe.my_skins", "My skins")
                : LvnWords.Of("wardrobe.nothing_yet", "Nothing yet — look through the sections");
                RebuildSubRow(animate); // на «Моё» это ряд основы
                RefreshLabel();
                RefreshArrows();
                // ПОДСВЕТКА — ТОЖЕ ЧАСТЬ ПЕРЕСБОРКИ. Ветка «Моё» выходила без
                // неё, и после «Выбрать» лента оставалась серой: надетое
                // ничем не отмечено, хотя сам StyleStrip вкладку «Моё» умеет
                // (он отмечает НАДЕТОЕ, а не k-ю карточку). Обычная ветка его
                // зовёт последней строкой — эта уходила раньше.
                StyleStrip();
                return;
            }
            var items = Items(_tab);
            // ЛЕНТА СВЕРЯЕТСЯ, А НЕ ПЕРЕСОБИРАЕТСЯ (правило Монтажёра). Её
            // трогают на каждый чих: тап свотча, покупка, ответ кошелька, смена
            // персонажа. Пересборка стоила дорого не тактами, а видом: карточка
            // рождалась заново, вместе с ней заново ехал арт, и лента моргала
            // ровно там, где ничего не изменилось. Тот же предмет остаётся ТЕМ
            // ЖЕ элементом — с ним переживают загруженная картинка, скролл и
            // начатая анимация; меняется только то, что действительно менялось.
            var axis = _tab;
            Lvn.UI.LvnMontage.Sync(_strip.contentContainer, items,
                key: it => axis + "/" + it.value,
                create: it =>
                {
                    var card = StripCard(axis, items.IndexOf(it), it);
                    if (animate) EnterSoft(card, items.IndexOf(it));
                    return card;
                },
                update: (el, it) => RefreshCard(el, axis, it));
            AdoptStripCards();
            RebuildSubRow(animate);
            RefreshArrows();
            StyleStrip();
        }

        /// <summary>
        /// ПРИНЯТЬ КАРТОЧКИ ЛЕНТЫ — пересчитать список и решить, видна ли она.
        ///
        /// <para>Правило одно: лента видна, если в ней ЕСТЬ КАРТОЧКИ. Стояло
        /// оно двумя написаниями — вкладка «Моё» считала показанное, обычная
        /// вкладка спрашивала длину списка ДО сборки. Оба ответа сегодня
        /// совпадают, но отвечают на разные вопросы: «сколько вышло» и
        /// «сколько было данных». Разойдутся они в первый же день, когда
        /// сборка начнёт что-нибудь пропускать, — и лента покажется пустой
        /// полосой.</para>
        ///
        /// <para>Спрашивать после сборки и строго: не «было ли из чего», а
        /// «получилось ли что-нибудь».</para>
        /// </summary>
        private void AdoptStripCards()
        {
            _stripCards.Clear();
            foreach (var child in _strip.contentContainer.Children()) _stripCards.Add(child);
            // ЛЕНТА ДЕРЖИТ ВЫСОТУ И ПУСТОЙ. display:none ронял лист на высоту
            // плитки («Моё» без покупок), а первый же раздел поднимал его
            // обратно — та же дрожь строк, что и у ряда поднастроек. Пустая
            // лента невидима, но место держит.
            bool any = _stripCards.Count > 0;
            _strip.style.visibility = any ? Visibility.Visible : Visibility.Hidden;
            _strip.style.minHeight = StripCardH;
        }

        /// <summary>
        /// ОБНОВИТЬ ЖИВУЮ КАРТОЧКУ вместо рождения новой: плитка (LvnSkinCard)
        /// получает свежие сведения — ценник, арт (у шаблонной иконки он
        /// меняется вместе с соседней осью), подпись, ступень — и сама решает,
        /// что перерисовать; арт перегружается, только если сменился адрес.
        /// </summary>
        private void RefreshCard(VisualElement card, string axis, LvnWardrobeItem item)
        {
            if (!(card is LvnSkinCard skin)) return;
            var (zoom, ay) = LvnWardrobeStage.Framing(axis);
            bool owned = IsOwnedIn(axis, item);
            // «ТОЛЬКО ИЗ КРУТКИ» (флаг gacha, TR-93) — подарок вместо ценника и при
            // цене: цена у такого скина — за продажу копии, а не за покупку.
            bool gift = item.gacha;
            skin.Bind(new LvnSkinCard.Info
            {
                // Название наряда — подпись, а не идентификатор: в английском
                // интерфейсе «Орхидея» читается как недоделанный перевод.
                Title = Lvn.Content.LvnWords.Name("skin", item.value, item.name),
                Art = ResolveIcon(item.icon),
                // Сильный зум (украшения) на 256px-мини даёт кашу — такой кадр
                // берёт чёткий арт (@2k) сразу.
                SharpArt = zoom >= 3f,
                Frame = zoom, FrameY = ay,
                None = item.value == LvnWardrobe.NoneValue,
                Rarity = Rarity(item), RarityWord = LvnRarity.Word(item.rarity),
                Price = item.price, Currency = item.currency,
                Gift = gift, Owned = owned,
                Obtain = Obtain(item, owned),
                Description = item.description,
                Radius = _radius, TextColor = _text,
            }, _assets);
        }

        /// <summary>Способ получения словами (Илья 15.09: «надо у скинов писать
        /// их способы получения»): есть · крутки · покупка · бесплатно.</summary>
        private static string Obtain(LvnWardrobeItem item, bool owned)
        {
            // СПОСОБ ПОЛУЧЕНИЯ — ВСЕГДА, «есть» — приставкой (TR-118). Слово
            // «уже есть» вместо способа ничего не говорило о вещи («надо писать,
            // как получено, и описание» — Илья): у имеющегося наряда способ
            // тот же, только в прошедшем времени.
            string how = item.gacha
                ? Lvn.Content.LvnWords.Of(owned ? "skin.got_gacha" : "skin.get_gacha", owned ? "Won in spins" : "Drops from spins")
                : item.price > 0
                    ? Lvn.Content.LvnWords.Of(owned ? "skin.got_buy" : "skin.get_buy", owned ? "Bought for {0}" : "Buy: {0}", PriceText(item))
                    : Lvn.Content.LvnWords.Of("skin.get_free", "Free");
            return owned ? Lvn.Content.LvnWords.Of("skin.get_owned", "Yours") + " · " + how : how;
        }

        /// <summary>Цвет редкости предмета, если автор его назвал: ключ у
        /// предмета (<c>rarity: "rare"</c>) ищется в палитре гардероба
        /// (<c>ui.wardrobe.rarity_colors</c>). Нет ключа или нет палитры —
        /// нет и ободка: движок не придумывает за автора, что считать
        /// редким.</summary>
        private Color? Rarity(LvnWardrobeItem item)
        {
            var key = item?.rarity;
            if (string.IsNullOrEmpty(key)) return null;
            var palette = _cfg?.rarity_colors;
            if (palette == null || !palette.TryGetValue(key, out var hex) || string.IsNullOrEmpty(hex))
                return null;
            return UiColor.Named(hex, LvnTokens.Accent);
        }

        /// <summary>Цена словом — для мест, где места вдоволь: тост, вопрос о
        /// покупке. На карточке её показывает значок (см. PriceBadge).</summary>
        private static string PriceText(LvnWardrobeItem item)
            => Lvn.UI.LvnPriceTag.Full(item?.currency, item?.price ?? 0);

        // Карточка — общая плитка (LvnSkinCard): облик и загрузка арта у неё,
        // здесь — имя элемента, сведения и что делать по тапу.
        private VisualElement StripCard(string axis, int i, LvnWardrobeItem item)
        {
            // КАРТОЧКА НАЗЫВАЕТ СЕБЯ. На витрине «Моё» лента собрана из разных
            // осей, а подсветка искала текущую по НОМЕРУ в пределах вкладки —
            // номер там ничего не значит, и зелёная отметка не появлялась
            // вовсе (живой репорт 01.09). По имени видно, что это за вещь.
            var card = new LvnSkinCard { name = "card-" + axis + "/" + item.value };
            card.style.marginRight = LvnTokens.Space2;
            RefreshCard(card, axis, item);
            if (i < 0)
            {
                // Сборный таб «Все»: подсветка надетого рисуется сразу —
                // карусельного индекса у этой ленты нет.
                card.SetChosen(IsWornIn(axis, item.value), ChosenInk);
            }
            // ОДИН ОБРАБОТЧИК НА ОБЕ ЛЕНТЫ, И РЕШАЕТ ОН В МОМЕНТ ТАПА.
            // Монтажёр сверяет карточки ПО КЛЮЧУ «ось/значение», а ключ у
            // «Моё» и у раздела ОДИН И ТОТ ЖЕ — значит один и тот же элемент
            // служит обеим лентам. Раздельный «если это «Моё» — выйти» делал
            // строку мёртвой (Илья 08.09). Кто карточку родил, теперь неважно.
            // Долгое нажатие — подробности, их плитка показывает сама.
            var a2 = axis; var v2 = item.value;
            var n2 = Lvn.Content.LvnWords.Name("skin", item.value, item.name);
            card.Tapped += () =>
            {
                if (_tab == null) return;
                if (_tab == AllTab)
                {
                    // Тап примеряет предмет в ЕГО ось; подсветка и имя
                    // обновляются перестройкой (кэш делает её мгновенной).
                    LvnWardrobe.Preview(_entity, a2, v2);
                    RebuildStrip(animate: false);
                    _itemName.text = n2;
                    RefreshConfirm(); // примерка состоялась — кнопкам ожить
                    return;
                }
                // МЕСТО ИЩЕМ СЕЙЧАС, А НЕ ПОМНИМ С РОЖДЕНИЯ: та же карточка
                // живёт в разных лентах, и номер, снятый при создании, к
                // нынешней ленте отношения не имеет.
                var list = Items(_tab);
                int at = -1;
                for (int n = 0; n < list.Count; n++)
                    if (list[n].value == v2) { at = n; break; }
                if (at < 0) return;
                _index[_tab] = at;
                ShowItem(); // примерка + имя в карусели + подсветка — одно состояние
            };
            return card;
        }

        // Носится ли значение на оси прямо сейчас (превью сильнее надетого).
        private bool IsWornIn(string axis, string value)
            => LvnCostumer.Wearing(_entity, axis, value, _def?.defaults);

        /// <summary>Арт в чужой элемент (свотчи и ряд поднастроек): тот же тракт,
        /// что у плитки — мини-вариант, иначе полный, — и та же сверка адреса:
        /// за чем ходили, то и ставим, иначе побеждает не последняя загрузка, а
        /// та, что доехала позже.</summary>
        private async Task AssignCardArtAsync(VisualElement art, VisualElement ph, string icon, bool sharp = false)
        {
            var s = await LvnSkinCard.LoadSpriteAsync(_assets, icon, sharp);
            if (s == null) return;
            if (art.userData is string want && want != icon) return;
            Lvn.UI.LvnPicture.Paint(art, s, slice: 0);
            Lvn.UI.LvnPicture.Pin(art, s, _assets); // видимый арт LRU не трогает
            if (ph != null) ph.style.display = DisplayStyle.None;
        }

        // Подсветка текущего и доводка ленты: выбранная карточка всегда в кадре
        // (стрелки карусели листают — лента едет следом).
        private void StyleStrip()
        {
            int cur = _tab != null && _index.TryGetValue(_tab, out var i) ? i : 0;
            for (int k = 0; k < _stripCards.Count; k++)
            {
                // На «Моё» отмечается НАДЕТОЕ, а не k-я карточка: лента там из
                // разных осей, и номер вкладки к ней отношения не имеет.
                bool on = _tab == AllTab ? IsWornCard(_stripCards[k]) : k == cur;
                (_stripCards[k] as LvnSkinCard)?.SetChosen(on, ChosenInk);
            }
            // Довозим В КАДР ТУ ЖЕ карточку, что и отметили, — иначе на «Моё»
            // лента уезжала к безразличной k-й, а отмеченная оставалась за краем.
            VisualElement target = null;
            if (_tab == AllTab) { foreach (var c in _stripCards) if (IsWornCard(c)) { target = c; break; } }
            else if (cur >= 0 && cur < _stripCards.Count) target = _stripCards[cur];
            if (target != null)
            {
                // Отложенно — после лейаута; к этому моменту ленту могли уже
                // перестроить (смена оси, покупка, переоткрытие листа), и чужая
                // карточка роняет ScrollTo ArgumentException'ом (живой лог).
                _strip.schedule.Execute(() =>
                {
                    if (target.panel == null || target.parent != _strip.contentContainer) return;
                    GlideTo(target);
                });
            }
        }

        /// <summary>
        /// ЛЕНТА ДОВОЗИТ КАРТОЧКУ ПЛАВНО. ScrollTo ставил её в кадр скачком:
        /// на смене раздела и по стрелкам лента дёргалась, и это читалось как
        /// сбой, а не как ход («сделай премиальный плавный гардероб» — Илья
        /// 14.09). Цель та же, что у ScrollTo — карточка в окне целиком,
        /// ближним краем; поколение обрывает прежний ход, если карточку
        /// сменили быстрее, чем доехали. Геометрии ещё нет — едем скачком,
        /// как раньше.
        /// </summary>
        private int _glideEpoch;

        private void GlideTo(VisualElement card)
        {
            if (_strip == null || card == null || card.panel == null
                || card.parent != _strip.contentContainer) return;
            float viewW = _strip.contentViewport.layout.width;
            float x = card.layout.x, w = card.layout.width;
            float contentW = _strip.contentContainer.layout.width;
            if (float.IsNaN(viewW) || viewW <= 1f || float.IsNaN(x) || float.IsNaN(contentW))
            { _strip.ScrollTo(card); return; }
            float from = _strip.scrollOffset.x;
            float to = from;
            if (x < from) to = x;
            else if (x + w > from + viewW) to = x + w - viewW;
            to = Mathf.Clamp(to, 0f, Mathf.Max(0f, contentW - viewW));
            if (Mathf.Abs(to - from) < 1f) return;
            int mine = ++_glideEpoch;
            LvnAsync.Fire(LvnMotion.PlayAsync(_strip, LvnMotion.Normal, (e, p) =>
            {
                if (mine != _glideEpoch) return;
                ((ScrollView)e).scrollOffset = new Vector2(Mathf.Lerp(from, to, p), 0f);
            }), "StripGlide");
        }

        /// <summary>Надета ли вещь этой карточки: имя карточки — «ось/значение»,
        /// и ответ даёт костюмер по её оси.</summary>
        private bool IsWornCard(VisualElement card)
        {
            var id = card?.name;
            if (string.IsNullOrEmpty(id) || !id.StartsWith("card-")) return false;
            int slash = id.IndexOf('/');
            if (slash < 0) return false;
            var axis = id.Substring(5, slash - 5);
            var value = id.Substring(slash + 1);
            return !string.IsNullOrEmpty(axis) && CurrentValueOf(axis) == value;
        }

        internal void Step(int dir)
        {
            if (_tab == AllTab)
            {
                // На сборной витрине стрелки листают ОСНОВУ: примерка идёт в её
                // ось, поэтому и свотчи под лентой, и кукла соглашаются сами.
                var basis = AllTabAxis;
                if (basis == null) return;
                var list = Items(basis);
                if (list.Count == 0) return;
                var now = CurrentValueOf(basis);
                int at = 0;
                for (int k = 0; k < list.Count; k++) if (list[k].value == now) { at = k; break; }
                var next = list[(at + dir + list.Count) % list.Count];
                LvnWardrobe.Preview(_entity, basis, next.value);
                RebuildSubRow(animate: false);
                RefreshLabel();
                RefreshConfirm();
                return;
            }
            var items = Items(_tab);
            if (items.Count == 0) return;
            _index[_tab] = ((_index.TryGetValue(_tab, out var i) ? i : 0) + dir + items.Count) % items.Count;
            ShowItem();
        }

        private void ShowItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            var item = items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
            RefreshLabel();
            bool owned = IsOwned(item);
            LvnLog.Trace($"[lvn-wardrobe] sheet preview {_entity}.{_tab} = '{item.value}' " +
                      $"(price={item.price} {item.currency ?? "-"}, owned={owned})");
            LvnWardrobe.Preview(_entity, _tab, item.value); // the live actor is the mirror
            StyleStrip(); // лента подсвечивает и довозит выбранную карточку
            RefreshConfirm();
        }

        // The item the carousel is showing on the active tab — the button's
        // subject. BUY and CHOOSE are separate acts (partner's ask): browsing
        // an unowned priced item offers to buy JUST IT (the sheet stays open,
        // so a hairstyle and a jacket buy back-to-back); once owned, the same
        // button turns into the plain "choose" that commits the look.
        private LvnWardrobeItem CurrentItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return null;
            return items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
        }
    }
}
