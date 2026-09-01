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
	"sync"
	"time"
)

type adReward struct {
	Currency string `json:"currency"`
	Amount   int64  `json:"amount"`
	DailyCap int    `json:"daily_cap"` // watches per user per UTC day; 0 = unlimited

	// ЗАРЯДЫ С ПЕРЕЗАРЯДКОЙ — короткий цикл поверх дневного потолка: «3/3
	// рекламы, после последней кнопка уходит в перезарядку» (запрос партнёра
	// TR-34). Дневной потолок отвечает за сутки, заряды — за ближайшие
	// минуты: без них игрок высматривает всю дневную норму за минуту и
	// остаётся без повода вернуться.
	Charges     int `json:"charges"`      // сколько показов подряд; 0 = не ограничиваем
	RechargeSec int `json:"recharge_sec"` // через сколько заряды восстанавливаются целиком
}

type adsUserDoc struct {
	Day    string         `json:"day"`
	Counts map[string]int `json:"counts"`

	// Когда истрачен последний заряд — от него считается восстановление.
	// Unix-секунды: в файле их читает человек, а часовые пояса тут ни при чём.
	Spent map[string]int   `json:"spent"` // placement → сколько зарядов истрачено в текущем цикле
	Since map[string]int64 `json:"since"` // placement → когда начался текущий цикл
}

type AdsService struct {
	mu      sync.Mutex
	dir     string
	auth    *AuthService
	wallet  *WalletService
	catalog *hotJSON[map[string]adReward] // follows disk edits live
}

func NewAdsService(dir string, auth *AuthService, wallet *WalletService, catalogPath string) (*AdsService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &AdsService{dir: dir, auth: auth, wallet: wallet,
		catalog: newHotJSON(catalogPath, map[string]adReward{})}, nil
}

// Сколько зарядов осталось и когда цикл восстановится. Считается ПО ЧАСАМ, а
// не по счётчику: игрок закрывает игру, и «начислять заряды тикающим таймером»
// означало бы не начислять их вовсе, пока приложение свёрнуто.
func chargesLeft(a adReward, doc *adsUserDoc, placement string, now time.Time) (left int, readyAt int64) {
	if a.Charges <= 0 {
		return -1, 0 // без ограничения
	}
	// ИСТЁКШИЙ ЦИКЛ ЗАКРЫВАЕТСЯ ЗДЕСЬ ЖЕ, в самом документе. Считать остаток
	// «как будто сброшено», оставив в файле старый счётчик, — значит развести
	// показанное и записанное: следующий показ прибавлял бы к мёртвому циклу,
	// и заряды то кончались бы мгновенно, то не кончались вовсе.
	if a.RechargeSec > 0 && doc.Since[placement] > 0 &&
		now.Unix()-doc.Since[placement] >= int64(a.RechargeSec) {
		if doc.Spent != nil {
			delete(doc.Spent, placement)
		}
		if doc.Since != nil {
			delete(doc.Since, placement)
		}
	}
	spent := doc.Spent[placement]
	since := doc.Since[placement]
	left = a.Charges - spent
	if left < 0 {
		left = 0
	}
	if left == 0 && a.RechargeSec > 0 && since > 0 {
		readyAt = since + int64(a.RechargeSec)
	}
	return left, readyAt
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
		// Состояние ЭТОГО игрока: без него кнопка «3/3» не может себя
		// нарисовать, а спрашивать вторым запросом — значит показать её
		// сначала неправильной.
		Left    int   `json:"left"`     // -1 = без ограничения
		ReadyAt int64 `json:"ready_at"` // unix; 0 = заряды есть
	}
	catalog := s.catalog.Get()
	userID := s.auth.UserFromRequest(r)
	now := time.Now().UTC()
	var doc *adsUserDoc
	if userID != "" && reUserFile.MatchString(userID) {
		s.mu.Lock()
		loaded, err := s.loadUser(userID)
		s.mu.Unlock()
		if err != nil {
			// Витрину показать нечем: пустые счётчики выдали бы «награда
			// доступна» за уже просмотренное.
			writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "ads_unavailable"})
			return
		}
		doc = loaded
	} else {
		doc = &adsUserDoc{Counts: map[string]int{}, Spent: map[string]int{}, Since: map[string]int64{}}
	}
	out := make([]row, 0, len(catalog))
	for p, a := range catalog {
		left, readyAt := chargesLeft(a, doc, p, now)
		out = append(out, row{Placement: p, adReward: a, Left: left, ReadyAt: readyAt})
	}
	writeJSON(w, http.StatusOK, map[string]any{"placements": out})
}

