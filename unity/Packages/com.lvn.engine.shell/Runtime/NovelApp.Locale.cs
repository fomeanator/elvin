using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЯЗЫК НА ЛЕТУ — что происходит, когда игрок меняет его посреди игры.
    ///
    /// <para>Работа не в одном действии, а в ПОРЯДКЕ: подобрать словарь
    /// оболочки под новый язык, прогреть каталоги новеллы, перечитать подписи —
    /// и всё это не роняя того, что сейчас на экране. Игрок ждёт мгновенного
    /// отклика: он открыл настройки ровно ради этого.</para>
    ///
    /// <para>Тема отдельная от подъёма приложения, хотя один шаг у них общий:
    /// на старте язык применяется тем же путём, что и при смене, — иначе
    /// первый экран вышел бы на языке по умолчанию, а следующий уже на
    /// выбранном.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // The Settings language row writes LvnPrefs.Locale; pick the change up
        // and swap the running chapter's string catalog — new lines render in
        // the new language immediately (the visible line updates on advance).
        private void OnPrefsMaybeLocale() => LvnAsync.Fire(OnPrefsMaybeLocaleAsync(), "OnPrefsMaybeLocale");
        private async Task OnPrefsMaybeLocaleAsync()
        {
            var want = CurrentLocale;
            if (want == _localeApplied) return;

            // СЛОВА ОБОЛОЧКИ ТОЖЕ ПЕРЕВОДЯТСЯ. Раньше переводился только текст
            // главы, а подписи движка оставались авторскими: игрок переключал
            // язык и получал английские реплики в русском интерфейсе —
            // двуязычие наполовину выглядит поломкой, а не выбором.
            var words = await LoadUiWordsAsync(want);

            // ПЕРЕКЛЮЧИЛИ, ПОКА МЫ ГРУЗИЛИ — их выбор новее нашего. То же
            // правило, что у бута строкой ниже, и по той же причине: два
            // быстрых переключения подряд идут двумя загрузками, а приходят в
            // любом порядке. Победившая последней ставила СВОИ слова, и
            // интерфейс оставался на языке, который игрок уже отменил, — а
            // отметка о применённом языке при этом показывала новый, и
            // повторное переключение туда-обратно его не чинило.
            if (CurrentLocale != want) return;
            _localeApplied = want;
            Lvn.Content.LvnWords.Translate(words);

            if (_currentChapter != null && Stage != null)
            {
                System.Collections.Generic.IReadOnlyDictionary<string, string> strings;
                try { strings = await LoadCatalogAsync(_currentChapter.script_url); }
                catch { strings = null; } // no catalog → the inline original
                if (CurrentLocale != want) return;   // и здесь: каталог главы тоже едет сетью
                Stage.Strings = strings;
                // РЕАЛТАЙМ: реплика, уже стоящая на экране, перерисовывается
                // новым языком сразу (штатный RerenderCurrent — тот же вариант
                // текста, без сдвига {a|b|c}), а не со следующей строки.
                Stage.Player?.RerenderCurrent();
            }
        }
        /// <summary>Прогреть словари объявленных языков. Тишина при отказе:
        /// прогрев — оптимизация, а не обязанность, и без него переключение
        /// просто окажется медленнее.</summary>
        private async Task WarmLocalesAsync()
        {
            var langs = LvnPrefs.AvailableLocales;
            if (langs == null) return;
            foreach (var lang in langs)
            {
                if (string.IsNullOrEmpty(lang) || _uiWordsCache.ContainsKey(lang)) continue;
                try { await LoadUiWordsAsync(lang); }
                catch { /* прогрев — оптимизация: не вышло, значит переключение будет медленнее */ }
            }
        }
        private async Task ApplyLocaleAtBootAsync()
        {
            var locale = CurrentLocale;
            var words = await LoadUiWordsAsync(locale);
            // ПЕРЕКЛЮЧИЛИ, ПОКА МЫ ГРУЗИЛИ — их выбор новее нашего. Бут идёт
            // фоном, а язык переключают из настроек в любой момент; затри мы
            // здесь чужой выбор, игрок увидел бы, как язык откатывается сам.
            if (CurrentLocale != locale) return;
            _localeApplied = locale;
            Lvn.Content.LvnWords.Translate(string.IsNullOrEmpty(locale) ? null : words);
            await WarmLocalesAsync();
        }
        private async Task<System.Collections.Generic.Dictionary<string, string>> LoadUiWordsAsync(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return null;
            if (_uiWordsCache.TryGetValue(locale, out var cached)) return cached;
            // Манифест — первый: он уже в руках, и перевод из него доезжает
            // мгновенно, без второго запроса и без шанса «файл не задеплоили».
            var fromManifest = _manifest?.ui?.words_locales;
            if (fromManifest != null && fromManifest.TryGetValue(locale, out var inline) && inline != null)
            {
                _uiWordsCache[locale] = inline;
                return inline;
            }
            if (_assets?.Loader == null) return null;
            try
            {
                var json = await _assets.Loader.DownloadScriptText(
                    LvnAssetPath.Under("ui/words." + locale + ".json"), default, singleAttempt: true);
                var words = string.IsNullOrEmpty(json) ? null : Newtonsoft.Json.JsonConvert
                    .DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
                _uiWordsCache[locale] = words;   // и ОТСУТСТВИЕ файла запоминаем: второй раз не ходим
                return words;
            }
            catch
            {
                // Нет файла — не беда и не ошибка: игра просто остаётся на
                // авторском языке интерфейса.
                _uiWordsCache[locale] = null;
                return null;
            }
        }
    }
}
