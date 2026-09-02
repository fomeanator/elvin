package main

// ktx2.go — background KTX2 (Basis Universal / UASTC) encoding for the
// content server: a client whose runtime can transcode KTX2 (Unity's
// com.unity.cloud.ktx) requests "<path>.ktx2" instead of "<path>.png"/".jpg".
// Cached files serve statically; a cold miss 404s IMMEDIATELY (client falls
// back to PNG/JPG) and queues a one-at-a-time background encode via the
// basisu CLI (nothing vendored — `brew install basis_universal`), cached to
// disk forever. Unlike downscale.go/astc.go the request never waits: UASTC
// runs seconds per file at full-machine load, and blocking a chapter-entry
// burst on that stalled scenes in practice.
//
// Why KTX2 over the raw-.astc path (astc.go, currently kill-switched in the
// client): Basis UASTC is encoded ONCE and transcoded on-device to whatever
// the GPU speaks — ASTC on modern phones, BC7 on desktop, ETC2 on older
// Android — so one server artifact serves every platform. And the container
// (plus Unity's official reader) owns the block-alignment bookkeeping that
// broke the raw path on non-multiple-of-6 image sizes.
//
// Variant composition: the client asks for the KTX2 of the SAME file its
// PNG path would read — usually the "@2k" downscale variant
// ("X@2k.ktx2"). When that @2k source PNG doesn't exist yet, it is
// materialized first through the shared downscaler; a source that already
// fits inside the 2048 box encodes from the original (mirroring how
// withDownscale serves the original for errFitsAlready).

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"image"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

// КОДИРОВЩИК НЕ ИМЕЕТ ПРАВА ЗАБРАТЬ МАШИНУ.
//
// basisu по умолчанию берёт ВСЕ ядра, и это верно для отдельной машины сборки.
// У нас сервер живёт там же, где игра: прогрев поставил в очередь коды для
// полотна витрины и прочей крупной обшивки — и один кодировщик на восьми
// потоках поднял среднюю нагрузку до 26. В логе 02.09 это видно насквозь:
// расшифровка слоя выросла с 38 до 936 мс, проба сервера отвалилась по
// таймауту (5806 мс на «server resolved»), кадры проваливались на 955 мс.
//
// nice здесь не спасает: он делит процессор между процессами, а беда в том,
// что ОДИН процесс занял все ядра. Ограничивать надо потоки.
//
// Четверть ядер, но не меньше одного: кодирование — работа «когда-нибудь», а
// игра идёт сейчас. Один файл от этого кодируется дольше, и это правильная
// цена: очередь всё равно фоновая, а провал кадра игрок видит.
func ktx2EncodeThreads() int {
	n := runtime.NumCPU() / 4
	if n < 1 {
		n = 1
	}
	return n
}

// ktx2EncodeTimeout bounds a single encode. UASTC level 2 measures ~4s for a
// 1080×2089 sprite on this machine; the largest art (already capped by the
// @2k variant) stays well under a minute.
const ktx2EncodeTimeout = 120 * time.Second

// ktx2Transcoder encodes MISSES IN THE BACKGROUND, one at a time. Unlike the
// downscale/astc middlewares, a cold .ktx2 request does NOT wait for the
// encoder: UASTC runs seconds per file and saturates every core, so a chapter
// entry that bursts a dozen cold requests would stall the scene AND slow the
// PNG fallbacks it races against (live-observed: 2s "decodes" of a 600×900
// cover while basisu owned the machine). Instead the handler answers 404
// immediately — the client falls back to the PNG path it always had — and a
// single worker goroutine grinds the queue so the NEXT session hits the disk
// cache. First visit costs nothing extra; every visit after is compressed.
type ktx2Transcoder struct {
	mu      sync.Mutex
	pending map[string]bool // queued or encoding — dedupes enqueues
	queue   chan string     // ktx2 output paths awaiting encode

	d *downscaler // materializes missing @2k sources (shared with withDownscale)

	binOnce sync.Once
	binPath string // "" if basisu isn't on PATH — every request then 404s straight through
}

