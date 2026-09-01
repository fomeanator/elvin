using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// КЛАДОВЩИК СЦЕНЫ — что держать в памяти, пока оно на экране.
    ///
    /// <para>Стриминговое окно выгружает давно не запрошенный арт, и это верно:
    /// память телефона кончается быстрее терпения. Но «давно не запрошенный» и
    /// «не нужный» — разные вещи: показанному арту запросы больше не приходят,
    /// он просто ВИСИТ НА ЭКРАНЕ. Пока разницы не было, LRU забирал текстуры
    /// прямо из-под живой картинки — кукла меню становилась белым квадратом,
    /// полотно белело во весь кадр, а фон, скачанный десять раз, качался в
    /// одиннадцатый.</para>
    ///
    /// <para>Правило одно: ЧТО СЕЙЧАС РИСУЕТСЯ — закреплено. Слоты
    /// («bg», «actor:&lt;id&gt;») отражают кадр: замена содержимого слота
    /// освобождает прежнее, уход актёра — его слой. Три исключения выведены
    /// живыми дефектами и записаны здесь же: героиня между главами, задержка
    /// освобождения под кроссфейд облика и полотно, которое остаётся до нового
    /// фона.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        // ── ЖИВЫЕ СПРАЙТЫ СЦЕНЫ ЗАКРЕПЛЕНЫ (27.08): LRU стримингового окна
        // уничтожал текстуры прямо на экране — кукла меню становилась белым
        // квадратом, канвас серел («переключение актёров фон убивает»). Grace
        // окна считается от последнего ЗАПРОСА, а показанному давно арту
        // запросы не приходят. Всё, что сцена сейчас рисует, пиннится в
        // лоадере; замена или уход снимает пин. Слоты: "bg", "actor:<id>".
        private readonly Dictionary<string, List<Sprite>> _scenePins
            = new Dictionary<string, List<Sprite>>();

        private void RepinSceneSprites(string slot, IReadOnlyList<Sprite> next)
        {
            var cl = (Assets as CachingAssets)?.Loader;
            if (cl == null) return;
            List<Sprite> keep = null;
            if (next != null && next.Count > 0)
            {
                keep = new List<Sprite>(next.Count);
                foreach (var s in next)
                    // pin ДО unpin прежних: общий слой переживает замену
                    if (s != null) { cl.PinSprite(s, true); keep.Add(s); }
            }
            if (_scenePins.TryGetValue(slot, out var prev) && prev != null)
            {
                // Анпин ПРЕЖНИХ — С ЗАДЕРЖКОЙ: прокси смены облика ещё
                // показывает старые слои весь кроссфейд, и мгновенный анпин
                // отдавал их LRU прямо под ним — актёр вставал БЕЛЫМ
                // прямоугольником (живой скрин 27.08). Две секунды покрывают
                // самый длинный своп с запасом.
                LvnAsync.Fire(UnpinLaterAsync(prev), "UnpinLater");
            }
            if (keep == null) _scenePins.Remove(slot);
            else _scenePins[slot] = keep;
        }

        private async Task UnpinLaterAsync(List<Sprite> sprites)
        {
            await Task.Delay(2000);
            var cl = (Assets as CachingAssets)?.Loader;
            if (cl == null) return;
            foreach (var s in sprites) cl.PinSprite(s, false);
        }

        /// <summary>Актёр, чьи слои НЕ отпускаются при уборке сцены. Кукла меню
        /// стоит между главами всё время, и выгружать её арт на вход в главу
        /// значит перезагружать его на выходе — а пока он едет, слои рисуют
        /// сплошные прямоугольники («белый квадрат вместо героини»). Дешевле
        /// удержать один облик в памяти, чем каждый раз собирать заново
        /// (мысль Ильи 26.08: «нахера очищать героиню — её надо переодевать»).
        /// Хост ставит сюда своего фаворита меню.</summary>
        public string KeepActorAlive { get; set; }

        /// <summary>Слот тёплого полотна витрины — оно живёт вне кадра сцены и
        /// не отпускается уборкой.</summary>
        private const string MenuCanvasSlot = "menu-canvas";

        /// <summary>Ядро створа: живёт столько же, сколько сам створ, — то есть
        /// МЕЖДУ сценами. Обычная уборка его не касается.</summary>
        internal const string PortalCoreSlot = "portal-core";

        /// <summary>
        /// ПОЛОТНО ВИТРИНЫ ГРЕЕТСЯ ЗАРАНЕЕ.
        ///
        /// <para>Меню открывается не «когда-нибудь», а всегда: с него начинается
        /// запуск и им кончается каждая глава. При этом его полотно ставилось
        /// как обычный фон — команда уходила, картинка качалась и декодилась
        /// (крупный канвас ~0.6с), и всё это время витрина стояла ЧЁРНОЙ, а
        /// героиня — уже нет: она в кадре, мир под ней пустой. Картинка
        /// доезжала «позже» и вставала щелчком (живой репорт Ильи 27.08).</para>
        ///
        /// <para>Один известный из манифеста файл греется сразу после манифеста
        /// и остаётся закреплённым: витрина открывается с готовым полотном, а
        /// возврат из главы не платит декод заново.</para>
        /// </summary>
        /// <summary>
        /// ПРОГРЕТЬ ГЕРОИНЮ ВИТРИНЫ — до того, как её попросят показать.
        ///
        /// <para>Полотно витрины грелось заранее, а кукла — нет: её слои
        /// начинали качаться и распаковываться в тот миг, когда меню уже
        /// открыто. На живом запуске 01.09 это дало пять секунд ожидания —
        /// причём не из-за сети: три места в декодере занимали полотно
        /// (2000×1500) и ядро створа, а слои героини стояли к ним в очередь
        /// по 1,2–5,0 с каждый.</para>
        ///
        /// <para>Греем ТЕМИ ЖЕ адресами, что и покажем: облик берётся из
        /// каталога с текущими осями гардероба, а не угадывается. Прогрев
        /// ничего не рисует — он только кладёт спрайты в кэш, чтобы показ был
        /// мгновенным.</para>
        /// </summary>
        public async Task WarmActorAsync(string id)
        {
            if (string.IsNullOrEmpty(id) || Assets == null || Catalog == null) return;
            try
            {
                var art = ResolveActorArt(id, new JObject { ["id"] = id });
                if (art.Urls == null || art.Urls.Count == 0) return;
                var loads = new List<Task<Sprite>>(art.Urls.Count);
                foreach (var url in art.Urls)
                    if (!string.IsNullOrEmpty(url))
                        loads.Add(Assets.LoadSpriteAsync(url, _cts?.Token ?? default));
                var sprites = await Task.WhenAll(loads);
                int ok = 0;
                foreach (var sp in sprites) if (sp != null) ok++;
                LvnLog.Trace($"[lvn-warm] героиня витрины прогрета: {ok} из {art.Urls.Count} слоёв ({id})");
            }
            catch (System.OperationCanceledException) { }   // прогрев — не обязательство
            catch (System.Exception e)
            {
                LvnLog.Trace($"[lvn-warm] героиня витрины не прогрелась ({id}): {e.Message}");
            }
        }

        public async Task WarmMenuCanvasAsync(string url)
        {
            if (string.IsNullOrEmpty(url) || Assets == null) return;
            try
            {
                var s = await Assets.LoadSpriteAsync(url, _cts?.Token ?? default);
                if (s != null)
                {
                    RepinSceneSprites(MenuCanvasSlot, new[] { s });
                    LvnLog.Trace($"[lvn-bg] полотно витрины прогрето: {url}");
                }
                else Debug.LogWarning($"[lvn-bg] полотно витрины не прогрелось: {url}");
            }
            catch (System.OperationCanceledException) { }   // прогрев — не обязательство
            catch (System.Exception e)
            {
                Debug.LogWarning($"[lvn-bg] полотно витрины не прогрелось ({url}): {e.Message}");
            }
        }

        private void UnpinAllSceneSprites()
        {
            var cl = (Assets as CachingAssets)?.Loader;
            string keep = string.IsNullOrEmpty(KeepActorAlive) ? null : "actor:" + KeepActorAlive;
            List<string> drop = null;
            foreach (var kv in _scenePins)
            {
                // Полотно витрины греется на весь запуск: его показывают после
                // КАЖДОЙ главы, и отпустить его на уборке значит купить чёрную
                // витрину на каждом выходе.
                if (kv.Key == MenuCanvasSlot) continue;
                // СТВОР ПЕРЕЖИВАЕТ УБОРКУ — и его ядро тоже. Картинка ядра
                // нужна ровно там, где сцену чистят: на уходе в главу и на
                // возвращении. Сняв пин, мы отдаём её кэшу ровно перед тем,
                // как она понадобится, — отсюда «иногда шар портала пропадает»
                // (живой репорт Ильи 28.08).
                if (kv.Key == PortalCoreSlot) continue;
                // ПОЛОТНО НЕ ОТПУСКАЕМ. Оно ОСТАЁТСЯ на экране до нового bg —
                // так задумано и так написано у обоих мест вызова, — а пин с
                // него всё-таки снимался. Кэш забирал текстуру прямо из-под
                // видимой картинки, и полотно белело посреди кадра:
                //   «[lvn-bg] полотно СТАЛО ПУСТЫМ И БЕЛЫМ: tex=НЕТ» —
                // под затемнением это и есть «серый экран» в катсцене ухода
                // (лог Ильи, 27.08). Тот же промах заставлял заново качать и
                // декодировать фоны, скачанные десять раз до этого.
                // Слот один, и следующий bg сам заменит его содержимое.
                if (kv.Key == "bg") continue;
                if (keep != null && kv.Key == keep) continue; // этот облик остаётся жить
                if (cl != null) foreach (var s in kv.Value) cl.PinSprite(s, false);
                (drop ??= new List<string>()).Add(kv.Key);
            }
            if (drop != null) foreach (var k in drop) _scenePins.Remove(k);
        }
    }
}
