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
	"errors"
	"fmt"
	"io/fs"
	"log"
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
	// Regen anchors: currency → unix seconds the refill clock last settled.
	// Absent/zero for a currency means the clock is NOT running (its balance is
	// at or above the free cap). See accrue.
	Regen map[string]int64 `json:"regen_anchors,omitempty"`
	// AppliedOps: recent client op_ids (LRU) — a retried mutation whose
	// RESPONSE was lost must not apply twice. Money survives flaky links.
	AppliedOps []string `json:"applied_ops,omitempty"`
}

// regenRule configures a lives/energy-style regenerating currency (from
// services/energy.json, hot-reloaded). A balance below Cap refills +1 every
// Interval seconds up to Cap; buying past Cap is allowed and neither regens
// nor decays. New wallets seed at Start.
type regenRule struct {
	Cap      int64 `json:"cap"`
	Interval int64 `json:"interval_seconds"`
	Start    int64 `json:"start"`
}

// regenView is the client-facing refill state per regen currency — enough for
// the HUD/popup to show "N/Cap" and a countdown without duplicating the
// accrual formula. NextRefillUnix is 0 when the balance is at/above the cap.
type regenView struct {
	Balance        int64 `json:"balance"`
	Cap            int64 `json:"cap"`
	IntervalSecs   int64 `json:"interval_seconds"`
	NextRefillUnix int64 `json:"next_refill_unix,omitempty"`
}

// walletResponse is what the client receives: the raw doc plus computed regen
// state (embedded doc promotes its own json fields).
type walletResponse struct {
	*walletDoc
	RegenState map[string]regenView `json:"regen,omitempty"`
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
	// Section groups packs in the store screen (e.g. "currency1", "currency2",
	// "bundles"). Empty = the default ungrouped list. The client maps the id to
	// a display title (store.section_titles); packs appear section-by-section in
	// Order.
	Section string `json:"section,omitempty"`
	// Grants lets a "bundle" pack award MULTIPLE currencies at once
	// (currency→amount). When set it takes precedence over Currency/Amount for
	// the grant; Currency/Amount may still be set for the card's headline.
	Grants map[string]int64 `json:"grants,omitempty"`
}

type WalletService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	iapDev  bool
	catalog *hotJSON[map[string]iapProduct] // sku → grant; follows disk edits live
	regen   *hotJSON[map[string]regenRule]  // currency → refill rule (energy.json)

	// clock is the time source, swappable in tests. nil → time.Now.
	clock func() time.Time

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

	// EarnDisabled closes /v1/wallet/earn (the client-initiated credit route).
	// The zero value keeps it open — the current test-mode behaviour; flip via
	// -wallet-earn=false before wiring real payments.
	EarnDisabled bool
}

var reUserFile = regexp.MustCompile(`^[a-zA-Z0-9_-]+$`)

// NewWalletService: catalogPath is optional (missing file = empty catalog —
// every IAP verify then 404s on the sku, which is the safe default).
func NewWalletService(dir string, auth *AuthService, catalogPath string, iapDev bool) (*WalletService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	// Regen config lives beside the other economy catalogs, one level up from
	// the per-user wallet dir (services/energy.json). Missing file = no regen
	// currencies, so this is a no-op for anyone who doesn't ship energy.
	regenPath := filepath.Join(filepath.Dir(dir), "energy.json")
	s := &WalletService{dir: dir, auth: auth, iapDev: iapDev,
		catalog: newHotJSON(catalogPath, map[string]iapProduct{}),
		regen:   newHotJSON(regenPath, map[string]regenRule{})}
	s.verifyApple = verifyAppleReceipt
	return s, nil
}

// now is the wallet's time source (tests inject a fixed clock).
func (s *WalletService) now() time.Time {
	if s.clock != nil {
		return s.clock()
	}
	return time.Now()
}

