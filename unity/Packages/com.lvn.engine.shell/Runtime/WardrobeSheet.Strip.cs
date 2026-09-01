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
                if (_def?.wardrobe != null)
                    foreach (var kv in _def.wardrobe)
                    {
                        // Поднастройка — не самостоятельный скин: цвет волос
                        // выбирается только внутри «Причёски», и в витрине
                        // покупок ему делать нечего (Илья 26.08).
                        if (IsSubAxis(kv.Key)) continue;
                        foreach (var it in Items(kv.Key))
                        {
                            // Только КУПЛЕННЫЕ платные (Илья 28.08): бесплатная
                            // база есть у всех — витрина «Все» показывает именно
                            // коллекцию покупок.
                            if (it.value == LvnWardrobe.NoneValue || it.price <= 0
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
            _strip.style.display = _stripCards.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// ОБНОВИТЬ ЖИВУЮ КАРТОЧКУ вместо рождения новой.
        ///
        /// <para>От состояния в ней зависят ровно три вещи: бейдж цены (пока не
        /// куплено), арт (у шаблонной иконки он меняется вместе с соседней
        /// осью — причёска показывает выбранный цвет) и подпись. Всё остальное
        /// — геометрия, кадрирование, обработчик тапа — от состояния не зависит
        /// и переживает обновление вместе с элементом.</para>
        /// </summary>
        private void RefreshCard(VisualElement card, string axis, LvnWardrobeItem item)
        {
            if (card == null) return;
            bool owned = IsOwnedIn(axis, item);

            // Ярлык цены пересобирается целиком: внутри значок валюты, и при
            // смене предмета меняется не только число, но и он.
            var badge = card.Q("card-price");
            badge?.RemoveFromHierarchy();
            if (!owned) card.Add(PriceBadge(item));

            var name = card.Q<Label>("card-name");
            // Название наряда — подпись, а не идентификатор: в английском
            // интерфейсе «Орхидея» читается как недоделанный перевод.
            if (name != null) name.text = Lvn.Content.LvnWords.Name("skin", item.value, item.name);

            // РЕДКОСТЬ ПРЕДМЕТА — цветной ободок карточки. Поле `rarity` у
            // предмета и палитра `rarity_colors` в конфигурации гардероба
            // существовали с самого начала, автор разметил ими три десятка
            // вещей — и не читал их НИКТО: описание связи жило в комментарии
            // рядом с полем, а кода за ним не стояло.
            //
            // Ободок ставится на обновлении, а не при рождении карточки:
            // редкость приходит из данных предмета и может смениться вместе с
            // ними (переимпорт, правка манифеста).
            var ring = Rarity(item);
            if (ring.HasValue) LvnChrome.Border(card, ring.Value, 2f);
            else LvnChrome.ClearBorder(card);

            // Арт переназначается, ТОЛЬКО если сменился адрес: иначе каждая
            // сверка снова гоняла бы загрузку и гасила плитку под плейсхолдер.
            var art = card.Q<VisualElement>("card-art");
            var ph = card.Q<VisualElement>("card-ph");
            var url = ResolveIcon(item.icon);
            if (art != null && !string.IsNullOrEmpty(url) && (art.userData as string) != url)
            {
                art.userData = url;
                var (zoom, _) = LvnWardrobeStage.Framing(axis);
                LvnAsync.Fire(AssignCardArtAsync(art, ph ?? new VisualElement(), url, sharp: zoom >= 3f),
                    "WardrobeCard");
            }
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

        /// <summary>
        /// Бейдж цены — общий для рождения карточки и её обновления: два
        /// одинаковых бейджа в двух местах разъезжаются на первой же правке
        /// стиля.
        ///
        /// <para>Цвет берётся у ЦЕННИКА по валюте предмета. Здесь стоял
        /// прибитый ромб «◆» и золото — то есть предмет за энергию всё равно
        /// выглядел как проданный за самоцветы: роль была выделена, а бейдж
        /// ходил мимо неё (шестой признак канона).</para>
        /// </summary>
        private VisualElement PriceBadge(LvnWardrobeItem item)
        {
            // ЗНАЧОК, А НЕ СЛОВО. Со словом («1 200 кристаллов») ярлык шире
            // плитки, а прижат он к правому краю — число уезжало за левый край,
            // и на карточке оставалось голое «кристаллов» (Илья, 28.08).
            var badge = Lvn.UI.LvnPriceTag.Tag(item.currency, item.price,
                new Lvn.UI.LvnPriceTag.Row { FontSize = 19f, Gap = 3f });
            badge.name = "card-price";
            badge.style.position = Position.Absolute;
            badge.style.top = 6; badge.style.right = 6;
            badge.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.PadX(badge, LvnTokens.Space1);
            LvnAir.PadY(badge, 3);
            LvnChrome.Round(badge, LvnTokens.RadiusSm);
            return badge;
        }

        // Карточка: арт скина во всю плитку, цена бейджем ПРЯМО на арте
        // (у купленных и бесплатных бейджа нет), имя на серой подложке снизу.
        private VisualElement StripCard(string axis, int i, LvnWardrobeItem item)
        {
            bool owned = IsOwnedIn(axis, item);
            // Крупнее (Илья 27.08): плитка подросла, арт занимает почти всю
            // её площадь (~+70%), имя заметно больше (~+50%).
            var card = new VisualElement();
            // КАРТОЧКА НАЗЫВАЕТ СЕБЯ. На витрине «Моё» лента собрана из разных
            // осей, а подсветка искала текущую по НОМЕРУ в пределах вкладки —
            // номер там ничего не значит, и зелёная отметка не появлялась
            // вовсе (живой репорт 01.09). По имени видно, что это за вещь.
            card.name = "card-" + axis + "/" + item.value;
            card.style.width = 150; card.style.height = 208;
            card.style.marginRight = LvnTokens.Space2;
            card.style.flexShrink = 0;
            // Платина #D1D1D6 (Илья 26.08) вместо прежней тускло-серой заливки:
            // арт скинов тёмный, и светлый задник держит его силуэт.
            card.style.backgroundColor = UiColor.Named("#D1D1D6", new Color(0.82f, 0.82f, 0.84f));
            LvnChrome.Round(card, _radius);
            card.style.overflow = Overflow.Hidden; // арт и подложка не выходят за скругление

            // ЗУМ ВИТРИНЫ ПО РАЗДЕЛУ (Илья 27.08): причёска кадрируется к
            // голове, украшения — к шее, платье — к корпусу; «Все» — фигура
            // целиком. Элемент больше карточки, карточка клипует излишек.
            var (zoom, ay) = LvnWardrobeStage.Framing(axis);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.name = "card-art";
            art.style.position = Position.Absolute;
            art.style.width = Length.Percent(zoom * 100f);
            art.style.height = Length.Percent(zoom * 100f);
            art.style.left = Length.Percent(50f - zoom * 100f * 0.50f); // якорь X в центре окна
            art.style.top = Length.Percent(50f - zoom * 100f * ay);    // якорь Y в центре окна
            LvnPicture.Fit(art, cover: false);
            card.Add(art);
            // Плейсхолдер-вешалка, пока арт едет (Илья 27.08): пустая чёрная
            // плитка читалась как «не грузит». Пункт «Нет» живёт с постоянным
            // глифом «×» — у снятия арта нет по определению.
            bool none = item.value == LvnWardrobe.NoneValue;
            // Тёмный глиф: задник плитки светлый (платина), светлый значок на
            // нём растворялся бы.
            var ph = LvnIcons.Make(none ? LvnIcon.Close : LvnIcon.Wardrobe, 42f,
                new Color(0.18f, 0.18f, 0.22f));
            ph.pickingMode = PickingMode.Ignore;
            ph.name = "card-ph";
            ph.style.position = Position.Absolute;
            ph.style.left = Length.Percent(50f);
            ph.style.top = Length.Percent(38f);
            ph.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
            ph.style.opacity = 0.55f;
            card.Add(ph);
            if (!string.IsNullOrEmpty(item.icon))
            {
                // Сильный зум (украшения) на 256px-мини даёт кашу — такой кадр
                // берёт чёткий арт (@2k) сразу. Адрес запоминаем на элементе:
                // по нему сверка узнаёт, менялся ли арт вообще.
                var url = ResolveIcon(item.icon);
                art.userData = url;
                LvnAsync.Fire(AssignCardArtAsync(art, ph, url, sharp: zoom >= 3f), "WardrobeCard");
            }

            var plate = new VisualElement { pickingMode = PickingMode.Ignore };
            plate.style.position = Position.Absolute;
            plate.style.left = 0; plate.style.right = 0; plate.style.bottom = 0;
            plate.style.backgroundColor = new Color(0.16f, 0.16f, 0.19f, 0.85f);
            LvnAir.Pad(plate, LvnTokens.Space1);
            card.Add(plate);

            var name = Lvn.UI.LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore },
                () => Lvn.Content.LvnWords.Name("skin", item.value, item.name));
            name.name = "card-name";
            name.style.color = _text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            plate.Add(name);

            if (!owned) card.Add(PriceBadge(item));

            int at = i;
            if (at < 0)
            {
                // Сборный таб «Все»: тап примеряет предмет в ЕГО ось; подсветка
                // и имя обновляются перестройкой (кэш делает её мгновенной).
                bool worn = IsWornIn(axis, item.value);
                LvnChrome.Border(card, worn ? _accent : new Color(1f, 1f, 1f, 0.12f),
                    worn ? 2.5f : 1.5f);
                var a2 = axis; var v2 = item.value;
                var n2 = Lvn.Content.LvnWords.Name("skin", item.value, item.name);
                card.RegisterCallback<ClickEvent>(_ =>
                {
                    LvnWardrobe.Preview(_entity, a2, v2);
                    RebuildStrip(animate: false);
                    _itemName.text = n2;
                    RefreshConfirm(); // примерка состоялась — кнопкам ожить
                });
                return card;
            }
            card.RegisterCallback<ClickEvent>(_ =>
            {
                if (_tab == null || _tab == AllTab) return;
                _index[_tab] = at;
                ShowItem(); // примерка + имя в карусели + подсветка — одно состояние
            });
            return card;
        }

        // Носится ли значение на оси прямо сейчас (превью сильнее надетого).
        private bool IsWornIn(string axis, string value)
            => LvnCostumer.Wearing(_entity, axis, value, _def?.defaults);

        // Арт карточки — МИНИ-ВЕРСИЯ (Илья 27.08: «не тянуть огромные, если
        // юзер даже не тыкнет»): витрина живёт на @mini, полноразмер грузит
        // только примерка на кукле. Пока не доехал (или мини нет и доезжает
        // полный) — стоит плейсхолдер-вешалка.
        private async Task AssignCardArtAsync(VisualElement art, VisualElement ph, string icon,
            bool sharp = false)
        {
            Sprite s = null;
            var mini = sharp ? null : Lvn.Content.DownloadPolicy.MiniVariant(icon);
            string via = "mini";
            try { if (!string.IsNullOrEmpty(mini)) s = await _assets.LoadSpriteAsync(mini, CancellationToken.None); }
            catch (Exception ex) { Debug.LogWarning($"[lvn-card-art] mini {mini}: {ex.Message}"); }
            if (s == null)
            {
                via = "full";
                try { s = await _assets.LoadSpriteAsync(icon, CancellationToken.None); }
                catch (Exception ex) { Debug.LogWarning($"[lvn-card-art] full {icon}: {ex.Message}"); }
            }
            // Полный файловый след тракта витрины: какой url пробовали, чем
            // кончилось — «одни вешалки» разбираются по этому логу, а не
            // догадками (просьба Ильи 27.08).
            if (s == null)
            {
                Debug.LogWarning($"[lvn-card-art] ПУСТО: mini={mini ?? "-"} и full={icon} не дали спрайта");
                return;
            }
            LvnLog.Trace($"[lvn-card-art] ok via {via}: {(via == "mini" ? mini : icon)} ({s.texture?.width}x{s.texture?.height})");
            // НЕ проверять art.panel: мгновенный кэш-хит завершается ДО того,
            // как RebuildStrip добавил карточку в панель, и страж по panel
            // молча выбрасывал арт — «показались, а при возврате на таб
            // пропали» (живой скрин 27.08).
            //
            // А ВОТ АДРЕС ПРОВЕРИТЬ ОБЯЗАТЕЛЬНО. Карточка причёски меняет свой
            // арт вслед за свотчем цвета, и два быстрых тапа посылают за одну и
            // ту же карточку две загрузки. Побеждала не последняя, а та, что
            // доехала позже: выбран чёрный — на карточке рыжий, до следующей
            // пересборки ленты. Сцена от этого класса гонок закрыта поколениями
            // (LvnStageClock), витрина — вот этой сверкой: адрес, за которым
            // ходили, обязан быть тем же, что запрошен сейчас.
            if (art.userData is string want && want != icon) return;
            art.style.backgroundImage = new StyleBackground(s);
            Lvn.UI.LvnPicture.Pin(art, s, _assets); // видимый арт LRU не трогает
            ph.style.display = DisplayStyle.None;
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
                LvnChrome.Border(_stripCards[k],
                    on ? _accent : new Color(1f, 1f, 1f, 0.12f), on ? 2.5f : 1.5f);
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
                    _strip.ScrollTo(target);
                });
            }
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
