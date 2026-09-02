using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if LVN_KTX2
using KtxUnity; // also brings the TextureOrientation extension methods into scope
using Unity.Collections;
#endif

namespace Lvn.Content
{
    /// <summary>
    /// ЕДИНСТВЕННЫЙ формат арта истории: KTX2 (Basis Universal / UASTC).
    /// Предшественника — сырой .astc — сняли 01.09: он лежал выключенным с
    /// 06.07 (блочная выгрузка ломалась на невыровненных размерах), а этот
    /// умеет то же самое и везде. Второго формата нет намеренно: пока их
    /// было два, «который из них не работает» никто не спрашивал.
    /// A sprite load first asks
    /// the server for the "@2k.ktx2" variant (server/ktx2.go encodes it on
    /// demand and caches to disk) and, on success, transcodes it IN A JOB
    /// THREAD to whatever this GPU speaks — ASTC on modern phones, BC7 on
    /// desktop, ETC2 on older Android — via Unity's official
    /// <c>com.unity.cloud.ktx</c> package, which owns all block-alignment
    /// bookkeeping.
    ///
    /// Why: PNG/JPG shrink only wire/disk — decoded, a texture is full RGBA in
    /// VRAM (16 MB per @2k background). A transcoded UASTC texture is GPU-
    /// sampled compressed: ~4–8× less VRAM and a millisecond-scale transcode
    /// instead of a 250 ms image decode.
    ///
    /// Strictly opt-in and additive, three ways:
    ///  • compile-time — everything meaningful sits behind LVN_KTX2, defined
    ///    (asmdef versionDefines) only when com.unity.cloud.ktx is installed;
    ///  • server — no basisu on PATH → 404 → session-latched fallback;
    ///  • per-asset — any decode error falls through to the PNG/JPG path.
    /// Orientation: the server encodes with -y_flip (bottom-up, Unity's
    /// convention) because compressed pixels can't be flipped client-side and
    /// the sprite path has no per-draw UV flip; the KTX orientation metadata is
    /// deliberately ignored here.
    /// </summary>
    public partial class ContentLoader
    {
        // ОДИН ФОРМАТ ДЛЯ АРТА ИСТОРИИ — И НИКАКОГО ТИХОГО ОТКАТА.
        //
        // Здесь дважды стояла защёлка «сдаёмся на растр»: сперва сессионная
        // («первый промах гасит весь тракт»), потом счётчик восьми промахов
        // подряд. Обе были костылём под одну и ту же беду: сервер кодирует
        // .ktx2 ЛЕНИВО и на первый запрос честно отвечает «пока нет», а клиент
        // читал этот ответ как «нет никогда».
        //
        // На холодном старте холодных файлов ровно восемь и больше — значит
        // защёлка срабатывала ВСЕГДА, весь тракт уходил в растр, и героиня
        // распаковывалась по 1,2–3,7 с на слой вместо 110 мс (живой лог
        // 01.09). Быстрый формат при этом числился «сделанным»: 62 файла
        // @2k.ktx2 в каталоге и почти ни одного показа через них.
        //
        // Костыль был удобнее беды: он делал поломку незаметной. Поэтому его
        // здесь больше нет. «Ещё не закодирован» — это ПОВТОРИТЬ ПОЗЖЕ, а не
        // «переключиться на медленный путь навсегда».
        /// <summary>
        /// ОДИН ФОРМАТ НА АРТ ИСТОРИИ. Выключается только для разбора: с
        /// растровым запасным путём поломка кодов становится незаметной, и
        /// именно так она прожила полгода.
        /// </summary>
        public static bool Ktx2Only = true;

        /// <summary>
        /// КОД ВЗЯТЬ НЕОТКУДА — и это не «удобнее не брать», а «нечем».
        ///
        /// <para>Строгое правило «арт истории только кодом» держится на том, что
        /// код в принципе достижим. Три случая, когда он недостижим по
        /// устройству, а не по лени:</para>
        /// <list type="bullet">
        ///   <item>видеокарта не рисует формат (проба на старте);</item>
        ///   <item><b>в сборке нет пакета-декодера</b> — без
        ///   <c>LVN_KTX2</c> расшифровывать нечем вовсе. Этого не хватало
        ///   полдня: признак выставлялся ТОЛЬКО внутри той же условной сборки,
        ///   и движок, собранный без пакета, не показывал арт истории ВООБЩЕ,
        ///   советуя при этом проверить кодировщик на сервере;</item>
        ///   <item>содержимое лежит локально (сид в установщике, каталог на
        ///   диске) — в него кладут оригиналы, кодов там нет и не будет.</item>
        /// </list>
        ///
        /// <para>Разница с костылём принципиальная и её стоит держать в голове:
        /// костыль срабатывает, когда правило ВЫПОЛНИТЬ ТРУДНО, и потому прячет
        /// поломку. Здесь правило выполнить НЕЧЕМ, и растр — объявленный путь.
        /// </para>
        /// </summary>
        internal bool GpuCannotKtx2
        {
            get
            {
#if !LVN_KTX2
                return true;   // декодера нет в сборке
#else
                if (_local) return true;   // локальная база: коды туда не кладут
                lock (_ktx2Lock) return _gpuWithoutKtx2;
#endif
            }
        }