func newKtx2Transcoder(d *downscaler) *ktx2Transcoder {
	t := &ktx2Transcoder{
		pending: map[string]bool{},
		queue:   make(chan string, 1024),
		d:       d,
	}
	go t.worker()
	return t
}

// ЭТОМУ АРТУ ПОЛОЖЕН КОД ДЛЯ ВИДЕОКАРТЫ — три отказа, и все три «положено
// растром», а не «не смогли»: пиксель-арт и обшивка интерфейса (блочное сжатие
// с потерями размажет пиксельную сетку и тонкие линии) и крошка-заготовка
// (@mini) — её показывают, пока едет крупный.
//
// Правило стояло ВНУТРИ прогрева, и ленивый обработчик его не знал: запрос
// «X@mini.ktx2» он ставил в очередь и кодировал, отменяя намеренное решение.
// Клиент задаёт тот же вопрос у себя (DownloadPolicy.CodedArt); списки
// сверяет страж TestCodedArtIsOneList.
//
// Путь ожидается в нижнем регистре.
func ktx2Coded(lowPath string) bool {
	return !strings.Contains(lowPath, "/pixel/") &&
		!strings.Contains(lowPath, miniSuffix+".")
}

// ktx2ChromeBox — с какой длинной стороны обшивка интерфейса перестаёт быть
// обшивкой. Ниже — кнопки, рамки, иконки: у них пиксельная сетка и тонкие
// линии, которые блочное сжатие размажет. Выше — полноэкранная картинка,
// живущая в /ui/ по месту, а не по природе.
const ktx2ChromeBox = 1024

// КРУПНОЕ В /ui/ — НЕ ОБШИВКА.
//
// Папку исключали целиком, и правило было верным для мелочи. Полотно витрины
// (/content/ui/menu-canvas.jpg, 2000×1500) под него попадало вместе со всеми:
// кода ему не собирали, показ шёл процессорной распаковкой — 3334 мс в живом
// логе 02.09, вуаль снималась без полотна, и первое, что видел игрок, был
// пустой экран.
//
// Судить надо по размеру, а не по месту: тонкие линии живут в мелких файлах.
func ktx2WorthCoding(path, lowPath string) bool {
	if !strings.Contains(lowPath, "/ui/") {
		return true
	}
	if path == "" {
		return false // исходника нет — судить не по чему
	}
	w, h, ok := imageSize(path)
	if !ok {
		return false // размер не прочитался — считаем обшивкой, как раньше
	}
	return w >= ktx2ChromeBox || h >= ktx2ChromeBox
}

// Что вообще считается КРУПНЫМ АРТОМ ИСТОРИИ. Отдельный вопрос от «положен ли
// код»: у клиента он живёт у уменьшителя, здесь — рядом.
func ktx2LargeArt(lowPath string) bool {
	return strings.Contains(lowPath, "/bg/") || strings.Contains(lowPath, "/art/") ||
		strings.Contains(lowPath, "/sprites/") || strings.Contains(lowPath, "/spine/")
}

