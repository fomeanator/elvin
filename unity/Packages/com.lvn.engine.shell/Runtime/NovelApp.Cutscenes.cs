using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КАТСЦЕНЫ ИГРОКА — что он прожил и как это пересмотреть.
    ///
    /// <para>Сцена объявляется в сценарии словами автора: <c>cutscene start
    /// Имя|id</c> … <c>cutscene end</c>. Пройдя её, игрок получает адрес —
    /// новелла, глава, id и кадр превью (см. <see cref="LvnCutsceneStore"/>).
    /// Ни ролика, ни снимка нигде не заводится: пересмотр ПЕРЕИГРЫВАЕТ тот же
    /// отрезок сценария, поэтому на сервере лежит только сценарий, который там
    /// и так был.</para>
    ///
    /// <para>Собираем по ВСЕМ новеллам каталога, а не по текущей: профиль —
    /// дом игрока, и его сцены не делятся по вкладкам.</para>
    /// </summary>
    public partial class NovelApp
    {
        private readonly Dictionary<string, string> _cutsceneTitles = new Dictionary<string, string>();

        /// <summary>Сколько сцен игрок открыл — подпись пункта в профиле.</summary>
        private int CutsceneCount() => CollectCutscenes().Count;

        private List<CutsceneGalleryScreen.Entry> CollectCutscenes()
        {
            var list = new List<CutsceneGalleryScreen.Entry>();
            _cutsceneTitles.Clear();
            var titles = _manifest?.titles;
            if (titles == null) return list;
            foreach (var t in titles)
            {
                if (t == null || string.IsNullOrEmpty(t.id)) continue;
                foreach (LvnCutsceneStore.Seen seen in LvnCutsceneStore.Seens(t.id))
                {
                    if (seen == null || string.IsNullOrEmpty(seen.Id)) continue;
                    // Ключ карточки — «новелла:сцена»: два разных романа вправе
                    // назвать свою сцену одинаково, и без новеллы в ключе
                    // пересмотр открыл бы чужую главу.
                    var key = t.id + ":" + seen.Id;
                    _cutsceneTitles[key] = t.id;
                    list.Add(new CutsceneGalleryScreen.Entry
                    {
                        Id = key,
                        TitleId = t.id,
                        SceneId = seen.Id,
                        Name = seen.Name,
                        Chapter = seen.Chapter,
                        Poster = seen.Poster,
                    });
                }
            }
            return list;
        }

        /// <summary>Наполнить галерею живыми записями, показать её и — если
        /// игрок выбрал сцену — проиграть ПОСЛЕ закрытия экрана. Порядок тут
        /// важнее краткости: закрытие возвращает интерфейс витрины, и сцена,
        /// начатая раньше, играла бы под панелями главной.</summary>
        private async Task OpenCutscenesAsync()
        {
            var screen = _shell?.Cutscenes;
            if (screen == null) return;
            // КРУГ, А НЕ ДОРОГА В ОДИН КОНЕЦ: посмотрел сцену — вернулся в
            // галерею и смотришь следующую. Раньше пересмотр высаживал игрока
            // на витрине, и за второй сценой он шёл через профиль заново
            // («после показа катсцены в меню перебрасывает, а надо чтобы экран
            // катсцен был открыт» — Илья 09.09).
            while (true)
            {
                screen.ClearPick();
                screen.SetEntries(CollectCutscenes());
                await _shell.OpenCutscenesAsync();
                var picked = screen.Picked;
                screen.ClearPick();
                if (picked == null) return;   // закрыл галерею — вышли из круга
                await PlayCutsceneAsync(picked);
            }
        }

        /// <summary>Пересмотреть сцену: открыть её главу и проиграть отрезок с
        /// метки. Адрес разбирается обратно на новеллу и id.</summary>
        private Task PlayCutsceneAsync(CutsceneGalleryScreen.Entry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return Task.CompletedTask;
            if (!_cutsceneTitles.TryGetValue(entry.Id, out var titleId)) return Task.CompletedTask;
            int sep = entry.Id.IndexOf(':');
            var cutsceneId = sep >= 0 ? entry.Id.Substring(sep + 1) : entry.Id;
            return ReplayCutsceneAsync(titleId, entry.Chapter, cutsceneId);
        }

        /// <summary>
        /// Проиграть отрезок заново: берём скрипт той же главы и отдаём сцене
        /// адрес катсцены. Скрипт скачивается тем же кэширующим путём, что и
        /// обычная глава, — на сервере для галереи ничего отдельного нет.
        ///
        /// <para>Прогресс не трогаем: пересмотр не двигает главу и не пишет
        /// автосохранение. Кончилась сцена — возвращаем игрока туда, откуда он
        /// пришёл, то есть на витрину.</para>
        /// </summary>
        private async Task ReplayCutsceneAsync(string titleId, string chapterId, string cutsceneId)
        {
            if (Stage == null || _assets?.Loader == null) return;
            var title = FindTitle(titleId);
            var chapter = FindChapter(title, chapterId);
            if (chapter == null || string.IsNullOrEmpty(chapter.script_url))
            {
                LvnLog.Trace($"[lvn-cutscene] нет главы «{chapterId}» у «{titleId}» — пересмотр отменён");
                return;
            }
            string json;
            try { json = await _assets.Loader.DownloadScriptCached(chapter.script_url); }
            catch (System.Exception ex)
            {
                LvnLog.Trace($"[lvn-cutscene] скрипт не доехал: {ex.Message}");
                return;
            }
            if (string.IsNullOrEmpty(json)) return;

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shell?.BeginCutsceneSession();
            if (!Stage.PlayCutscene(json, cutsceneId, () => done.TrySetResult(true)))
            {
                _shell?.EndCutsceneSession();
                return;
            }
            await done.Task;
            Stage.ClearStage();
            _shell?.EndCutsceneSession();
            ShowMenuScene();
        }

        private LvnTitle FindTitle(string titleId)
        {
            var titles = _manifest?.titles;
            if (titles == null || string.IsNullOrEmpty(titleId)) return null;
            foreach (var t in titles) if (t != null && t.id == titleId) return t;
            return null;
        }

        /// <summary>Глава по id внутри новеллы. Главы живут в сезонах — путь
        /// тот же, каким их обходит чтение, и второй карты глав заводить
        /// нельзя: разойдётся с первой в первую же правку модели.</summary>
        private static LvnChapter FindChapter(LvnTitle title, string chapterId)
        {
            if (title?.seasons == null || string.IsNullOrEmpty(chapterId)) return null;
            foreach (var se in title.seasons)
            {
                if (se?.chapters == null) continue;
                foreach (var c in se.chapters) if (c != null && c.id == chapterId) return c;
            }
            return null;
        }
    }
}
