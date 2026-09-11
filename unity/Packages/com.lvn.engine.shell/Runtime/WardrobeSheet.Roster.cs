using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КОГО ОДЕВАЕМ — список персонажей гардероба и переключение между ними.
    ///
    /// <para>Гардероб открывается и на героине из меню, и на том, кто стоит в
    /// текущей сцене. Список — это не украшение: пока его не было, «одеть
    /// другого» означало закрыть лист и открыть его иначе, и половина
    /// персонажей была недоступна вовсе.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        private List<(string id, string name)> _roster;

        /// <summary>Give the sheet a character roster (menu/hub mode). Null or a
        /// single entry hides the pills. Call before ShowAsync — cleared state
        /// persists on the shared instance otherwise.</summary>
        public void SetRoster(List<(string id, string name)> roster) => _roster = roster;

        private void RebuildRoster()
        {
            if (_rosterRow == null) return;
            _rosterRow.Clear();
            int shown = 0;
            if (_roster != null && _roster.Count > 1)
            {
                foreach (var (id, name) in _roster)
                {
                    // В МОЕЙ КОЛЛЕКЦИИ РОСТЕР НЕ РЕДЕЕТ. Иначе вход в неё мог
                    // оставить один кафель, ряд бы скрылся (показываем от двух)
                    // — и выйти обратно стало бы нечем.
                    if (OnlySeen && !_myCollection && id != _entity && !HasAnyCollected(id)) continue;
                    _rosterRow.Add(RosterTile(id, name, id == _entity));
                    shown++;
                }
            }
            _rosterRow.style.display = shown > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Размер лица в ростере. Пилюля с одним словом (было) не отвечала на
        // вопрос «кто это»: игрок узнаёт героя в лицо, а не по подписи.
        private const float RosterIcon = 128f;
        /// <summary>…но полка у него не бесконечная: сверху навбар, снизу лист.
        /// Когда лист поднялся над нарисованным нижним меню, зазор ужался, и
        /// колонка в полный размер полезла ЗА него — героев накрывало
        /// карточками («вот гардероб как сплющивает» — Илья 08.09). Лицо
        /// ужимается под полку, как ужимаются разделы (ApplyTabFit).</summary>
        private float _rosterIcon = RosterIcon;

        /// <summary>Уместить колонку героев в полку заданной высоты. Зовёт
        /// раскладка (LayoutEmotions) — она и знает, сколько места осталось
        /// между навбаром и листом.</summary>
        public void FitRoster(float height)
        {
            if (_rosterRow == null || _roster == null) return;
            int count = 0;
            foreach (var (id, _) in _roster)
            {
                if (OnlySeen && !_myCollection && id != _entity && !HasAnyCollected(id)) continue;
                count++;
            }
            if (count <= 1) return;
            // ПЛИТКИ НЕ УЖИМАЕМ — КОЛОНКА ЛИСТАЕТСЯ. Ужатие под полку было
            // компромиссом на троих и резало третьего по низу; с героиней и
            // пятью фаворитами оно бессмысленно. Лицо одного размера, лишнее
            // за полкой достаётся прокруткой (ScrollView в WardrobeSheet).
            const float Chrome = 46f;
            float need = count * (RosterIcon + Chrome);
            Lvn.LvnLog.Trace($"[lvn-wardrobe] герои: {count} шт. по {RosterIcon:0}, нужно {need:0}, "
                           + $"полка {height:0} → {(need > height ? "листаем" : "влезают")}");
            if (Mathf.Abs(RosterIcon - _rosterIcon) < 2f) return;
            _rosterIcon = RosterIcon;
            RebuildRoster();
        }

        private VisualElement RosterTile(string id, string catalogName, bool active)
        {
            var pid = id;
            var b = new Button(() => SwitchTo(pid));
            b.style.flexDirection = FlexDirection.Column;
            b.style.alignItems = Align.Center;
            b.style.width = _rosterIcon + 24f;   // имени хватает на одну строку
            LvnAir.MarginX(b, 0);
            b.style.marginBottom = LvnTokens.Space2;
            LvnAir.Pad(b, LvnTokens.Tight);
            // ВЫБРАННЫЙ ГЕРОЙ — ПОДЛОЖКА, А НЕ ЗАЛИВКА. Плитка красилась
            // акцентом целиком, а подпись остаётся светлой: у темы «хроно»
            // акцент — яркий голубой, и белое имя на нём не читалось
            // («цвет у героев сделай не такой яркий, а то имя не прочесть» —
            // Илья 08.09). Кто выбран, и так говорит грань акцентом; плитке
            // хватает его следа, приглушённого до фона панели.
            SkinButton(b, accent: false);
            if (active)
                b.style.backgroundColor = Color.Lerp(_accent, LvnTokens.PanelBg, 0.7f);
            LvnStyler.Chosen(b, active, _accent);

            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.style.width = _rosterIcon;
            icon.style.height = _rosterIcon;
            icon.style.flexShrink = 0;
            icon.style.overflow = Overflow.Hidden;
            icon.style.backgroundColor = UiColor.WithAlpha(LvnTokens.PanelBg, 0.9f);
            LvnChrome.Circle(icon, _rosterIcon);   // круг знает свою половину сам
            b.Add(icon);
            FillRosterIcon(icon, pid);

            // Имя ЧИТАЕМОЕ: прежний TextXs в пилюле шириной в слово обрезал
            // длинные имена, а перенос был запрещён. Подпись знает свой
            // источник — при смене языка и переименовании её перечитает дом.
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => RosterName(pid, catalogName));
            lbl.style.color = _text;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            // ИМЯ В ОДНУ СТРОКУ. Перенос «Виктори/я» делал плитку выше, чем
            // считал FitRoster, и столбик резал нижнего героя («боковушки
            // срезаются» — Илья 08.09). Длинное имя укорачивается многоточием.
            lbl.style.whiteSpace = WhiteSpace.NoWrap;
            lbl.style.overflow = Overflow.Hidden;
            lbl.style.textOverflow = TextOverflow.Ellipsis;
            // ШИРИНА — ПО ПЛИТКЕ, А НЕ ПО ЛИЦУ, и длинное имя ужимается кеглем
            // раньше, чем многоточием: «Виктор…» на плитке героини (TR-62) —
            // это подпись, которая не называет. Сокращать имя человека можно
            // только тогда, когда его уже не уместить никаким размером.
            lbl.style.maxWidth = _rosterIcon + 20f;
            var shown = RosterName(pid, catalogName);
            lbl.style.fontSize = (shown != null && shown.Length > 9)
                ? LvnTokens.TextXs : LvnTokens.TextSm;
            lbl.style.marginTop = LvnTokens.Tight;
            b.Add(lbl);
            StageRosterTile(b, lbl, active);
            return b;
        }

        /// <summary>Как зовут персонажа в ростере. ИМЯ ГГ — ИЗ ПРОФИЛЯ: игрок
        /// задал его сам, и авторское имя в гардеробе значит «это не ты».
        /// Кто именно ГГ, говорит МАНИФЕСТ (<c>ui.wardrobe.entity</c>) — величина
        /// авторская и постоянная. Брать «любимца меню» нельзя: его игрок
        /// переставляет сам, и имя переехало бы на другого героя.</summary>
        private string RosterName(string id, string catalogName)
        {
            var own = _manifest?.ui?.wardrobe?.entity;
            if (!string.IsNullOrEmpty(own) && id == own)
            {
                var player = Lvn.UI.LvnPlayerName.Display;
                if (!string.IsNullOrEmpty(player)) return player;
            }
            return Lvn.Content.LvnWords.Name("actor", id, catalogName);
        }

        // Лицо героя в кружке: слои его ТЕКУЩЕГО облика (что надето, то и видно
        // — иначе ростер спорил бы с куклой), сложенные стопкой и приближенные
        // к голове. Без зума в кружок попадал бы живот: слои нарисованы в полный
        // рост. Нет каталога или слоёв — остаётся плашка, подпись всё скажет.
        private void FillRosterIcon(VisualElement icon, string id)
        {
            if (_manifest?.sprites == null
                || !_manifest.sprites.TryGetValue(id, out var def) || def == null) return;
            var axes = new Dictionary<string, string>();
            if (def.wardrobe != null)
                foreach (var kv in def.wardrobe)
                {
                    var v = LvnCostumer.Committed(id, kv.Key, def.defaults);
                    if (!string.IsNullOrEmpty(v)) axes[kv.Key] = v;
                }
            var urls = new SpriteCatalog(_manifest.sprites).Resolve(id, axes);
            if (urls == null || urls.Count == 0) return;
            // КРУТИЛКА, ПОКА ЛИЦО ЕДЕТ. Пустой кружок читается как «героя нет»;
            // вращение говорит «сейчас будет». Снимаем, когда приехал последний
            // слой: пропади она раньше, лицо доскладывалось бы на глазах.
            var spin = Spinner();
            icon.Add(spin);
            var loads = new List<Task>();
            // ОКНО НА ГОЛОВУ — ОБЩЕЕ С ПОРТРЕТОМ ПРОФИЛЯ (TR-68): кружок здесь
            // и кружок в шапке показывают одно лицо, и своими числами они
            // разъехались бы при первой же правке.
            Lvn.UI.LvnHeroPortrait.Window(_manifest, out float zoom, out float ay);
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                var layer = new VisualElement { pickingMode = PickingMode.Ignore };
                layer.style.position = Position.Absolute;
                layer.style.width = Length.Percent(zoom * 100f);
                layer.style.height = Length.Percent(zoom * 100f);
                layer.style.left = Length.Percent(50f - zoom * 100f * 0.5f);
                layer.style.top = Length.Percent(50f - zoom * 100f * ay);
                LvnPicture.Fit(layer, cover: false);
                icon.Add(layer);
                loads.Add(LvnPicture.AssignAsync(layer, url, _assets));
            }
            LvnAsync.Fire(DropWhenLoaded(spin, loads), "RosterIcon");
        }

        private static async Task DropWhenLoaded(VisualElement spin, List<Task> loads)
        {
            try { await Task.WhenAll(loads); }
            catch { }   // ждём только КОНЦА загрузок: крутилка обязана уйти в любом исходе
            spin?.RemoveFromHierarchy();
        }

        // Кольцо с одной светлой дугой, вращаемое расписанием элемента.
        // Отдельного дома под крутилку в движке нет: LvnBusy умеет только
        // кнопки, а вертеть надо ровно пока едет картинка. Расписание
        // умирает вместе с элементом, гасить вручную нечего.
        private VisualElement Spinner()
        {
            const float d = 40f;
            var ring = new VisualElement { pickingMode = PickingMode.Ignore };
            ring.style.position = Position.Absolute;
            ring.style.left = Length.Percent(50f); ring.style.top = Length.Percent(50f);
            ring.style.marginLeft = -d * 0.5f; ring.style.marginTop = -d * 0.5f;
            LvnChrome.Circle(ring, d);   // размер и круглые углы разом
            var faint = UiColor.WithAlpha(_text, 0.16f);
            // НАРОЧНО по сторонам: кольцо ожидания — одна грань акцентом, три бледные, и его вертят.
            ring.style.borderTopWidth = ring.style.borderBottomWidth = 3f;   // НАРОЧНО
            ring.style.borderLeftWidth = ring.style.borderRightWidth = 3f;   // НАРОЧНО
            ring.style.borderTopColor = _accent;                             // НАРОЧНО
            ring.style.borderRightColor = faint;                             // НАРОЧНО
            ring.style.borderBottomColor = faint;                            // НАРОЧНО
            ring.style.borderLeftColor = faint;                              // НАРОЧНО
            float a = 0f;
            ring.schedule.Execute(() =>
            {
                a = (a + 12f) % 360f;
                ring.style.rotate = new Rotate(new Angle(a, AngleUnit.Degree));
            }).Every(16);
            return ring;
        }

        /// <summary>«МОЯ КОЛЛЕКЦИЯ» — нажатие на УЖЕ выбранного героя. Раньше
        /// такой тап не делал НИЧЕГО: <see cref="SwitchTo"/> выходил на первой
        /// строке, и палец бил в пустоту. Витрина показывает весь каталог
        /// скинов, коллекция — только то, что у игрока есть; это и есть «моё».
        /// Повторный тап возвращает витрину.</summary>
        private bool _myCollection;

        private void ToggleMyCollection()
        {
            _myCollection = !_myCollection;
            OnlySeen = _myCollection;
            BuildFor(_entity);   // фильтр разделов и лент меняется целиком
            RefreshBalances();
        }

        private void SwitchTo(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (id == _entity) { ToggleMyCollection(); return; }
            var from = _entity;
            LvnWardrobe.ClearPreview(from); // the outgoing look blends back
            OnCharacterPicked?.Invoke(from, id);
            BuildFor(id);
            RefreshBalances();
        }

        // Does this entity have anything to show in collection mode? Mirrors
        // Items()' Encountered rule without switching the sheet to it.
        private bool HasAnyCollected(string id)
        {
            if (_manifest?.sprites == null || !_manifest.sprites.TryGetValue(id, out var d)
                || d?.wardrobe == null) return false;
            foreach (var kv in d.wardrobe)
                if (kv.Value?.items != null)
                    foreach (var it in kv.Value.items)
                        if (it != null && !string.IsNullOrEmpty(it.value) && Encountered(id, kv.Key, it.value))
                            return true;
            return false;
        }
    }
}
