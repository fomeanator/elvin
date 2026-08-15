package main

import (
	"crypto/rand"
	"encoding/json"
	"log"
	"math/big"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ── Дуэль по сети ────────────────────────────────────────────────────────────
//
// Сервер НЕ ЗНАЕТ ПРАВИЛ БОЯ и знать не должен. Дуэль детерминирована — в ней
// ноль случайности, — поэтому двум клиентам достаточно обменяться планами на
// обмен, и оба посчитают одинаковый исход сами, тем же кодом, что играет
// одиночную партию. Отсюда весь размер этого файла: комната, две скамьи и
// почтовый ящик на раунд.
//
// Что это даёт кроме простоты. Правила живут в одном месте — в .lvns-пакете, —
// и меняются автором без единой правки сервера и без выкатки. Серверная копия
// правил рано или поздно разошлась бы с клиентской, и разошлась бы молча.
//
// Чем платим: клиент доверенный. Подменив свой план после чужого, можно
// сжульничать. Для игры вдвоём с партнёром это цена нулевая, и она снимается
// не сервером с правилами, а обычной схемой «сначала обещание, потом раскрытие»
// (хэш плана вперёд, сам план следом) — когда и если понадобится.

const (
	duelRoomTTL     = 2 * time.Hour  // комната живёт, пока в неё ходят
	duelMaxRooms    = 500            // потолок против забивания памяти
	duelMaxWait     = 30 * time.Second
	duelMaxPlanSize = 512
)

// Буквы кода без похожих начертаний: ноль и О, единица и I на чужом экране
// читаются одинаково, а код диктуют голосом.
const duelAlphabet = "ACEFHJKLMNPQRTUVWXY3479"

type duelSeat struct {
	token string
	// plans[раунд] — что игрок выбрал на этот обмен.
	plans map[int]string
}

type duelRoom struct {
	code  string
	seats map[string]*duelSeat // "a" / "b"
	seen  time.Time
	// Каждое изменение закрывает текущий канал: все, кто ждёт, просыпаются.
	// Дешевле опроса и не требует ни одного лишнего запроса.
	change chan struct{}
}

type DuelService struct {
	mu    sync.Mutex
	rooms map[string]*duelRoom
}

func NewDuelService() *DuelService {
	s := &DuelService{rooms: map[string]*duelRoom{}}
	go s.sweep()
	return s
}

func (s *DuelService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/duel/rooms", s.handleCreate)
	mux.HandleFunc("/v1/duel/rooms/", s.handleRoom)
}

// ── создание и вход ─────────────────────────────────────────────────────────

// POST /v1/duel/rooms — создать комнату. Отдаёт код для партнёра и токен
// скамьи, которым дальше подписаны все обращения.
func (s *DuelService) handleCreate(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.rooms) >= duelMaxRooms {
		s.dropStaleLocked()
	}
	if len(s.rooms) >= duelMaxRooms {
		http.Error(w, "too many rooms", http.StatusServiceUnavailable)
		return
	}
	code := s.freeCodeLocked()
	room := &duelRoom{
		code:   code,
		seats:  map[string]*duelSeat{"a": {token: randomToken(), plans: map[int]string{}}},
		seen:   time.Now(),
		change: make(chan struct{}),
	}
	s.rooms[code] = room
	log.Printf("[duel] комната %s создана", code)
	writeJSON(w, http.StatusOK, map[string]any{
		"code": code, "seat": "a", "token": room.seats["a"].token,
	})
}

// POST /v1/duel/rooms/{code}/join — занять вторую скамью.
func (s *DuelService) handleJoin(w http.ResponseWriter, r *http.Request, code string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	room := s.rooms[code]
	if room == nil {
		http.Error(w, "room not found", http.StatusNotFound)
		return
	}
	if _, taken := room.seats["b"]; taken {
		http.Error(w, "room is full", http.StatusConflict)
		return
	}
	seat := &duelSeat{token: randomToken(), plans: map[int]string{}}
	room.seats["b"] = seat
	room.touchLocked()
	log.Printf("[duel] в комнату %s вошёл второй", code)
	writeJSON(w, http.StatusOK, map[string]any{"code": code, "seat": "b", "token": seat.token})
}

// ── ход ─────────────────────────────────────────────────────────────────────

// POST /v1/duel/rooms/{code}/plan — сдать свой план на обмен.
//
// План принимается ОДИН раз на раунд. Повтор с другим содержимым отвергается:
// иначе, увидев чужой ход, можно было бы переиграть свой, и весь смысл
// одновременного выбора исчез бы.
func (s *DuelService) handlePlan(w http.ResponseWriter, r *http.Request, code string) {
	var body struct {
		Round   int    `json:"round"`
		Actions string `json:"actions"`
	}
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, duelMaxPlanSize)).Decode(&body) != nil {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}
	if body.Round < 0 || strings.TrimSpace(body.Actions) == "" {
		http.Error(w, "round and actions required", http.StatusBadRequest)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	room, seat, ok := s.seatLocked(code, r)
	if !ok {
		http.Error(w, "unknown seat", http.StatusUnauthorized)
		return
	}
	if prev, exists := seat.plans[body.Round]; exists && prev != body.Actions {
		http.Error(w, "plan already submitted for this round", http.StatusConflict)
		return
	}
	seat.plans[body.Round] = body.Actions
	room.touchLocked()
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "round": body.Round})
}

