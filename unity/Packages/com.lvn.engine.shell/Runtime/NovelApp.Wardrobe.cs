using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ГАРДЕРОБ ИЗ ОБОЛОЧКИ — часть <see cref="NovelApp"/>: как лист примерки
    /// открывается из квик-меню сцены и из вкладки хаба, кого он одевает и
    /// откуда берёт ростер героинь.
    ///
    /// <para>Два входа в один лист, и у каждого свои условия: в сцене одевают
    /// того, кто на ней стоит, в меню — фаворита игрока; из главы лист обязан
    /// вернуть сцену как была, из хаба — оставить куклу витрины. Тема
    /// самостоятельная и путаная, и лежать ей отдельно.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        private async Task OpenWardrobeFromMenuAsync(VnStage stage)
        {
            var entity = ResolveMenuWardrobeEntity(stage);
            if (string.IsNullOrEmpty(entity)) return; // no dressable cast — nothing to open
            stage.CloseQuickMenu();
            // The story holds only because nothing advances it — block taps for
            // the sheet's whole life (a story-opened sheet gets this from Hold()).
            stage.InputBlocked = true;
            try
            {
                await ShowStorySheetAsync(entity,
                    onlySeen: _manifest?.ui?.wardrobe?.collection_only ?? true,
                    roster: BuildWardrobeRoster(entity));
            }
            finally { stage.InputBlocked = false; }
        }

        // The character pills of the always-open wardrobe: every dressable
        // entity, alias entities of the SAME character collapsed (they share
        // the exact set of story vars — Mira/demo_main/Главный_герой are one
        // heroine). The primary (resolved) character leads.
        private List<(string id, string name)> BuildWardrobeRoster(string primary)
        {
            var sprites = _manifest?.sprites;
            if (sprites == null) return null;
            var list = new List<(string id, string name)>();
            var sigs = new HashSet<string>();
            void TryAdd(string id)
            {
                if (string.IsNullOrEmpty(id) || !sprites.TryGetValue(id, out var d)
                    || d?.wardrobe == null || d.wardrobe.Count == 0) return;
                var vars = new List<string>();
                foreach (var kv in d.wardrobe)
                    if (!string.IsNullOrEmpty(kv.Value?.storyVar)) vars.Add(kv.Value.storyVar);
                vars.Sort();
                var sig = vars.Count > 0 ? string.Join("|", vars) : "id:" + id;
                if (!sigs.Add(sig)) return; // same character under another entity id
                // Имя персонажа — тоже подпись: игрок читает его рядом с
                // переведёнными репликами.
                list.Add((id, Lvn.Content.LvnWords.Name("actor", id, d.name)));
            }
            // Явный ростер (ui.wardrobe.characters) — закон: только персонажи
            // ЭТОЙ новеллы, в авторском порядке, героиня первой. Без него —
            // все одеваемые сущности каталога (наследие одиночных новелл).
            var explicitRoster = _manifest?.ui?.wardrobe?.characters;
            if (explicitRoster != null && explicitRoster.Count > 0)
            {
                foreach (var id in explicitRoster) TryAdd(id);
            }
            else
            {
                TryAdd(primary);
                foreach (var id in sprites.Keys) TryAdd(id);
            }
            // The protagonist's pill wears the name the PLAYER chose, not the
            // import's internal label (Mira/demo_main/Главный_герой are all her).
            // С явным ростером первая таблетка — ГГ по контракту списка.
            bool firstIsHeroine = list.Count > 0
                && (list[0].id == primary
                    || (explicitRoster != null && explicitRoster.Count > 0));
            if (firstIsHeroine && !string.IsNullOrEmpty(_playerName))
                list[0] = (list[0].id, _playerName);
            return list;
        }

        private bool AnyWardrobeEntity()
        {
            var sprites = _manifest?.sprites;
            if (sprites == null) return false;
            foreach (var kv in sprites)
                if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0) return true;
            return false;
        }

        // The HUB wardrobe: same sheet, same canvas — the hub cross-fades away,
        // the stage dresses itself with the last scene the player saw (or the
        // engine's dark), the hero steps on, the sheet fades in. Closing plays
        // it all back. ONE wardrobe everywhere; the old fullscreen screen died.
        private async Task OpenWardrobeFromHubAsync()
        {
            var stage = Stage;
            if (stage == null) return;
            var entity = ResolveMenuWardrobeEntity(stage);
            if (string.IsNullOrEmpty(entity)) return;
            var hub = _shell?.Hub;
            if (hub != null)
            {
                await ScreenFx.FadeAsync(hub, 1f, 0f, 0.25f, destroyCancellationToken);
                hub.style.display = DisplayStyle.None;
            }
            try
            {
                var bg = Lvn.UI.VnStage.LastSceneBgUrl;
                if (!string.IsNullOrEmpty(bg))
                    stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
                    { ["op"] = "bg", ["sprite_url"] = bg }, LvnSender.Wardrobe);
                await ShowStorySheetAsync(entity,
                    onlySeen: _manifest?.ui?.wardrobe?.collection_only ?? true,
                    roster: BuildWardrobeRoster(entity));
            }
            finally
            {
                if (hub != null)
                {
                    hub.style.display = DisplayStyle.Flex;
                    LvnAsync.Fire(ScreenFx.FadeAsync(hub, 0f, 1f, 0.25f, destroyCancellationToken), "Fade");
                }
            }
        }

        // Who the menu wardrobe dresses: the configured hero, else the one on
        // stage whose wardrobe writes story vars (the imported protagonist),
        // else anyone sensible with a wardrobe.
        private string ResolveMenuWardrobeEntity(VnStage stage)
        {
            var sprites = _manifest?.sprites;
            if (sprites == null || sprites.Count == 0) return null;
            bool HasWardrobe(string id) => !string.IsNullOrEmpty(id)
                && sprites.TryGetValue(id, out var d) && d?.wardrobe != null && d.wardrobe.Count > 0;
            bool WritesStory(string id)
            {
                if (!sprites.TryGetValue(id, out var d) || d?.wardrobe == null) return false;
                foreach (var slot in d.wardrobe.Values)
                    if (!string.IsNullOrEmpty(slot?.storyVar)) return true;
                return false;
            }

            var cfg = _manifest?.ui?.wardrobe;
            if (HasWardrobe(cfg?.entity)) return cfg.entity;
            var onStage = stage != null ? stage.ActorsOnStage() : new List<string>();
            foreach (var id in onStage) if (HasWardrobe(id) && WritesStory(id)) return id;
            foreach (var id in sprites.Keys) if (HasWardrobe(id) && WritesStory(id)) return id;
            foreach (var id in onStage) if (HasWardrobe(id)) return id;
            foreach (var id in sprites.Keys) if (HasWardrobe(id)) return id;
            return null;
        }

        private async Task ShowStorySheetAsync(string entity, bool onlySeen,
            List<(string id, string name)> roster = null)
        {
            if (_storySheet == null)
            {
                var ui = _manifest?.ui ?? new LvnUiConfig();
                _storySheet = new WardrobeSheet(ui.wardrobe, ui.dialogue, ui.choices, _assets);
                _storySheet.SetManifest(_manifest);
                _storySheet.OpenStore = () => _shell.OpenPackShopAsync();
                // Кнопки — из economy-конфига, как у энергетических ворот: жёсткий
                // англ. хардкод здесь светился игроку («а че у нас тут инглишь»).
                _storySheet.ConfirmTopUp = (title, msg) => _shell.ConfirmAsync(title, msg,
                    _manifest?.economy?.gate_buy ?? "Store",
                    _manifest?.economy?.gate_cancel ?? "Not now");
                _storySheet.Alert = (title, msg) => _shell.AlertAsync(title, msg);
                // Write the player's wardrobe pick back into the novel's story state
                // (nested, like the script's own `set`). LvnWardrobe.Equip and the
                // following ClearPreview already publish Changed and refresh the live
                // actor; starting a third refresh here made async sprite loads race and
                // produced a visible wardrobe snap.
                // «Во весь рост»: панель прячется, примерка продолжается.
                _storySheet.OnPeek = on => Stage?.SetPanelPeek(on);
                _storySheet.OnEquip = (ent, storyVar, value) =>
                {
                    var p = Stage?.Player;
                    if (p == null || string.IsNullOrEmpty(storyVar)) return;
                    Newtonsoft.Json.Linq.JToken jv =
                        long.TryParse(value, out var n) ? new Newtonsoft.Json.Linq.JValue(n)
                        : double.TryParse(value, out var d) ? new Newtonsoft.Json.Linq.JValue(d)
                        : (Newtonsoft.Json.Linq.JToken)new Newtonsoft.Json.Linq.JValue(value);
                    p.SetVar(storyVar, jv);
                };
            }
            _storySheet.OnlySeen = onlySeen; // shared instance — set on EVERY open
            _storySheet.SetRoster(roster);
            // Platform back while the sheet is up = the sheet's own cancel.
            var st0 = Stage;
            if (st0 != null) st0.PanelCancelRequested = () => _storySheet?.Hide();
            var st = Stage;
            // Who stood on stage BEFORE the fitting. The wardrobe temporarily
            // shows exactly one mannequin; this original cast returns at close.
            var wasOn = new HashSet<string>(st != null ? st.ActorsOnStage() : new List<string>());
            // Кого сцена НЕ помнит по сценарию: их гардероб выводит своим
            // манекеном, и после примерки о них надо забыть — вместе с местом
            // и размером, которые манекен принёс с собой.
            var borrowed = new HashSet<string>();
            void NoteBorrowed(string id)
            {
                if (st != null && !string.IsNullOrEmpty(id) && !st.RememberedByScript(id))
                    borrowed.Add(id);
            }
            NoteBorrowed(entity);
            _storySheet.OnCharacterPicked = (_, to) =>
            {
                if (st == null) return;
                NoteBorrowed(to);
                // ОТСТАВШИЙ ВЫБОР ОТМЕНЯЕТ САМА СЦЕНА — у неё для этого полоса
                // Хронометриста (WardrobeFocusLane): игрок жмёт таблетки
                // быстрее, чем грузятся слои, и приземлиться имеет право только
                // самый новый показ. Здесь оболочке остаётся её собственная
                // правда: открыт ли ещё лист и та ли на нём таблетка. Свой
                // счётчик поколений тут был ВТОРОЙ реализацией того же правила
                // — ровно тот случай, из-за которого и заводились роли.
                LvnAsync.Fire(st.FocusWardrobeActorAsync(to,
                    () => _storySheet != null && _storySheet.CurrentEntity == to),
                    "FocusWardrobeActor");
            };
            // Engine invariant: before the wardrobe becomes visible, remove every
            // other staged character and keep only the menu-selected mannequin.
            await st.FocusWardrobeActorAsync(entity);
            SeedWardrobeFromStoryVars(entity);
            var done = _storySheet.ShowAsync(entity);   // logic only — the host animates
            await st.ShowPanelAsync(_storySheet);       // dialogue and wardrobe cross-fade
            try { await done; }
            finally
            {
                // ЛИСТ ЗАКРЫТ — КУКЛА ВОЗВРАЩАЕТСЯ ИСТОРИИ. Пока он был открыт,
                // героиня принадлежала игроку: его примерку не вправе перебить
                // ни реплика, ни витрина. Забыть отпустить — значит оставить
                // историю без её же героя до истечения срока держания.
                st.ReleaseWardrobeFocus();
                OnWardrobeSection(null);                // глава продолжается общим планом
                var cur = _storySheet.CurrentEntity ?? entity;
                if (!wasOn.Contains(cur))
                    await st.HideActorTemporarilyAndWaitAsync(cur);
                foreach (var original in wasOn)
                    st.EnsureActorShown(original, fadeOnly: true);
                // Манекен, которого в сцене не было, уходит БЕЗ СЛЕДА: его
                // синтетическая команда (центр, 0.92×1.06) липкая, и следующая
                // авторская без position приклеилась бы к ней — героиня так и
                // осталась бы стоять по центру до конца главы.
                foreach (var guest in borrowed)
                    if (!wasOn.Contains(guest)) st.ForgetActor(guest);
                await st.HidePanelAsync();              // frame leaves, dialogue returns
            }
        }

        // The in-story sheet decides "what's worn" from LvnWardrobe's OWN equip
        // registry (session state / restored save) — it has no idea the story's
        // {Wardrobe.*} var might already hold a different value (the chapter's
        // own default `set`, or a scene-forced costume change). Left unsynced,
        // BuildFor sees no match for that axis and jumps the preview to the
        // list's first item — a visible flash to a random outfit right as the
        // sheet opens. Sync every axis with a storyVar from the CURRENT var
        // value first, so the sheet's own initial pick already matches the
        // actor standing on stage.
        private void SeedWardrobeFromStoryVars(string entity)
        {
            var p = Stage?.Player;
            var wardrobe = _manifest?.sprites != null && _manifest.sprites.TryGetValue(entity, out var def)
                ? def?.wardrobe : null;
            if (p == null || wardrobe == null) return;
            // Обратная сторона обряда — у СВЯЗНОГО.
            Lvn.UI.LvnWardrobeSync.FromVars(entity, wardrobe, name => p.GetVar(name)?.ToString());
        }
    }
}
