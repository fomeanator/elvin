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
	"sort"
	"sync"
	"time"
)

type walletDoc struct {
	Version   int              `json:"__v"`
	Balances  map[string]int64 `json:"balances"`
	Inventory map[string]int64 `json:"inventory"`
	History   []walletEntry    `json:"history"`
	// Store transaction ids already granted — replaying the same receipt
	// (restore, retry, or an attack) must never double-credit.
	Transactions []string `json:"transactions,omitempty"`
}

type walletEntry struct {
	TS       string `json:"ts"`
	Type     string `json:"type"` // earn | spend | iap
	Currency string `json:"currency,omitempty"`
	Amount   int64  `json:"amount,omitempty"`
	SKU      string `json:"sku,omitempty"`
	Reason   string `json:"reason,omitempty"`
}

// iapProduct is a catalog entry. Currency+Amount are what /v1/iap/verify
// grants (the original format); the rest is storefront presentation served by
// /v1/iap/catalog — a plain {currency,amount} catalog still works, the store
// screen just renders it without a price tag or title.
type iapProduct struct {
	Currency string `json:"currency"`
	Amount   int64  `json:"amount"`
	Title    string `json:"title,omitempty"`
	Price    string `json:"price,omitempty"` // display string ("$4.99"); the store bills, not us
	Icon     string `json:"icon,omitempty"`  // content url
	Bonus    int64  `json:"bonus,omitempty"` // extra amount shown as "+N bonus" (already inside Amount)
	Order    int    `json:"order,omitempty"` // catalog sort key; ties break by amount
}

type WalletService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	iapDev  bool
	catalog *hotJSON[map[string]iapProduct] // sku → grant; follows disk edits live

	// AppleSharedSecret enables REAL App Store receipt validation on
	// /v1/iap/verify (platform "appstore"). Google Play needs a service
	// account and stays an honest 501 until one is configured.
	AppleSharedSecret string
	// AppleBundleID pins receipts to OUR app — without it any genuine App
	// Store receipt (from anyone's app) with a same-named product would pass.
	AppleBundleID string

	// verifyApple validates a receipt against Apple and returns the matching
	// transaction id for a sku. Swappable in tests.
	verifyApple func(receipt, sku, sharedSecret, bundleID string) (txID string, err error)
}

var reUserFile = regexp.MustCompile(`^[a-zA-Z0-9_-]+$`)

// NewWalletService: catalogPath is optional (missing file = empty catalog —
// every IAP verify then 404s on the sku, which is the safe default).
func NewWalletService(dir string, auth *AuthService, catalogPath string, iapDev bool) (*WalletService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &WalletService{dir: dir, auth: auth, iapDev: iapDev,
		catalog: newHotJSON(catalogPath, map[string]iapProduct{})}
	s.verifyApple = verifyAppleReceipt
	return s, nil
}

func (s *WalletService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/wallet", s.handleGet)
	mux.HandleFunc("/v1/wallet/earn", s.mutate("earn"))
	mux.HandleFunc("/v1/wallet/spend", s.mutate("spend"))
	mux.HandleFunc("/v1/iap/verify", s.handleIAP)
	mux.HandleFunc("/v1/iap/catalog", s.handleCatalog)
}

// handleCatalog lists the purchasable packs for the store screen. Public (no
// auth): prices are as secret as a shop window, and the screen may render
// before the device session lands.
func (s *WalletService) handleCatalog(w http.ResponseWriter, r *http.Request) {
	type pack struct {
		SKU string `json:"sku"`
		iapProduct
	}
	catalog := s.catalog.Get()
	packs := make([]pack, 0, len(catalog))
	for sku, p := range catalog {
		packs = append(packs, pack{SKU: sku, iapProduct: p})
	}
	sort.Slice(packs, func(i, j int) bool {
		if packs[i].Order != packs[j].Order {
			return packs[i].Order < packs[j].Order
		}
		if packs[i].Amount != packs[j].Amount {
			return packs[i].Amount < packs[j].Amount
		}
		return packs[i].SKU < packs[j].SKU
	})
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"packs": packs})
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

// AdminLoad returns a read-only copy of a user's wallet for the admin views.
func (s *WalletService) AdminLoad(userID string) *walletDoc {
	if !reUserFile.MatchString(userID) {
		return &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.load(userID)
}

// AllUserIDs lists every wallet on disk (the admin ledger walks these).
func (s *WalletService) AllUserIDs() []string {
	entries, _ := os.ReadDir(s.dir)
	var ids []string
	for _, e := range entries {
		name := e.Name()
		if !e.IsDir() && len(name) > 5 && name[len(name)-5:] == ".json" {
			ids = append(ids, name[:len(name)-5])
		}
	}
	return ids
}

// Clawback removes currency (support/ops corrections), flooring at zero —
// audited like everything else.
func (s *WalletService) Clawback(userID, currency string, amount int64, reason string) {
	if !reUserFile.MatchString(userID) || amount <= 0 {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc := s.load(userID)
	doc.Balances[currency] -= amount
	if doc.Balances[currency] < 0 {
		doc.Balances[currency] = 0
	}
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "spend",
		Currency: currency, Amount: amount, Reason: reason,
	})
	s.save(userID, doc)
}

// Grant credits a user outside an HTTP request (the daily service etc.) —
// same lock, same audit history as any earn.
func (s *WalletService) Grant(userID, currency string, amount int64, reason string) {
	if !reUserFile.MatchString(userID) || amount <= 0 {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc := s.load(userID)
	doc.Balances[currency] += amount
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "earn",
		Currency: currency, Amount: amount, Reason: reason,
	})
	s.save(userID, doc)
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
	grant, known := s.catalog.Get()[req.SKU]
	if !known {
		http.Error(w, "unknown sku", http.StatusNotFound)
		return
	}

	// Establish trust in the receipt, platform by platform. txID guards
	// against replaying the same purchase.
	txID := ""
	switch {
	case s.iapDev:
		// test builds trust anything — never run production with -iap-dev
	case req.Platform == "appstore":
		if s.AppleSharedSecret == "" {
			http.Error(w, "appstore verification not configured (set -apple-shared-secret)", http.StatusNotImplemented)
			return
		}
		id, err := s.verifyApple(req.Receipt, req.SKU, s.AppleSharedSecret, s.AppleBundleID)
		if err != nil {
			http.Error(w, "receipt rejected: "+err.Error(), http.StatusPaymentRequired)
			return
		}
		txID = "appstore:" + id
	case req.Platform == "gplay":
		// Real Play validation needs a service-account credential (Android
		// Publisher API). Honest 501 until one exists — a fake "verified"
		// would be worse than not-implemented.
		http.Error(w, "gplay verification not configured (service account required)", http.StatusNotImplemented)
		return
	default:
		http.Error(w, "unknown platform (use appstore | gplay, or -iap-dev for test builds)", http.StatusNotImplemented)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	doc := s.load(userID)
	if txID != "" {
		for _, t := range doc.Transactions {
			if t == txID {
				// Already granted — idempotent OK with the current state, so a
				// client-side retry/restore never double-credits.
				w.Header().Set("Content-Type", "application/json")
				_ = json.NewEncoder(w).Encode(doc)
				return
			}
		}
		// Never trimmed: evicting an old id would re-open it for replay, and
		// at ~25 bytes per purchase the list stays trivially small for life.
		doc.Transactions = append(doc.Transactions, txID)
	}
	doc.Balances[grant.Currency] += grant.Amount
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "iap",
		Currency: grant.Currency, Amount: grant.Amount, SKU: req.SKU, Reason: "iap:" + req.Platform,
	})
	s.save(userID, doc)
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(doc)
}