// warmAll walks the content tree at startup and queues an encode for every
// piece of story art that has no fresh .ktx2 yet.
//
// КОДИРОВАТЬ НАДО ЗАРАНЕЕ, А НЕ ПО ПЕРВОМУ ЗАПРОСУ. Ленивая схема
// («первый заход платит, остальные получают сжатое») выглядела аккуратной и
// на деле не работала ни разу: первый заход получал 404, клиент считал его
// окончательным, и второго захода не наступало — быстрый формат не
// использовался почти никогда. На живом запуске 01.09 это стоило 1,2–3,7 с
// распаковки на КАЖДЫЙ слой героини вместо 110 мс через ktx2.
//
// Обход идёт в фоне под nice, по одному файлу за раз — тем же работником, что
// и раньше. Игре он не мешает: очередь просто наполняется сразу, а не по
// капле от случайных запросов.
func (t *ktx2Transcoder) warmAll(contentRoot string) {
	if t.bin() == "" {
		return // basisu нет — кодировать нечем, и это видно по первому же запросу
	}
	go func() {
		queued, skipped := 0, 0
		_ = filepath.Walk(contentRoot, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() {
				return nil
			}
			low := strings.ToLower(path)
			if !(strings.HasSuffix(low, ".png") || strings.HasSuffix(low, ".jpg")) {
				return nil
			}
			if !ktx2Coded(low) || !ktx2WorthCoding(path, low) ||
				!(ktx2LargeArt(low) || strings.Contains(low, "/ui/")) {
				skipped++
				return nil
			}
			// КОД НУЖЕН КАЖДОЙ СТУПЕНИ, А НЕ ТОЛЬКО ИСХОДНОМУ ИМЕНИ.
			//
			// Игра просит адрес СВОЕЙ ступени качества: устройство на 1440
			// спрашивает «X@1440.ktx2», экономный режим — «X@1k.ktx2».
			// Прогрев же строил один «X.ktx2», а «@2k» появлялся случайно —
			// от ленивого тракта, когда кто-то однажды его запросил.
			//
			// Итог 01.09 на живом запуске: у героини витрины коды @2k
			// расшифровывались за 117–123 мс, а показ просил @1440, кода не
			// находил и падал в PNG — 864, 2299, 2903, 3877 мс на слой, плюс
			// очередь. Одна и та же картинка грузилась дважды, и второй раз
			// самым дорогим способом. Фон без кода не показывался вовсе.
			//
			// Поэтому ставим в очередь ВСЕ ступени. Промежуточный растр
			// (X@1440.png) работник материализует сам (ensureKtx2Source), а
			// если исходник и так меньше коробки — закодирует исходник под
			// именем ступени, чтобы адрес, который спрашивает игра,
			// существовал. Крошка (@mini) остаётся растром намеренно: её
			// показывают, пока едет крупный.
			stem := strings.TrimSuffix(path, filepath.Ext(path))
			// ВАРИАНТ НЕ ПОРОЖДАЕТ ВАРИАНТА.
			//
			// Обход видит и ИСХОДНИКИ, и уже материализованные ступени
			// («X@2k.png» кладёт на диск сам же тракт). Навесив ступень на
			// ступень, мы просим «X@2k@1440.ktx2» — работник послушно
			// СОЗДАЁТ ради него растр «X@2k@1440.png», кодирует мусор, и на
			// следующем рестарте обход видит уже его и порождает следующее
			// поколение. Рост неограничен по числу перезапусков, а
			// перезапуск у прод-сервера случается на каждой выкладке
			// контента: 01.09 таких файлов набралось 860 за один вечер.
			//
			// Коды всех ступеней ставит в очередь ИСХОДНИК — этого достаточно.
			if variantSource(path) != "" {
				skipped++
				return nil
			}
			for _, v := range []string{"", downscaleSuffix, midSuffix, ecoSuffix} {
				out := stem + v + ".ktx2"
				if fileExists(out) && !ktx2Stale(out) {
					continue
				}
				if t.enqueueWaiting(out) {
					queued++
				}
			}
			return nil
		})
		log.Printf("[ktx2] прогрев кодов (все ступени): в очередь поставлено %d, пропущено файлов %d (пиксель-арт, обшивка, крошки)",
			queued, skipped)
	}()
}

// enqueue schedules a background encode for ktx2Path (deduped). Returns false
// when the queue is full — the request just 404s and a later one retries.
func (t *ktx2Transcoder) enqueue(ktx2Path string) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.pending[ktx2Path] {
		return true
	}
	select {
	case t.queue <- ktx2Path:
		t.pending[ktx2Path] = true
		return true
	default:
		return false
	}
}

