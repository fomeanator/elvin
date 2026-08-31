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
        /// иначе дождаться сети.
        ///
        /// <para>Три исхода, и каждый существует не зря: сеть есть — берём и
        /// кладём в кэш; сети нет, но кэш есть — играем офлайн; нет ни того, ни
        /// другого — держим вуаль и ждём, потому что свежая установка без сети
        /// это НЕ тупик: появится сеть — приложение стартует само.</para>
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
            if (online)
            {
                try { manifest = await manifestTask; CacheManifest(manifest); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch failed: {ex.Message} — falling back to cache");
                    online = false;
                    LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            else
                // The in-flight fetch will fail on its own timeline; observe the
                // fault so it can't surface as an unobserved-exception warning.
                _ = manifestTask.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            if (manifest == null) manifest = LoadCachedManifest();
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
                catch { /* genuinely unreachable — recovery loop below */ }
            }
            if (manifest == null)
            {
                // A fresh install that can't reach the server is NOT a dead end:
                // hold on the veil and keep retrying — the moment the network
                // appears the app boots itself, no restart needed.
                Debug.LogWarning("[novelapp] no manifest and no cache — holding boot for connectivity");
                for (int attempt = 1; manifest == null; attempt++)
                {
                    BootVeil.Status(LvnWords.Of("boot.reconnecting", "no connection to the server — reconnecting… ({n})", attempt));
                    // Компонент умер (смена сцены, снос встраивателем) — уходим
                    // без манифеста: вызывающий это увидит и прекратит загрузку.
                    try { await Task.Delay(5000, destroyCancellationToken); }
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
                        Debug.Log($"[novelapp] manifest retry {attempt}: {ex.Message}");
                    }
                }
                mark("manifest (recovered)");
                BootVeil.Progress(60, "");
            }
            return (manifest, online);
        }
        /// <summary>
        /// Сцена под манифест: ассеты, каталог спрайтов, тема диалога и язык.
        ///
        /// <para>Тема берётся из того же manifest.ui, что и экраны оболочки, —
        /// иначе игра выглядела бы двумя разными продуктами: оболочка одной
        /// темы, диалог другой.</para>
        /// </summary>
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
            Lvn.Content.LvnWords.Learn(manifest.ui?.words, manifest.ui?.menu?.labels, manifest.ui);
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
                catch (Exception ex) when (attempt < 3)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch attempt {attempt} failed: {ex.Message} — retrying");
                    await Task.Delay(700 * attempt);
                }
            }
        }
        private static void CacheManifest(LvnManifest m)
        {
            if (m == null) return;
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
    }
}
