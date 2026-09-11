package main

// КРУТКИ (TR-47) — гача с одним бесплатным прокрутом в день.
//
// Решает СЕРВЕР: сектор выбирается здесь, приз начисляется здесь, и выбитое
// из супер-сектора помнится здесь же. Клиент показывает анимацию и результат,
// но не решает, что выпало, — иначе выигрыш стоил бы правки памяти телефона.
//
// Настройка живёт в <content>/gacha.json и перечитывается на лету, как
// ежедневные награды: шансы и призы — контент, а не сборка.

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"math/rand"
	"net/http"
	"strings"
	"sync"
	"time"
)

// gachaSector — один сектор рулетки. Валютный отдаёт число, супер — вещь.
type gachaSector struct {
	ID       string  `json:"id"`
	Kind     string  `json:"kind"`     // currency | super
	Currency string  `json:"currency"` // для валютного
	Amount   int64   `json:"amount"`
	Weight   float64 `json:"weight"` // вес в жеребьёвке; супер — 10 из 100 по умолчанию
	Label    string  `json:"label"`
	Icon     string  `json:"icon"`
}

// gachaPrize — награда супер-сектора: наряд, фон, аватарка.
type gachaPrize struct {
	SKU   string `json:"sku"`
	Label string `json:"label"`
	Art   string `json:"art"`
}

type gachaConfig struct {
	Sectors  []gachaSector `json:"sectors"`
	Prizes   []gachaPrize  `json:"prizes"`
	Currency string        `json:"spin_currency"` // чем платить за платный прокрут
	Price    int64         `json:"spin_price"`
}

type gachaDoc struct {
	Taken   []string // выбитые супер-призы
	FreeDay string   // YYYY-MM-DD последнего бесплатного прокрута
	Spins   int
}

type GachaService struct {
	mu     sync.Mutex
	db     *sql.DB
	auth   *AuthService
	wallet *WalletService
	cfg    *hotJSON[gachaConfig]
	now    func() time.Time
	roll   func() float64
}

func NewGachaService(db *sql.DB, auth *AuthService, wallet *WalletService, cfgPath string) (*GachaService, error) {
	if db == nil {
		return nil, errors.New("крутки без базы")
	}
	return &GachaService{
		db: db, auth: auth, wallet: wallet,
		cfg: newHotJSON(cfgPath, gachaConfig{}),
		now: time.Now, roll: rand.Float64,
	}, nil
}

func (s *GachaService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/gacha", s.handleStatus)
	mux.HandleFunc("/v1/gacha/spin", s.handleSpin)
}

func (s *GachaService) load(userID string) (*gachaDoc, error) {
	doc := &gachaDoc{}
	var taken string
	err := s.db.QueryRow(`SELECT taken, free_day, spins FROM gacha_players WHERE user_id = ?`, userID).
		Scan(&taken, &doc.FreeDay, &doc.Spins)
	if errors.Is(err, sql.ErrNoRows) {
		return doc, nil // ещё не крутил — это не ошибка
	}
	if err != nil {
		return nil, err
	}
	if taken != "" {
		doc.Taken = strings.Split(taken, ",")
	}
	return doc, nil
}

func (s *GachaService) save(userID string, doc *gachaDoc) error {
	_, err := s.db.Exec(`INSERT INTO gacha_players (user_id, taken, free_day, spins) VALUES (?, ?, ?, ?)
		ON CONFLICT(user_id) DO UPDATE SET taken = excluded.taken, free_day = excluded.free_day, spins = excluded.spins`,
		userID, strings.Join(doc.Taken, ","), doc.FreeDay, doc.Spins)
	return err
}

// left — призы супер-сектора, которые игрок ещё не выбил.
func (c gachaConfig) left(taken []string) []gachaPrize {
	out := make([]gachaPrize, 0, len(c.Prizes))
	for _, p := range c.Prizes {
		got := false
		for _, t := range taken {
			if t == p.SKU {
				got = true
				break
			}
		}
		if !got {
			out = append(out, p)
		}
	}
	return out
}