func (s *AdsService) handleReward(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
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
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyTiny)).Decode(&req); err != nil || req.Placement == "" {
		http.Error(w, "placement required", http.StatusBadRequest)
		return
	}
	reward, known := s.catalog.Get()[req.Placement]
	if !known {
		http.Error(w, "unknown placement", http.StatusNotFound)
		return
	}

	now := time.Now().UTC()
	day := now.Format("2006-01-02")
	s.mu.Lock()
	doc, err := s.loadUser(userID)
	if err != nil {
		s.mu.Unlock()
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "ads_unavailable"})
		return
	}
	if doc.Day != day {
		doc.Day, doc.Counts = day, map[string]int{}
	}
	// Заряды проверяются ДО дневного потолка: у них своя причина отказа и своя
	// подпись на кнопке («ещё 1:12»), а «на сегодня хватит» — совсем другой
	// разговор с игроком.
	if left, readyAt := chargesLeft(reward, doc, req.Placement, now); left == 0 {
		s.mu.Unlock()
		writeJSON(w, http.StatusTooManyRequests, map[string]any{
			"error": "recharging", "ready_at": readyAt, "charges": reward.Charges})
		return
	}
	if reward.DailyCap > 0 && doc.Counts[req.Placement] >= reward.DailyCap {
		s.mu.Unlock()
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "daily_cap", "cap": reward.DailyCap})
		return
	}
	doc.Counts[req.Placement]++
	// Заряд списан, и если цикл ещё не начат — он начинается СЕЙЧАС: отсчёт
	// перезарядки идёт от первого показа в цикле, а не от последнего, иначе
	// частые просмотры отодвигали бы восстановление бесконечно.
	if reward.Charges > 0 {
		if doc.Spent == nil {
			doc.Spent = map[string]int{}
		}
		if doc.Since == nil {
			doc.Since = map[string]int64{}
		}
		if doc.Spent[req.Placement] == 0 || doc.Since[req.Placement] == 0 {
			doc.Since[req.Placement] = now.Unix()
		}
		doc.Spent[req.Placement]++
	}
	left := -1
	if reward.DailyCap > 0 {
		left = reward.DailyCap - doc.Counts[req.Placement]
	}
	chargesNow, readyAt := chargesLeft(reward, doc, req.Placement, now)
	// The watch is counted BEFORE the payout (same trade-off as the daily
	// service): a failed grant loses one watch to support, a payout before a
	// failed count would be replayable for free currency.
	if err := s.saveUser(userID, doc); err != nil {
		s.mu.Unlock()
		http.Error(w, "persist failed", http.StatusInternalServerError)
		return
	}
	s.mu.Unlock()

	if err := s.wallet.Grant(userID, reward.Currency, reward.Amount, "ad:"+req.Placement); err != nil {
		http.Error(w, "grant failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"granted": true, "currency": reward.Currency, "amount": reward.Amount,
		"left_today": left, "left": chargesNow, "ready_at": readyAt,
	})
}

// Четвёртое хранилище с тем же изъяном, что были у награды и лидеров: ошибка
// чтения давала пустой документ, и он сохранялся поверх (см. readJSONFile).
// Для рекламы это значит сброшенные счётчики просмотров — то есть выданную
// заново награду за уже просмотренное.
func (s *AdsService) loadUser(userID string) (*adsUserDoc, error) {
	doc := &adsUserDoc{Counts: map[string]int{}}
	path, err := userFilePath(s.dir, userID)
	if err != nil {
		return nil, err
	}
	if _, err := readJSONFile(path, doc); err != nil {
		return nil, err
	}
	{
		if doc.Counts == nil {
			doc.Counts = map[string]int{}
		}
		if doc.Spent == nil {
			doc.Spent = map[string]int{}
		}
		if doc.Since == nil {
			doc.Since = map[string]int64{}
		}
	}
	return doc, nil
}

func (s *AdsService) saveUser(userID string, doc *adsUserDoc) error {
	data, _ := json.Marshal(doc)
	return writeUserFile(s.dir, userID, data)
}
