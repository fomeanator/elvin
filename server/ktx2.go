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
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

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
			if fileExists(ktx2Path) {
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
			return p
		}
	}
	// Not on disk — maybe it's a not-yet-generated downscale variant.
	for _, ext := range sourceExts {
		variant := base + ext
		src := variantSource(variant)
		if src == "" || !fileExists(src) {
			continue
		}
		lock := d.lockFor(variant)
		lock.Lock()
		err := error(nil)
		if !fileExists(variant) {
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
	cmd := exec.CommandContext(ctx, t.bin(),
		"-ktx2", "-uastc", "-uastc_level", "2", "-uastc_rdo_l", "1.0", "-y_flip", "-mipmap",
		srcPath, "-output_file", tmp)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return &transcodeError{srcPath, string(out), err}
	}
	return os.Rename(tmp, ktx2Path)
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

// withKTX2 wraps the content handler: a ".ktx2" request whose encode is
// already cached serves as a plain file; a cold miss answers 404 IMMEDIATELY
// (the client's loader falls back to its PNG/JPG path, exactly as fast as a
// server that never heard of KTX2) while the encode is queued in the
// background for future sessions. basisu missing / no source → plain 404.
func (s *server) withKTX2(d *downscaler, next http.Handler) http.Handler {
	t := newKtx2Transcoder(d)
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
			next.ServeHTTP(w, r) // already encoded — plain file-serve hit
			return
		}
		if t.bin() != "" && hasKtx2Source(ktx2Path) {
			t.enqueue(ktx2Path) // warm for the future; never block this request
		}
		http.NotFound(w, r)
	})
}
