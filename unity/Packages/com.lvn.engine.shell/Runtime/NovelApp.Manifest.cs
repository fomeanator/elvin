using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОТКУДА БЕРЁТСЯ КАТАЛОГ — и что делать, когда его нет.
    ///
    /// <para>Манифест это список новелл, глав, спрайтов и правил экономики:
    /// без него игра не знает ни что показывать, ни чем это рисовать. Взять
    /// его можно тремя путями — с сервера, из кэша прошлого запуска, из
    /// вложенного в сборку сида, — и порядок между ними не вкусовой:
    /// НЕ ДОШЛА СЕТЬ — ЭТО НЕ ПУСТАЯ ИГРА. Игрок, у которого нет связи,
    /// обязан открыть то, что уже скачал.</para>
    ///
    /// <para>Здесь же каталог РАЗДАЁТСЯ ДОМАМ: пришедший манифест объявляет
    /// темы, валюты, экономику, шрифты и наборы — и каждый дом узнаёт об этом
    /// одним обрядом, а не десятью присваиваниями по месту.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        /// <summary>
        /// Достать манифест — свежий с сервера, иначе последний сохранённый,
        /// иначе из набора в сборке; если и его нет — дождаться сети.
        ///
        /// <para>Сеть есть — берём и кладём в кэш; сети нет, но кэш есть —
        /// играем офлайн. На первой установке после отказа запроса пробуем
        /// манифест из сида. Если и его нет — держим вуаль и ждём сеть.</para>
        ///
        /// <para>Средний случай тонкий: проба связи могла соврать (её трёхсекундный
        /// срок проиграл медленному первому запуску), пока сам запрос манифеста
        /// уже почти успел. Поэтому перед медленными повторами мы даём шанс
        /// запросу, который всё ещё в полёте.</para>
        /// </summary>
        private async Task<(LvnManifest manifest, bool online)> ResolveManifestAsync(
            Task<LvnManifest> manifestTask, bool online, Action<string> mark)
        {
            // Manifest: fresh from the server when online (cached for next time), else
            // the last cached copy — so a previously-online install still plays offline.
            LvnManifest manifest = null;
            Exception manifestFailure = null;
            if (online)
            {
                try { manifest = await manifestTask; CacheManifest(manifest); }
                catch (Exception ex)
                {
                    manifestFailure = ex;
                    LogManifestFailure(ex, "fetch failed — falling back to cache");
                    online = false;
                    if (ManifestRecovery.KindFor(ex) == ManifestFailureKind.Network)
                        LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            else
                // The in-flight fetch will fail on its own timeline; observe the
                // fault so it can't surface as an unobserved-exception warning.
                _ = manifestTask.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            if (manifest == null) manifest = LoadCachedManifest();
            // СЛОВА — КАК ТОЛЬКО МАНИФЕСТ В РУКАХ, А НЕ КОГДА ДОЙДЁТ ОЧЕРЕДЬ.
            //
            // Подписи учил один ApplyManifest, то есть уже после запуска. А
            // вуаль говорит РАНЬШЕ: ниже стоит цикл восстановления, и это самый
            // долгий экран, какой игрок вообще видит. Каталог к тому моменту
            // пуст, LvnWords.Of отдаёт запасную строку из кода — и на экране,
            // где важнее всего понять, что происходит, движок разговаривает не
            // на языке игры.
            //
            // Замер 07.09: живой манифест несёт ui.words на 292 ключа, и ключ
            // boot.reconnecting среди них ЕСТЬ. Игрок при этом видел «no
            // connection to the server — reconnecting… (2)»: перевод автора
            // лежал готовым и не использовался.
            LearnWords(manifest);
            mark("manifest");
            BootVeil.Progress(60);
            if (manifest == null)
            {
                // The probe may have lied (its 3s deadline lost to a slow first
                // launch) while the manifest fetch itself was about to succeed —
                // give the in-flight task its chance before slow retries.
                try
                {
                    manifest = await manifestTask;
                    CacheManifest(manifest);
                    online = true;
                    LvnNetworkStatus.MarkOnline("boot manifest arrived despite failed probe");
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(manifestFailure, ex)) LogManifestFailure(ex, "fetch failed");
                    manifestFailure = ex;
                }
            }
            if (ManifestSeedFallback.ShouldTry(manifestFailure != null, manifest != null))
            {
                var json = await _assets.Loader.ReadSeedTextAsync("manifest.json");
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
                        if (manifest != null)
                        {
                            LearnWords(manifest);
                            online = false;
                            LvnLog.Info("[lvn-app] starting offline from the bundled seed manifest");
                        }
                        else LvnLog.Warn("[lvn-app] seed manifest is null — continuing recovery");
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        LvnLog.Warn($"[lvn-app] seed manifest parse failed — continuing recovery: {ex}");
                    }
                }
            }
            if (manifest == null)
            {
                // Keep the veil responsive, but describe the actual failure:
                // a broken initializer cannot be fixed by reconnecting.
                Debug.LogWarning("[lvn-app] no manifest and no cache — holding boot for recovery");
                for (int attempt = 1; manifest == null; attempt++)
                {
                    var kind = ManifestRecovery.KindFor(manifestFailure);
                    BootVeil.Status(ManifestFailureMessage(kind, attempt));
                    // Компонент умер (смена сцены, снос встраивателем) — уходим
                    // без манифеста: вызывающий это увидит и прекратит загрузку.
                    try { await Task.Delay((int)(ManifestRecovery.PauseSeconds(kind, attempt) * 1000f), _quitting); }
                    catch (OperationCanceledException) { return (null, online); }
                    try
                    {
                        manifest = await FetchManifestAsync();
                        CacheManifest(manifest);
                        online = true;
                        LvnNetworkStatus.MarkOnline("boot manifest retry succeeded");
                    }
                    catch (Exception ex)
                    {
                        manifestFailure = ex;
                        LogManifestFailure(ex, $"retry {attempt}");
                    }
                }
                mark("manifest (recovered)");
                BootVeil.Progress(60, "");
            }
            return (manifest, online);
        }
        /// <summary>
        /// СЛОВА ОБОЛОЧКИ ИЗ МАНИФЕСТА — ОДНО ПРАВИЛО, СКОЛЬКО БЫ РАЗ ЕГО НИ
        /// ПРИМЕНИЛИ.
        ///
        /// <para>Зовётся дважды и намеренно: рано — как только манифест
        /// оказался в руках (свежий, из кэша прошлого запуска или из набора в
        /// сборке), чтобы вуаль говорила на языке игры; и потом из
        /// <c>ApplyManifest</c> обычным ходом. Повтор безвреден: Learn
        /// пересобирает словарь целиком, и свежий манифест переучивает поверх
        /// старого.</para>
        ///
        /// <para>Второй дороги к словам заводить нельзя: одно правило в двух
        /// местах расходится молча, и разойдётся оно там, где никто не смотрит
        /// — на запасном пути.</para>
        /// </summary>
        private static void LearnWords(LvnManifest m)
        {
            if (m == null) return;
            Lvn.Content.LvnWords.Learn(m.ui?.words, m.ui?.menu?.labels, m.ui);
        }

        private static void LogManifestFailure(Exception error, string phase)
        {
            if (ManifestRecovery.KindFor(error) == ManifestFailureKind.Network)
                LvnLog.Info($"[lvn-app] manifest {phase}: {error.Message}");
            else
                LvnLog.Error($"[lvn-app] manifest {phase}: {error}");
        }

        // Слова берутся у каталога — ровно тем же способом, что и всё
        // остальное в движке. Своей развилки по языку здесь НЕ ЗАВОДИМ: до
        // манифеста каталога ещё нет, и подпись выйдет английской. Это
        // отдельный пробел вуали (её первые слова непереводимы в принципе),
        // и лечить его подстановкой одного языка в языконезависимый движок
        // значило бы завести второй путь локализации рядом с настоящим.
        private static string ManifestFailureMessage(ManifestFailureKind kind, int attempt)
            => kind == ManifestFailureKind.Network
                ? LvnWords.Of("boot.reconnecting", "no connection to the server — reconnecting… ({n})", attempt)
                : LvnWords.Of("boot.app_error",
                    "The app ran into an error. This is not a connection problem. Please restart the app.");

        /// <summary>
        /// ЧЕМУ ДОМА УЧАТСЯ У МАНИФЕСТА — одним списком, а не двумя.
        ///
        /// <para>Слова автора живут не только на экранах: как зовут деньги, как
        /// зовут безымянного игрока, каким словом называть главу, что писать на
        /// кнопках движка, кто есть кто среди актёров. Всё это раздавалось по
        /// домам при старте — и НЕ раздавалось при живом обновлении контента.
        /// Автор правил «Кристаллы» на «Осколки», выкатывал — и у игрока с живой
        /// сессией валюта оставалась прежней, хотя карточки новелл уже
        /// обновились.</para>
        ///
        /// <para>Список получателей вёлся руками в двух местах и разошёлся, как
        /// и список экранов в <c>ApplyLiveUpdate</c> (роль 197). Теперь он один,
        /// и добавить в него нового ученика можно только здесь.</para>
        /// </summary>
        private void TeachHousesFrom(LvnManifest manifest)
        {
            // КАКИЕ ЯЗЫКИ У НОВЕЛЛЫ ЕСТЬ. Список объявлялся только при старте, и
            // доложенный автором перевод не появлялся в настройках, пока игрок
            // не перезапустит игру: ряд языков строится ровно по этому списку.
            LvnPrefs.OriginalLocale = manifest.language ?? "ru";
            LvnPrefs.AvailableLocales = manifest.languages != null && manifest.languages.Count > 0
                ? manifest.languages : System.Array.Empty<string>();
            // ЦЕННИК узнаёт, как называются деньги ЭТОЙ игры: слова
            // принадлежат автору, движок знает только форму показа.
            Lvn.UI.LvnPriceTag.Learn(manifest.ui?.currency_look);
            // И как игра зовёт безымянного игрока — тоже слово автора.
            if (!string.IsNullOrEmpty(manifest.ui?.guest_name))
                Lvn.UI.LvnPlayerName.GuestLabel = manifest.ui.guest_name;
            // …и в какую переменную истории игрок вписывает своё имя: без этого
            // назвавшийся в прологе игрок оставался для оболочки безымянным.
            Lvn.UI.LvnPlayerName.Var = string.IsNullOrEmpty(manifest.ui?.player_name_var)
                ? Lvn.UI.LvnPlayerName.DefaultVar : manifest.ui.player_name_var;
            // …и как она зовёт главу: «Глава», «Эпизод», «Дело».
            if (!string.IsNullOrEmpty(manifest.ui?.chapter_word))
                Lvn.Content.LvnCaptions.ChapterWord = manifest.ui.chapter_word;
            // Словарь оболочки: всё, что движок пишет на экране сам.
            LearnWords(manifest);
            // …и кто есть кто: имя говорящего в сцене — та же строка, что имя
            // героя в гардеробе, только приходит она из скрипта, а не по id.
            Lvn.Content.LvnWords.LearnActors(manifest.sprites);
        }
        private async Task<LvnManifest> FetchManifestAsync()
        {
            // The manifest is the boot's single point of truth — a fresh install
            // has nothing without it. One transient failure (flaky emulator NAT,
            // a mid-handshake reset) must not fall through to "no manifest":
            // three quick attempts before the caller's slower recovery paths.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var json = await _assets.Loader.DownloadScriptText("/v1/content/manifest", default, singleAttempt: true);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json) ?? new LvnManifest();
                }
                catch (LvnFetchException ex) when (attempt < 3)
                {
                    // Пауза перед повтором — по общему правилу движка
                    // (LvnBackoff), а не своя лесенка: «сколько ждать» не может
                    // зависеть от того, какой файл споткнулся.
                    float pause = Lvn.Content.LvnBackoff.DelaySeconds(attempt + 1);
                    Debug.LogWarning($"[lvn-app] manifest fetch attempt {attempt} failed: {ex.Message} — retry in {pause:F1}s");
                    await Task.Delay((int)(pause * 1000f));
                }
            }
        }
        /// <summary>
        /// ПУСТОЙ КАТАЛОГ ПОВЕРХ НЕПУСТОГО — ЭТО НЕ НОВОСТЬ, А АВАРИЯ.
        ///
        /// <para>Сервер, у которого манифест пропал или не читается, отвечает не
        /// ошибкой, а <c>200 {"titles":[]}</c>: «свежая установка ещё ничего не
        /// опубликовала» и «выкладка сломалась» выглядят с провода одинаково
        /// (замерено живым сервером, qa/empty-manifest-check.sh). Кривая
        /// выкладка, записавшая пустой манифест, — тот же случай.</para>
        ///
        /// <para>Для игрока разница огромна: принять такой каталог значит не
        /// только показать пустую витрину, но и ЗАТЕРЕТЬ офлайновую копию —
        /// после чего пустой станет и игра без сети. Замерено живым прогоном
        /// (CatalogGuardTests): офлайновая копия действительно затиралась.</para>
        ///
        /// <para>Правило поэтому простое: пустой каталог принимается ТОЛЬКО
        /// когда своего ещё нет. Настоящее «автор снял всё с публикации» от
        /// аварии не отличить, и выбор здесь в пользу игрока: он теряет не
        /// библиотеку, а несколько минут актуальности.</para>
        /// </summary>
        private static bool EmptyOverNonEmpty(LvnManifest fresh)
        {
            if (fresh?.titles != null && fresh.titles.Count > 0) return false;
            var cached = LoadCachedManifest();
            return cached?.titles != null && cached.titles.Count > 0;
        }

        private static void CacheManifest(LvnManifest m)
        {
            if (m == null) return;
            if (EmptyOverNonEmpty(m))
            {
                LvnLog.Warn("[lvn-app] сервер отдал пустой каталог, а свой не пуст — "
                          + "офлайновая копия сохранена (похоже на сломанную выкладку)");
                return;
            }
            try
            {
                LvnKeep.Put(ManifestCacheKey, Newtonsoft.Json.JsonConvert.SerializeObject(m));
            }
            catch { /* cache write best-effort */ }
        }
        /// <summary>Тот же каталог, что лежит в кэше? Сравниваем ЗАПИСЬ, а не
        /// поля: кэш и есть запись, и вопрос ровно в том, изменится ли она.</summary>
        private static bool SameAsCached(LvnManifest m)
        {
            if (m == null) return false;
            try
            {
                var cached = LvnKeep.Get(ManifestCacheKey, null);
                return !string.IsNullOrEmpty(cached)
                       && cached == Newtonsoft.Json.JsonConvert.SerializeObject(m);
            }
            catch { return false; }
        }

        private static LvnManifest LoadCachedManifest()
        {
            try
            {
                var json = LvnKeep.Get(ManifestCacheKey, null);
                return string.IsNullOrEmpty(json)
                    ? null
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            }
            catch { return null; }
        }
        /// <summary>
        /// ПРИМЕНИТЬ МАНИФЕСТ — что в приложении меняется, когда меняется
        /// содержимое каталога.
        ///
        /// <para>Не путать с <c>AdoptManifestAsync</c> (NovelApp.Boot): тот про
        /// СОБЫТИЕ «приехал свежий каталог» — кэш, байты меню, экраны, забытые
        /// облики. Здесь про СОДЕРЖИМОЕ: что из манифеста куда кладётся.</para>
        ///
        /// <para>Работа была одна, а написана дважды: на старте
        /// (<c>PrepareStage</c>) и на живом обновлении. Оба списка перечисляли
        /// одни и те же присваивания — дома учатся словам автора, витрина берёт
        /// расстановку, сцена берёт каталог спрайтов и оформление формы ввода,
        /// набор объёмных сцен уходит в загрузчик.</para>
        ///
        /// <para>Два списка одного факта расходятся не «когда-нибудь», а при
        /// СЛЕДУЮЩЕМ поле: автор добавляет поле в манифест, оно попадает в тот
        /// список, где его писали, и живое обновление молча оставляет старое
        /// значение. Отладить это нельзя — на старте всё правильно.</para>
        ///
        /// <para>Один разъезд уже случился и виден только рядом: тему сцены две
        /// стороны строили по РАЗНЫМ правилам. Живое обновление собирало её
        /// начисто и накладывало оформление играющей новеллы поверх; старт брал
        /// за основу тему уже созданной сцены и про новеллу не знал. Совпадали
        /// они по случайности — у свежей сцены тема и есть пустая.</para>
        /// </summary>
        private void ApplyManifest(LvnManifest manifest)
        {
            if (manifest == null) return;
            _manifest = manifest;
            _globalUi = manifest.ui;
            // Дома учатся заново тем же списком, что и при старте: слова автора
            // (валюты, «Глава», подписи движка, имена актёров) меняются вместе с
            // контентом, и без этой строки они оставались от прошлой выкладки.
            TeachHousesFrom(manifest);
            ApplyMenuStaging(manifest);
            _assets.Set3DSetCatalog(manifest.sets3d);
            // СЮЖЕТНЫЙ ГАРДЕРОБ живёт манифестом наравне с экранами оболочки,
            // но в её набор не входит: создаёт его приложение и показывает
            // поверх сцены, а не в витрине. Пометка у него общая
            // (ILvnContentAware) — здесь стоит ЕДИНСТВЕННОЕ вручение, а не
            // строка-напоминание в обработчике обновления, где её однажды и
            // забыли.
            (_storySheet as ILvnContentAware)?.SetContent(manifest);
            if (Stage == null) return;
            Stage.Catalog = new SpriteCatalog(manifest.sprites);
            Stage.NameInput = manifest.ui?.name_input;   // оформление формы ввода — авторское
            Stage.ApplyTheme(ThemeFrom(manifest));
        }

        /// <summary>
        /// КАК ВЫГЛЯДИТ ИГРА ПО ЭТОМУ МАНИФЕСТУ — начисто, двумя слоями.
        ///
        /// <para>Начисто — не мелочь: собирать поверх ДЕЙСТВУЮЩЕЙ темы значит
        /// оставлять на экране поля, которых в новом манифесте уже нет. Автор
        /// убрал скругление — оно осталось; убрал рамку — она осталась.
        /// Убранное поле неотличимо от ненаписанного, и правильно отвечает на
        /// это только чистая основа.</para>
        ///
        /// <para>Оформление играющей новеллы ищется в НОВОМ манифесте по её
        /// имени: правка per-title обязана доехать до открытой главы.</para>
        /// </summary>
        private VnTheme ThemeFrom(LvnManifest manifest)
        {
            var theme = VnThemeBuilder.From(manifest.ui, new VnTheme());
            if (_currentTitle == null || manifest.titles == null) return theme;
            var live = manifest.titles.Find(t => t != null && t.id == _currentTitle.id);
            return live?.ui != null ? VnThemeBuilder.From(live.ui, theme) : theme;
        }
    }
}