// enqueueWaiting — вход ДЛЯ ПРОГРЕВА: он ждёт места в очереди, а не бросает
// работу.
//
// `enqueue` отказывает при полной очереди намеренно: он обслуживает ЗАПРОС, и
// лучше ответить «пока нет», чем держать соединение. У прогрева цена обратная:
// он идёт в своей горутине, никого не держит, и потерянный файл — это код,
// которого не будет, пока кто-нибудь случайно его не спросит.
//
// Разница стала важна 01.09, когда прогрев начал брать все ступени: работы
// вчетверо больше буфера, и с прежним «не влезло — забыли» три четверти
// очереди уходили в тишину. Ровно тот тихий отказ, который потом читается как
// «сервер почему-то не собрал коды».
func (t *ktx2Transcoder) enqueueWaiting(ktx2Path string) bool {
	t.mu.Lock()
	if t.pending[ktx2Path] {
		t.mu.Unlock()
		return false
	}
	t.pending[ktx2Path] = true
	t.mu.Unlock()
	t.queue <- ktx2Path // ждём места: горутина прогрева никого не держит
	return true
}

// worker drains the queue strictly one encode at a time — basisu is already
// multithreaded, so a single job uses the machine well without starving the
// game/server the way three concurrent encodes did.
func (t *ktx2Transcoder) worker() {
	for ktx2Path := range t.queue {
		func() {
			defer func() {
				t.mu.Lock()
				delete(t.pending, ktx2Path)
				t.mu.Unlock()
			}()
			if fileExists(ktx2Path) && !ktx2Stale(ktx2Path) {
				return
			}
			src := ensureKtx2Source(t.d, ktx2Path)
			if src == "" {
				return
			}
			start := time.Now()
			if err := t.transcode(src, ktx2Path); err != nil {
				log.Printf("ktx2: %v", err)
				return
			}
			log.Printf("ktx2: encoded %s in %.1fs", filepath.Base(ktx2Path), time.Since(start).Seconds())
		}()
	}
}

func (t *ktx2Transcoder) bin() string {
	t.binOnce.Do(func() {
		if p, err := exec.LookPath("basisu"); err == nil {
			t.binPath = p
		}
	})
	return t.binPath
}

// ensureKtx2Source finds (or materializes) the image a ".ktx2" request should
// encode from. Returns "" when there is nothing to encode.
//   - sibling source already on disk ("X.png" for "X.ktx2") → use it;
//   - "X@2k.ktx2" whose "X@2k.png" is missing → generate the downscale from
//     the original first (shared single-flight downscaler); errFitsAlready →
//     encode straight from the original.
func ensureKtx2Source(d *downscaler, ktx2Path string) string {
	base := strings.TrimSuffix(ktx2Path, filepath.Ext(ktx2Path))
	for _, ext := range sourceExts {
		if p := base + ext; fileExists(p) {
			// An on-disk @2k.png older than its original is yesterday's art —
			// skip to the materialization loop below, which regenerates it.
			if src := variantSource(p); src != "" && fileExists(src) && sourceNewer(src, p) {
				break
			}
			return p
		}
	}
	// Not on disk (or stale) — a downscale variant to (re)generate.
	for _, ext := range sourceExts {
		variant := base + ext
		src := variantSource(variant)
		if src == "" || !fileExists(src) {
			continue
		}
		lock := d.lockFor(variant)
		lock.Lock()
		err := error(nil)
		if !fileExists(variant) || sourceNewer(src, variant) {
			heavyGen <- struct{}{}
			err = d.generate(src, variant)
			<-heavyGen
		}
		lock.Unlock()
		switch {
		case err == nil:
			return variant
		case errors.Is(err, errFitsAlready):
			return src // small enough already — the original IS the variant
		default:
			log.Printf("ktx2: downscale for %s: %v", ktx2Path, err)
			return ""
		}
	}
	return ""
}

