using System.Collections.Generic;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПОЛОСА ОСЕЙ — вкладки «причёска / платье / украшения» и ряд поднастроек
    /// под ними.
    ///
    /// <para>Это ВЁРСТКА выбора, а не сам выбор: какие оси есть и что из них
    /// надето, отвечает соседняя тема (<c>WardrobeSheet.Axes</c>); что именно
    /// показать плиткой — лента (<c>.Strip</c>); чем платят — покупка
    /// (<c>.Buy</c>). Здесь только полоса: как вкладки ПОМЕЩАЮТСЯ (у гардероба
    /// их бывает и три, и десять, и полоса обязана остаться читаемой на
    /// телефоне), что происходит при выборе, и как выглядят свотчи
    /// поднастройки.</para>
    ///
    /// <para>Шаблонная иконка живёт здесь же: `{ось}` в пути арта подменяется
    /// ТЕКУЩИМ значением этой оси — так вкладка причёсок показывает выбранный
    /// цвет, а не общий значок.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        // Шаблонные иконки: `{ось}` в пути арта подменяется ТЕКУЩИМ значением
        // этой оси (превью → надетое → дефолт → первый предмет). Так карточки
        // причёсок показывают выбранный цвет: hair_rose_{hair} → hair_rose_black.
        private string ResolveIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon) || icon.IndexOf('{') < 0
                || _def?.wardrobe == null) return icon;
            foreach (var kv in _def.wardrobe)
            {
                var token = "{" + kv.Key + "}";
                if (!icon.Contains(token)) continue;
                icon = icon.Replace(token, CurrentValueOf(kv.Key));
            }
            return icon;
        }

        // Ряд поднастроек активного раздела: подпись слота + свотчи. Тап
        // примеряет на живую куклу и перестраивает ленту — шаблонные иконки
        // карточек сразу показывают, например, новый цвет волос.
        private void RebuildSubRow(bool animate = true)
        {
            if (_subRow == null) return;
            _subRow.Clear();
            bool any = false;
            // Вкладка «Моё» тоже носит свои поднастройки — там живёт основа
            // фигуры (запад/север). Раньше ряд молчал на ней целиком.
            if (_tab != null)
                foreach (var sub in SubAxesOf(_tab))
                {
                    var items = Items(sub);
                    if (items.Count == 0) continue;
                    var slot = _def.wardrobe[sub];
                    // Подпись подоси («Основа», «Цвет волос») — через словарь:
                    // она стоит рядом с переведёнными плитками и остаётся
                    // последним русским словом в английском гардеробе.
                    var lbl = Lvn.UI.LvnRedress.Bind(new Label(),
                        () => Lvn.Content.LvnWords.Name("axis", sub, slot?.name));
                    lbl.style.color = _dim;
                    lbl.style.fontSize = LvnTokens.TextSm;
                    lbl.style.marginLeft = any ? 22 : 0; // зазор между слотами
                    lbl.style.marginRight = 10;
                    _subRow.Add(lbl);
                    int n = 0;
                    foreach (var it in items)
                    {
                        var sw = SubSwatch(sub, it);
                        _subRow.Add(sw);
                        if (animate) EnterSoft(sw, n);
                        n++;
                    }
                    any = true;
                }
            _subRow.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Свотч: круг цвета из манифеста (item.color), иначе — мини-арт;
        // непринадлежащий помечен «◆», текущий обведён акцентом. Покупку
        // непринадлежащего цвета предлагает кнопка «Выбрать» (PendingBuy).
        private VisualElement SubSwatch(string axis, LvnWardrobeItem item)
        {
            bool worn = IsWornIn(axis, item.value);
            bool none = item.value == LvnWardrobe.NoneValue;
            var b = new Button(() =>
            {
                LvnWardrobe.Preview(_entity, axis, item.value); // NoneValue = «снять»
                RebuildStrip(animate: false); // свотчи + шаблонные иконки карточек
                RefreshLabel();   // «Рыжая: Голливудские волны» — цвет И причёска
                RefreshConfirm(); // кнопка предложит купить этот цвет
            }) { text = "" };
            b.style.width = 54; b.style.height = 54;
            b.style.marginLeft = 5; b.style.marginRight = 5;
            b.style.paddingLeft = 0; b.style.paddingRight = 0;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnChrome.Round(b, Lvn.UI.LvnTokens.RadiusLg);
            var fallback = new Color(0.30f, 0.31f, 0.35f, 0.9f);
            b.style.backgroundColor = string.IsNullOrEmpty(item.color)
                ? fallback : UiColor.Named(item.color, fallback);
            if (none)
                b.Add(LvnIcons.Make(LvnIcon.Close, 22f, _text));
            else if (string.IsNullOrEmpty(item.color) && !string.IsNullOrEmpty(item.icon))
            {
                var art = new VisualElement { pickingMode = PickingMode.Ignore };
                art.style.width = 44; art.style.height = 44;
                LvnPicture.Fit(art, cover: false);
                LvnAsync.Fire(AssignCardArtAsync(art, new VisualElement(),
                    ResolveIcon(item.icon)), "WardrobeSwatch");
                b.Add(art);
            }
            LvnChrome.Border(b, worn ? _accent : new Color(1f, 1f, 1f, 0.28f),
                worn ? 3f : 1.5f);
            if (!IsOwnedIn(axis, item))
            {
                var dot = new Label("◆") { pickingMode = PickingMode.Ignore };
                dot.style.position = Position.Absolute;
                dot.style.top = -6; dot.style.right = -6;
                dot.style.color = LvnTokens.Gold;
                dot.style.fontSize = Lvn.UI.LvnTokens.TextMicro;
                b.Add(dot);
            }
            return b;
        }






        // ── ряд разделов ужимается вместо переноса ───────────────────────────
        // Ужимаемся ступенями и ровно настолько, насколько нужно: сперва
        // отступы, потом иконки, и лишь в последнюю очередь кегль подписи —
        // слово целиком важнее его размера. Обратно ступени не отыгрываются:
        // ряд пересобирается при смене персонажа, там счётчик и обнуляется.
        private int _tabFit;
        private const int TabFitLast = 3;

        private void FitTabs()
        {
            if (_tabs == null || _tabFit >= TabFitLast) return;
            float room = _tabs.resolvedStyle.width;
            if (room <= 1f) return;              // ещё не мерили — придёт следующим событием
            float need = 0f;
            foreach (var c in _tabs.Children())
            {
                float w = c.resolvedStyle.width;
                if (float.IsNaN(w) || w <= 0f) return;   // дети ещё без геометрии
                need += w + c.resolvedStyle.marginLeft + c.resolvedStyle.marginRight;
            }
            if (need <= room) return;
            ApplyTabFit(++_tabFit);
        }

        private void ApplyTabFit(int step)
        {
            float side = step >= 1 ? (step >= 2 ? 8f : 12f) : 18f;
            float gap = step >= 1 ? (step >= 2 ? 3f : 4f) : 6f;
            float font = step >= 3 ? 18f : (step >= 2 ? 20f : 22f);
            bool icons = step < 2;
            foreach (var c in _tabs.Children())
            {
                c.style.paddingLeft = side; c.style.paddingRight = side + 2f;
                c.style.marginLeft = gap; c.style.marginRight = gap;
                var lbl = c.Q<Label>("ax-label");
                if (lbl != null) lbl.style.fontSize = font;
                foreach (var n in new[] { "ax-ic-off", "ax-ic-on" })
                {
                    var ic = c.Q<VisualElement>(n);
                    if (ic == null) continue;
                    // Иконку прячем шириной, а не display: display у этих двух
                    // глифов означает «какой из них сейчас активен», и SelectTab
                    // им распоряжается — трогать его отсюда значило бы спорить.
                    ic.style.width = icons ? 24f : 0f;
                    ic.style.marginRight = icons ? 10f : 0f;
                    ic.style.overflow = icons ? Overflow.Visible : Overflow.Hidden;
                }
                var img = c.Q<VisualElement>("ax-art");
                if (img != null)
                {
                    img.style.width = icons ? 30f : 0f;
                    img.style.marginRight = icons ? 10f : 0f;
                }
            }
            LvnLog.Trace($"[lvn-wardrobe] разделы ужаты до ступени {step}: отступ {side}, кегль {font}, иконки {(icons ? "есть" : "нет")}");
        }

        private void SelectTab(string axis)
        {
            _tab = axis;
            // Камера хоста едет к зоне раздела: причёска — к голове, украшения
            // — к шее, платье — к корпусу; «Моё» — лёгкий наезд по центру.
            FireSectionFocus(_tab);
            foreach (var c in _tabs.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = (b.userData as string) == _tab;
                SkinButton(b, active);
                LvnChrome.Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
                // Дети пилюли (лейбл и пара глифов) красятся вручную — кнопочный
                // skin их не достаёт.
                var lbl = b.Q<Label>("ax-label");
                if (lbl != null) lbl.style.color = active ? _accentText : _text;
                var icOff = b.Q<VisualElement>("ax-ic-off");
                var icOn = b.Q<VisualElement>("ax-ic-on");
                if (icOff != null) icOff.style.display = active ? DisplayStyle.None : DisplayStyle.Flex;
                if (icOn != null) icOn.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            }

            var items = Items(_tab);
            if (items.Count == 0)
            {
                _itemName.text = "";
                RebuildStrip();
                RefreshConfirm();
                return;
            }
            if (!_index.ContainsKey(_tab))
            {
                // start the carousel on what's worn (previewed beats equipped;
                // ничего не выбирали — надет дефолт каталога, и карусель должна
                // открыться на нём, иначе показ примеряет чужое)
                var current = LvnCostumer.Chosen(_entity, _tab, _def?.defaults);
                int at = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].value == current) { at = i; break; }
                _index[_tab] = at;
            }
            RebuildStrip();
            ShowItem(); // also previews it, so the carousel and the actor agree
        }

        // ── лента карточек: второй руль карусели ─────────────────────────────
        /// <summary>Сборный таб «Моё»: купленные скины со всех осей одной
        /// лентой. Публичен: камера хоста узнаёт его в SectionFocus (лёгкий
        /// наезд вместо общего плана). Значение — из витрины гардероба: кадр
        /// для этой вкладки выбирается там же.</summary>
        public const string AllTab = LvnWardrobeStage.AllAxis;












        /// <summary>Лист живёт ВКЛАДКОЙ (уйти можно навбаром), а не модалкой.
        /// Тогда «Отменить» — не единственный выход, и его честно гасить,
        /// когда отменять нечего; в сюжетном листе он гаснуть не смеет.</summary>
        public bool TabMode;








        private List<LvnWardrobeItem> Items(string axis)
        {
            var list = new List<LvnWardrobeItem>();
            if (axis != null && _def?.wardrobe != null
                && _def.wardrobe.TryGetValue(axis, out var slot) && slot?.items != null)
            {
                // Съёмный слот (украшения) открывается пунктом «Нет»: снятие —
                // такой же выбор, с примеркой (NoneValue) и коммитом (Equip
                // трактует его как «снять»). Просьба Ильи 27.08.
                if (slot.removable == true)
                    list.Add(new LvnWardrobeItem { value = LvnWardrobe.NoneValue, name = LvnWords.Of("wardrobe.none", "None") });
                foreach (var it in slot.items)
                    if (it != null && !string.IsNullOrEmpty(it.value)
                        && (!OnlySeen || Encountered(axis, it.value)))
                        list.Add(it);
            }
            // ПОРЯДОК — АВТОРСКИЙ, И ОН НЕ ЗАВИСИТ ОТ НАДЕТОГО.
            //
            // Здесь стояла перестановка «текущее первым» (просьба 26.08: «не
            // скакать при входе»). Задачу она решала, но ценой: каталог
            // выглядел по-разному в зависимости от того, что на героине, — к
            // ленте нельзя было привыкнуть, а «Нет» на съёмной оси уезжало со
            // своего места, стоило что-нибудь надеть (репорт 01.09).
            //
            // «Видно надетое при входе» достигается дешевле и без побочки:
            // лента ДОВОЗИТ выбранную карточку в кадр (StyleStrip → ScrollTo) и
            // обводит её акцентом. Номер выбранного ищется по значению, а не по
            // месту, поэтому порядку он безразличен.
            return list;
        }

        // Part of the player's collection: seen along the way, bought (the
        // wallet remembers across reinstalls), or what they're wearing right now.
        private bool Encountered(string axis, string value) => Encountered(_entity, axis, value);

        private static bool Encountered(string entity, string axis, string value)
        {
            if (LvnWardrobe.IsSeen(entity, axis, value)) return true;
            // ContainsKey здесь НАРОЧНО, в отличие от проверки владения рядом:
            // вопрос не «есть сейчас», а «было когда-либо», и ключ с нулём —
            // законное свидетельство, что вещь у игрока была.
            if (LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(entity, axis, value))) return true;
            return LvnWardrobe.Equipped(entity).TryGetValue(axis, out var worn) && worn == value;
        }

        private LvnWardrobeItem Find(string axis, string value)
        {
            foreach (var it in Items(axis)) if (it.value == value) return it;
            return null;
        }

    }
}