        /// <summary>Видеокарта не рисует ktx2 — единственное честное «нельзя».
        /// Свойство устройства: узнаётся один раз и не меняется.</summary>
        private bool _gpuWithoutKtx2;

        private static bool _saidNoKtx2;

        /// <summary>СКАЗАТЬ ОДИН РАЗ, ПОЧЕМУ ПОКАЗ ИДЁТ ПРОЦЕССОРОМ.
        ///
        /// <para>Причин отказа три — нет декодера в сборке, локальная база,
        /// видеокарта не потянула, — и все три выглядели в логе ОДИНАКОВО:
        /// никак. Картинки просто распаковывались процессором, и понять, почему
        /// формат для видеокарты не работает, было нечем.</para></summary>
        private static void NoteNoKtx2Once(string why)
        {
            if (_saidNoKtx2) return;
            _saidNoKtx2 = true;
            Debug.LogWarning("[lvn-ktx2] показ идёт процессорной распаковкой PNG/JPEG — " + why);
        }

        /// <summary>
        /// ХОЛОДНЫЙ КОД И БИТЫЙ КОД — РАЗНЫЕ БЕДЫ, И СЧЁТ У НИХ РАЗНЫЙ.
        ///
        /// <para>«Сервер ещё не собрал» проходит само: файл ждут и просят
        /// снова. «Файл на сервере битый» не проходит никогда — и его тоже
        /// просили снова, потому что чужой успех очищал метки ВСЕМ. Каждый
        /// показ битого арта качал его целиком, расшифровывал, выбрасывал — и
        /// так до конца сессии.</para>
        ///
        /// <para>Поэтому не множество, а счёт промахов на адрес: три подряд —
        /// файл считается битым до конца сессии, и его перестают просить.
        /// Чужой успех сбрасывает счёт только тем, кто ещё не исчерпал своё, —
        /// «сервер догнал» им поможет, «файл битый» им не оправдание.</para>
        /// </summary>
        private const int Ktx2Strikes = 3;
        private readonly Dictionary<string, int> _ktx2Cold = new Dictionary<string, int>();
        private readonly object _ktx2Lock = new object();

        /// <summary>Сколько раз подождать код, который сервер ещё не собрал, и
        /// по сколько. Полторы секунды на попытку — примерно столько basisu
        /// кодирует один крупный файл; пять попыток покрывают очередь из
        /// нескольких, не превращая холодный старт в зависание.</summary>
        private const int Ktx2Waits = 5;
        private const int Ktx2WaitMs = 1500;

        /// <summary>Забыть, что этот код был холодным: иначе повтор уйдёт в тот
        /// же пропуск и ждать будет незачем.</summary>
        private void ForgetKtx2Cold(string url)
        {
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url == null) return;
            lock (_ktx2Lock)
            {
                // Исчерпавшего своё не будим: он битый, а не холодный.
                if (_ktx2Cold.TryGetValue(ktx2Url, out var strikes) && strikes < Ktx2Strikes)
                    _ktx2Cold.Remove(ktx2Url);
            }
        }

        /// <summary>Пропустить ktx2 для этого адреса ИМЕННО СЕЙЧАС: он холодный,
        /// сервер его кодирует. Следующий заход спросит снова — память о холоде
        /// живёт до первого попадания, а не до перезапуска.</summary>
        private bool Ktx2Skipped(string ktx2Url)
        {
            lock (_ktx2Lock) return _gpuWithoutKtx2 || _ktx2Cold.ContainsKey(ktx2Url);
        }

        private void NoteKtx2Miss(string ktx2Url)
        {
            lock (_ktx2Lock)
            {
                _ktx2Cold.TryGetValue(ktx2Url, out var strikes);
                _ktx2Cold[ktx2Url] = strikes + 1;
            }
        }

