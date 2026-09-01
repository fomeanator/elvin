using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
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
            _confirmCoin.style.marginLeft = Lvn.UI.LvnTokens.Space1;
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

        // «Штук больше нуля», а не «ключ есть»: инвентарь считает штуки, и
        // потраченная вещь остаётся ключом с нулём — здесь она выглядела
        // купленной, тогда как язык (has_item) считал её отсутствующей.
        private bool IsOwnedIn(string axis, LvnWardrobeItem item) =>
            item == null || item.price <= 0
            || LvnWallet.Has(LvnWardrobe.Sku(_entity, axis, item.value));

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
                    // НАРОЧНО мимо костюмера: вопрос узкий — что ПРИМЕРЕНО и не
                    // куплено. Лесенка «примерка → надетое → дефолт» ответила бы
                    // и про надетое, а за него платить не надо.
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
            // НАРОЧНО мимо костюмера: вопрос «есть ли НЕПОДТВЕРЖДЁННАЯ примерка».
            // Лесенка ответила бы и про надетое — то есть «да» там, где игрок
            // ничего не менял, и кнопка вечно предлагала бы подтвердить.
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
                // ВАЛЮТУ НА КНОПКЕ ПОКАЗЫВАЕТ ЗНАЧОК. Правило было другое:
                // слово, если новелла его назвала (currency_label) — и кнопка
                // единственная во всём гардеробе писала «300 кристаллов», пока
                // соседние ярлыки и пилюля кошелька рисовали самоцвет. Слово
                // ещё и не переводится вместе с интерфейсом: оно авторское.
                // Сумму пишет Ценник: «:N0» брал разделитель разрядов из
                // настроек телефона, тогда как весь остальной интерфейс — из
                // языка новеллы. В одном окне цена «1 200» соседствовала с
                // балансом «1 200», собранным по другому правилу.
                string price = Lvn.UI.LvnPriceTag.Amount(item.price);
                string buy = LvnWords.Pick("wardrobe.buy", _cfg.buy_text, "Buy");
                SetConfirmText($"{buy}:  {price}", item.currency);
                LvnLog.Trace($"[lvn-wardrobe] sheet buy offer {_entity}.{axis}='{item.value}' " +
                          $"{item.price} {item.currency}, have {LvnWallet.Balance(item.currency)}");
            }
            else SetConfirmText(LvnWords.Pick("wardrobe.choose", _cfg.confirm_text, "Choose"));
        }

        /// <summary>
        /// СВОЙ ЗАМОК ЗДЕСЬ ЗАКОННЫЙ — и это стоит сказать, потому что рядом
        /// стоит дом (<c>LvnBusy</c>), который ведёт занятость самой кнопкой.
        ///
        /// <para>Дому нужна кнопка с подписью в <c>text</c>: он её гасит,
        /// подменяет и возвращает. У листа кнопка составная — подпись живёт
        /// отдельной меткой внутри (<c>SetConfirmText</c>), потому что рядом с
        /// ней стоит цена и значок валюты.</para>
        ///
        /// <para>Вторая причина важнее: флаг здесь читает НЕ ТОЛЬКО замок.
        /// Пересборка ленты (<c>RefreshConfirm</c>) обновляет доступность кнопки
        /// на каждый выбор плитки — и во время покупки обязана этого не делать.
        /// Дом такого не знает: для него занятость — состояние кнопки, а не
        /// признак, который спрашивают со стороны.</para>
        /// </summary>
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
                // НАРОЧНО мимо костюмера: снимок ВСЕХ примерок целиком, а не
                // ответ про одну ось. Лесенка тут не нужна — нужно ровно то,
                // что игрок сейчас крутит и за что ещё не заплатил.
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
                _gate.Release(true);
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
        //
        // Порядок обряда (списать → предложить магазин → повторить → объяснить)
        // здесь БОЛЬШЕ НЕ ЖИВЁТ: он у Кассира, общий с воротами входа в главу.
        // Своим экземпляром гардероб уже успел разойтись с оригиналом — писал
        // исход по первой попытке, и покупка после магазина оставалась в отчёте
        // провалом. Здесь остаётся гардеробное: чем платят, какими словами и что
        // обновить на экране после успеха.
        private async Task BuyCurrentAsync(string axis, LvnWardrobeItem item)
        {
            var sku = LvnWardrobe.Sku(_entity, axis, item.value);
            LvnLog.Trace($"[lvn-wardrobe] buying {sku}: {item.price} {item.currency ?? "(null currency!)"}");

            // В попапе значок не нарисуешь — здесь фраза, и валюта в ней стоит
            // словом. Слово берётся у ЦЕННИКА (падежная форма из манифеста), а
            // не из гардеробного currency_label: два источника одного слова
            // расходились, и в одном окне цена называлась двумя способами.
            // Облика нет — остаётся голая сумма: врать про валюту хуже.
            string title = LvnWords.Pick("wallet.not_enough", _cfg.insufficient_text, "Not enough");
            string msg = $"{title}: {LvnPriceTag.Full(item.currency, item.price)}";

            var charge = new LvnCashier.Charge
            {
                Currency = item.currency,
                Amount = item.price,
                Reason = "wardrobe",
                Sku = sku,
                Title = title,
                Message = msg,
                Marks = new (string, object)[] { ("entity", _entity), ("sku", sku) },
            };

            var outcome = await LvnCashier.ChargeAsync(charge,
                ConfirmTopUp == null || OpenStore == null ? null
                    : new System.Func<string, string, Task<bool>>((t, m) => ConfirmTopUp(t, m)),
                OpenStore == null ? null : new System.Func<Task>(() => OpenStore()),
                Alert == null ? null : new System.Func<string, string, Task>((t, m) => Alert(t, m)));

            bool ok = outcome.Ok();
            LvnLog.Trace($"[lvn-wardrobe] buy {sku} → {outcome}; " +
                      $"balances now [{string.Join(", ", ToPairs(LvnWallet.Balances))}]");
            // Исход — ПО ИТОГУ обряда, а не по первой попытке: покупка после
            // захода в магазин это покупка.
            LvnAnalytics.Track(ok ? Lvn.Services.LvnEvents.WardrobeBuy : Lvn.Services.LvnEvents.WardrobeBuyFail,
                ("entity", _entity), ("sku", sku));

            if (ok)
            {
                _buying = false;
                RebuildStrip(); // бейджи «◆» на карточках и свотчах устарели
                RefreshConfirm();
                return;
            }

            if (outcome != LvnCashier.Outcome.NoOffer)
            {
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