// transcode invokes basisu (UASTC LDR, effort 2, RDO 1.0 — high quality at
// ~2 bits/texel; transcodes on-device to ASTC/BC7/ETC2), writing to a temp
// file and renaming into place so a concurrent reader never sees a partial
// file and a crashed encode never poisons the cache.
func (t *ktx2Transcoder) transcode(srcPath, ktx2Path string) error {
	// ФОРМАТ УЗНАЮТ ПО СОДЕРЖИМОМУ, А НЕ ПО ИМЕНИ. basisu смотрит на
	// расширение, и файл, названный .jpg с PNG внутри, он просто отказывается
	// читать: "Failed reading source image". На проде таких оказался 191 —
	// весь импортированный арт, — и ни у одного не было ktx2: сжатие молча
	// пропускало их годами, а фоны ехали к игроку несжатыми.
	//
	// Отказ был не виден никому: клиент на 404 честно берёт PNG-путь, картинка
	// на экране есть, и только вес трафика говорит правду.
	srcPath, undo := honestExtension(srcPath)
	defer undo()

	dir, base := filepath.Split(ktx2Path)
	ext := filepath.Ext(base)
	tmp := filepath.Join(dir, fmt.Sprintf("%s.tmp-%d%s", strings.TrimSuffix(base, ext), time.Now().UnixNano(), ext))
	defer os.Remove(tmp) // no-op once renamed away

	ctx, cancel := context.WithTimeout(context.Background(), ktx2EncodeTimeout)
	defer cancel()
	// -y_flip bakes Unity's bottom-up texture orientation into the encode —
	// GPU-compressed pixels can't be flipped client-side, and the sprite path
	// has no per-draw UV flip (the KTX docs themselves recommend baking it).
	// -mipmap ships the full chain: minified draws (actors scaled down, zoomed
	// scenes) sample a proper mip instead of shimmering over a 2K level 0.
	// ~+33% bytes on art the compression just shrank 4-8× — a good trade.
	args := []string{"-ktx2", "-uastc", "-uastc_level", "2", "-uastc_rdo_l", "1.0", "-y_flip", "-mipmap",
		"-max_threads", strconv.Itoa(ktx2EncodeThreads()),
		srcPath, "-output_file", tmp}
	var cmd *exec.Cmd
	// The encoder is a BACKGROUND filler that saturates every core it gets —
	// on a small host it must always lose the CPU to live player traffic.
	// nice 19 keeps the queue draining on idle cycles only.
	if nicePath, err := exec.LookPath("nice"); err == nil {
		cmd = exec.CommandContext(ctx, nicePath, append([]string{"-n", "19", t.bin()}, args...)...)
	} else {
		cmd = exec.CommandContext(ctx, t.bin(), args...)
	}
	out, err := cmd.CombinedOutput()
	if err != nil {
		return &transcodeError{srcPath, string(out), err}
	}
	return os.Rename(tmp, ktx2Path)
}

// ktx2SourceFor — растровый файл, ИЗ КОТОРОГО кодируется этот .ktx2: сосед по
// имени, а для имени со ступенью — оригинал за ней. Нужен, чтобы спросить у
// картинки размер, не раскодировав её (см. ktx2WorthCoding). Пустая строка —
// исходника рядом нет.
func ktx2SourceFor(ktx2Path string) string {
	base := strings.TrimSuffix(ktx2Path, filepath.Ext(ktx2Path))
	for _, ext := range sourceExts {
		if fileExists(base + ext) {
			return base + ext
		}
		if src := variantSource(base + ext); src != "" && fileExists(src) {
			return src
		}
	}
	return ""
}

// hasKtx2Source is the HANDLER-side eligibility check — fast fileExists probes
// only, no image work: a sibling source on disk, or a variant name whose
// original exists (the worker materializes the @2k itself, later).
func hasKtx2Source(ktx2Path string) bool {
	base := strings.TrimSuffix(ktx2Path, filepath.Ext(ktx2Path))
	for _, ext := range sourceExts {
		if fileExists(base + ext) {
			return true
		}
		if src := variantSource(base + ext); src != "" && fileExists(src) {
			return true
		}
	}
	return false
}