// accrue credits time-based regeneration for every regen currency, mutating
// balances and anchors in place. Idempotent when no interval elapsed. A
// currency below its cap gains +1 per interval up to the cap; at/above the cap
// the clock is parked (anchor deleted) so a purchased surplus neither regens
// nor decays. Returns true if it changed anything (caller should persist).
func (s *WalletService) accrue(doc *walletDoc, now time.Time) bool {
	rules := s.regen.Get()
	if len(rules) == 0 {
		return false
	}
	nowU := now.Unix()
	changed := false
	for cur, rule := range rules {
		if rule.Interval <= 0 || rule.Cap <= 0 {
			continue
		}
		bal := doc.Balances[cur]
		anchor := doc.Regen[cur]
		if bal >= rule.Cap {
			if anchor != 0 { // at/above cap — park the refill clock
				s.setAnchor(doc, cur, 0)
				changed = true
			}
			continue
		}
		if anchor == 0 { // below cap with a stopped clock — start it now
			s.setAnchor(doc, cur, nowU)
			changed = true
			continue
		}
		if nowU <= anchor {
			continue
		}
		gained := (nowU - anchor) / rule.Interval
		if gained <= 0 {
			continue
		}
		if newBal := bal + gained; newBal >= rule.Cap {
			doc.Balances[cur] = rule.Cap
			s.setAnchor(doc, cur, 0) // hit the cap — stop the clock
		} else {
			doc.Balances[cur] = newBal
			s.setAnchor(doc, cur, anchor+gained*rule.Interval)
		}
		changed = true
	}
	return changed
}

// setAnchor writes (or clears, when v==0) a currency's refill anchor.
func (s *WalletService) setAnchor(doc *walletDoc, cur string, v int64) {
	if v == 0 {
		delete(doc.Regen, cur)
		return
	}
	if doc.Regen == nil {
		doc.Regen = map[string]int64{}
	}
	doc.Regen[cur] = v
}

// regenState computes the client-facing refill state for every regen currency.
func (s *WalletService) regenState(doc *walletDoc, now time.Time) map[string]regenView {
	rules := s.regen.Get()
	if len(rules) == 0 {
		return nil
	}
	out := make(map[string]regenView, len(rules))
	for cur, rule := range rules {
		if rule.Interval <= 0 || rule.Cap <= 0 {
			continue
		}
		bal := doc.Balances[cur]
		var next int64
		if bal < rule.Cap {
			anchor := doc.Regen[cur]
			if anchor == 0 {
				anchor = now.Unix()
			}
			next = anchor + rule.Interval
		}
		out[cur] = regenView{Balance: bal, Cap: rule.Cap, IntervalSecs: rule.Interval, NextRefillUnix: next}
	}
	return out
}

// writeDoc encodes the wallet plus computed regen state to the client.
func (s *WalletService) writeDoc(w http.ResponseWriter, doc *walletDoc) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(walletResponse{walletDoc: doc, RegenState: s.regenState(doc, s.now())})
}

func (s *WalletService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/wallet", s.handleGet)
	// SECURITY-TODO(monetization): /v1/wallet/earn is OPEN by default for now —
	// any authenticated device can credit itself arbitrary currency. This is a
	// DELIBERATE test-mode affordance while store payments aren't wired up yet
	// (we still need SOME way to hand out currency). The kill switch exists:
	// run with -wallet-earn=false (EarnDisabled) and the route 403s while
	// spend/iap/ads/daily keep working. BEFORE shipping real IAP either flip
	// that flag, or replace the route with server-defined earns (a
	// reason→amount table + per-day cap, like ads.json/daily-rewards.json).
	// Until then the soft economy is not truly server-authoritative — do not
	// enable real IAP/ads payouts against the same balances with earn open.
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

// load reads a user's wallet. Only a MISSING file means "new wallet" — an
// unreadable or corrupt one is an error, because treating it as empty would
// let the next save overwrite someone's real balance with zeros.
func (s *WalletService) load(userID string) (*walletDoc, error) {
	doc := &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}
	data, err := os.ReadFile(filepath.Join(s.dir, userID+".json"))
	if err != nil {
		if !errors.Is(err, fs.ErrNotExist) {
			return nil, err
		}
		// Brand-new wallet: seed every regen currency at its starting balance
		// (e.g. 3 chapter energy). Non-regen currencies start at 0.
		for cur, rule := range s.regen.Get() {
			if rule.Start > 0 {
				doc.Balances[cur] = rule.Start
			}
		}
		return doc, nil
	}
	if err := json.Unmarshal(data, doc); err != nil {
		return nil, fmt.Errorf("wallet %s: %w", userID, err)
	}
	if doc.Balances == nil {
		doc.Balances = map[string]int64{}
	}
	if doc.Inventory == nil {
		doc.Inventory = map[string]int64{}
	}
	return doc, nil
}

