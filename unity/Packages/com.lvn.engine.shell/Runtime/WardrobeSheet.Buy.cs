using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВЫБОР И ПОКУПКА — часть <see cref="WardrobeSheet"/>: что предлагает
    /// кнопка подтверждения, чем игрок уже владеет, что он примерил и что из
    /// этого коммитится.
    ///
    /// <para>Здесь легче всего ошибиться, и ошибались: покупка молча надевала
    /// вещь, кнопки горели над пустой примеркой, правило «предмет куплен» жило
    /// в пяти копиях. Тема стоит того, чтобы читаться целиком.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        /// <summary>Подпись кнопки подтверждения. <paramref name="currency"/> —
        /// валюта цены: её значок встаёт справа от числа вместо служебного имени
        /// («Купить: 20 crystals» игроку ничего не говорит).</summary>
        private void SetConfirmText(string text, string currency = null)
        {
            if (_confirmLabel == null) { _confirm.text = text; return; }
            _confirmLabel.text = text;
            if (_confirmCoin != null) { _confirmCoin.RemoveFromHierarchy(); _confirmCoin = null; }
            if (string.IsNullOrEmpty(currency)) return;
            _confirmCoin = LvnIcons.MakeCurrency(currency, 26f);
            _confirmCoin.style.marginLeft = 8;
            _confirmRow.Add(_confirmCoin);
        }

        /// <summary>Что сейчас предлагает кнопка подтверждения («Выбрать»,
        /// «Купить: 300»). Своё свойство, потому что подпись составная: у
        /// Button.text её больше нет — там дочерняя строка со значком валюты.</summary>
        public string ConfirmCaption => _confirmLabel != null ? _confirmLabel.text : _confirm?.text;

        private string ConfirmText => ConfirmCaption;

        // Кошелёк сменился (покупка/начисление) — бейджи цен на карточках
        // обязаны пересчитаться: купленный скин тут же теряет ценник.
        private void OnWalletChanged() { RefreshBalances(); RebuildStrip(); RefreshConfirm(); }

        private bool IsOwnedIn(string axis, LvnWardrobeItem item) =>
            item == null || item.price <= 0
            || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, axis, item.value));

        // Предмет уже принадлежит игроку: бесплатный или лежит в инвентаре
        // кошелька. Правило одно и живёт в IsOwnedIn; здесь — «на активной
        // вкладке». Тело этой проверки было списано в пяти местах листа, и
        // любая правка (скидка, подарок, отладочная выдача) требовала найти
        // все пять.
        private bool IsOwned(LvnWardrobeItem item) => IsOwnedIn(_tab, item);

        // Что кнопке предлагать купить: предмет карусели, а если он свой —
        // непринадлежащая примерка из ПОДНАСТРОЕК раздела (цвет волос тоже
        // товар, но своей карусели у него нет).
        private (string axis, LvnWardrobeItem item) PendingBuy()
        {
            var cur = CurrentItem();
            if (cur != null && !IsOwned(cur)) return (_tab, cur);
            if (_tab != null && _tab != AllTab)
                foreach (var sub in SubAxesOf(_tab))
                {
                    LvnWardrobe.Previewed(_entity).TryGetValue(sub, out var v);
                    if (v == null) continue;
                    var it = Find(sub, v);
                    if (it != null && !IsOwnedIn(sub, it)) return (sub, it);
                }
            return (null, null);
        }

        // Есть ли НЕСОХРАНЁННАЯ примерка: превью, отличающееся от надетого, по
        // ГАРДЕРОБНОЙ оси. Лицо примеряется мимо гардероба («Выбрать» его не
        // коммитит) и в счёт не идёт, иначе тап по эмоции оживлял бы кнопки.
        // Оси, которые лист примерил САМ при открытии (надетое «не из этого
        // листа»): ось → что примерено. Пока превью совпадает с этим значением,
        // выбор игрока в нём не участвует, и кнопкам нечего подтверждать.
        private readonly Dictionary<string, string> _autoDressed = new Dictionary<string, string>();

        private bool HasPendingLook()
        {
            if (_def?.wardrobe == null) return false;
            foreach (var kv in LvnWardrobe.Previewed(_entity))
            {
                if (!_def.wardrobe.ContainsKey(kv.Key)) continue;
                if (_autoDressed.TryGetValue(kv.Key, out var auto) && auto == kv.Value) continue;
                var worn = LvnCostumer.Committed(_entity, kv.Key, _def.defaults);
                // «Ничего не надето» и примерка пункта «Нет» — одно состояние:
                // подтверждать в нём нечего, и кнопки не должны оживать.
                if (Bare(kv.Value) && Bare(worn)) continue;
                if (kv.Value != worn) return true;
            }
            return false;
        }

        private static bool Bare(string value) => LvnCostumer.Bare(value);

        private void RefreshConfirm()
        {
            var (axis, item) = PendingBuy();
            // Кнопки честны, как стрелки (Илья 26.08): «Выбрать» живёт, пока
            // есть что купить или что применить, «Отменить» — пока есть что
            // отменять. Живая кнопка, которая ничего не сделает, врёт.
            bool pending = HasPendingLook();
            if (!_buying) _confirm.SetEnabled(item != null || pending);
            _cancel?.SetEnabled(pending || !TabMode);
            _confirm.style.opacity = _confirm.enabledSelf ? 1f : 0.4f;
            if (_cancel != null) _cancel.style.opacity = _cancel.enabledSelf ? 1f : 0.4f;

            if (item != null)
            {
                // Слово вместо значка — только если новелла сама его назвала
                // (currency_label). Иначе игрок читал внутренний идентификатор.
                bool named = !string.IsNullOrEmpty(_cfg.currency_label);
                SetConfirmText(named
                        ? $"{_cfg.buy_text ?? "Buy"}:  {item.price:N0} {_cfg.currency_label}"
                        : $"{_cfg.buy_text ?? "Buy"}:  {item.price:N0}",
                    named ? null : item.currency);
                LvnLog.Trace($"[lvn-wardrobe] sheet buy offer {_entity}.{axis}='{item.value}' " +
                          $"{item.price} {item.currency}, have {(LvnWallet.Balances.TryGetValue(item.currency ?? "", out var b) ? b : 0)}");
            }
            else SetConfirmText(_cfg.confirm_text ?? "Choose");
        }

        internal async Task ConfirmAsync()
        {
            if (_buying) return;
            _buying = true;
            var label = ConfirmText;
            _confirm.SetEnabled(false);
            SetConfirmText("…");
            try
            {
                // Re-sync the wallet FIRST: ownership decisions below must run
                // against the server's inventory, not a stale mirror — otherwise
                // an already-bought item could be charged twice.
                await LvnWallet.RefreshAsync();

                var (buyAxis, buyItem) = PendingBuy();
                if (buyItem != null)
                {
                    await BuyCurrentAsync(buyAxis, buyItem);
                    return; // the sheet stays open — buying is not choosing
                }

                // CHOOSE: commit every previewed piece the player owns (or that
                // is free). An unowned priced piece browsed on another tab was
                // never bought — snap it back rather than silently charging.
                var previewed = new Dictionary<string, string>();
                foreach (var kv in LvnWardrobe.Previewed(_entity)) previewed[kv.Key] = kv.Value;
                LvnLog.Trace($"[lvn-wardrobe] sheet CHOOSE: previewed [{string.Join(", ", ToPairs(previewed))}], " +
                          $"inventory [{string.Join(", ", LvnWallet.Inventory.Keys)}]");
                foreach (var kv in previewed)
                {
                    var item = Find(kv.Key, kv.Value);
                    if (item == null)
                    {
                        Debug.LogWarning($"[lvn-wardrobe] previewed {kv.Key}='{kv.Value}' has NO catalog item — skipped");
                        continue;
                    }
                    bool owned = IsOwnedIn(kv.Key, item);
                    if (!owned)
                    {
                        LvnWardrobe.Preview(_entity, kv.Key, null); // browsed, not bought
                        continue;
                    }
                    LvnWardrobe.Equip(_entity, kv.Key, kv.Value);
                    // Write the pick back into the novel's story state (nested var) so
                    // its downstream logic reads the choice — only for axes bound to one.
                    string sv = _def?.wardrobe != null && _def.wardrobe.TryGetValue(kv.Key, out var slot)
                        ? slot.storyVar : null;
                    if (!string.IsNullOrEmpty(sv)) OnEquip?.Invoke(_entity, sv, kv.Value);
                }
                LvnLog.Trace($"[lvn-wardrobe] sheet choose DONE — equipped [{string.Join(", ", ToPairs(LvnWardrobe.Equipped(_entity)))}]");
                LvnWardrobe.ClearPreview(_entity); // equips now cover the look
                _tcs?.TrySetResult(true);
            }
            finally
            {
                _buying = false;
                if (_confirm.enabledSelf == false && ConfirmText == "…")
                { _confirm.SetEnabled(true); SetConfirmText(label); }
            }
        }

        // Buy exactly the browsed item; on success the button flips to the
        // plain "choose" (RefreshConfirm sees it owned) and the player keeps
        // shopping — the next tab's piece is one more press away.
        private async Task BuyCurrentAsync(string axis, LvnWardrobeItem item)
        {
            var sku = LvnWardrobe.Sku(_entity, axis, item.value);
            LvnLog.Trace($"[lvn-wardrobe] buying {sku}: {item.price} {item.currency ?? "(null currency!)"}");
            bool ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
            LvnLog.Trace($"[lvn-wardrobe] buy {sku} → {(ok ? "OK" : "FAILED")}; " +
                      $"balances now [{string.Join(", ", ToPairs(LvnWallet.Balances))}]");
            LvnAnalytics.Track(ok ? "wardrobe_buy" : "wardrobe_buy_fail",
                ("entity", _entity), ("sku", sku));
            if (ok)
            {
                _buying = false;
                RebuildStrip(); // бейджи «◆» на карточках и свотчах устарели
                RefreshConfirm();
                return;
            }

            // В попапе значок не нарисуешь — но и служебное имя валюты
            // показывать нельзя: без названного currency_label остаётся цена.
            string title = _cfg.insufficient_text ?? "Not enough";
            string msg = string.IsNullOrEmpty(_cfg.currency_label)
                ? $"{title}: {item.price:N0}"
                : $"{title}: {item.price:N0} {_cfg.currency_label}";

            // Offer the store and retry once — same pattern as the chapter/title
            // entry gates. Falls back to just flashing the reason on the button
            // if the host hasn't wired the popup hooks (older shells).
            if (ConfirmTopUp != null && OpenStore != null)
            {
                bool toStore = await ConfirmTopUp(title, msg);
                if (toStore)
                {
                    await OpenStore();
                    await LvnWallet.RefreshAsync();
                    ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                    if (!ok && Alert != null) await Alert(title, msg);
                }
                _buying = false;
                RefreshConfirm();
                return;
            }

            // No popup hooks wired — fall back to flashing the reason on the button.
            SetConfirmText(msg);
            _confirm.schedule.Execute(() =>
            {
                _confirm.SetEnabled(true);
                RefreshConfirm();
            }).ExecuteLater(LvnMotion.Ms(LvnMotion.NoticeLong));
        }

        private static IEnumerable<string> ToPairs<T>(IReadOnlyDictionary<string, T> map)
        {
            foreach (var kv in map) yield return $"{kv.Key}={kv.Value}";
        }
    }
}
