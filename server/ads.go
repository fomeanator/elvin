package main

// Rewarded ads — currency for watching a video (CAS.AI or any mediator on the
// client). Server-authoritative: the client only reports "the placement
// completed"; the AMOUNT lives here (content/ads.json) and a per-user daily
// cap blunts replay abuse. When the CAS account exists, its server-side
// verification callback can harden this further; the endpoint is shaped so
// that only the trust check tightens, not the contract.
//
//	content/ads.json: { "gold_small": {"currency":"gold","amount":20,"daily_cap":10} }

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type adReward struct {
	Currency string `json:"currency"`
	Amount   int64  `json:"amount"`
	DailyCap int    `json:"daily_cap"` // watches per user per UTC day; 0 = unlimited
}

type adsUserDoc struct {
	Day    string         `json:"day"`
	Counts map[string]int `json:"counts"`
}

type AdsService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	wallet  *WalletService
	catalog map[string]adReward
}

func NewAdsService(dir string, auth *AuthService, wallet *WalletService, catalogPath string) (*AdsService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &AdsService{dir: dir, auth: auth, wallet: wallet, catalog: map[string]adReward{}}
	if data, err := os.ReadFile(catalogPath); err == nil {
		_ = json.Unmarshal(data, &s.catalog)
	}
	return s, nil
}

func (s *AdsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/ads/catalog", s.handleCatalog)
	mux.HandleFunc("/v1/ads/reward", s.handleReward)
}

// Public — the store screen shows "watch an ad, get N" from this.
func (s *AdsService) handleCatalog(w http.ResponseWriter, r *http.Request) {
	type row struct {
		Placement string `json:"placement"`
		adReward
	}
	out := make([]row, 0, len(s.catalog))
	for p, a := range s.catalog {
		out = append(out, row{Placement: p, adReward: a})
	}
	writeJSON(w, http.StatusOK, map[string]any{"placements": out})
}

func (s *AdsService) handleReward(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req struct {
		Placement string `json:"placement"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil || req.Placement == "" {
		http.Error(w, "placement required", http.StatusBadRequest)
		return
	}
	reward, known := s.catalog[req.Placement]
	if !known {
		http.Error(w, "unknown placement", http.StatusNotFound)
		return
	}

	day := time.Now().UTC().Format("2006-01-02")
	s.mu.Lock()
	doc := s.loadUser(userID)
	if doc.Day != day {
		doc.Day, doc.Counts = day, map[string]int{}
	}
	if reward.DailyCap > 0 && doc.Counts[req.Placement] >= reward.DailyCap {
		s.mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusTooManyRequests)
		_ = json.NewEncoder(w).Encode(map[string]any{"error": "daily_cap", "cap": reward.DailyCap})
		return
	}
	doc.Counts[req.Placement]++
	left := -1
	if reward.DailyCap > 0 {
		left = reward.DailyCap - doc.Counts[req.Placement]
	}
	s.saveUser(userID, doc)
	s.mu.Unlock()

	s.wallet.Grant(userID, reward.Currency, reward.Amount, "ad:"+req.Placement)
	writeJSON(w, http.StatusOK, map[string]any{
		"granted": true, "currency": reward.Currency, "amount": reward.Amount, "left_today": left,
	})
}

func (s *AdsService) loadUser(userID string) *adsUserDoc {
	doc := &adsUserDoc{Counts: map[string]int{}}
	if data, err := os.ReadFile(filepath.Join(s.dir, userID+".json")); err == nil {
		_ = json.Unmarshal(data, doc)
		if doc.Counts == nil {
			doc.Counts = map[string]int{}
		}
	}
	return doc
}

func (s *AdsService) saveUser(userID string, doc *adsUserDoc) {
	data, _ := json.Marshal(doc)
	_ = atomicWrite(filepath.Join(s.dir, userID+".json"), data, 0o600)
}