func (s *WalletService) save(userID string, doc *walletDoc) error {
	doc.Version++
	if len(doc.History) > 100 {
		doc.History = doc.History[len(doc.History)-100:]
	}
	data, _ := json.MarshalIndent(doc, "", "  ")
	return atomicWrite(filepath.Join(s.dir, userID+".json"), data, 0o600)
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
	doc, err := s.load(userID)
	if err == nil && s.accrue(doc, s.now()) {
		// Persist any regen the client is now seeing. Regen is derived from
		// time anchors, so a failed write just re-accrues on the next request
		// — log it and still serve the computed state.
		if serr := s.save(userID, doc); serr != nil {
			log.Printf("wallet: persist regen for %s: %v", userID, serr)
		}
	}
	s.mu.Unlock()
	if err != nil {
		log.Printf("wallet: load %s: %v", userID, err)
		http.Error(w, "wallet unavailable", http.StatusInternalServerError)
		return
	}
	s.writeDoc(w, doc)
}

func (s *WalletService) mutate(kind string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "POST only", http.StatusMethodNotAllowed)
			return
		}
		if kind == "earn" && s.EarnDisabled {
			http.Error(w, "client-initiated earn is disabled on this server", http.StatusForbidden)
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
			// Client-generated idempotency key: the same op replayed (offline
			// queue, lost response) applies EXACTLY once.
			OpID string `json:"op_id"`
		}
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil ||
			req.Currency == "" || req.Amount <= 0 || req.Reason == "" {
			http.Error(w, "currency, amount>0 and reason required", http.StatusBadRequest)
			return
		}

		s.mu.Lock()
		defer s.mu.Unlock()
		doc, err := s.load(userID)
		if err != nil {
			log.Printf("wallet: load %s: %v", userID, err)
			http.Error(w, "wallet unavailable", http.StatusInternalServerError)
			return
		}
		s.accrue(doc, s.now()) // credit pending regen before checking the balance
		if req.OpID != "" {
			for _, id := range doc.AppliedOps {
				if id == req.OpID {
					// Replay of an op that already landed: idempotent OK with
					// the CURRENT state — never a second apply, never a 409.
					s.writeDoc(w, doc)
					return
				}
			}
		}
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
		s.accrue(doc, s.now()) // re-settle the refill clock for the new balance
		doc.History = append(doc.History, walletEntry{
			TS: time.Now().UTC().Format(time.RFC3339), Type: kind,
			Currency: req.Currency, Amount: req.Amount, SKU: req.SKU, Reason: req.Reason,
		})
		if req.OpID != "" {
			doc.AppliedOps = append(doc.AppliedOps, req.OpID)
			if len(doc.AppliedOps) > 200 {
				doc.AppliedOps = doc.AppliedOps[len(doc.AppliedOps)-200:]
			}
		}
		if err := s.save(userID, doc); err != nil {
			// The write never reached the real file, so on-disk money is
			// unchanged — fail loudly and do NOT echo the new balance.
			log.Printf("wallet: persist %s for %s: %v", kind, userID, err)
			http.Error(w, "persist failed", http.StatusInternalServerError)
			return
		}
		s.writeDoc(w, doc)
	}
}