// ktx2Stale reports whether an encoded ktx2 is older than the image it
// derives from. BOTH layers count: the sibling encode source ("X@2k.png" for
// "X@2k.ktx2") and, for variant names, the ORIGINAL behind it ("X.png") —
// replacing the original makes the whole chain stale even while the
// intermediate @2k.png still sits on disk untouched.
//
// Mtime alone is not enough: an encode made from art that has since been
// REPLACED IN PLACE (same name, same or older timestamp) keeps serving
// yesterday's pixels forever. So geometry counts too — see ktx2Misshapen.
func ktx2Stale(ktx2Path string) bool {
	base := strings.TrimSuffix(ktx2Path, filepath.Ext(ktx2Path))
	for _, ext := range sourceExts {
		p := base + ext
		if fileExists(p) && sourceNewer(p, ktx2Path) {
			return true
		}
		if src := variantSource(p); src != "" && fileExists(src) && sourceNewer(src, ktx2Path) {
			return true
		}
	}
	return ktx2Misshapen(ktx2Path)
}

// ktx2Dims reads the container's own pixelWidth/pixelHeight — the KTX2 header
// is a fixed layout, so this is a 28-byte read, not an image decode.
func ktx2Dims(ktx2Path string) (int, int, bool) {
	f, err := os.Open(ktx2Path)
	if err != nil {
		return 0, 0, false
	}
	defer f.Close()
	var head [28]byte
	if _, err := io.ReadFull(f, head[:]); err != nil {
		return 0, 0, false
	}
	if string(head[:12]) != ktx2Identifier {
		return 0, 0, false
	}
	return int(binary.LittleEndian.Uint32(head[20:24])),
		int(binary.LittleEndian.Uint32(head[24:28])), true
}

// ktx2Identifier is the fixed 12-byte magic every KTX2 file opens with.
const ktx2Identifier = "\xabKTX 20\xbb\r\n\x1a\n"

// ktx2Misshapen reports whether an encode's picture is a different SIZE than
// the art it claims to represent — the signature of an encode made from a
// source that was later replaced by differently-shaped art under the same
// name. Live case (26.08): 37 of Victoria's layers were 1210×2048 encodes of
// long-gone art while the PNGs beside them were 1600×2048, so every layer the
// KTX2 path served was squeezed horizontally AND carried the old source's
// coarse detail — the "pixels on the necklace" report. Nothing in the
// mtime-only staleness test could see it: the bad encodes were NEWER than the
// art. Both dimensions are compared against what the downscaler would produce
// (a source inside the box passes through at its own size), with a couple of
// pixels of slack for rounding.
func ktx2Misshapen(ktx2Path string) bool {
	w, h, ok := ktx2Dims(ktx2Path)
	if !ok || w <= 0 || h <= 0 {
		return true // unreadable header — no reason to trust the payload
	}
	base := strings.TrimSuffix(ktx2Path, filepath.Ext(ktx2Path))
	for _, ext := range sourceExts {
		p := base + ext
		src, box := p, downscaleMax
		if s, b := variantSourceBox(p); s != "" {
			if fileExists(p) {
				src, box = p, 0 // the encode source itself is on disk — measure THAT
			} else {
				src, box = s, b
			}
		}
		if !fileExists(src) {
			continue
		}
		sw, sh, err := imageDims(src)
		if err != nil {
			return false // can't measure — leave the encode alone
		}
		ew, eh := sw, sh
		if box > 0 && (sw > box || sh > box) {
			scale := float64(box) / float64(max(sw, sh))
			ew, eh = max(1, int(float64(sw)*scale+0.5)), max(1, int(float64(sh)*scale+0.5))
		}
		return abs(w-ew) > 2 || abs(h-eh) > 2
	}
	return false
}

// imageDims reads just the header of an image file.
func imageDims(path string) (int, int, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, 0, err
	}
	defer f.Close()
	cfg, _, err := image.DecodeConfig(f)
	if err != nil {
		return 0, 0, err
	}
	return cfg.Width, cfg.Height, nil
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

