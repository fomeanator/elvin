using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// АВАТАРКА ИГРОКА — кем он показан в шапке и в профиле.
    ///
    /// <para>До TR-79 аватар был один на всех: картинка из манифеста, которую
    /// нельзя поменять. Теперь это ВЫБОР из набора, и часть набора продаётся —
    /// по той же механике, что фоны меню в гардеробе: у платной есть цена и
    /// товар, покупка списывается кошельком, купленное остаётся навсегда.</para>
    ///
    /// <para>Набор объявляет автор в манифесте (<c>ui.browse.avatars</c>), а не
    /// код: лица игры — контент, и добавлять их должен тот, кто рисует, а не
    /// тот, кто собирает сборку.</para>
    /// </summary>
    public static class LvnAvatars
    {
        /// <summary>Одна аватарка: адрес картинки и, если платная, цена.</summary>
        public sealed class Choice
        {
            public string Id;
            public string Url;
            public string Currency;   // пусто — бесплатная
            public long Price;
            public string Sku;        // товар для инвентаря; пусто — id
            public bool Paid => !string.IsNullOrEmpty(Currency) && Price > 0;
            public string Item => string.IsNullOrEmpty(Sku) ? "avatar." + Id : Sku;
        }

        /// <summary>Ключ выбранного лица. Забвение аккаунта сносит его по
        /// имени (LvnForget): дом аватарок живёт в оболочке, а забвение — в
        /// движке, и знать друг о друге они не могут.</summary>
        public const string PickedKey = "lvn.avatar.picked";

        /// <summary>Что предлагает новелла. Пусто — выбора нет, и экран не
        /// открывается: обещать выбор без набора хуже, чем не обещать.</summary>
        public static List<Choice> Offered(LvnManifest m)
        {
            var list = new List<Choice>();
            var raw = m?.ui?.browse?.avatars;
            if (raw == null) return list;
            foreach (var a in raw)
            {
                if (a == null || string.IsNullOrEmpty(a.url)) continue;
                list.Add(new Choice
                {
                    Id = string.IsNullOrEmpty(a.id) ? a.url : a.id,
                    Url = a.url,
                    Currency = a.currency,
                    Price = a.price ?? 0,
                    Sku = a.sku,
                });
            }
            return list;
        }

        /// <summary>Выбранная игроком аватарка или пусто — тогда показывается
        /// та, что назвал автор (<c>ui.browse.avatar</c>).</summary>
        public static string Picked
        {
            get => Lvn.LvnKeep.Get(PickedKey, "");
            set => Lvn.LvnKeep.Put(PickedKey, value ?? "");
        }

        /// <summary>Адрес картинки для показа: выбранная, если она ещё есть в
        /// наборе, иначе авторская. Пропавшая из манифеста аватарка не должна
        /// оставлять игрока с пустым кружком.</summary>
        public static string Url(LvnManifest m)
        {
            var picked = Picked;
            if (!string.IsNullOrEmpty(picked))
                foreach (var c in Offered(m))
                    if (c.Id == picked) return c.Url;
            return m?.ui?.browse?.avatar;
        }

        /// <summary>Куплена ли платная аватарка. Бесплатная доступна всегда.</summary>
        public static bool Owned(Choice c)
        {
            if (c == null) return false;
            if (!c.Paid) return true;
            return Lvn.Services.LvnWallet.Inventory != null
                && Lvn.Services.LvnWallet.Inventory.TryGetValue(c.Item, out var n) && n > 0;
        }
    }
}
