package main

// ktx2.go — on-demand KTX2 (Basis Universal / UASTC) transcoding for the
// content server: a client whose runtime can transcode KTX2 (Unity's
// com.unity.cloud.ktx) requests "<path>.ktx2" instead of "<path>.png"/".jpg".
// Missing files are encoded once via the basisu CLI (nothing vendored —
// "basisu" just needs to be on PATH, e.g. `brew install basis_universal`)
// and cached to disk forever — the same pattern astc.go and downscale.go use.
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

// ktx2Transcoder serializes concurrent encodes of the SAME output path while
// letting different files encode in parallel — the astcTranscoder shape.
type ktx2Transcoder struct {
	mu       sync.Mutex
	inFlight map[string]*sync.Mutex

	binOnce sync.Once
	binPath string // "" if basisu isn't on PATH — every request then 404s straight through
}

func (t *ktx2Transcoder) lockFor(path string) *sync.Mutex {
	t.mu.Lock()
	defer t.mu.Unlock()
	m, ok := t.inFlight[path]
	if !ok {
		m = &sync.Mutex{}
		t.inFlight[path] = m
	}
	return m
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
	cmd := exec.CommandContext(ctx, t.bin(),
		"-ktx2", "-uastc", "-uastc_level", "2", "-uastc_rdo_l", "1.0", "-y_flip",
		srcPath, "-output_file", tmp)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return &transcodeError{srcPath, string(out), err}
	}
	return os.Rename(tmp, ktx2Path)
}

// withKTX2 wraps the content handler: a ".ktx2" request that isn't already
// cached on disk gets encoded on demand, then falls through to the normal
// static-file handler. Every failure mode (basisu missing, no source image,
// encode error) 404s — the client's loader treats that as "no KTX2 variant
// available" and falls back to its PNG/JPG path, so a server without basisu
// behaves identically to one that never heard of KTX2.
func (s *server) withKTX2(d *downscaler, next http.Handler) http.Handler {
	t := &ktx2Transcoder{inFlight: map[string]*sync.Mutex{}}
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
		if t.bin() == "" {
			http.NotFound(w, r) // basisu not installed on this server
			return
		}
		srcPath := ensureKtx2Source(d, ktx2Path)
		if srcPath == "" {
			http.NotFound(w, r) // nothing to encode from
			return
		}

		lock := t.lockFor(ktx2Path)
		lock.Lock()
		defer lock.Unlock()
		if !fileExists(ktx2Path) { // re-check: a queued sibling request may have just finished it
			heavyGen <- struct{}{}
			err := t.transcode(srcPath, ktx2Path)
			<-heavyGen
			if err != nil {
				log.Printf("ktx2: %v", err)
				http.NotFound(w, r)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}