        /// <summary>Код доехал — значит сервер их кодирует, и холодные адреса
        /// стоит спросить заново: очередь кодирования движется.</summary>
        private void NoteKtx2Hit()
        {
            lock (_ktx2Lock)
            {
                // Сервер догнал — будим тех, кто ещё не исчерпал своё.
                List<string> woken = null;
                foreach (var kv in _ktx2Cold)
                    if (kv.Value < Ktx2Strikes) (woken ??= new List<string>()).Add(kv.Key);
                if (woken != null) foreach (var k in woken) _ktx2Cold.Remove(k);
            }
        }

#if LVN_KTX2
        // GPU honesty probe, once per session: SystemInfo happily CLAIMS
        // ASTC/ETC2 support on GPUs that then sample the texture as black —
        // live-hit on BlueStacks (every ktx2-transcoded texture rendered as a
        // black cutout while raw RGBA art was fine). A tiny solid-red KTX2
        // ships in Resources; transcode it, draw it into a RenderTexture, read
        // the pixel back — not red means the whole path lies on this GPU and
        // the session falls back to PNG/JPG.
        private static bool? _gpuHonest;

        private static async Task<bool> GpuRendersKtx2Async()
        {
            if (_gpuHonest.HasValue) return _gpuHonest.Value;
            try
            {
                var probe = Resources.Load<TextAsset>("LvnKtxProbe");
                if (probe == null) return (_gpuHonest = true).Value; // no probe shipped — trust the GPU
                using var data = new NativeArray<byte>(probe.bytes, Allocator.Persistent);
                var ktx = new KtxTexture();
                var result = await ktx.LoadFromBytes(data.AsReadOnly(), linear: false);
                if (result?.texture == null) return (_gpuHonest = false).Value;

                // Та же пересъёмка, что у переноса под бюджет памяти, — через
                // общий дом: пиксель пробника надо ПРОЧИТАТЬ, поэтому текстура
                // остаётся читаемой, а исходник гасим сами строкой ниже.
                var read = LvnTexCopy.Rescale(result.texture, 4, 4, readable: true);
                var c = read.GetPixel(2, 2);
                UnityEngine.Object.Destroy(read);
                UnityEngine.Object.Destroy(result.texture);
                bool honest = c.r > 0.5f && c.g < 0.3f && c.b < 0.3f; // the probe is solid red
                if (!honest)
                    Debug.LogWarning($"[lvn-content] ktx2 disabled for this session: GPU claims support but sampled the probe as {c} (emulator?) — falling back to PNG/JPG");
                _gpuHonest = honest;
                return honest;
            }
            catch (System.Exception ex)
            {
                // ОТКАЗ НАЗЫВАЕТ СЕБЯ. Раньше здесь стоял немой `catch`, и весь
                // тракт видеокарты выключался без единого слова: в логе просто
                // не появлялось ни одной строки про ktx2, а картинки как ни в
                // чём не бывало распаковывались процессором по 800–6000 мс.
                // Отличить «выключено пробой» от «не собрано» и от «сервер не
                // отдал» было НЕЧЕМ — три разные беды выглядели одинаково.
                Debug.LogWarning("[lvn-ktx2] проба не выполнилась → весь показ идёт "
                               + "процессорной распаковкой PNG/JPEG до конца сессии. "
                               + ex.GetType().Name + ": " + ex.Message);
                _gpuHonest = false; // a probe that can't even run is a no
                return false;
            }
        }
#endif

        // Attempts the KTX2 path for `url`. Returns (null, 0) on ANY failure —
        // the caller (DecodeSpriteAsync) then runs the ordinary decode exactly
        // as if this method didn't exist.
        private async Task<(Sprite sprite, long bytes)> TryDecodeKtx2Async(string url, CancellationToken ct)
        {
#if !LVN_KTX2
            NoteNoKtx2Once("в сборке нет декодера: пакет com.unity.cloud.ktx не подключён к ПРОЕКТУ "
                         + "(признак LVN_KTX2 объявляется его наличием в manifest.json)");
            return (null, 0);
#else
            if (_local)
                NoteNoKtx2Once("база локальная (file:// или jar:) — коды для видеокарты туда не кладут");
#endif
#if LVN_KTX2
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url == null || Ktx2Skipped(ktx2Url)) return (null, 0);
            // ЕДИНСТВЕННАЯ законная причина не показывать через ktx2 —
            // видеокарта, которая его не рисует. Это свойство устройства, а не
            // сервера, и потому оно ПОСТОЯННОЕ: спрашивать её каждый кадр
            // незачем.
            if (!await GpuRendersKtx2Async()) { _gpuWithoutKtx2 = true; return (null, 0); }

