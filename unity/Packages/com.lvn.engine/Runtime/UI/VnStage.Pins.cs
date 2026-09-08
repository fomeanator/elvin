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
        // Механизм — у доски (LvnPinBoard); здесь остаётся ПРАВИЛО: две
        // секунды до отпускания прежнего набора. Столько живёт прокси смены
        // облика: он показывает старые слои весь кроссфейд, и отпустив их
        // сразу, мы отдавали их окну прямо под ним — актёр вставал белым
        // прямоугольником (живой скрин 27.08).
        private readonly LvnPinBoard<string> _scenePins = new LvnPinBoard<string>(2f);

        private void RepinSceneSprites(string slot, IReadOnlyList<Sprite> next)
            => _scenePins.Hold(slot, (Assets as CachingAssets)?.Loader, next);

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

        /// <summary>Арт витрины, прогретый при старте: рамки, нижнее меню,
        /// лого, аватар, значки. Живёт весь запуск — витрину показывают после
        /// каждой главы.</summary>
        internal const string MenuArtSlot = "menu-art";

        /// <summary>
        /// ПРОГРЕТЬ АРТ ВИТРИНЫ — рамки, нижнее меню, лого, аватар, значки.
        ///
        /// <para>Полотно и куклу грели, а рамки интерфейса — нет: они
        /// качались и декодились в тот миг, когда вуаль уже снята. Живой лог
        /// 08.09: вуаль снята на +7308 мс, рамки приехали через 0,3–0,45 с
        /// после неё — панель, карточка, кнопка награды всплывали по одной.
        /// Через секунду витрину пересобрало живое обновление: элементы ушли
        /// из панели, их пины отпустились, и весь набор декодировался ВТОРОЙ
        /// раз (46–199 мс) — второе мелькание («картинки с главного меню
        /// надо при старте прогревать, а то они мелькают» — Илья 08.09).</para>
        ///
        /// <para>Греем разом и ДЕРЖИМ весь запуск, как полотно: набор мал
        /// (шесть рамок, лого, аватар, значки), а показывают его на каждом
        /// возврате в меню. Пересборка витрины находит спрайты в кэше и
        /// ставит их тем же кадром. Один пропавший файл прогрев не роняет.</para>
        /// </summary>
        public async Task WarmMenuArtAsync(IReadOnlyList<string> urls)
        {
            if (urls == null || urls.Count == 0 || Assets == null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var loads = new List<Task<Sprite>>(urls.Count);
            foreach (var url in urls)
                if (!string.IsNullOrEmpty(url))
                    loads.Add(LoadQuietAsync(url));
            var sprites = await Task.WhenAll(loads);
            var got = new List<Sprite>(sprites.Length);
            foreach (var s in sprites) if (s != null) got.Add(s);
            RepinSceneSprites(MenuArtSlot, got);
            LvnLog.Trace($"[lvn-warm] арт витрины прогрет: {got.Count} из {loads.Count} "
                       + $"за {clock.ElapsedMilliseconds} мс — держим весь запуск");
        }

        // WhenAll бросил бы первую же ошибку, а остальные спрайты уже приехали:
        // пропавший файл — строка в логе, не сорванный прогрев.
        private async Task<Sprite> LoadQuietAsync(string url)
        {
            try { return await Assets.LoadSpriteAsync(url, _cts?.Token ?? default); }
            catch (System.OperationCanceledException) { return null; }
            catch (System.Exception e)
            {
                LvnLog.Trace($"[lvn-warm] арт витрины не прогрелся: {url} — {e.Message}");
                return null;
            }
        }

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

        /// <summary>
        /// ЧТО ПЕРЕЖИВАЕТ УБОРКУ СЦЕНЫ. Правило — чистая функция: страж
        /// сверяет список, а не читает уборку глазами, и новый слот (арт
        /// витрины) добавляется одним словом, а не четвёртым `continue`.
        ///
        /// <para>Полотно и арт витрины греются на весь запуск: их показывают
        /// после КАЖДОЙ главы, и отпустить их на уборке значит купить чёрную
        /// витрину с всплывающими рамками на каждом выходе.</para>
        ///
        /// <para>СТВОР ПЕРЕЖИВАЕТ УБОРКУ — и его ядро тоже. Картинка ядра
        /// нужна ровно там, где сцену чистят: на уходе в главу и на
        /// возвращении. Сняв пин, мы отдавали её кэшу ровно перед тем, как
        /// она понадобится, — отсюда «иногда шар портала пропадает» (живой
        /// репорт Ильи 28.08).</para>
        ///
        /// <para>ФОН НЕ ОТПУСКАЕМ. Он ОСТАЁТСЯ на экране до нового bg — так
        /// задумано и так написано у обоих мест вызова, — а пин с него
        /// всё-таки снимался. Кэш забирал текстуру прямо из-под видимой
        /// картинки, и полотно белело посреди кадра:
        ///   «[lvn-bg] полотно СТАЛО ПУСТЫМ И БЕЛЫМ: tex=НЕТ» —
        /// под затемнением это и есть «серый экран» в катсцене ухода (лог
        /// Ильи, 27.08). Тот же промах заставлял заново качать и
        /// декодировать фоны, скачанные десять раз до этого. Слот один, и
        /// следующий bg сам заменит его содержимое.</para>
        ///
        /// <para>Облик хранимого актёра (<see cref="KeepActorAlive"/>)
        /// остаётся жить — см. там.</para>
        /// </summary>
        internal static bool SurvivesCleanup(string slot, string keepActor)
        {
            if (slot == MenuCanvasSlot || slot == MenuArtSlot || slot == PortalCoreSlot || slot == "bg")
                return true;
            return !string.IsNullOrEmpty(keepActor) && slot == "actor:" + keepActor;
        }

        private void UnpinAllSceneSprites()
        {
            foreach (var slot in _scenePins.Keys())
            {
                if (SurvivesCleanup(slot, KeepActorAlive)) continue;
                _scenePins.Release(slot);
            }
        }
    }
}
