package main

// Daily bonus service — the retention classic: one claim per UTC day, a
// streak that grows on consecutive days and resets after a gap, rewards
// configured per streak day in <content>/daily-rewards.json (an array; the
// last entry repeats forever). Grants go through the wallet service so the
// audit history shows them like any other earn.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type dailyDoc struct {
	LastClaim string `json:"last_claim"` // YYYY-MM-DD (UTC)
	Streak    int    `json:"streak"`
}

type dailyReward struct {
	Currency string `json:"currency"`
	Amount   int64  `json:"amount"`
}

type DailyService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	wallet  *WalletService
	rewards *hotJSON[[]dailyReward] // follows disk edits live
	// now is swappable so tests can travel in time.
	now func() time.Time
}

func NewDailyService(dir string, auth *AuthService, wallet *WalletService, rewardsPath string) (*DailyService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &DailyService{dir: dir, auth: auth, wallet: wallet, now: time.Now,
		rewards: newHotJSON(rewardsPath, []dailyReward{})}
	return s, nil
}

func (s *DailyService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/daily", s.handleStatus)
	mux.HandleFunc("/v1/daily/claim", s.handleClaim)
}

func (s *DailyService) load(userID string) *dailyDoc {
	doc := &dailyDoc{}
	if data, err := os.ReadFile(filepath.Join(s.dir, userID+".json")); err == nil {
		_ = json.Unmarshal(data, doc)
	}
	return doc
}

func (s *DailyService) save(userID string, doc *dailyDoc) {
	data, _ := json.Marshal(doc)
	tmp := filepath.Join(s.dir, userID+".json.tmp")
	if err := os.WriteFile(tmp, data, 0o600); err == nil {
		_ = os.Rename(tmp, filepath.Join(s.dir, userID+".json"))
	}
}

func (s *DailyService) rewardFor(streak int) dailyReward {
	idx := streak - 1
	if idx < 0 {
		idx = 0
	}
	rewards := s.rewards.Get()
	if len(rewards) == 0 {
		rewards = []dailyReward{{Currency: "gold", Amount: 25}}
	}
	if idx >= len(rewards) {
		idx = len(rewards) - 1 // the last configured day repeats
	}
	return rewards[idx]
}

// nextStreak computes the streak the NEXT claim would have, given the doc.
func (s *DailyService) nextStreak(doc *dailyDoc, today string) (streak int, claimedToday bool) {
	switch doc.LastClaim {
	case today:
		return doc.Streak, true
	case s.now().UTC().AddDate(0, 0, -1).Format("2006-01-02"):
		return doc.Streak + 1, false
	default:
		return 1, false
	}
}

func (s *DailyService) handleStatus(w http.ResponseWriter, r *http.Request) {
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	s.mu.Lock()
	doc := s.load(userID)
	s.mu.Unlock()
	today := s.now().UTC().Format("2006-01-02")
	streak, claimed := s.nextStreak(doc, today)
	next := s.rewardFor(streak)
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"streak": doc.Streak, "claimed_today": claimed,
		"next_streak": streak, "next_reward": next,
	})
}

func (s *DailyService) handleClaim(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	doc := s.load(userID)
	today := s.now().UTC().Format("2006-01-02")
	streak, claimed := s.nextStreak(doc, today)
	if claimed {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusConflict)
		_ = json.NewEncoder(w).Encode(map[string]any{"error": "already_claimed", "streak": doc.Streak})
		return
	}
	reward := s.rewardFor(streak)
	doc.LastClaim = today
	doc.Streak = streak
	s.save(userID, doc)
	s.wallet.Grant(userID, reward.Currency, reward.Amount, "daily:day"+itoa(streak))

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"streak": streak, "reward": reward,
	})
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var b [20]byte
	i := len(b)
	for n > 0 {
		i--
		b[i] = byte('0' + n%10)
		n /= 10
	}
	return string(b[i:])
}
