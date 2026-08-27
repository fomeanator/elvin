using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ИГРОК — часть <see cref="NovelApp"/>: кто он для сервера, что показывает
    /// его профиль (отношения, кошелёк, пройденное) и как он уходит навсегда.
    ///
    /// <para>Данные для профиля собираются из трёх мест сразу — статы новелл,
    /// кошелёк, прогресс, — и это единственная причина, по которой экран
    /// профиля вообще что-то знает о новеллах.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // The debug faucet's grant: credit the wallet (EarnAsync fires
        // LvnWallet.Changed — the shell's HUD pill updates itself) and
        // reconcile with the server so the balance survives restarts.
        private async Task GrantFaucetAsync(string currency, int amount)
        {
            await Lvn.Services.LvnWallet.EarnAsync(currency, amount, "debug_faucet");
            await Lvn.Services.LvnWallet.RefreshAsync();
        }

        // The save identity for /v1/state. An explicit UserId (an account) wins; else
        // a per-device id generated once and kept in PlayerPrefs.
        private string ResolveUserId()
        {
            if (!string.IsNullOrEmpty(UserId)) return UserId;
            // Double-homed identity: PlayerPrefs AND a plain file. The id is the
            // key to every server-side possession (wallet, stats, progress
            // backup) — a corrupted prefs blob must never orphan them.
            var idFile = System.IO.Path.Combine(Application.persistentDataPath, "lvn_user.id");
            var id = PlayerPrefs.GetString("lvn_user", "");
            if (string.IsNullOrEmpty(id))
            {
                try { if (System.IO.File.Exists(idFile)) id = System.IO.File.ReadAllText(idFile).Trim(); }
                catch { /* unreadable second home — fall through */ }
            }
            if (string.IsNullOrEmpty(id)) id = System.Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString("lvn_user", id);
            PlayerPrefs.Save();
            try { System.IO.File.WriteAllText(idFile, id); } catch { /* prefs copy still holds */ }
            return id;
        }

        // Seed the rich detail page with the real title (name/art/synopsis/cost),
        // then its player-facing stat vars, before showing it — so "Твои статы"
        // reads live numbers instead of the placeholder the screen falls back to
        // when nothing has seeded it. The stats fetch never blocks the open on a
        // slow/offline state store — it's best-effort, empty vars just read as 0.
        // Профиль без фейка (живой репорт): отношения с фаворитами — из
        // РЕАЛЬНЫХ статов. По каждому тайтлу с relationship-статами читаем
        // сохранённые переменные и превращаем в полосы «имя → доля от max».
        // Пустой прогресс честно прячет секцию — рисованных процентов нет.
        private async Task OpenProfileWithRelationsAsync()
        {
            var p = _shell?.Profile;
            if (p == null) return;
            var rel = new List<Lvn.UI.Screens.ProfileScreen.Relation>();
            var titles = _manifest?.titles;
            if (titles != null)
            {
                foreach (var t in titles)
                {
                    if (t?.stats == null || t.id == null) continue;
                    Newtonsoft.Json.Linq.JObject vars = null;
                    foreach (var s in t.stats)
                    {
                        if (s == null || !s.relationship || string.IsNullOrEmpty(s.key)) continue;
                        if (vars == null)
                        {
                            try { vars = await LoadScopedVarsAsync(t.id); }
                            catch { vars = new Newtonsoft.Json.Linq.JObject(); }
                        }
                        float val = 0f;
                        // Путь статы может не существовать или указывать на объект —
                // тогда стата просто показывает ноль, а не роняет экран.
                try { val = (float?)vars?.SelectToken(s.key) ?? 0f; } catch { }
                        if (val <= 0f) continue; // не начатые романы полку не занимают
                        float max = s.max > 0 ? s.max : 20f;
                        rel.Add(new Lvn.UI.Screens.ProfileScreen.Relation(
                            string.IsNullOrEmpty(s.label) ? s.key : s.label,
                            Mathf.Clamp01(val / max)));
                    }
                }
            }
            rel.Sort((a, b) => b.Affection.CompareTo(a.Affection));
            p.Relations = rel;
            // Честная цифра прогресса: пройденные главы по всем историям.
            int done = 0;
            if (titles != null)
                foreach (var t in titles)
                    if (t != null)
                        done += Mathf.Max(0, Mathf.Min(LvnProgress.Reached(t), t.ChaptersOf().Count));
            p.ChaptersDone = done;
            // Профиль — дом данных ИГРОКА: настоящие имя и ID (в экране зашиты
            // демо-заглушки), живой кошелёк, удаление аккаунта. Жалоба-ориентир:
            // «в настройках больше данных для профиля, чем в профиле».
            p.PlayerName = _playerName;
            var uid = Lvn.Services.LvnBackend.UserId;
            if (!string.IsNullOrEmpty(uid)) p.Uid = uid;
            p.Wallet = BuildWalletTiles();
            p.OnDeleteAccount = DeleteAccountAndForgetAsync;
            p.OnOpenSettings = () => LvnAsync.Fire(_shell.OpenSettingsAsync(), "OpenSettings");
            await _shell.TabGoTo(3); // вкладка ленты, не модалка
        }

        // Единственная правда о валютах игры: ui.browse.currencies, дефолт —
        // прежняя пара. И шапка хаба, и кошелёк профиля идут отсюда.
        private List<string> HubCurrencies()
        {
            var cfg = _manifest?.ui?.browse?.currencies;
            return cfg != null && cfg.Count > 0 ? cfg : new List<string> { "energy", "gold" };
        }

        // Плитки кошелька для профиля: те же валюты, что в шапке хаба, подписи
        // из ui.store.currency_names (данные, не хардкод).
        private List<Lvn.UI.Screens.ProfileScreen.Stat> BuildWalletTiles()
        {
            var tiles = new List<Lvn.UI.Screens.ProfileScreen.Stat>();
            var names = _manifest?.ui?.store?.currency_names;
            foreach (var cur in HubCurrencies())
            {
                string value = Lvn.Services.LvnWallet.Display(cur);
                string caption = names != null && names.TryGetValue(cur, out var n) && !string.IsNullOrEmpty(n)
                    ? n : cur;
                tiles.Add(new Lvn.UI.Screens.ProfileScreen.Stat(value, caption));
            }
            return tiles;
        }

        // «Удалить аккаунт»: сервер стирает учётку/кошелёк/сейвы (LvnBackend),
        // затем локальное забвение — прогресс и статы всех историй, имя,
        // пройденность воронки. Порядок важен: локальное трём только после
        // успешного ответа сервера, иначе отказ сети выглядел бы как удаление.
        private async Task<bool> DeleteAccountAndForgetAsync()
        {
            bool ok = await Lvn.Services.LvnBackend.DeleteAccountAsync();
            if (!ok) return false;
            var titles = _manifest?.titles;
            if (titles != null)
                foreach (var t in titles)
                    if (t != null)
                        try { await ResetTitleProgressAsync(t); }
                        catch (Exception e) { Debug.LogWarning($"[novelapp] wipe {t.id}: {e.Message}"); }
            LvnPrefs.PlayerName = "";
            LvnPrefs.IntroDone = false;
            LvnPrefs.SeenWelcome = false;
            _playerName = "";
            Debug.Log("[novelapp] аккаунт удалён — сервер и локальные данные стёрты");
            return true;
        }

        private async Task<bool> OpenDetailWithStatsAsync(LvnTitle t)
        {
            if (_shell.Detail != null)
            {
                // КАРТОЧКЕ ДАЮТ НОВЕЛЛУ, а не её разобранные поля: имя,
                // обложку, синопсис и цену она достаёт из неё сама. Раньше
                // здесь стояли четыре присваивания рядом с этой же строкой —
                // и держались на памяти того, кто их пишет.
                _shell.Detail.Title = t;
                _shell.Detail.OnResetProgress = ResetTitleProgressAsync;
                Newtonsoft.Json.Linq.JObject vars = null;
                if (t?.id != null)
                {
                    try { vars = await LoadScopedVarsAsync(t.id); }
                    catch (Exception e) { Debug.LogWarning($"[novelapp] stat vars load failed: {e.Message}"); }
                }
                _shell.Detail.StatVars = vars ?? new Newtonsoft.Json.Linq.JObject();
                _shell.Detail.Rebuild();
            }
            return await _shell.OpenDetailAsync();
        }
    }
}
