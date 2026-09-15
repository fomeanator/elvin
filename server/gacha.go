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
	"sort"
	"strconv"
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
//
// Rarity — ступень редкости (common … immortal, как в манифесте у предмета);
// Weight — вес приза внутри супер-сектора, сервер заполняет его из
// rarity_weights и отдаёт клиенту, чтобы «Что внутри» показывало честный шанс.
type gachaPrize struct {
	SKU    string  `json:"sku"`
	Label  string  `json:"label"`
	Art    string  `json:"art"`
	Rarity string  `json:"rarity,omitempty"`
	Weight float64 `json:"weight,omitempty"`
	// Цена скина в гардеробе — за неё продаётся КОПИЯ (см. handleSpin), если
	// не названа своя цена копии.
	Price     int64  `json:"price,omitempty"`
	Currency  string `json:"currency,omitempty"`
	SellPrice int64  `json:"sell_price,omitempty"`
}

// saleOf — за сколько уходит копия: своя цена копии, иначе цена скина.
func (p gachaPrize) saleOf() int64 {
	if p.SellPrice > 0 {
		return p.SellPrice
	}
	return p.Price
}

// gachaCase — НАБОР (кейс): свои сектора, призы и цена (Илья и партнёр
// 15.09: «крутки на наборы, в крутке выбрать какой кейс крутить; пока один,
// потом второй»). Верхний уровень gacha.json — набор по умолчанию, чтобы
// старые клиенты и старые файлы жили как жили.
type gachaCase struct {
	ID          string        `json:"id"`
	Name        string        `json:"name,omitempty"`
	Description string        `json:"description,omitempty"`
	Cover       string        `json:"cover,omitempty"`
	Currency    string        `json:"spin_currency,omitempty"`
	Price       int64         `json:"spin_price,omitempty"`
	Sectors     []gachaSector `json:"sectors,omitempty"`
	Prizes      []gachaPrize  `json:"prizes,omitempty"`
	Order       int           `json:"order,omitempty"`
	Hidden      bool          `json:"hidden,omitempty"`
}

const defaultCaseID = "base"

type gachaConfig struct {
	Sectors  []gachaSector `json:"sectors"`
	Prizes   []gachaPrize  `json:"prizes"`
	Currency string        `json:"spin_currency"` // чем платить за платный прокрут
	Price    int64         `json:"spin_price"`
	Cases    []gachaCase   `json:"cases,omitempty"`
	// РАЗНЫЕ ШАНСЫ ПО РЕДКОСТИ (TR-109, Илья 15.09): вес ступени внутри
	// супер-сектора; ступень без веса и приз без ступени весят 1. Доли, не
	// проценты: добавить ступень не значит пересчитать остальные.
	RarityWeights map[string]float64 `json:"rarity_weights"`
}

// cases — наборы на выбор: названные в файле, иначе один набор по умолчанию
// из верхнего уровня. Скрытые не показываются.
func (c gachaConfig) cases() []gachaCase {
	out := make([]gachaCase, 0, len(c.Cases)+1)
	for _, k := range c.Cases {
		if !k.Hidden && k.ID != "" {
			out = append(out, k)
		}
	}
	if len(out) == 0 {
		out = append(out, gachaCase{ID: defaultCaseID, Currency: c.Currency, Price: c.Price, Sectors: c.Sectors, Prizes: c.Prizes})
	}
	return out
}

// pick — набор по id (пусто — первый); у набора без своих секторов, призов или
// цены они берутся с верхнего уровня.
func (c gachaConfig) pickCase(id string) (gachaConfig, gachaCase, bool) {
	list := c.cases()
	var k gachaCase
	found := false
	for _, cand := range list {
		if cand.ID == id || (id == "" && !found) {
			k, found = cand, true
			if cand.ID == id {
				break
			}
		}
	}
	if !found {
		return c, k, false
	}
	view := c
	if len(k.Sectors) > 0 {
		view.Sectors = k.Sectors
	}
	if len(k.Prizes) > 0 || len(c.Cases) > 0 {
		view.Prizes = k.Prizes
	}
	if k.Currency != "" {
		view.Currency = k.Currency
	}
	if k.Price > 0 {
		view.Price = k.Price
	}
	return view, k, true
}

// caseSummaries — то, что видит выбор набора: без секторов и призов.
func (c gachaConfig) caseSummaries() []map[string]any {
	out := []map[string]any{}
	for _, k := range c.cases() {
		v, _, _ := c.pickCase(k.ID)
		out = append(out, map[string]any{"id": k.ID, "name": k.Name, "description": k.Description, "cover": k.Cover,
			"spin_currency": v.Currency, "spin_price": v.Price, "prizes": len(v.Prizes)})
	}
	return out
}

// prizeWeight — вес приза в жеребьёвке супер-сектора: по его ступени, иначе 1.
func (c gachaConfig) prizeWeight(p gachaPrize) float64 {
	if w, ok := c.RarityWeights[p.Rarity]; ok && w > 0 {
		return w
	}
	return 1
}

type gachaDoc struct {
	Taken   []string       // выбитые супер-призы (что уже есть)
	Copies  map[string]int // сколько раз приз выпал СВЕРХ первого — копии
	FreeDay string         // YYYY-MM-DD последнего бесплатного прокрута
	Spins   int
}

func (d *gachaDoc) has(sku string) bool {
	for _, t := range d.Taken {
		if t == sku {
			return true
		}
	}
	return false
}