// AdminLoad returns a read-only copy of a user's wallet for the admin views.
// It never writes back, so an unreadable wallet degrades to an empty view
// (logged) rather than failing the whole admin page.
func (s *WalletService) AdminLoad(userID string) *walletDoc {
	if !reUserFile.MatchString(userID) {
		return &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc, err := s.load(userID)
	if err != nil {
		log.Printf("wallet: admin load %s: %v", userID, err)
		return &walletDoc{Balances: map[string]int64{}, Inventory: map[string]int64{}}
	}
	return doc
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
// audited like everything else. A non-nil error means nothing was removed.
func (s *WalletService) Clawback(userID, currency string, amount int64, reason string) error {
	if !reUserFile.MatchString(userID) || amount <= 0 {
		return fmt.Errorf("bad clawback: user %q amount %d", userID, amount)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc, err := s.load(userID)
	if err != nil {
		return err
	}
	s.accrue(doc, s.now())
	doc.Balances[currency] -= amount
	if doc.Balances[currency] < 0 {
		doc.Balances[currency] = 0
	}
	s.accrue(doc, s.now())
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "spend",
		Currency: currency, Amount: amount, Reason: reason,
	})
	return s.save(userID, doc)
}

// Grant credits a user outside an HTTP request (the daily service etc.) —
// same lock, same audit history as any earn. A non-nil error means nothing
// was credited (the write goes through a temp file, so a failed save leaves
// the previous balance intact).
func (s *WalletService) Grant(userID, currency string, amount int64, reason string) error {
	if !reUserFile.MatchString(userID) || amount <= 0 {
		return fmt.Errorf("bad grant: user %q amount %d", userID, amount)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc, err := s.load(userID)
	if err != nil {
		return err
	}
	s.accrue(doc, s.now())
	doc.Balances[currency] += amount
	s.accrue(doc, s.now())
	doc.History = append(doc.History, walletEntry{
		TS: time.Now().UTC().Format(time.RFC3339), Type: "earn",
		Currency: currency, Amount: amount, Reason: reason,
	})
	return s.save(userID, doc)
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
	doc, err := s.load(userID)
	if err != nil {
		log.Printf("wallet: load %s: %v", userID, err)
		http.Error(w, "wallet unavailable", http.StatusInternalServerError)
		return
	}
	if txID != "" {
		for _, t := range doc.Transactions {
			if t == txID {
				// Already granted — idempotent OK with the current state, so a
				// client-side retry/restore never double-credits. A failed
				// regen persist here is only lost time-derived accrual.
				if s.accrue(doc, s.now()) {
					if serr := s.save(userID, doc); serr != nil {
						log.Printf("wallet: persist regen for %s: %v", userID, serr)
					}
				}
				s.writeDoc(w, doc)
				return
			}
		}
		// Never trimmed: evicting an old id would re-open it for replay, and
		// at ~25 bytes per purchase the list stays trivially small for life.
		doc.Transactions = append(doc.Transactions, txID)
	}
	s.accrue(doc, s.now())
	// A bundle grants several currencies; a plain pack grants Currency/Amount.
	grants := grant.Grants
	if len(grants) == 0 {
		grants = map[string]int64{grant.Currency: grant.Amount}
	}
	for cur, amt := range grants {
		if cur == "" || amt <= 0 {
			continue
		}
		doc.Balances[cur] += amt
		doc.History = append(doc.History, walletEntry{
			TS: time.Now().UTC().Format(time.RFC3339), Type: "iap",
			Currency: cur, Amount: amt, SKU: req.SKU, Reason: "iap:" + req.Platform,
		})
	}
	s.accrue(doc, s.now()) // a purchase past the cap parks the refill clock
	if err := s.save(userID, doc); err != nil {
		// Neither the grant nor the txID replay-guard reached disk, so the
		// client's retry of the same receipt credits cleanly exactly once.
		log.Printf("wallet: persist iap for %s: %v", userID, err)
		http.Error(w, "persist failed", http.StatusInternalServerError)
		return
	}
	s.writeDoc(w, doc)
}
