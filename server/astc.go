package main

// astc.go — on-demand ASTC transcoding for the content server: a client GPU
// that supports ASTC-compressed textures requests "<path>.astc" instead of
// "<path>.png"/".jpg"; if that .astc doesn't exist on disk yet, this
// transcodes the source image via the astcenc CLI (ARM's reference encoder —
// nothing is vendored, "astcenc" just needs to be on PATH, e.g.
// `npm install -g astcenc` pulls a prebuilt binary per platform) and writes
// the result next to the source, so every later request for the same file is
// a plain static-file hit — the same "encode once, cache to disk forever"
// pattern ContentLoader already uses client-side for its own disk cache.
//
// Why bother when JPEG/PNG recompression already shrinks these files (see
// tools/lvnconv's `optimize` command): those only shrink the WIRE/DISK
// footprint. Once decoded, a texture is full RGBA in VRAM regardless of
// source format — WebP wouldn't help either, for the same reason. ASTC is a
// GPU-NATIVE block format: the GPU samples the compressed bytes directly, so
// this is the one encoding that actually cuts runtime VRAM (4–8× at 6x6
// blocks), not just download size. A device whose GPU doesn't support ASTC
// (or a browser/WASM target) simply never requests it and gets the existing
// PNG/JPG path — this is a pure opt-in addition, never a replacement.
//
// Format: the raw ARM .astc container (16-byte header — 4-byte magic
// 0x5CA1AB13, block dims, width/height/depth as 3-byte little-endian fields —
// followed by block data). Unity's client-side reader parses this directly
// with Texture2D.LoadRawTextureData; no further container is needed.

import (
	"context"
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

// astcBlockDim is the block size baked into every transcode — ARM's own
// guidance is that 5x5–6x6 is the sane default for game art (4x4 is near-
// lossless but ~2× the bits; 8x8+ visibly softens detail).
const astcBlockDim = "6x6"

// astcEncodeTimeout bounds a single transcode. The largest assets in this
// project run 7000×8000+ (a Spine flipbook export) — astcenc is fast even at
// that size (seconds, not minutes: measured ~loading a full 8-core machine),
// but a generous ceiling still beats hanging a request forever if a giant
// asset or a slow disk shows up later.
const astcEncodeTimeout = 60 * time.Second

// sourceExts are tried in order to find the image a ".astc" request should
// transcode from — same base name, sibling extension.
var sourceExts = []string{".png", ".jpg", ".jpeg"}

// astcTranscoder serializes concurrent transcodes of the SAME output path (a
// burst of requests for one still-missing .astc must not spawn N encoder
// processes racing to write the same file) while letting different files
// transcode in parallel.
type astcTranscoder struct {
	mu       sync.Mutex
	inFlight map[string]*sync.Mutex

	binOnce sync.Once
	binPath string // "" if astcenc isn't on PATH — every request then 404s straight through
}

// lockFor returns (and lazily creates) the per-output-path lock, so two
// concurrent requests for the same missing .astc block each other instead of
// both invoking astcenc.
func (t *astcTranscoder) lockFor(path string) *sync.Mutex {
	t.mu.Lock()
	defer t.mu.Unlock()
	m, ok := t.inFlight[path]
	if !ok {
		m = &sync.Mutex{}
		t.inFlight[path] = m
	}
	return m
}

func (t *astcTranscoder) bin() string {
	t.binOnce.Do(func() {
		if p, err := exec.LookPath("astcenc"); err == nil {
			t.binPath = p
		}
	})
	return t.binPath
}

// findSource looks for a sibling source image next to the requested .astc
// path (same directory, same base name, a supported extension).
func findSource(astcPath string) string {
	base := strings.TrimSuffix(astcPath, filepath.Ext(astcPath))
	for _, ext := range sourceExts {
		if p := base + ext; fileExists(p) {
			return p
		}
	}
	return ""
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// transcode invokes astcenc, writing to a temp file first and renaming into
// place — a request that reads astcPath never sees a partially-written file,
// and a crashed/timed-out encode never leaves a corrupt .astc for the next
// request to serve as if it were valid.
func (t *astcTranscoder) transcode(srcPath, astcPath string) error {
	// astcenc infers the OUTPUT container format from the file extension, so
	// the uniquifying suffix must land before ".astc", not after it.
	dir, base := filepath.Split(astcPath)
	ext := filepath.Ext(base)
	tmp := filepath.Join(dir, fmt.Sprintf("%s.tmp-%d%s", strings.TrimSuffix(base, ext), time.Now().UnixNano(), ext))
	defer os.Remove(tmp) // no-op once renamed away

	ctx, cancel := context.WithTimeout(context.Background(), astcEncodeTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, t.bin(), "-cs", srcPath, tmp, astcBlockDim, "-medium")
	out, err := cmd.CombinedOutput()
	if err != nil {
		return &transcodeError{srcPath, string(out), err}
	}
	return os.Rename(tmp, astcPath)
}

type transcodeError struct {
	src    string
	output string
	err    error
}

func (e *transcodeError) Error() string {
	return e.src + ": " + e.err.Error() + ": " + e.output
}

// withASTC wraps the content handler: a ".astc" request that isn't already
// cached on disk gets transcoded on demand from its sibling PNG/JPG, then
// falls through to the normal static-file handler to actually serve it.
// Every failure mode (astcenc missing, no source image, encode error) 404s —
// the client's loader treats that exactly like "no ASTC variant available"
// and uses the regular texture URL instead, so this can never break a title
// that has no ASTC-capable client or a server without astcenc installed.
func (s *server) withASTC(next http.Handler) http.Handler {
	t := &astcTranscoder{inFlight: map[string]*sync.Mutex{}}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(strings.ToLower(r.URL.Path), ".astc") {
			next.ServeHTTP(w, r)
			return
		}

		rel := strings.TrimPrefix(r.URL.Path, "/content/")
		astcPath, ok := s.contentPath(rel)
		if !ok {
			http.NotFound(w, r)
			return
		}
		if fileExists(astcPath) {
			next.ServeHTTP(w, r) // already transcoded — plain file-serve hit
			return
		}

		srcPath := findSource(astcPath)
		if srcPath == "" {
			http.NotFound(w, r) // nothing to transcode from
			return
		}
		if t.bin() == "" {
			http.NotFound(w, r) // astcenc not installed on this server
			return
		}

		lock := t.lockFor(astcPath)
		lock.Lock()
		defer lock.Unlock()
		if !fileExists(astcPath) { // re-check: a queued sibling request may have just finished it
			heavyGen <- struct{}{}
			err := t.transcode(srcPath, astcPath)
			<-heavyGen
			if err != nil {
				log.Printf("astc: %v", err)
				http.NotFound(w, r)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}

// contentPath resolves a content-relative request path to an on-disk path,
// rejecting any ".." segment outright (defence in depth — net/http's own
// ServeMux already cleans ".." out of incoming paths before routing here, and
// filepath.Join can't actually escape a fixed base either way, but a
// transcoding endpoint that WRITES files earns an explicit, easy-to-audit
// reject rather than leaning on that implicit behaviour).
func (s *server) contentPath(rel string) (string, bool) {
	if strings.Contains(rel, "..") {
		return "", false
	}
	return filepath.Join(s.content, filepath.Clean("/"+rel)), true
}