// withKTX2 wraps the content handler: a ".ktx2" request whose encode is
// already cached serves as a plain file; a cold miss answers 404 IMMEDIATELY
// (the client's loader falls back to its PNG/JPG path, exactly as fast as a
// server that never heard of KTX2) while the encode is queued in the
// background for future sessions. basisu missing / no source → plain 404.
func (s *server) withKTX2(d *downscaler, next http.Handler) http.Handler {
	t := newKtx2Transcoder(d)
	t.warmAll(s.content)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(strings.ToLower(r.URL.Path), ".ktx2") {
			next.ServeHTTP(w, r)
			return
		}

		rel := strings.TrimPrefix(r.URL.Path, "/content/")
		ktx2Path, ok := s.contentPath(rel)
		if !ok {
			http.NotFound(w, r)
			return
		}
		if fileExists(ktx2Path) {
			if !ktx2Stale(ktx2Path) {
				next.ServeHTTP(w, r) // already encoded and still fresh — plain file-serve hit
				return
			}
			// The source image was replaced after this encode — serving it
			// would show yesterday's art under today's cache key. Drop it so
			// the static handler can't pick it up and fall through to the
			// 404-now/re-encode-behind path: THIS request gets the fresh
			// PNG/JPG fallback, the next session gets the fresh ktx2.
			_ = os.Remove(ktx2Path)
		}
		// ТОТ ЖЕ ВОПРОС, ЧТО У ПРОГРЕВА. Без него запрос «X@mini.ktx2»
		// ставился в очередь и кодировался — намеренное решение «крошка живёт
		// растром» отменялось тем, что кто-то один раз спросил.
		low := strings.ToLower(ktx2Path)
		if t.bin() != "" && ktx2Coded(low) && hasKtx2Source(ktx2Path) &&
			ktx2WorthCoding(ktx2SourceFor(ktx2Path), low) {
			t.enqueue(ktx2Path) // warm for the future; never block this request
		}
		http.NotFound(w, r)
	})
}

// honestExtension даёт кодировщику путь, чьё расширение НЕ ВРЁТ о содержимом.
//
// Когда имя и формат совпадают — возвращается сам путь, без работы. Когда
// расходятся, рядом во временном каталоге появляется ссылка с правильным
// расширением: копировать сотни мегабайт ради подсказки декодеру незачем.
//
// Формат определяется по сигнатуре: у PNG она восьмибайтная и однозначная, у
// JPEG — три байта SOI. Ничего третьего кодировщику мы и не отдаём.
func honestExtension(path string) (string, func()) {
	nop := func() {}
	f, err := os.Open(path)
	if err != nil {
		return path, nop
	}
	var head [8]byte
	n, _ := io.ReadFull(f, head[:])
	f.Close()
	if n < 3 {
		return path, nop
	}

	real := ""
	switch {
	case n >= 8 && string(head[:8]) == "\x89PNG\r\n\x1a\n":
		real = ".png"
	case head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF:
		real = ".jpg"
	default:
		return path, nop // не наш формат — пусть кодировщик скажет своё слово
	}

	ext := strings.ToLower(filepath.Ext(path))
	if ext == real || (real == ".jpg" && ext == ".jpeg") {
		return path, nop
	}

	dir, err := os.MkdirTemp("", "lvn-src-")
	if err != nil {
		return path, nop
	}
	link := filepath.Join(dir, strings.TrimSuffix(filepath.Base(path), filepath.Ext(path))+real)
	abs, err := filepath.Abs(path)
	if err != nil {
		abs = path
	}
	if err := os.Symlink(abs, link); err != nil {
		os.RemoveAll(dir)
		return path, nop
	}
	log.Printf("[ktx2] %s назван %s, а внутри %s — кодируем по содержимому", filepath.Base(path), ext, real)
	return link, func() { os.RemoveAll(dir) }
}
