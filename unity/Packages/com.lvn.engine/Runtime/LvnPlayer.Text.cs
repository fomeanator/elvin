using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// ЧТО ИМЕННО СКАЖЕТ ГЕРОЙ — выбор текста реплики и имени говорящего.
    ///
    /// <para>Строка в скрипте — не всегда та строка, которую увидит игрок:
    /// поверх неё лежит каталог перевода (ключ — сама исходная строка, как в
    /// gettext), подстановка переменных и правила отображаемого имени. Дом
    /// отдельный, потому что вопрос «какой текст» не имеет отношения к
    /// вопросу «какая команда следующая», а путались они постоянно.</para>
    /// </summary>
    public sealed partial class LvnPlayer
    {
        private string Localized(JObject c)
        {
            var inline = (string)c["text"];
            if (inline != null)
                return Lookup(inline) ?? inline;
            var id = (string)c["text_id"];
            if (id == null) return "";
            return Lookup(id) ?? id;
        }

        /// <summary>
        /// КАК ЗОВУТ ГОВОРЯЩЕГО НА ЯЗЫКЕ ИГРОКА — второй ответ на тот же
        /// вопрос, если каталог главы промолчал.
        ///
        /// <para>Имя героини живёт в двух местах и приходит к игроку двумя
        /// путями. В сцене «who» — авторская строка, и её переводит каталог
        /// главы (ключ — сама строка). В оболочке то же имя берётся из
        /// манифеста по идентификатору и переводится словарём
        /// (<c>actor.victoria</c>). Пути независимы, и расходятся они
        /// постоянно: в живом контенте партнёрской новеллы имена персонажей переведены
        /// в словаре оболочки и НЕ переведены в каталогах глав — 52 случая из
        /// 84 проверенных. Игрок с английским интерфейсом видел «Victoria» в
        /// гардеробе и «Виктория» над репликой той же героини.</para>
        ///
        /// <para>Ядро словаря оболочки не видит (границы сборок: он в Content,
        /// а Content зависит от ядра, не наоборот), поэтому здесь шов: кто
        /// знает имена — тот их и подставляет. Не подставил никто — остаётся
        /// авторская строка, как было.</para>
        /// </summary>
        public static Func<string, string> SpeakerNames;

        private string LocalizedWho(string who)
        {
            if (who == null) return null;
            var byCatalog = Lookup(who);
            if (byCatalog != null) return byCatalog;   // автор перевёл строку — она главнее
            return SpeakerNames?.Invoke(who) ?? who;
        }

        // Old imported stories encode system hints as ordinary dialogue lines
        // spoken by "Подсказка". Keep that content playable, but present it
        // through the real non-blocking hint op: no dialogue card, tap pause,
        // backlog entry or synthetic actor speaker.
        private static bool IsLegacyHintSpeaker(string who)
        {
            if (string.IsNullOrWhiteSpace(who)) return false;
            var value = who.Trim();
            return string.Equals(value, "Подсказка", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Hint", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject LegacyHintCommand(string text) => new JObject
        {
            ["op"] = "hint",
            ["text"] = text ?? string.Empty,
            ["duration"] = 4f
        };

        /// <summary>
        /// Поиск строки в каталоге, устойчивый к форме записи юникода.
        ///
        /// <para>«Ё» и любая буква с диакритикой существуют в двух видах: одним
        /// символом (NFC) и буквой с комбинирующим знаком (NFD). macOS отдаёт
        /// имена и содержимое в NFD, редакторы и веб-формы — по-разному, и
        /// каталог, собранный из одной формы, молча не находит строку в другой.
        /// Цена промаха несоразмерна причине: реплика остаётся непереведённой
        /// без единого сообщения, и ищут это в переводе, а не в кодировке.</para>
        ///
        /// <para>Сначала точное совпадение (это горячий путь, лишней работы в
        /// нём нет), и только при промахе — попытка нормализованным ключом.</para>
        /// </summary>
        private string Lookup(string key)
        {
            if (Strings == null || key == null) return null;
            if (Strings.TryGetValue(key, out var hit)) return hit;
            var nfc = key.Normalize(NormalizationForm.FormC);
            if (!string.Equals(nfc, key, StringComparison.Ordinal)
                && Strings.TryGetValue(nfc, out var normalized)) return normalized;
            return null;
        }

        /// <summary>
        /// Optional override for string <c>expr</c> conditions (option filters
        /// and <c>if</c>). When unset, the built-in <see cref="LvnExpression"/>
        /// evaluator is used; set this only to plug in a different expression
        /// dialect. Structured <c>cond</c> is unaffected.
        /// </summary>
        public Func<string, IReadOnlyDictionary<string, JToken>, bool> ExprEvaluator;

        // A malformed expression in the content must never crash the runtime — a
        // bad condition simply gates closed (false). Authoring tools catch these at
        // compile time; the player degrades gracefully.
    }
}