// GET /v1/duel/rooms/{code}?round=N&wait=25 — состояние обмена.
//
// Чужой план отдаётся ТОЛЬКО когда сдали оба. До этого видно лишь «готов он или
// нет» — знать больше означало бы подсмотреть.
//
// wait включает долгое ожидание: соединение висит до появления второго плана
// или до таймаута. Это не оптимизация, а разница в ощущении: без него между
// «партнёр нажал» и «у меня поехало» стоит интервал опроса, и бой ощущается
// вялым независимо от скорости сети.
func (s *DuelService) handleState(w http.ResponseWriter, r *http.Request, code string) {
	round, _ := strconv.Atoi(r.URL.Query().Get("round"))
	wait, _ := strconv.Atoi(r.URL.Query().Get("wait"))
	deadline := time.Now().Add(time.Duration(wait) * time.Second)
	if d := time.Until(deadline); d > duelMaxWait {
		deadline = time.Now().Add(duelMaxWait)
	}

	for {
		s.mu.Lock()
		room, seat, ok := s.seatLocked(code, r)
		if !ok {
			s.mu.Unlock()
			http.Error(w, "unknown seat", http.StatusUnauthorized)
			return
		}
		var other *duelSeat
		for id, st := range room.seats {
			if st != seat {
				other = st
				_ = id
			}
		}
		room.touchLocked()

		resp := map[string]any{
			"code":     code,
			"round":    round,
			"seats":    len(room.seats),
			"mine":     seat.plans[round] != "",
			"opponent": false,
		}
		ready := false
		if other != nil && other.plans[round] != "" {
			resp["opponent"] = true
			// Раскрываем только при обоюдной сдаче.
			if seat.plans[round] != "" {
				resp["opponent_actions"] = other.plans[round]
				ready = true
			}
		}
		resp["ready"] = ready
		ch := room.change
		s.mu.Unlock()

		if ready || wait <= 0 || !time.Now().Before(deadline) {
			writeJSON(w, http.StatusOK, resp)
			return
		}
		select {
		case <-ch: // что-то изменилось — пересчитываем ответ
		case <-time.After(time.Until(deadline)):
			writeJSON(w, http.StatusOK, resp)
			return
		case <-r.Context().Done(): // игрок свернул приложение
			return
		}
	}
}

// ── маршрутизация и мелочи ──────────────────────────────────────────────────

func (s *DuelService) handleRoom(w http.ResponseWriter, r *http.Request) {
	rest := strings.Trim(strings.TrimPrefix(r.URL.Path, "/v1/duel/rooms/"), "/")
	if rest == "" {
		http.NotFound(w, r)
		return
	}
	parts := strings.SplitN(rest, "/", 2)
	code := strings.ToUpper(parts[0])
	action := ""
	if len(parts) > 1 {
		action = parts[1]
	}
	switch {
	case action == "join" && r.Method == http.MethodPost:
		s.handleJoin(w, r, code)
	case action == "plan" && r.Method == http.MethodPost:
		s.handlePlan(w, r, code)
	case action == "" && r.Method == http.MethodGet:
		s.handleState(w, r, code)
	default:
		http.NotFound(w, r)
	}
}

// seatLocked находит комнату и скамью по токену. Токен берём из заголовка, а не
// из адреса: адреса пишутся в логи прокси, а токен — это право ходить за
// игрока.
func (s *DuelService) seatLocked(code string, r *http.Request) (*duelRoom, *duelSeat, bool) {
	room := s.rooms[code]
	if room == nil {
		return nil, nil, false
	}
	tok := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
	if tok == "" {
		tok = r.URL.Query().Get("token")
	}
	for _, seat := range room.seats {
		if seat.token != "" && seat.token == tok {
			return room, seat, true
		}
	}
	return nil, nil, false
}

// touchLocked отмечает комнату живой и будит всех ожидающих.
func (r *duelRoom) touchLocked() {
	r.seen = time.Now()
	close(r.change)
	r.change = make(chan struct{})
}

func (s *DuelService) freeCodeLocked() string {
	for i := 0; i < 50; i++ {
		c := randomCode(4)
		if _, busy := s.rooms[c]; !busy {
			return c
		}
	}
	return randomCode(6) // столько совпадений подряд — берём код длиннее
}

func (s *DuelService) dropStaleLocked() {
	cutoff := time.Now().Add(-duelRoomTTL)
	for code, room := range s.rooms {
		if room.seen.Before(cutoff) {
			delete(s.rooms, code)
		}
	}
}

// sweep убирает заброшенные комнаты. Раз в четверть часа: комнаты живут часами,
// и чаще ходить незачем.
func (s *DuelService) sweep() {
	for range time.Tick(15 * time.Minute) {
		s.mu.Lock()
		before := len(s.rooms)
		s.dropStaleLocked()
		if n := before - len(s.rooms); n > 0 {
			log.Printf("[duel] убрано заброшенных комнат: %d", n)
		}
		s.mu.Unlock()
	}
}

func randomCode(n int) string {
	b := make([]byte, n)
	for i := range b {
		k, _ := rand.Int(rand.Reader, big.NewInt(int64(len(duelAlphabet))))
		b[i] = duelAlphabet[k.Int64()]
	}
	return string(b)
}

func randomToken() string {
	const hex = "0123456789abcdef"
	b := make([]byte, 32)
	for i := range b {
		k, _ := rand.Int(rand.Reader, big.NewInt(16))
		b[i] = hex[k.Int64()]
	}
	return string(b)
}