// wheel — секторы, которые участвуют в этой жеребьёвке. Опустевший супер
// уходит из рулетки: обещать сектор, из которого нечего достать, — обман.
func (c gachaConfig) wheel(taken []string) []gachaSector {
	superLeft := len(c.left(taken)) > 0
	out := make([]gachaSector, 0, len(c.Sectors))
	for _, sec := range c.Sectors {
		if sec.Kind == "super" && !superLeft {
			continue
		}
		out = append(out, sec)
	}
	return out
}

func (s *GachaService) handleStatus(w http.ResponseWriter, r *http.Request) {
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc, err := s.load(userID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "gacha_unavailable"})
		return
	}
	cfg := s.cfg.Get()
	writeJSON(w, http.StatusOK, map[string]any{
		"sectors":       cfg.wheel(doc.Taken),
		"prizes_left":   cfg.left(doc.Taken),
		"free_today":    doc.FreeDay != s.today(),
		"spin_currency": cfg.Currency,
		"spin_price":    cfg.Price,
		"spins":         doc.Spins,
	})
}

func (s *GachaService) today() string { return s.now().UTC().Format("2006-01-02") }

func (s *GachaService) handleSpin(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	doc, err := s.load(userID)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "gacha_unavailable"})
		return
	}
	cfg := s.cfg.Get()
	wheel := cfg.wheel(doc.Taken)
	if len(wheel) == 0 {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "no_sectors"})
		return
	}

	// ПЕРВЫЙ ПРОКРУТ ЗА ДЕНЬ БЕСПЛАТНЫЙ, остальные — за валюту. Списываем ДО
	// жеребьёвки: иначе отказ кошелька пришёл бы после того, как игрок уже
	// увидел приз.
	free := doc.FreeDay != s.today()
	if !free {
		if cfg.Price <= 0 || cfg.Currency == "" {
			writeJSON(w, http.StatusConflict, map[string]any{"error": "already_spun_today"})
			return
		}
		if err := s.wallet.Charge(userID, cfg.Currency, cfg.Price, "gacha spin"); err != nil {
			writeJSON(w, http.StatusConflict, map[string]any{"error": "insufficient_funds"})
			return
		}
	}

	sector := s.draw(wheel)
	result := map[string]any{"sector": sector.ID, "kind": sector.Kind}
	switch sector.Kind {
	case "super":
		left := cfg.left(doc.Taken)
		prize := left[int(s.roll()*float64(len(left)))%len(left)]
		if err := s.wallet.GrantItem(userID, prize.SKU, "gacha"); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "grant_failed"})
			return
		}
		doc.Taken = append(doc.Taken, prize.SKU)
		result["prize"] = prize
	default:
		if err := s.wallet.Grant(userID, sector.Currency, sector.Amount, "gacha"); err != nil {
			writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "grant_failed"})
			return
		}
		result["currency"] = sector.Currency
		result["amount"] = sector.Amount
	}

	if free {
		doc.FreeDay = s.today()
	}
	doc.Spins++
	if err := s.save(userID, doc); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "gacha_save_failed"})
		return
	}
	result["free_today"] = doc.FreeDay != s.today()
	result["prizes_left"] = cfg.left(doc.Taken)
	writeJSON(w, http.StatusOK, result)
}

// draw — жеребьёвка по весам. Вес — доля, а не проценты: сумма может быть
// любой, и добавить сектор не значит пересчитать все остальные.
func (s *GachaService) draw(wheel []gachaSector) gachaSector {
	total := 0.0
	for _, sec := range wheel {
		if sec.Weight > 0 {
			total += sec.Weight
		}
	}
	if total <= 0 {
		return wheel[int(s.roll()*float64(len(wheel)))%len(wheel)]
	}
	point := s.roll() * total
	for _, sec := range wheel {
		if sec.Weight <= 0 {
			continue
		}
		point -= sec.Weight
		if point <= 0 {
			return sec
		}
	}
	return wheel[len(wheel)-1]
}

var _ = fmt.Sprintf
var _ = json.Marshal
