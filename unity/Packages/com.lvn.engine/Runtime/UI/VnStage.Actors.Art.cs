using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ВО ЧТО ФИГУРА ОДЕТА — и чем показать её, пока едет полный арт.
    ///
    /// <para>Две работы, и обе про АРТ, а не про постановку: собрать список
    /// слоёв по команде (три пути к нему — каталог новеллы, блок `cast`
    /// документа, прямые адреса) и, если байтов нет локально, выпустить актёра
    /// вовремя крошечной затемнённой заготовкой, пока полный арт доезжает
    /// фоном.</para>
    ///
    /// <para>Стояли они посреди показа — метода на полтысячи строк, где рядом
    /// живут память сцены, барьеры времени, расталкивание по слотам и
    /// перетаскивание. Показ спрашивает у них дважды и больше про них ничего
    /// не знает.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>Чем кончилась попытка вывести заготовку.</summary>
        private enum Stopgap
        {
            /// <summary>Заготовка не понадобилась или не собралась целиком —
            /// показ идёт обычным путём, ждём полный арт.</summary>
            None,
            /// <summary>Заготовка на экране: актёр уже виден.</summary>
            Shown,
            /// <summary>Сцену закрыли посреди ожидания — показ прекращается.</summary>
            Cancelled,
        }
        /// <summary>
        /// СИЛУЭТ-ЗАГОТОВКА: МЕДЛЕННАЯ СЕТЬ НЕ ЗАДЕРЖИВАЕТ ВЫХОД АКТЁРА.
        ///
        /// <para>Актёр входит вовремя крошечной <c>@mini</c>-заготовкой,
        /// затемнённой тинтом; полный арт доезжает фоном и проявляет его
        /// кроссфейдом облика.</para>
        ///
        /// <para>ТОЛЬКО КОГДА БАЙТОВ НЕТ ЛОКАЛЬНО — ни в кэше, ни в сиде APK.
        /// Локальные байты декодируются за сотни миллисекунд, и заготовка лишь
        /// мигала бы на каждой смене эмоции («уменьшилась и прыгает»).</para>
        ///
        /// <para>ТОЛЬКО НА ПЕРВОМ ВХОДЕ. На уже видимом актёре затемнённая
        /// заготовка читается как вспышка посреди смены лица: видимый держит
        /// прежний облик, пока едет новый, и меняется одним кроссфейдом.</para>
        ///
        /// <para>И ТОЛЬКО ЦЕЛИКОМ. Раньше заготовка собиралась из тех слоёв, у
        /// кого нашёлся <c>@mini</c>, а остальные молча пропускались — и на
        /// экран выходила фигура БЕЗ ЛИЦА (партнёрский репорт 29.08, первая
        /// сессия, пролог). Ждать лишние полсекунды честнее: задержку игрок
        /// читает как загрузку, безликость — как сломанную игру.</para>
        /// </summary>
        private async Task<Stopgap> ShowSilhouetteAsync(
            string id, JObject cmd, ActorArt art, Placement placement, System.Action onClick,
            Task<Sprite>[] loads, bool wardrobeSwap, bool wasVisibleBeforeShow,
            int epoch, string lane, int gen)
        {
            if (!(Theme?.LoadingSilhouette ?? true) || !placement.Show
                || !IsCharacterCommand(cmd) || wardrobeSwap || wasVisibleBeforeShow
                || !((Assets as CachingAssets)?.Loader is Lvn.Content.ContentLoader cl))
                return Stopgap.None;

            var urls = art.Urls;
            bool allLocal = true;
            foreach (var u in urls)
                if (!cl.HasLocalSpriteBytes(u)) { allLocal = false; break; }
            if (allLocal) return Stopgap.None; // всё лежит рядом — полный арт успеет сам

            var allLoads = Task.WhenAll(loads);
            if (await Task.WhenAny(allLoads, Task.Delay(250)) == allLoads)
                return Stopgap.None; // успели за четверть секунды — заготовка ни к чему

            var mini = new List<Sprite>(urls.Count);
            var miniIds = art.Ids != null ? new List<string>(urls.Count) : null;
            var miniRects = art.Rects != null ? new List<Vector4>(urls.Count) : null;
            var miniDefs = art.Defs != null ? new List<SpriteCatalog.ResolvedLayer>(urls.Count) : null;
            for (int i = 0; i < urls.Count; i++)
            {
                var mu = Lvn.Content.DownloadPolicy.MiniVariant(urls[i]);
                if (mu == null) continue;
                Sprite ms = null;
                try { ms = await Assets.LoadSpriteAsync(mu, _cts.Token); }
                catch (OperationCanceledException) { return Stopgap.Cancelled; }
                catch { /* мини недоступен — слой пропускается */ }
                if (ms == null) continue;
                mini.Add(ms);
                miniIds?.Add(i < art.Ids.Count ? art.Ids[i] : null);
                miniRects?.Add(i < art.Rects.Count ? art.Rects[i] : Vector4.zero);
                miniDefs?.Add(i < art.Defs.Count ? art.Defs[i] : default);
            }
            if (mini.Count != urls.Count || !_clock.MayTouch(epoch, lane, gen)) return Stopgap.None;

            var silPl = placement;
            silPl.Silhouette = true;
            LvnLog.Trace($"[lvn-actor] {id}: силуэт-заготовка ({mini.Count} слоёв) — полный арт доедет фоном");
            _renderer?.ApplyActor(id, mini, silPl, onClick, miniIds, miniRects, miniDefs);
            RepinSceneSprites("actor:" + id, mini);   // заготовка на экране — держим
            _memory.SetWhere(id, silPl);              // полный apply увидит «уже видим» → проявление
            return Stopgap.Shown;
        }
        /// <summary>
        /// ВО ЧТО ФИГУРА ОДЕТА — слои, которые надо нарисовать по этой команде.
        ///
        /// <para>Путей к ним три, и они разной силы: каталог новеллы
        /// (<c>manifest.sprites</c> — слои с условиями <c>when</c>), блок
        /// <c>cast</c> самого документа и, наконец, прямые адреса в команде
        /// (<c>body_url</c>/<c>clothes_url</c>/<c>hair_url</c>, иначе одна
        /// картинка <c>sprite_url</c>). Первый нашедшийся и отвечает.</para>
        ///
        /// <para>ПЕРВЫЕ ДВА ПУТИ СПРАШИВАЮТ ОСИ ОДИНАКОВО. Раньше путь
        /// <c>cast</c> брал СЫРЫЕ оси команды: на такого персонажа не
        /// действовали ни переменные ({var} уезжал в имя файла как есть), ни
        /// гардероб — примерка и надетое до него просто не доходили. Два пути
        /// одевали героя по разным правилам, а отличались одной буквой в имени
        /// метода.</para>
        /// </summary>
        private readonly struct ActorArt
        {
            public readonly List<string> Urls;
            /// <summary>Имена слоёв — по ним живут моргание и губы. Есть только
            /// у пути каталога: остальные два слоёв по именам не знают.</summary>
            public readonly List<string> Ids;
            /// <summary>Кусок картинки на слой (x,y,w,h); w≤0 — «весь файл».</summary>
            public readonly List<Vector4> Rects;
            /// <summary>Полные описания слоёв — для костей (родитель, ось, пружина).</summary>
            public readonly List<SpriteCatalog.ResolvedLayer> Defs;

            public ActorArt(List<string> urls, List<string> ids,
                            List<Vector4> rects, List<SpriteCatalog.ResolvedLayer> defs)
            { Urls = urls; Ids = ids; Rects = rects; Defs = defs; }
        }
        private ActorArt ResolveActorArt(string id, JObject cmd)
        {
            if (Catalog != null && Catalog.Has(id))
            {
                var axes = AxesOf(cmd);
                // Настоящая постановка (а не обход предзагрузки, который сюда не
                // заходит) — это наряд, ПОПАВШИЙСЯ ИГРОКУ НА ГЛАЗА: из таких и
                // растёт коллекция всегда открытого гардероба.
                foreach (var ax in axes) LvnWardrobe.MarkSeen(id, ax.Key, ax.Value);
                var rls = Catalog.ResolveLayers(id, axes, CatalogCond());
                // Диагностика облика: «почему лысая/не тот наряд» решается одной
                // строкой лога вместо круга скриншотов — видно, какие слои и из
                // каких осей собрались.
                LvnLog.Trace($"[lvn-actor] {id}: слои [{string.Join(",", rls.ConvertAll(r => r.Id))}] "
                    + $"оси {{{string.Join(", ", System.Linq.Enumerable.Select(axes, kv => kv.Key + "=" + kv.Value))}}}");
                var urls = new List<string>(rls.Count);
                var ids = new List<string>(rls.Count);
                var rects = new List<Vector4>(rls.Count);
                foreach (var rl in rls) { urls.Add(rl.Url); ids.Add(rl.Id); rects.Add(new Vector4(rl.X, rl.Y, rl.W, rl.H)); }
                return new ActorArt(urls, ids, rects, rls);
            }

            if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                var axes = AxesOf(cmd);
                foreach (var ax in axes) LvnWardrobe.MarkSeen(id, ax.Key, ax.Value);
                return new ActorArt(SpriteComposer.Resolve(entity, axes), null, null, null);
            }

            var direct = new List<string>();
            var body = (string)cmd["body_url"]; if (!string.IsNullOrEmpty(body)) direct.Add(body);
            var clothes = (string)cmd["clothes_url"]; if (!string.IsNullOrEmpty(clothes)) direct.Add(clothes);
            var hair = (string)cmd["hair_url"]; if (!string.IsNullOrEmpty(hair)) direct.Add(hair);
            if (direct.Count == 0)
            {
                var sp = (string)cmd["sprite_url"]; if (!string.IsNullOrEmpty(sp)) direct.Add(sp);
            }
            return new ActorArt(direct, null, null, null);
        }
    }
}