            byte[] bytes;
            try
            {
                bytes = await DownloadAssetBytes(ktx2Url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                NoteKtx2Miss(ktx2Url); // этот файл — мимо; тракт живёт
                return (null, 0);
            }
            if (bytes == null || bytes.Length == 0) { NoteKtx2Miss(ktx2Url); return (null, 0); }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var data = new NativeArray<byte>(bytes, Allocator.Persistent);
                var ktx = new KtxTexture();
                var result = await ktx.LoadFromBytes(data.AsReadOnly(), linear: false);
                if (result?.texture == null)
                {
                    // Байты не декодятся — в кэше обрезок (скачан, пока сервер
                    // ещё кодировал): выбрасываем, следующий заход перекачает
                    // целый. Битое не должно залипать («данные восстановить» —
                    // самолечением, Илья 27.08).
                    DeleteCachedAsset(ktx2Url);
                    NoteKtx2Miss(ktx2Url);
                    return (null, 0);
                }

                var tex = result.texture;
                tex.wrapMode = TextureWrapMode.Clamp;
                // The server bakes a mip chain into every encode (basisu -mipmap);
                // trilinear blends between mips so minified art (shrunk actors,
                // zoom-outs) doesn't shimmer. Bilinear when a chain is absent.
                tex.filterMode = tex.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                // РЕКТ — ПО ОРИГИНАЛУ, НЕ ПО ТЕКСТУРЕ. Блочные GPU-форматы
                // паддятся до кратности 4: слой 1210 px становится текстурой
                // 1212 px, и спрайт «на всю текстуру» менял аспект и смещал
                // контент на пару пикселей против обычного пути («героиня
                // меньше и перескакивает» — живой репорт). Контейнер KTX2
                // помнит исходные размеры; паддинг лежит в хвосте данных
                // (верх текстуры) — рект от низа его отрезает.
                // Размеры — ИЗ ЗАГОЛОВКА КОНТЕЙНЕРА (pixelWidth/pixelHeight,
                // little-endian u32 по смещениям 20/24 спеки KTX2), не из
                // ktx.baseWidth: LoadFromBytes сам делает Dispose() в конце,
                // и обращение к свойству после него кидало NRE — уже
                // транскоженная текстура выбрасывалась, файл стирался из
                // кэша как «битый», и каждый показ платил полный PNG-декод
                // (живой лог 26.08).
                uint origW = bytes.Length >= 28 ? BitConverter.ToUInt32(bytes, 20) : 0;
                uint origH = bytes.Length >= 28 ? BitConverter.ToUInt32(bytes, 24) : 0;
                float w = origW > 0 && origW <= (uint)tex.width ? origW : (uint)tex.width;
                float h = origH > 0 && origH <= (uint)tex.height ? origH : (uint)tex.height;
                var sprite = Sprite.Create(tex, new Rect(0, 0, w, h),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                NoteKtx2Hit(); // «жив» = ДЕКОД прошёл: сброс до декода прятал серию битых файлов от общего счётчика
                if (sw.ElapsedMilliseconds > 30)
                    // БЕЗ orientation в логе: result.orientation бывает null, и
                    // NRE в ЛОГ-СТРОКЕ ронял весь декод уже ПОСЛЕ успешного
                    // транскода — целый файл читался как «битый» (живой стек
                    // 27.08, hair_orchid_red@2k).
                    LvnLog.Trace($"[lvn-perf] ktx2 transcode {ktx2Url}: {sw.ElapsedMilliseconds}ms ({tex.width}x{tex.height}, {tex.format})");
                // Budget the LRU by the COMPRESSED size — that's what actually
                // occupies VRAM; charging width*height*4 would evict 4-8× early.
                return (sprite, bytes.LongLength);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Один битый файл ≠ сломанный транскодер: помечаем файл, тракт
                // живёт; серию подряд добьёт общий счётчик промахов. Битые
                // байты выбрасываются из кэша — перекачаются целыми.
                Debug.LogWarning($"[lvn-content] ktx2 decode failed for {ktx2Url}: {ex.Message}");
                DeleteCachedAsset(ktx2Url);
                NoteKtx2Miss(ktx2Url);
                return (null, 0);
            }
#endif
        }

