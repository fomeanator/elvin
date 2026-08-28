using System;
using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// СЛОВАРЬ ОБОЛОЧКИ — откуда берётся ЛЮБАЯ подпись, которую движок пишет
    /// на экране сам.
    ///
    /// <para>Роль нашлась не поиском дублей, а закономерностью: три подряд
    /// выделенные роли (Ценник, Имя игрока, Титровальщик) вскрыли одну и ту же
    /// болезнь — русские слова, зашитые в движок. «Кристаллы», «Гость»,
    /// «Глава» лежали в коде и не переопределялись ничем, то есть любая другая
    /// новелла получала их насильно.</para>
    ///
    /// <para>Причина глубже отдельных строк: у подписей нет ВЛАДЕЛЬЦА. Часть
    /// берётся из манифеста с русским умолчанием (<c>nav_home ?? "Главная"</c>),
    /// часть — с английским (<c>equip_text ?? "Equip"</c>), а целые экраны
    /// (ежедневные награды, профиль) пишут русским прямо в коде и не
    /// переопределяются вовсе. Три правила на одну работу — второй признак из
    /// списка выше.</para>
    ///
    /// <para>Ответственность: по ключу дать слово. Порядок один и тот же
    /// всегда: что сказала новелла (<c>ui.words</c>) → что просит вызывающий
    /// как умолчание → английское слово движка. Перевод НЕ здесь: каталоги
    /// локали живут у своего механизма и подставляются новеллой; словарь лишь
    /// не мешает ей это сделать.</para>
    ///
    /// <para>Границы. Словарь — про подписи ДВИЖКА (кнопки оболочки, заголовки
    /// её экранов). Текст новеллы — реплики, названия глав, имена предметов —
    /// приходит из контента и через словарь не проходит.</para>
    ///
    /// <para>ЖИВЁТ В НИЖНЕМ СЛОЕ намеренно. Сперва он лежал среди интерфейса, но
    /// его спрашивают и оттуда, и из модели контента (Титровальщик — имя главы),
    /// а нижняя сборка верхнюю не видит. Слова — инфраструктура текста, а не
    /// украшение экрана.</para>
    /// </summary>
    public static class LvnWords
    {
        private static Dictionary<string, string> _words;

        /// <summary>Принять словарь новеллы (<c>ui.words</c>): ключ → слово.
        /// Зовётся при загрузке манифеста, до первого показа экрана.</summary>
        public static void Learn(Dictionary<string, string> words)
            => Learn(words, null);

        /// <summary>
        /// То же, но с ВТОРЫМ словарём манифеста — подписями меню
        /// (<c>ui.menu.labels</c>). Их читает тема, но искать слово надо в
        /// обоих: автор кладёт его туда, где ему кажется естественным, и
        /// промах не должен молча оборачиваться английским текстом. При
        /// совпадении ключа выигрывает <c>ui.words</c> — он общий.
        /// </summary>
        public static void Learn(Dictionary<string, string> words, Dictionary<string, string> menuLabels)
        {
            var merged = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (menuLabels != null)
                foreach (var kv in menuLabels) merged[kv.Key] = kv.Value;
            if (words != null)
                foreach (var kv in words) merged[kv.Key] = kv.Value;
            _base = merged.Count == 0 ? null : merged;
            _words = Merge(_base, _translated);
        }

        /// <summary>
        /// КОГО КАК ЗОВУТ — авторское имя актёра рядом с его идентификатором.
        ///
        /// <para>В скрипте говорящий назван СТРОКОЙ («Виктория»), в манифесте
        /// тот же герой лежит под идентификатором («victoria»), а перевод имени
        /// живёт по ключу от идентификатора (<c>actor.victoria</c>). Без этой
        /// карты два имени одного человека — разные строки, и сцена с
        /// гардеробом расходились: «Виктория» над репликой и «Victoria» в
        /// гардеробе одновременно.</para>
        ///
        /// <para>Карта заодно чинит и обратный случай: автор перевёл имя в
        /// каталоге главы, но не в словаре оболочки — тогда каталог главы
        /// старше, и подставится его вариант (см. <c>LvnPlayer.LocalizedWho</c>).</para>
        /// </summary>
        public static void LearnActors(Dictionary<string, LvnSpriteEntity> sprites)
        {
            _actorIdByName = null;
            if (sprites != null)
            {
                var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in sprites)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    map[kv.Key] = kv.Key;                                   // сам id тоже имя
                    var authored = kv.Value?.name;
                    if (!string.IsNullOrEmpty(authored)) map[authored] = kv.Key;
                }
                if (map.Count > 0) _actorIdByName = map;
            }
            // Шов с ядром: плеер словаря не видит (границы сборок), поэтому имя
            // говорящего подставляем отсюда.
            Lvn.LvnPlayer.SpeakerNames = Speaker;
        }

        private static Dictionary<string, string> _actorIdByName;

        /// <summary>Имя говорящего на языке игрока: перевод по идентификатору
        /// актёра, иначе перевод по самому имени (так автор называет тех, кого
        /// нет в манифесте — «Система», «Голос»), иначе авторское имя, на
        /// латинице прочитанное транслитом.</summary>
        public static string Speaker(string who)
        {
            if (string.IsNullOrEmpty(who)) return who;
            if (_actorIdByName != null && _actorIdByName.TryGetValue(who, out var id)
                && TryTranslated("actor." + id, out var byId)) return byId;
            if (TryTranslated("actor." + who, out var byName)) return byName;
            return Readable(who);
        }

        /// <summary>
        /// ПЕРЕВОД СЛОВ ОБОЛОЧКИ — поверх авторского набора.
        ///
        /// <para>Текст главы переводился каталогом, а подписи движка («Играть»,
        /// «Стереть», «Осталось 2 из 3») — нет: их автор задаёт один раз в
        /// манифесте, на своём языке. Игрок переключал язык истории и получал
        /// английские реплики в русском интерфейсе — двуязычие наполовину, что
        /// хуже одноязычия: выглядит как поломка, а не как выбор.</para>
        ///
        /// <para>Перевод НАКЛАДЫВАЕТСЯ, а не заменяет: чего в нём нет, остаётся
        /// авторским словом, а не английским умолчанием движка. Пустой словарь
        /// (или язык оригинала) снимает наложение целиком.</para>
        /// </summary>
        public static void Translate(Dictionary<string, string> words)
        {
            _translated = words == null || words.Count == 0
                ? null : new Dictionary<string, string>(words, System.StringComparer.OrdinalIgnoreCase);
            _words = Merge(_base, _translated);
            Changed?.Invoke();
        }

        /// <summary>
        /// ПОДПИСЬ С УЧЁТОМ ВСЕХ ТРЁХ ИСТОЧНИКОВ, в порядке старшинства:
        /// перевод игрока → авторское поле конфигурации → словарь и умолчание.
        ///
        /// <para>Автор задаёт подписи не только словарём, но и полями
        /// (<c>ui.settings.title</c>, <c>ui.browse.nav_home</c> — шестьдесят три
        /// места). Поле стояло ВПЕРЕДИ перевода, поэтому «Настройки», «Закрыть»
        /// и нижнее меню оставались на языке автора, даже когда всё вокруг уже
        /// переключилось. Игрок видит смесь и считает, что переключатель
        /// сломан.</para>
        ///
        /// <para>Перевод сильнее поля по той же причине, по какой он сильнее
        /// темы: поле — это выбор АВТОРА, а язык — выбор ИГРОКА, и спор между
        /// ними решается в пользу того, кто сейчас смотрит на экран.</para>
        /// </summary>
        public static string Pick(string key, string authored, string fallback)
        {
            if (TryTranslated(key, out var tr)) return tr;
            if (!string.IsNullOrEmpty(authored)) return authored;
            return Of(key, fallback);
        }

        /// <summary>
        /// ИМЯ, КОТОРОЕ ВИДИТ ИГРОК — через словарь, если у него есть перевод.
        ///
        /// <para>Названия новелл, коллекций, персонажей и нарядов приходят из
        /// данных: автор пишет их на своём языке в манифесте и каталоге. При
        /// переключении языка реплики становились английскими, а «Агентство»,
        /// «Экспедиции» и имена героев оставались русскими — полстраницы на
        /// одном языке, полстраницы на другом.</para>
        ///
        /// <para>Ключ собирается по виду и идентификатору: <c>title.agency</c>,
        /// <c>collection.stories</c>, <c>actor.hill</c>, <c>skin.rose</c>. Нет
        /// перевода — остаётся авторское имя: это НЕ повод показать
        /// идентификатор или английское умолчание.</para>
        /// </summary>
        public static string Name(string kind, string id, string authored)
        {
            if (!string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(id)
                && TryTranslated(kind + "." + id, out var tr)) return tr;
            return Readable(string.IsNullOrEmpty(authored) ? id : authored);
        }

        /// <summary>
        /// ЧТО ДЕЛАТЬ С НЕПЕРЕВЕДЁННЫМ. Пока игрок читает на своём языке —
        /// ничего: авторское имя и есть правильное. Как только он перешёл на
        /// латиницу, кириллическое имя посреди английской фразы читается как
        /// ошибка, а не как выбор — и его транслитерируют.
        ///
        /// <para>Транслит не выдаёт себя за перевод: это способ прочитать имя
        /// вслух. Он включается ТОЛЬКО когда перевод для языка вообще есть —
        /// иначе игра без переводов начала бы латинизировать сама себя.</para>
        /// </summary>
        public static string Readable(string authored)
        {
            if (string.IsNullOrEmpty(authored)) return authored;
            if (_translated == null) return authored;          // язык оригинала
            if (!LvnTranslit.HasCyrillic(authored)) return authored;
            return LvnTranslit.ToLatin(authored);
        }

        /// <summary>Есть ли ПЕРЕВОД для ключа. Спрашивают те, у кого свой
        /// словарь ближе к экрану (тема сцены): выбор языка обязан побеждать
        /// авторские подписи, иначе игрок переключает язык и видит переведённые
        /// реплики в неперёведенном меню — а меню он читает первым.</summary>
        public static bool TryTranslated(string key, out string value)
        {
            value = null;
            if (_translated == null || string.IsNullOrEmpty(key)) return false;
            return _translated.TryGetValue(key, out value) && !string.IsNullOrEmpty(value);
        }

        /// <summary>Словарь сменился — экраны, уже нарисованные прежними
        /// словами, обязаны перерисоваться. Иначе перевод доедет только до
        /// того, что откроют ПОСЛЕ него.</summary>
        public static event System.Action Changed;

        private static Dictionary<string, string> _base, _translated;

        private static Dictionary<string, string> Merge(
            Dictionary<string, string> baseWords, Dictionary<string, string> over)
        {
            if (over == null || over.Count == 0) return baseWords;
            var merged = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (baseWords != null) foreach (var kv in baseWords) merged[kv.Key] = kv.Value;
            foreach (var kv in over) merged[kv.Key] = kv.Value;
            return merged;
        }

        /// <summary>
        /// Слово по ключу. <paramref name="fallback"/> — что показать, если
        /// новелла ключ не назвала: обычно английское умолчание движка.
        /// </summary>
        public static string Of(string key, string fallback)
        {
            if (!string.IsNullOrEmpty(key) && _words != null
                && _words.TryGetValue(key, out var w) && !string.IsNullOrEmpty(w))
                return w;
            return fallback;
        }

        /// <summary>То же, но с подстановкой одного числа: «День {0}» → «День 3».
        /// Порядок слов в разных языках разный, поэтому число подставляется
        /// шаблоном, а не склеиванием.</summary>
        public static string Of(string key, string fallback, object arg0)
        {
            var pattern = Of(key, fallback);
            return string.IsNullOrEmpty(pattern) ? pattern
                 : pattern.Contains("{0}") ? string.Format(pattern, arg0)
                 : pattern + " " + arg0;
        }

        /// <summary>Шаблон с ДВУМЯ подстановками: «{0} МБ на устройстве, ещё
        /// {1} МБ». Склеивать такие фразы нельзя вовсе: в другом языке числа
        /// стоят в другом порядке, и склейка даёт бессмыслицу, которую автор
        /// не может поправить словарём.</summary>
        public static string Of(string key, string fallback, object arg0, object arg1)
        {
            var pattern = Of(key, fallback);
            if (string.IsNullOrEmpty(pattern)) return pattern;
            try { return string.Format(pattern, arg0, arg1); }
            catch (FormatException)
            {
                // Кривой шаблон из манифеста не повод показать пустую строку:
                // жалуемся один раз именем ключа и отдаём как есть.
                UnityEngine.Debug.LogWarning($"[lvn-words] шаблон «{key}» не понят — показан без подстановки");
                return pattern;
            }
        }

        /// <summary>
        /// СЛОВО ПРИ ЧИСЛЕ: «1 глава», «2 главы», «5 глав».
        ///
        /// <para>Правило склонения было вписано в экран профиля прямо кодом —
        /// со славянскими остатками от 11 до 14 и русскими формами в
        /// <c>switch</c>. Английской новелле оно даёт «5 глава», и обойти его
        /// автор не может ничем.</para>
        ///
        /// <para>Форм не одна и не всегда три: язык выбирает СЕБЕ правило тем,
        /// сколько форм назвал автор. Дал <c>.few</c> — считаем язык славянским
        /// и применяем остатки; не дал — простое «один против прочих». Так
        /// движку не нужно знать список языков мира.</para>
        /// </summary>
        public static string Plural(string key, long n, string one, string other)
        {
            string w1 = Of(key + ".one", null);
            string few = Of(key + ".few", null);
            string many = Of(key + ".many", null);
            if (w1 != null && few != null && many != null)
            {
                long lastTwo = n % 100;
                if (lastTwo >= 11 && lastTwo <= 14) return many;
                switch (n % 10)
                {
                    case 1: return w1;
                    case 2: case 3: case 4: return few;
                    default: return many;
                }
            }
            return n == 1 ? Of(key + ".one", one) : Of(key + ".other", other);
        }
    }
}
