using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI
{
    /// <summary>
    /// ОБРАЗ ИЗ ЧУЖОГО ПРОХОЖДЕНИЯ (TR-18) — что на героине у того, кто
    /// прислал ссылку, чего у меня нет и сколько стоит повторить.
    ///
    /// <para>Замысел Ильи: блогерка выкладывает ссылку на свой акт, подписчицы
    /// покупают «то же, что у неё». Не абстрактный магазин с паками, а
    /// конкретный образ конкретного человека, которого они только что видели в
    /// истории.</para>
    ///
    /// <para>ГЛАВНОЕ ПРАВИЛО, записанное в самой задаче: <b>показывать чужой
    /// образ можно, ВЫДАВАТЬ его нельзя</b> — только продавать. Иначе один
    /// купил, десять получили даром. Поэтому здесь считается ЦЕНА, а не выдача:
    /// дом отвечает на вопрос «чего не хватает и почём», и ни на какой другой.</para>
    ///
    /// <para>Дом чистый: ни сети, ни экрана, ни кошелька — владение и цены ему
    /// приносят доводами. Поэтому правило целиком проверяется стражем, а
    /// ошибиться в нём — значит продать игроку то, что у него уже есть.</para>
    /// </summary>
    public static class LvnLookOffer
    {
        /// <summary>Одна вещь образа: что это, сколько стоит и есть ли она у
        /// смотрящего.</summary>
        public struct Piece
        {
            public string Axis;     // ось гардероба: наряд, причёска, фон…
            public string Value;    // значение оси
            public string Sku;      // товар кошелька
            public string Name;     // как называется в каталоге
            public string Currency;
            public long Price;
            public bool Owned;      // уже есть у смотрящего — не продаём второй раз
            public bool Gacha;      // только из круток: за деньги не продаётся
        }

        /// <summary>Итог: из чего состоит образ и во что обойдётся повторить.</summary>
        public sealed class Offer
        {
            public readonly List<Piece> Pieces = new List<Piece>();
            /// <summary>Валюта набора. Смешанных наборов не бывает: вещи
            /// гардероба продаются за одну валюту, и цена целиком имеет смысл
            /// только в ней.</summary>
            public string Currency;
            /// <summary>Сколько стоит докупить недостающее. Уже своё не
            /// считается: продавать второй раз то, что игрок купил, — обман.</summary>
            public long Price;
            /// <summary>Сколько вещей ещё не у игрока.</summary>
            public int Missing;
            /// <summary>Есть ли в образе вещь, которую нельзя купить (приз
            /// круток). Такую показываем, но в счёт не берём — иначе кнопка
            /// «Купить набор» обещала бы невозможное.</summary>
            public bool HasGachaOnly;
        }

        /// <summary>
        /// Собрать предложение по чужому образу.
        /// </summary>
        /// <param name="entity">кого одевали — герой гардероба новеллы</param>
        /// <param name="worn">что на нём надето: ось → значение</param>
        /// <param name="manifest">каталог: имена, цены, признак «только из круток»</param>
        /// <param name="owns">есть ли товар у смотрящего (кошелёк)</param>
        public static Offer Build(string entity, IReadOnlyDictionary<string, string> worn,
                                  LvnManifest manifest, System.Func<string, bool> owns)
        {
            var offer = new Offer();
            if (string.IsNullOrEmpty(entity) || worn == null || manifest?.sprites == null) return offer;
            if (!manifest.sprites.TryGetValue(entity, out var def) || def?.wardrobe == null) return offer;

            foreach (var kv in worn)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                // Лицо — не вещь: эмоция не продаётся и в образ не входит.
                if (LvnWardrobeStage.IsEmotion(kv.Key)) continue;
                if (!def.wardrobe.TryGetValue(kv.Key, out var slot) || slot?.items == null) continue;

                LvnWardrobeItem item = null;
                foreach (var it in slot.items)
                    if (it != null && it.value == kv.Value) { item = it; break; }
                if (item == null) continue;

                var sku = LvnWardrobe.Sku(entity, kv.Key, kv.Value);
                bool free = item.price <= 0 && !item.gacha;
                bool owned = free || (owns != null && owns(sku));
                var piece = new Piece
                {
                    Axis = kv.Key,
                    Value = kv.Value,
                    Sku = sku,
                    Name = string.IsNullOrEmpty(item.name) ? kv.Value : item.name,
                    Currency = item.currency,
                    Price = item.price,
                    Owned = owned,
                    Gacha = item.gacha,
                };
                offer.Pieces.Add(piece);

                if (owned) continue;
                offer.Missing++;
                if (item.gacha) { offer.HasGachaOnly = true; continue; }   // не продаётся
                if (string.IsNullOrEmpty(offer.Currency)) offer.Currency = item.currency;
                // Чужая валюта в наборе — редкость и повод не врать ценой:
                // считаем только свою, остальное игрок докупит поштучно.
                if (offer.Currency == item.currency) offer.Price += item.price;
            }
            return offer;
        }
    }
}
