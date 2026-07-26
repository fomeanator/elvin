package main

// Attribution: WHICH title an event belongs to, and WHOSE title that is.
//
// Written at the moment of the event, never derived later. That is the whole
// point: which title a spend happened in can be recovered from nothing else
// once the request is gone, and an author dashboard built six months from now
// can only ever show the history that was stamped as it happened.
//
// The split of trust mirrors the one already used for the user id: the client
// says WHICH TITLE it is playing (nobody else knows), the server says WHO OWNS
// it (the client must never be able to name its own payee). A title with no
// declared author simply attributes to "" — absent, not guessed.

import (
	"encoding/json"
	"os"
	"sync"
	"time"
)

// ownerIndex maps title id → author id, refreshed from the manifest on change.
// The manifest is edited live by the admin panel, so the index re-reads when
// the file's mtime/size moves rather than caching for the process lifetime.
type ownerIndex struct {
	path string

	mu      sync.RWMutex
	authors map[string]string
	modTime time.Time
	size    int64
	checked time.Time
}

func newOwnerIndex(manifestPath string) *ownerIndex {
	return &ownerIndex{path: manifestPath, authors: map[string]string{}}
}

// authorOf returns the declared author of a title, or "" when the title is
// unknown or declares none. Never an error: attribution must not be able to
// fail a payment or drop an analytics batch.
func (o *ownerIndex) authorOf(titleID string) string {
	if o == nil || titleID == "" {
		return ""
	}
	o.refresh()
	o.mu.RLock()
	defer o.mu.RUnlock()
	return o.authors[titleID]
}

// refresh reloads the index when the manifest changed. Stat is cheap, but a
// spend path should not stat on every call either — hence the 2s floor.
func (o *ownerIndex) refresh() {
	o.mu.RLock()
	fresh := time.Since(o.checked) < 2*time.Second
	o.mu.RUnlock()
	if fresh {
		return
	}
	st, err := os.Stat(o.path)
	if err != nil {
		o.mu.Lock()
		o.checked = time.Now()
		o.mu.Unlock()
		return
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	o.checked = time.Now()
	if st.ModTime().Equal(o.modTime) && st.Size() == o.size {
		return
	}
	body, err := os.ReadFile(o.path)
	if err != nil {
		return
	}
	var doc struct {
		Titles []struct {
			ID     string `json:"id"`
			Author string `json:"author"`
		} `json:"titles"`
	}
	if err := json.Unmarshal(body, &doc); err != nil {
		return // a half-written manifest keeps the previous index
	}
	next := make(map[string]string, len(doc.Titles))
	for _, t := range doc.Titles {
		if t.ID != "" {
			next[t.ID] = t.Author
		}
	}
	o.authors, o.modTime, o.size = next, st.ModTime(), st.Size()
}