        /// <summary>Байты этого арта уже ДОСТУПНЫ ЛОКАЛЬНО (диск-кэш любого из
        /// файлов показа — ktx2/@2k/оригинал — или сид в APK)? Решает, нужен ли
        /// «силуэт-проявление»: локальные байты декодируются за сотни мс, и
        /// заготовка только мигала бы; силуэт — лекарство от СЕТИ, не от декода.</summary>
        public bool HasLocalSpriteBytes(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            var v2k = DownloadPolicy.DownscaleVariant(url);
            var k = Ktx2UrlFor(url);
            if (k != null && IsAssetCached(k)) return true;
            if (v2k != null && IsAssetCached(v2k)) return true;
            if (IsAssetCached(url)) return true;
            // Сид: файл лежит в APK — чтение локальное и мгновенное.
            // Ключ сида считает ОДИН метод (ContentLoader.Seed): здесь стояла
            // его копия, и разойдись они — файл из APK находился бы через раз.
            if (SeedKey(url) != null) return true;
            return false;
        }

        /// <summary>Байтовый прогрев ТОГО ЖЕ файла, который возьмёт показ:
        /// живой KTX2-тракт → .ktx2, иначе обычный вариант. Без декода — это
        /// путь экрана загрузки (см. AssetScheduler.Warm).</summary>
        public async Task PrefetchSpriteBytes(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return;
#if LVN_KTX2
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url != null && !Ktx2Skipped(ktx2Url) && await GpuRendersKtx2Async())
            {
                try
                {
                    var b = await DownloadAssetBytes(ktx2Url, ct);
                    if (b != null && b.Length > 0) { NoteKtx2Hit(); return; }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* провалился в обычный файл ниже */ }
                NoteKtx2Miss(ktx2Url);
            }
#endif
            // Обычный путь: крупный арт показывается из @2k-варианта — греем
            // его; нет варианта (мелкий арт / статический хост) — оригинал.
            var variant = DownloadPolicy.DownscaleVariant(url);
            if (variant != null)
            {
                try { await DownloadAssetBytes(variant, ct); return; }
                catch (OperationCanceledException) { throw; }
                catch { /* вариант не отдался — оригинал ниже */ }
            }
            await DownloadAssetBytes(url, ct);
        }

        // Maps a sprite url onto the KTX2 the server can serve for it. Only
        // large story art plays: the "@2k" display variant maps by extension
        // swap, and an ORIGINAL large-art url maps through its @2k name (the
        // server encodes from the original when the source already fits the 2K
        // box — same errFitsAlready contract as the PNG variant). Pixel art and
        // UI skins return null and never leave the ordinary path.
        internal static string Ktx2UrlFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            // ЛЮБАЯ СТУПЕНЬ КАЧЕСТВА, а не только «@2k». Здесь стояло имя ОДНОЙ
            // ступени, и на устройстве, выбравшем другую (@1440), код рядом с
            // ней даже не искался: DownscaleVariant отказывается строить вариант
            // от адреса, в котором «@» уже есть, — и путь выходил молча, до
            // первой строки лога. Формат для видеокарты был написан с обеих
            // сторон, сервер его отдавал, и он не сработал НИ РАЗУ.
            // ПОЛОЖЕН ЛИ ЭТОМУ АРТУ КОД — отдельный вопрос, и раньше он
            // задавался ЗАОДНО: исключения (пиксель-арт, обшивка, крошка)
            // наследовались от уменьшителя, через который проходил каждый
            // адрес. Стоило пропустить уменьшитель — исключения ушли с ним, и
            // крошка @mini, которую нарочно не кодируют нигде, стала ждать
            // 7.5 с несуществующего кода вместо мгновенного показа.
            if (!DownloadPolicy.CodedArt(url)) return null;
            // ОТКУДА БРАТЬ ИМЯ КОДА — три случая, и третьего не было.
            //
            // Ступень уже в адресе — берём как есть. Ступени нет, но арт
            // крупный — идём через уменьшитель, показ возьмёт тот же вариант.
            // А обшивка интерфейса ступеней НЕ ПОЛУЧАЕТ (уменьшитель её не
            // обслуживает — у кнопок и рамок вариантов не бывает), и на ней
            // отображение возвращало null: клиент не просил кода НИКОГДА, а
            // сервер его кодировал. Полотно витрины так и грелось процессором
            // 3.3 с, пока кодировщик жёг ядра впустую (живой лог 02.09).
            //
            // Третий случай — код от исходного имени. Прогрев сервера именно
            // его и собирает: список ступеней начинается с пустой.
            var basis = url.IndexOf('@') >= 0
                ? url
                : (DownloadPolicy.DownscaleVariant(url) ?? url);
            return DownloadPolicy.SplitSourceImage(basis, out var stem, out _) ? stem + ".ktx2" : null;
        }
    }
}