// copiesText / parseCopies — копии в одной строке "sku:n,sku:n" (как taken).
func copiesText(m map[string]int) string {
	parts := make([]string, 0, len(m))
	for sku, n := range m {
		if n > 0 {
			parts = append(parts, sku+":"+strconv.Itoa(n))
		}
	}
	sort.Strings(parts)
	return strings.Join(parts, ",")
}

func parseCopies(text string) map[string]int {
	out := map[string]int{}
	for _, part := range strings.Split(text, ",") {
		i := strings.LastIndex(part, ":")
		if i <= 0 {
			continue
		}
		if n, err := strconv.Atoi(part[i+1:]); err == nil && n > 0 {
			out[part[:i]] = n
		}
	}
	return out
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
	doc := &gachaDoc{Copies: map[string]int{}}
	var taken, copies string
	err := s.db.QueryRow(`SELECT taken, copies, free_day, spins FROM gacha_players WHERE user_id = ?`, userID).
		Scan(&taken, &copies, &doc.FreeDay, &doc.Spins)
	if errors.Is(err, sql.ErrNoRows) {
		return doc, nil // ещё не крутил — это не ошибка
	}
	if err != nil {
		return nil, err
	}
	if taken != "" {
		doc.Taken = strings.Split(taken, ",")
	}
	doc.Copies = parseCopies(copies)
	return doc, nil
}

func (s *GachaService) save(userID string, doc *gachaDoc) error {
	_, err := s.db.Exec(`INSERT INTO gacha_players (user_id, taken, copies, free_day, spins) VALUES (?, ?, ?, ?, ?)
		ON CONFLICT(user_id) DO UPDATE SET taken = excluded.taken, copies = excluded.copies, free_day = excluded.free_day, spins = excluded.spins`,
		userID, strings.Join(doc.Taken, ","), copiesText(doc.Copies), doc.FreeDay, doc.Spins)
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
			p.Weight = c.prizeWeight(p)
			out = append(out, p)
		}
	}
	return out
}

// all — весь набор призов с весами: «Что внутри» показывает и выбитое
// (Илья 15.09: «почему только 3?»), а не только то, что ещё можно достать.
func (c gachaConfig) all() []gachaPrize {
	out := make([]gachaPrize, 0, len(c.Prizes))
	for _, p := range c.Prizes {
		p.Weight = c.prizeWeight(p)
		out = append(out, p)
	}
	return out
}

// pick — приз из оставшихся по весам ступеней: бессмертное выпадает реже
// обычного, а не поровну со всеми.
func (s *GachaService) pick(left []gachaPrize) gachaPrize {
	total := 0.0
	for _, p := range left {
		total += p.Weight
	}
	if total <= 0 {
		return left[int(s.roll()*float64(len(left)))%len(left)]
	}
	point := s.roll() * total
	for _, p := range left {
		point -= p.Weight
		if point <= 0 {
			return p
		}
	}
	return left[len(left)-1]
}

// wheel — секторы, которые участвуют в этой жеребьёвке. Супер-сектор стоит,
// пока в наборе вообще есть призы: выбитое из крутки НЕ уходит (Илья 15.09:
// «неправильно убирать скин, если выбил — лента лысеет»), повтор — копия.
func (c gachaConfig) wheel(taken []string) []gachaSector {
	superLeft := len(c.Prizes) > 0
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
	all := s.cfg.Get()
	cfg, k, ok := all.pickCase(r.URL.Query().Get("case"))
	if !ok {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "no_such_case"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"case":          k.ID,
		"cases":         all.caseSummaries(),
		"sectors":       cfg.wheel(doc.Taken),
		"prizes_left":   cfg.left(doc.Taken),
		"prizes":        cfg.all(),
		"copies":        doc.Copies,
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
	// Набор — в теле {"case": id} или в запросе ?case=; пусто — первый.
	var req struct {
		Case string `json:"case"`
	}
	_ = json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req)
	if req.Case == "" {
		req.Case = r.URL.Query().Get("case")
	}
	cfg, k, ok := s.cfg.Get().pickCase(req.Case)
	if !ok {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "no_such_case"})
		return
	}
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
	result := map[string]any{"sector": sector.ID, "kind": sector.Kind, "case": k.ID}
	switch sector.Kind {
	case "super":
		prize := s.pick(cfg.all())
		result["prize"] = prize
		if doc.has(prize.SKU) {
			// КОПИЯ (Илья 15.09): скин уже есть — копия продаётся за его цену в
			// гардеробе, кристаллы падают сразу («тогда это прям рулетка»).
			// Копии считаются — под крафт (5 одной ступени → 1 следующей).
			if doc.Copies == nil {
				doc.Copies = map[string]int{}
			}
			doc.Copies[prize.SKU]++
			result["copy"] = doc.Copies[prize.SKU]
			if sale := prize.saleOf(); sale > 0 && prize.Currency != "" {
				if err := s.wallet.Grant(userID, prize.Currency, sale, "gacha duplicate"); err != nil {
					writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "grant_failed"})
					return
				}
				result["sold"] = map[string]any{"currency": prize.Currency, "amount": sale}
			}
		} else {
			if err := s.wallet.GrantItem(userID, prize.SKU, "gacha"); err != nil {
				writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "grant_failed"})
				return
			}
			doc.Taken = append(doc.Taken, prize.SKU)
		}
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
