using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЦЕННИК — как называются деньги игры и как показывается сумма.
    ///
    /// <para>Списывает деньги Кассир; называть и показывать их — другая работа,
    /// и до сих пор её делали пятеро вразнобой. Магазин паков держал два
    /// <c>switch</c> по идентификатору валюты («crystals» → «Кристаллы»,
    /// «кристаллов») ПРЯМО В ДВИЖКЕ — то есть любая другая новелла получала
    /// чужие слова насильно, вопреки правилу «подписи на экране принадлежат
    /// автору» (docs/language-policy.md). Гардероб знал подпись из манифеста
    /// (<c>currency_label</c>) и не знал значка. Витрина скинов и ежедневные
    /// награды различали валюты булевым полем «золото или энергия» и рисовали
    /// «◆»/«⚡» прямо в строке. Хаб и профиль несли свой список валют по
    /// умолчанию.</para>
    ///
    /// <para>Из-за этого одна сумма в одном экране выглядела по-разному: на
    /// карточке гардероба «◆ 1 200», а на кнопке под ней — «Купить: 1 200
    /// золота».</para>
    ///
    /// <para>Ответственность одна: по идентификатору валюты дать её ОБЛИК
    /// (подпись, форма родительного падежа, значок, цвет) и собрать из суммы
    /// строку. Облик приходит из манифеста; движок знает только форму — не
    /// слова.</para>
    /// </summary>
    public static class LvnPriceTag
    {
        /// <summary>Облик валюты, каким его знает интерфейс.</summary>
        public sealed class Look
        {
            /// <summary>Как называется («Кристаллы»). По умолчанию — сам id:
            /// движок не придумывает слов за автора.</summary>
            public string Name;
            /// <summary>Форма при сумме («1 200 кристаллов»). Пусто — Name.</summary>
            public string Unit;
            /// <summary>Значок валюты.</summary>
            public LvnIcon Icon = LvnIcon.Gem;
            /// <summary>Цвет значка и суммы.</summary>
            public Color Tint = LvnTokens.Gold;
        }

        private static readonly Dictionary<string, Look> Looks =
            new Dictionary<string, Look>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>Принять облики валют из манифеста. Зовётся при загрузке
        /// контента — до первого показа цены.</summary>
        public static void Learn(Dictionary<string, CurrencyLook> from)
        {
            Looks.Clear();
            if (from == null) return;
            foreach (var kv in from)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                Looks[kv.Key] = new Look
                {
                    Name = string.IsNullOrEmpty(kv.Value.name) ? kv.Key : kv.Value.name,
                    Unit = kv.Value.unit,
                    Icon = ParseIcon(kv.Value.icon, DefaultIcon(kv.Key)),
                    Tint = string.IsNullOrEmpty(kv.Value.color)
                        ? DefaultTint(kv.Key) : UiColor.Parse(kv.Value.color, DefaultTint(kv.Key)),
                };
            }
        }

        /// <summary>Облик валюты: из манифеста, иначе разумное умолчание.</summary>
        public static Look Of(string currency)
        {
            if (!string.IsNullOrEmpty(currency) && Looks.TryGetValue(currency, out var look))
                return look;
            return new Look
            {
                Name = currency ?? string.Empty,
                Icon = DefaultIcon(currency),
                Tint = DefaultTint(currency),
            };
        }

        /// <summary>Сумма без валюты: «1 200». Разряды — чтобы четырёхзначные
        /// цены читались с одного взгляда, а разделитель — из языка новеллы, а
        /// не из настроек телефона (см. <see cref="LvnNumberFormat"/>).</summary>
        public static string Amount(long value) => LvnNumberFormat.Groups(value);

        /// <summary>Сумма с названием валюты: «1 200 кристаллов». Без названия
        /// (автор его не дал) остаётся голое число — врать про валюту хуже,
        /// чем промолчать.</summary>
        public static string Full(string currency, long value)
        {
            var look = Of(currency);
            var unit = !string.IsNullOrEmpty(look.Unit) ? look.Unit : look.Name;
            return string.IsNullOrEmpty(unit) ? Amount(value) : Amount(value) + " " + unit;
        }

        // Умолчания движка — про ФОРМУ, а не про слова. Догадка о значке живёт
        // у ИКОНОК (LvnIcons.ForCurrency) и знает больше, чем знала эта: золото,
        // монеты, ключи, сердца — на двух языках. Здесь стояла своя, бедная
        // («energy — молния, всё прочее камень»), и умолчания двух домов не
        // совпадали: валюта «золото» без настройки получала камень в магазине и
        // монету в строке состояния.
        private static LvnIcon DefaultIcon(string currency) => LvnIcons.ForCurrency(currency);

        private static Color DefaultTint(string currency) => LvnIcons.CurrencyColor(currency);

        private static LvnIcon ParseIcon(string name, LvnIcon fallback)
            => string.IsNullOrEmpty(name)
                ? fallback
                : System.Enum.TryParse<LvnIcon>(name, ignoreCase: true, out var ic) ? ic : fallback;
    }
}
