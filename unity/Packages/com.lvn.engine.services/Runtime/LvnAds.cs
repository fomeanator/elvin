using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Rewarded ads — currency for a completed video. The engine ships no ad
    /// SDK: the host installs its mediator (CAS.AI etc.) and plugs
    /// <see cref="ShowRewarded"/>; the SERVER owns the reward amounts and the
    /// per-user daily caps (content/ads.json → /v1/ads/reward), so a hacked
    /// client can at most watch its own quota. No hook — no ad surfaces
    /// anywhere, the store screen simply doesn't render the free cards.
    /// </summary>
    public static class LvnAds
    {
        /// <summary>Host hook: show a rewarded ad for a placement, resolve
        /// true when the user EARNED the reward (watched to completion).
        /// CAS.AI example: wrap MediationManager.ShowAd + OnAdCompleted.</summary>
        public static Func<string, Task<bool>> ShowRewarded;

        public static bool Available => ShowRewarded != null;

        /// <summary>One rewarded placement as the server advertises it.</summary>
        public sealed class Placement
        {
            public string Id;
            public string Currency;
            public long Amount;
            public int DailyCap;

            /// <summary>Сколько показов осталось в текущем цикле; -1 — цикла
            /// нет. Считает СЕРВЕР: клиент, ведущий свой счётчик, разошёлся бы
            /// с ним на первом же перезапуске игры.</summary>
            public int Left = -1;

            /// <summary>Сколько показов в полном цикле — «2 из ТРЁХ». Без него
            /// подпись знала бы только половину.</summary>
            public int Charges;

            /// <summary>Unix-время, когда заряды вернутся; 0 — они есть.</summary>
            public long ReadyAtUnix;

            /// <summary>Сколько ждать сейчас, в секундах. Ноль — можно
            /// смотреть.</summary>
            public long WaitSeconds
            {
                get
                {
                    if (ReadyAtUnix <= 0) return 0;
                    long left = ReadyAtUnix - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return left > 0 ? left : 0;
                }
            }

            /// <summary>Можно ли смотреть прямо сейчас.</summary>
            public bool Ready => Left != 0 && WaitSeconds <= 0;
        }

        /// <summary>The server's rewarded placements (GET /v1/ads/catalog).
        /// Null offline.</summary>
        public static async Task<List<Placement>> GetCatalogAsync()
        {
            EnsureOwner();
            string owner = LvnKeep.Owner;
            int request = ++_catalogRevision;
            var (code, body) = await LvnBackend.GetAsync("/v1/ads/catalog");
            if (owner != LvnKeep.Owner || request != _catalogRevision) return null;
            var answer = LvnBackend.Json(code, body);
            if (answer == null) return null;
            try
            {
                var list = new List<Placement>();
                foreach (var t in answer["placements"] as JArray ?? new JArray())
                {
                    if (!(t is JObject o)) continue;
                    list.Add(new Placement
                    {
                        Id = (string)o["placement"] ?? "",
                        Currency = (string)o["currency"] ?? "",
                        Amount = (long?)o["amount"] ?? 0,
                        DailyCap = (int?)o["daily_cap"] ?? 0,
                        Charges = (int?)o["charges"] ?? 0,
                        Left = (int?)o["left"] ?? -1,
                        ReadyAtUnix = (long?)o["ready_at"] ?? 0,
                    });
                }
                NoteCatalog(list);
                return list;
            }
            catch { return null; }
        }

        /// <summary>Show the ad, then claim the SERVER-side reward and refresh
        /// the wallet mirror. False on cancel/cap/offline.</summary>
        public static async Task<bool> WatchAndRewardAsync(string placement)
        {
            if (ShowRewarded == null || string.IsNullOrEmpty(placement)) return false;
            // The native SDK supports one video at a time, even when two UI
            // surfaces receive taps before either has finished opening.
            if (System.Threading.Interlocked.CompareExchange(ref _watching, 1, 0) != 0) return false;
            string owner = LvnKeep.Owner;
            try
            {
                var catalog = await GetCatalogAsync();
                var offer = catalog?.Find(p => p.Id == placement);
                if (owner != LvnKeep.Owner || offer == null || !offer.Ready) return false;
                bool completed;
                try { completed = await ShowRewarded(placement); }
                catch { return false; }
                if (!completed || owner != LvnKeep.Owner) return false;

                var (code, reply) = await LvnBackend.PostAsync("/v1/ads/reward",
                    new JObject { ["placement"] = placement }.ToString());
                if (owner != LvnKeep.Owner) return false;
                var result = LvnBackend.Json(code, reply);
                bool granted = result != null && LvnBool.Flag(result["granted"]);
                LvnAnalytics.Track(granted ? LvnEvents.AdReward : LvnEvents.AdRewardFail, ("placement", placement));
                NoteState(placement, reply);
                if (!granted) return false;
                await LvnWallet.RefreshAsync();
                return true;
            }
            finally { System.Threading.Interlocked.Exchange(ref _watching, 0); }
        }

        private static int _watching;
        private static Task _dueRefresh;
        private static float _lastDueRefresh = -1000f;

        /// <summary>Recheck an expired recharge with the server. Displaying 0:00
        /// alone cannot replenish charges; throttle failed/offline retries.</summary>
        public static Task RefreshDueAsync()
        {
            EnsureOwner();
            if (_dueRefresh != null && !_dueRefresh.IsCompleted) return _dueRefresh;
            if (LvnClock.Wall() - _lastDueRefresh < 5f) return Task.CompletedTask;
            foreach (var p in _state.Values)
            {
                if (p.Left != 0 || p.ReadyAtUnix <= 0 || p.WaitSeconds > 0) continue;
                _lastDueRefresh = LvnClock.Wall();
                return _dueRefresh = GetCatalogAsync();
            }
            return Task.CompletedTask;
        }

        /// <summary>Состояние зарядов, каким его назвал сервер в последнем
        /// ответе. Кнопка спрашивает ЗДЕСЬ: свой счётчик у неё разошёлся бы с
        /// сервером на первом перезапуске.</summary>
        public static Placement StateOf(string placement)
        {
            EnsureOwner();
            return !string.IsNullOrEmpty(placement) && _state.TryGetValue(placement, out var p) ? p : null;
        }

        /// <summary>Состояние обновилось — кнопке пора перерисоваться.</summary>
        public static event Action Changed;

        private static readonly Dictionary<string, Placement> _state = new Dictionary<string, Placement>();
        private static string _owner;
        private static int _catalogRevision;

        private static void EnsureOwner()
        {
            if (_owner == LvnKeep.Owner) return;
            _owner = LvnKeep.Owner;
            _state.Clear();
            ++_catalogRevision;
            _lastDueRefresh = -1000f;
            _dueRefresh = null;
        }

        internal static void NoteState(string placement, string replyJson)
        {
            EnsureOwner();
            if (string.IsNullOrEmpty(placement)) return;
            try
            {
                var o = string.IsNullOrEmpty(replyJson) ? null : JObject.Parse(replyJson);
                if (o == null) return;
                ++_catalogRevision;
                if (!_state.TryGetValue(placement, out var p))
                    _state[placement] = p = new Placement { Id = placement };
                if (o["left"] != null) p.Left = (int)o["left"];
                if (o["charges"] != null) p.Charges = (int)o["charges"];
                if (o["ready_at"] != null) p.ReadyAtUnix = (long)o["ready_at"];
                if (o["currency"] != null) p.Currency = (string)o["currency"];
                if (o["amount"] != null) p.Amount = (long)o["amount"];
                NotifyChanged();
            }
            catch { /* ответ не разобрался — кнопка останется с прежним состоянием */ }
        }

        /// <summary>Запомнить каталог: у кнопки должно быть состояние ДО первого
        /// показа, иначе она рисует себя доступной и обманывает.</summary>
        public static void NoteCatalog(List<Placement> catalog)
        {
            EnsureOwner();
            if (catalog == null) return;
            foreach (var p in catalog)
                if (!string.IsNullOrEmpty(p?.Id)) _state[p.Id] = p;
            NotifyChanged();
        }

        private static void NotifyChanged()
        {
            if (Changed == null) return;
            foreach (Action observer in Changed.GetInvocationList())
                try { observer(); }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[lvn-ads] observer: " + e.Message); }
        }
    }
}
