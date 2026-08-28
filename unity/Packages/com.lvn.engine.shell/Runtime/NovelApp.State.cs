using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СОСТОЯНИЕ ИСТОРИИ — часть <see cref="NovelApp"/>: переменные новеллы,
    /// их сохранение по областям, перенос между главами и сброс прогресса.
    ///
    /// <para>Тонкое место: у переменных две области — своя у каждой истории и
    /// общая на всех (`global.*`), — а сейв обязан пережить и смену главы, и
    /// перезапуск, и отсутствие сети.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        private readonly Dictionary<string, TitleVars> _titleVarsCache = new Dictionary<string, TitleVars>();

        private async Task<TitleVars> LoadTitleVarsAsync(LvnTitle title)
        {
            if (string.IsNullOrEmpty(title?.vars_url)) return null;
            if (_titleVarsCache.TryGetValue(title.id, out var hit)) return hit;
            TitleVars tv = null;
            try
            {
                var json = await _assets.Loader.DownloadScriptText(title.vars_url, destroyCancellationToken);
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                tv = new TitleVars
                {
                    game = root["game"] as Newtonsoft.Json.Linq.JObject,
                    chapter = root["chapter"] as Newtonsoft.Json.Linq.JObject,
                };
            }
            catch (Exception e)
            {
                // Declarations are an optimization, never a gate: chapters keep
                // playing on their own (older content carries inline defaults).
                Debug.LogWarning($"[novelapp] vars_url '{title.vars_url}' failed: {e.Message}");
            }
            _titleVarsCache[title.id] = tv; // cache the miss too — no refetch storm
            return tv;
        }

        // The next chapter by number, or null when this was the last one.
        private static LvnChapter NextChapterOf(LvnTitle title, LvnChapter current)
        {
            if (title?.seasons == null || current == null) return null;
            LvnChapter best = null;
            foreach (var s in title.seasons)
            {
                if (s?.chapters == null) continue;
                foreach (var c in s.chapters)
                {
                    if (c == null || c.number <= current.number) continue;
                    if (best == null || c.number < best.number) best = c;
                }
            }
            return best;
        }

        // Cross-chapter save routing: a slot taken in another chapter resolves to
        // its chapter by script url, fetches that script, plays it and restores —
        // all in place, while the shell's play-loop keeps driving whatever player
        // the stage currently holds. Wired into VnStage.CrossChapterLoader.
        private async Task<bool> CrossChapterLoadAsync(LvnSaveSlot slot)
        {
            var url = slot?.Snap?.ScriptUrl;
            if (string.IsNullOrEmpty(url) || Stage == null) return false;
            var (title, chapter) = FindChapterByScriptUrl(url);
            if (chapter == null)
            {
                Debug.LogWarning($"[novelapp] save points at unknown chapter: {url}");
                return false;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] cross-chapter fetch failed: {ex.Message}"); return false; }
            if (string.IsNullOrEmpty(json)) return false;

            Stage.ClearStage();
            Stage.Strings = await LoadCatalogAsync(url);
            Stage.SeedVars = await LoadScopedVarsAsync(title?.id);
            Stage.SetSaveContext(title?.id, chapter.id, url);
            Stage.Gallery = title?.gallery;
            Stage.EntryGate = null; // a save-load lands mid-scene — no entry choreography
            Stage.Play(json, warmIntroSpine: false); // the restore below advances
            if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                Stage.Player.Vars["player"] = _playerName;
            Stage.RestoreSnapshot(slot.Snap);
            EnterChapterContext(title ?? _currentTitle, chapter);
            _currentScriptJson = json;
            LvnProgress.SetCurrent(_currentTitle, chapter); // continue follows the jump
            Debug.Log($"[novelapp] loaded save into '{chapter.id}' (@{slot.Snap.Index})");
            return true;
        }

        // "Restart the whole expedition": wipe this title's persisted stats and
        // drop every save slot, then clear its reading progress/checkpoints so the
        // next play starts from chapter one, clean. The cross-novel `global` stats
        // are LEFT intact — they belong to the player, not this one expedition.
        // Wired into TitleDetailScreen.OnResetProgress.
        private async Task ResetTitleProgressAsync(LvnTitle title)
        {
            if (title == null) return;
            // LOCAL state first — a kill mid-network-await must not leave a
            // "continue" that resumes the middle of the novel with zeroed stats.
            foreach (var slot in new System.Collections.Generic.List<string>(LvnSaveStore.Slots(title.id).Keys))
                LvnSaveStore.Delete(title.id, slot);
            LvnProgress.ResetTitle(title.id);
            try { await _state.SaveVarsAsync(title.id, new Newtonsoft.Json.Linq.JObject(), default); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] stat wipe failed: {ex.Message}"); }
            Debug.Log($"[novelapp] restarted expedition '{title.id}' — stats & saves cleared");
            SyncProgressVault(); // the wipe is progress too — all homes agree
        }

        // Write a dotted path ("Wardrobe.mainCh_Clothes") into a seed JObject,
        // creating intermediate objects — mirrors the player's SetVar nesting.
        // Numeric strings store as numbers so conditions compare numerically.
        private static void SetVarPath(Newtonsoft.Json.Linq.JObject vars, string key, string value)
        {
            Newtonsoft.Json.Linq.JToken jv =
                long.TryParse(value, out var n) ? new Newtonsoft.Json.Linq.JValue(n)
                : double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? new Newtonsoft.Json.Linq.JValue(d)
                    : (Newtonsoft.Json.Linq.JToken)new Newtonsoft.Json.Linq.JValue(value);
            var parts = key.Split('.');
            var cur = vars;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(cur[parts[i]] is Newtonsoft.Json.Linq.JObject next))
                {
                    next = new Newtonsoft.Json.Linq.JObject();
                    cur[parts[i]] = next;
                }
                cur = next;
            }
            cur[parts[parts.Length - 1]] = jv;
        }

        private (LvnTitle title, LvnChapter chapter) FindChapterByScriptUrl(string scriptUrl)
        {
            if (_manifest?.titles == null) return (null, null);
            foreach (var t in _manifest.titles)
            {
                if (t?.seasons == null) continue;
                foreach (var s in t.seasons)
                {
                    if (s?.chapters == null) continue;
                    foreach (var c in s.chapters)
                        if (c != null && c.script_url == scriptUrl)
                            return (t, c);
                }
            }
            return (null, null);
        }

        // Load a title's stats plus the player's global stats, merged into one seed
        // (global stats land under the `global` var). Two blobs, one per scope.
        private async Task<Newtonsoft.Json.Linq.JObject> LoadScopedVarsAsync(string titleId)
        {
            var vars = await _state.LoadVarsAsync(titleId, default) ?? new Newtonsoft.Json.Linq.JObject();
            var global = await _state.LoadVarsAsync(GlobalScopeId, default);
            if (global != null && global.Count > 0) vars[GlobalVar] = global;
            return vars;
        }

        // Persist ending stats, splitting the `global` namespace out to its own
        // per-player blob so it survives beyond this novel.
        private async Task SaveScopedVarsAsync(string titleId, Newtonsoft.Json.Linq.JObject vars)
        {
            if (vars == null) return;
            if (vars[GlobalVar] is Newtonsoft.Json.Linq.JObject global)
            {
                vars = (Newtonsoft.Json.Linq.JObject)vars.DeepClone(); // don't mutate the caller's live vars
                vars.Remove(GlobalVar);
                await _state.SaveVarsAsync(GlobalScopeId, global, default);
            }
            await _state.SaveVarsAsync(titleId, vars, default);
        }

        // Snapshot the player's live variables as a JObject the state store persists.
        private static Newtonsoft.Json.Linq.JObject VarsToJObject(
            System.Collections.Generic.IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JToken> vars)
        {
            var jo = new Newtonsoft.Json.Linq.JObject();
            if (vars != null)
                foreach (var kv in vars)
                    jo[kv.Key] = kv.Value?.DeepClone();
            return jo;
        }

        // The vault sync: collect the bundle, write the atomic file home and
        // push the server backup (offline-first store queues it when offline).
        private void SyncProgressVault()
        {
            if (_manifest == null) return;
            try
            {
                var bundle = ProgressVault.Collect(_manifest);
                ProgressVault.WriteLocal(bundle);
                if (_state != null) LvnAsync.Fire(_state.SaveVarsAsync(ProgressVault.Scope, bundle, default), "SaveVars");
            }
            catch (Exception e) { Debug.LogWarning("[vault] sync failed: " + e.Message); }
        }
    }
}
