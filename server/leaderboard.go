package main

// Leaderboard service — named boards, best-score-wins submissions, top-N with
// the caller's own rank alongside (the number players actually care about).
// One JSON file per board; boards are created on first submit and named by a
// conservative slug so a client can't write outside the directory.

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

type lbEntry struct {
	User    string `json:"user"`
	Name    string `json:"name,omitempty"` // display name, client-chosen
	Score   int64  `json:"score"`
	Updated string `json:"updated"`
}

type LeaderboardService struct {
	mu   sync.Mutex
	db   *sql.DB
	dir  string // прежний каталог файлов — только ради разового переезда
	auth *AuthService
}

var reBoard = regexp.MustCompile(`^[a-z0-9_-]{1,40}$`)

func NewLeaderboardService(dir string, db *sql.DB, auth *AuthService) (*LeaderboardService, error) {
	if db == nil {
		return nil, errors.New("таблицы лидеров без базы")
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	if moved, err := importLeaderboardFiles(db, dir); err != nil {
		return nil, fmt.Errorf("переезд таблиц лидеров: %w", err)
	} else if moved > 0 {
		log.Printf("[leaderboard] в базу перенесено таблиц: %d", moved)
	}
	return &LeaderboardService{db: db, dir: dir, auth: auth}, nil
}

func (s *LeaderboardService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/leaderboard/", s.handle)
}

func (s *LeaderboardService) load(board string) ([]lbEntry, error) {
	// Ошибка чтения не превращается в пустую таблицу: сохранение поверх
	// стёрло бы её целиком.
	return lbLoad(s.db, board)
}

func (s *LeaderboardService) save(board string, entries []lbEntry) error {
	return lbSave(s.db, board, entries)
}

func (s *LeaderboardService) handle(w http.ResponseWriter, r *http.Request) {
	board := strings.TrimPrefix(r.URL.Path, "/v1/leaderboard/")
	if !reBoard.MatchString(board) {
		http.Error(w, "board: [a-z0-9_-]{1,40}", http.StatusBadRequest)
		return
	}
	switch r.Method {
	case http.MethodGet:
		s.handleTop(w, r, board)
	case http.MethodPost:
		s.handleSubmit(w, r, board)
	default:
		http.Error(w, "GET or POST", http.StatusMethodNotAllowed)
	}
}

func (s *LeaderboardService) handleSubmit(w http.ResponseWriter, r *http.Request, board string) {
	userID := s.auth.UserFromRequest(r)
	if !requireUser(w, userID) {
		return
	}
	var req struct {
		Score int64  `json:"score"`
		Name  string `json:"name"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyTiny)).Decode(&req); err != nil {
		http.Error(w, "score required", http.StatusBadRequest)
		return
	}
	if r := []rune(req.Name); len(r) > 32 {
		req.Name = string(r[:32]) // rune-safe: never cut a UTF-8 char in half
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	entries, err := s.load(board)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "leaderboard_unavailable"})
		return
	}
	improved := true
	found := false
	for i := range entries {
		if entries[i].User == userID {
			found = true
			if req.Score > entries[i].Score {
				entries[i].Score = req.Score
				entries[i].Updated = time.Now().UTC().Format(time.RFC3339)
			} else {
				improved = false
			}
			if req.Name != "" {
				entries[i].Name = req.Name
			}
			break
		}
	}
	if !found {
		entries = append(entries, lbEntry{
			User: userID, Name: req.Name, Score: req.Score,
			Updated: time.Now().UTC().Format(time.RFC3339),
		})
	}
	if err := s.save(board, entries); err != nil {
		http.Error(w, "persist failed", http.StatusInternalServerError)
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"improved": improved,
		"rank":     rankOf(entries, userID),
	})
}

func (s *LeaderboardService) handleTop(w http.ResponseWriter, r *http.Request, board string) {
	n := 10
	n = qtyParam(r, "n", n, 100)
	s.mu.Lock()
	entries, err := s.load(board)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "leaderboard_unavailable"})
		return
	}
	s.mu.Unlock()

	sortEntries(entries)
	top := entries
	if len(top) > n {
		top = top[:n]
	}
	out := map[string]any{"board": board, "total": len(entries), "top": top}
	// The caller's own rank, when signed in — the number that matters.
	if userID := s.auth.UserFromRequest(r); userID != "" {
		if rk := rankOf(entries, userID); rk > 0 {
			out["me"] = map[string]any{"rank": rk}
			for _, e := range entries {
				if e.User == userID {
					out["me"].(map[string]any)["score"] = e.Score
					break
				}
			}
		}
	}
	writeJSON(w, http.StatusOK, out)
}

func sortEntries(entries []lbEntry) {
	sort.SliceStable(entries, func(i, j int) bool {
		if entries[i].Score != entries[j].Score {
			return entries[i].Score > entries[j].Score
		}
		return entries[i].Updated < entries[j].Updated // earlier claim wins ties
	})
}

func rankOf(entries []lbEntry, userID string) int {
	sortEntries(entries)
	for i, e := range entries {
		if e.User == userID {
			return i + 1
		}
	}
	return 0
}
