package main

// Wallet service — server-authoritative currencies, inventory and purchases.
// One JSON blob per user, mutations under a lock, every change appended to
// the blob's own history (the audit trail refunds and support live off).
//
// IAP: /v1/iap/verify checks the sku against the catalog and, in -iap-dev
// mode, trusts the receipt (local/test builds). Real store verification is a
// deliberate 501 until store credentials exist — a fake "verified" would be
// worse than an honest not-implemented.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sync"
	"time"
)

type walletDoc struct {
	Version   int              `json:"__v"`
	Balances  map[string]int64 `json:"balances"`
	Inventory map[string]int64 `json:"inventory"`
	History   []walletEntry    `json:"history"`
}

type walletEntry struct {
	TS       string `json:"ts"`
	Type     string `json:"type"` // earn | spend | iap
	Currency string `json:"currency,omitempty"`
	Amount   int64  `json:"amount,omitempty"`
	SKU      string `json:"sku,omitempty"`
	Reason   string `json:"reason,omitempty"`
}

type iapProduct struct {
	Currency string `json:"currency"`
	Amount   int64  `json:"amount"`
}

type WalletService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	iapDev  bool
	catalog map[string]iapProduct // sku → grant
}

var reUserFile = regexp.MustCompile(`^[a-zA-Z0-9_-]+$`)

// NewWalletService: catalogPath is optional (missing file = empty catalog —
// every IAP verify then 404s on the sku, which is the safe default).
func NewWalletService(dir string, auth *AuthService, catalogPath string, iapDev bool) (*WalletService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &WalletService{dir: dir, auth: auth, iapDev: iapDev, catalog: map[string]iapProduct{}}
	if data, err := os.ReadFile(catalogPath); err == nil {
		_ = json.Unmarshal(data, &s.catalog)
	}
	return s, nil
}

func (s *WalletService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/wallet", s.handleGet)
	mux.HandleFunc("/v1/wallet/earn", s.mutate("earn"))
	mux.HandleFunc("/v1/wallet/spend", s.mutate("spend"))
	mux.HandleFunc("/v1/iap/verify", s.handleIAP)
}

func (s *WalletService) load(userID string) *walletDoc {
	doc := &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}
	if data, err := os.ReadFile(filepath.Join(s.dir, userID+".json")); err == nil {
		_ = json.Unmarshal(data, doc)
		if doc.Balances == nil {
			doc.Balances = map[string]int64{}
		}
		if doc.Inventory == nil {
			doc.Inventory = map[string]int64{}
		}
	}
	return doc
}

func (s *WalletService) save(userID string, doc *walletDoc) {
	doc.Version++
	if len(doc.History) > 100 {
		doc.History = doc.History[len(doc.History)-100:]
	}
	data, _ := json.MarshalIndent(doc, "", "  ")
	tmp := filepath.Join(s.dir, userID+".json.tmp")
	if err := os.WriteFile(tmp, data, 0o600); err == nil {
		_ = os.Rename(tmp, filepath.Join(s.dir, userID+".json"))
	}
}

func (s *WalletService) user(w http.ResponseWriter, r *http.Request) (string, bool) {
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return "", false
	}
	return userID, true
}

func (s *WalletService) handleGet(w http.ResponseWriter, r *http.Request) {
	userID, ok := s.user(w, r)
	if !ok {
		return
	}
	s.mu.Lock()
	doc := s.load(userID)
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(doc)
}

func (s *WalletService) mutate(kind string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "POST only", http.StatusMethodNotAllowed)
			return
		}
		userID, ok := s.user(w, r)
		if !ok {
			return
		}
		var req struct {
			Currency string `json:"currency"`
			Amount   int64  `json:"amount"`
			SKU      string `json:"sku"`
			Reason   string `json:"reason"`
		}
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil ||
			req.Currency == "" || req.Amount <= 0 || req.Reason == "" {
			http.Error(w, "currency, amount>0 and reason required", http.StatusBadRequest)
			return
		}

		s.mu.Lock()
		defer s.mu.Unlock()
		doc := s.load(userID)
		if kind == "spend" {
			if doc.Balances[req.Currency] < req.Amount {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusConflict)
				_ = json.NewEncoder(w).Encode(map[string]any{
					"error": "insufficient_funds", "balance": doc.Balances[req.Currency],
				})
				return
			}
			doc.Balances[req.Currency] -= req.Amount
			if req.SKU != "" {
				doc.Inventory[req.SKU]++
			}
		} else {
			doc.Balances[req.Currency] += req.Amount
		}
		doc.History = append(doc.History, walletEntry{
			TS: time.Now().UTC().Format(time.RFC3339), Type: kind,
			Currency: req.Currency, Amount: req.Amount, SKU: req.SKU, Reason: req.Reason,
		})
		s.save(userID, doc)
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(doc)
	}
}

func (s *WalletService) handleIAP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	userID, ok := s.user(w, r)
	if !ok {
		return
	}
	var req struct {
		Platform string `json:"platform"` // gplay | appstore | dev
		SKU      string `json:"sku"`
		Receipt  string `json:"receipt"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&req); err != nil ||
		req.SKU == "" || req.Receipt == "" {
		http.Error(w, "sku and receipt required", http.StatusBadRequest)
		return
	}
	grant, known := s.catalog[req.SKU]
	if !known {
		http.Error(w, "unknown sku", http.StatusNotFound)
		return
	}
	if !s.iapDev {
		// Store-side verification needs store credentials; pretending would be
		// a security hole, so the endpoint says so until they're configured.
		http.Error(w, "store verification not configured (run with -iap-dev for test builds)", http.StatusNotImplemented)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	doc := s.load(userID)
	doc.Balances[grant.Currency] += grant.Amount
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "iap",
		Currency: grant.Currency, Amount: grant.Amount, SKU: req.SKU, Reason: "iap:" + req.Platform,
	})
	s.save(userID, doc)
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(doc)
}
