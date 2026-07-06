package main

// hotJSON — a tiny mtime-watched JSON file: Get() re-reads the file only when
// it changed on disk, so the economy catalogs (IAP packs, ad rewards, daily
// streak) follow an admin-panel edit WITHOUT a server restart — the same
// "save is the deploy" contract the manifest already has. Reads are cheap
// (one stat per request on hot paths that are, in practice, rare).

import (
	"encoding/json"
	"os"
	"sync"
	"time"
)

type hotJSON[T any] struct {
	mu    sync.Mutex
	path  string
	mtime time.Time
	value T
	dirty bool // force reload on the first Get
}

func newHotJSON[T any](path string, initial T) *hotJSON[T] {
	return &hotJSON[T]{path: path, value: initial, dirty: true}
}

// Get returns the current value, re-parsing the file when its mtime moved.
// A missing/broken file keeps the last good value (or the initial one).
func (h *hotJSON[T]) Get() T {
	h.mu.Lock()
	defer h.mu.Unlock()
	info, err := os.Stat(h.path)
	if err != nil {
		return h.value
	}
	if !h.dirty && info.ModTime().Equal(h.mtime) {
		return h.value
	}
	data, err := os.ReadFile(h.path)
	if err != nil {
		return h.value
	}
	var fresh T
	if json.Unmarshal(data, &fresh) == nil {
		h.value = fresh
	}
	h.mtime = info.ModTime()
	h.dirty = false
	return h.value
}
