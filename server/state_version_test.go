package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
)

// A malformed version must not opt a client out of concurrency protection.
// Rejected writes must leave both the cached and persisted save unchanged.
func TestStateVersionValidation(t *testing.T) {
	for _, tc := range []struct {
		name       string
		body       string
		wantStatus int
	}{
		{"string", `{"score":9,"_version":"1"}`, http.StatusBadRequest},
		{"null", `{"score":9,"_version":null}`, http.StatusBadRequest},
		{"fraction", `{"score":9,"_version":1.5}`, http.StatusBadRequest},
		{"boolean", `{"score":9,"_version":true}`, http.StatusBadRequest},
		{"object", `{"score":9,"_version":{}}`, http.StatusBadRequest},
		{"array", `{"score":9,"_version":[]}`, http.StatusBadRequest},
		{"overflow", `{"score":9,"_version":9223372036854775808}`, http.StatusBadRequest},
		{"missing", `{"score":9}`, http.StatusOK},
		{"matching", `{"score":9,"_version":1}`, http.StatusOK},
		{"conflict", `{"score":9,"_version":0}`, http.StatusConflict},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := &server{content: t.TempDir(), state: map[string]stateEntry{}}
			put := func(body string) *httptest.ResponseRecorder {
				rec := httptest.NewRecorder()
				s.handleState(rec, httptest.NewRequest(http.MethodPut, "/v1/state?user=u1", strings.NewReader(body)))
				return rec
			}
			if rec := put(`{"score":1}`); rec.Code != http.StatusOK {
				t.Fatalf("initial PUT = %d: %s", rec.Code, rec.Body)
			}

			rec := put(tc.body)
			if rec.Code != tc.wantStatus {
				t.Errorf("PUT = %d: %s, want %d", rec.Code, rec.Body, tc.wantStatus)
			}
			wantDoc, wantVersion := `{"score":1}`, int64(1)
			switch tc.wantStatus {
			case http.StatusBadRequest:
				if got := rec.Body.String(); got != "_version must be an integer\n" {
					t.Errorf("error body = %q, want explanation of invalid _version", got)
				}
			case http.StatusOK:
				var saved struct {
					Saved   bool  `json:"saved"`
					Version int64 `json:"version"`
				}
				if err := json.Unmarshal(rec.Body.Bytes(), &saved); err != nil {
					t.Fatal(err)
				}
				if !saved.Saved || saved.Version != 2 {
					t.Errorf("save response = %+v, want saved:true, version:2", saved)
				}
				wantDoc, wantVersion = `{"score":9}`, 2
			case http.StatusConflict:
				var conflict struct {
					Error   string          `json:"error"`
					Version int64           `json:"version"`
					Doc     json.RawMessage `json:"doc"`
				}
				if err := json.Unmarshal(rec.Body.Bytes(), &conflict); err != nil {
					t.Fatal(err)
				}
				if conflict.Error != "version_conflict" || conflict.Version != 1 || string(conflict.Doc) != wantDoc {
					t.Errorf("conflict must carry the current document and version: %s", rec.Body)
				}
			}

			entry, ok := s.loadState("u1")
			if !ok || entry.version != wantVersion || string(entry.body) != wantDoc {
				t.Errorf("cached save = %s, version %d; want %s, version %d", entry.body, entry.version, wantDoc, wantVersion)
			}
			raw, err := os.ReadFile(s.stateFile("u1"))
			if err != nil {
				t.Fatal(err)
			}
			entry = decodeStateFile(raw)
			if entry.version != wantVersion || string(entry.body) != wantDoc {
				t.Errorf("persisted save = %s, version %d; want %s, version %d", entry.body, entry.version, wantDoc, wantVersion)
			}
		})
	}
}
