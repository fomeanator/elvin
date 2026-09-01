package main

import (
	"crypto/rand"
	"encoding/json"
	"log"
	"math/big"
	"net/http"
	"strings"
	"sync"
	"time"
)

// ── Комната на двоих и больше: общий стол с ящиками ──────────────────────────
//
// Здесь НЕТ ПРАВИЛ НИ ОДНОЙ ИГРЫ, и это главное свойство файла. Сервер держит
// комнату, места в ней и именованные ящики; кто что в них кладёт и что это
// значит — дело сценария.
//
// Замысел в одной мысли: почти любой мультиплеер — это ящик с ПРАВИЛОМ
// РАСКРЫТИЯ. Одновременный выбор (дуэль, камень-ножницы, Frozen Synapse) — это
// ящик, который не открывается, пока не положили все. Ход по очереди (шахматы,
// карты) — ящик, который виден сразу. Гонка «кто первый» — тот же ящик плюс
// порядок, в котором в него клали. Разные игры — разная настройка, но механизм
// один, и добавлять под каждую новую игру серверный код не нужно.
//
// Что сервер обязан знать сам: ПОРЯДОК. Всё остальное клиенты вычисляют, если
// игра детерминирована, а вот «кто нажал раньше» из своей копии не узнать —
// это единственный факт, который рождается на сервере.
//
// Чем платим: клиент доверенный. Правила считает он, значит их можно обойти.
// Для игры с друзьями цена нулевая; для соревнования лечится не сервером с
// правилами, а схемой «обещание, потом раскрытие» (хэш вперёд, значение
// следом) — поверх этого же механизма, без его переделки.

const (
	netRoomTTL      = 2 * time.Hour // комната живёт, пока в неё ходят
	netMaxRooms     = 500           // потолок против забивания памяти
	netMaxSeats     = 8
	netMaxWait      = 30 * time.Second
	netMaxValueSize = 4096
	netMaxCells     = 2000 // на комнату: партия в тысячу ходов — уже не партия
)

// Буквы кода без похожих начертаний: ноль и О, единица и I, восьмёрка и B на
// чужом экране читаются одинаково, а код диктуют голосом.
const netAlphabet = "ACEFHJKLMNPQRTUVWXY3479"

// Правила раскрытия ящика.
const (
	revealAll = "all" // видно, только когда положили ВСЕ места
	revealNow = "now" // видно сразу
)

type netSeat struct {
	id    string
	token string
}

type netCell struct {
	policy string
	values map[string]string // место → что положило
	order  []string          // места в порядке, в котором клали
}

type netRoom struct {
	code string
	// ЗЕРНО СЛУЧАЙНОСТИ комнаты. Раздаётся всем, кто сел, и никогда не
	// меняется. Нужно затем, что детерминированность — слишком дорогая цена
	// за сетевую игру: правила без единого случайного числа писать больно.
	//
	// Приём древний и проверенный: пусть у всех будет ОДИН И ТОТ ЖЕ поток
	// псевдослучайных чисел. Тогда «случайность» перестаёт быть источником
	// расхождения — оба клиента вытянут одни и те же числа в одном порядке,
	// и по проводу не пойдёт ни одного лишнего байта.
	//
	// Зерно даёт сервер, а не код комнаты: код короткий и его диктуют вслух,
	// то есть будущие броски можно было бы предсказать заранее.
	seed  uint64
	seats map[string]*netSeat
	order []string // места в порядке входа: "кто первый сел"
	cells map[string]*netCell
	seen  time.Time
	// Каждое изменение закрывает текущий канал: все ожидающие просыпаются.
	// Дешевле опроса и не требует ни одного лишнего запроса.
	change chan struct{}
}

type NetService struct {
	mu    sync.Mutex
	rooms map[string]*netRoom
}

func NewNetService() *NetService {
	s := &NetService{rooms: map[string]*netRoom{}}
	go s.sweep()
	return s
}

func (s *NetService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/net/rooms", s.handleCreate)
	mux.HandleFunc("/v1/net/rooms/", s.handleRoom)
}

// ── комната ─────────────────────────────────────────────────────────────────

// POST /v1/net/rooms — открыть комнату. Отдаёт код для партнёра и токен места.
func (s *NetService) handleCreate(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.rooms) >= netMaxRooms {
		s.dropStaleLocked()
	}
	if len(s.rooms) >= netMaxRooms {
		http.Error(w, "too many rooms", http.StatusServiceUnavailable)
		return
	}
	code := s.freeCodeLocked()
	room := &netRoom{
		code:   code,
		seed:   randomSeed(),
		seats:  map[string]*netSeat{},
		cells:  map[string]*netCell{},
		seen:   time.Now(),
		change: make(chan struct{}),
	}
	s.rooms[code] = room
	seat := room.addSeatLocked()
	log.Printf("[net] комната %s открыта", code)
	writeJSON(w, http.StatusOK, map[string]any{
		"code": code, "seat": seat.id, "token": seat.token,
		"seats": len(room.seats), "seed": room.seed,
	})
}

// POST /v1/net/rooms/{code}/join — сесть за стол.
func (s *NetService) handleJoin(w http.ResponseWriter, r *http.Request, code string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	room := s.rooms[code]
	if room == nil {
		http.Error(w, "room not found", http.StatusNotFound)
		return
	}
	if len(room.seats) >= netMaxSeats {
		http.Error(w, "room is full", http.StatusConflict)
		return
	}
	seat := room.addSeatLocked()
	room.touchLocked()
	log.Printf("[net] в комнату %s сел %s", code, seat.id)
	writeJSON(w, http.StatusOK, map[string]any{
		"code": code, "seat": seat.id, "token": seat.token,
		"seats": len(room.seats), "seed": room.seed,
	})
}

// ── ящики ───────────────────────────────────────────────────────────────────

// POST /v1/net/rooms/{code}/cells/{key} — положить своё в ящик.
//
// Значение кладётся ОДИН раз. Повтор того же принимается (это ретрай сети),
// другое значение отвергается: иначе, подсмотрев чужое, можно было бы
// переложить своё, и правило «пока не положили все» перестало бы что-либо
// значить.
func (s *NetService) handlePut(w http.ResponseWriter, r *http.Request, code, key string) {
	var body struct {
		Value  string `json:"value"`
		Reveal string `json:"reveal"`
	}
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, netMaxValueSize)).Decode(&body) != nil {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	room, seat, ok := s.seatLocked(code, r)
	if !ok {
		http.Error(w, "unknown seat", http.StatusUnauthorized)
		return
	}
	cell := room.cells[key]
	if cell == nil {
		if len(room.cells) >= netMaxCells {
			http.Error(w, "too many cells", http.StatusConflict)
			return
		}
		// Правило раскрытия задаёт ПЕРВЫЙ положивший — дальше оно неизменно.
		// Иначе второй мог бы переключить ящик на «видно сразу» и подсмотреть.
		policy := revealAll
		if body.Reveal == revealNow {
			policy = revealNow
		}
		cell = &netCell{policy: policy, values: map[string]string{}}
		room.cells[key] = cell
	}
	if prev, exists := cell.values[seat.id]; exists && prev != body.Value {
		http.Error(w, "value already placed", http.StatusConflict)
		return
	}
	if _, exists := cell.values[seat.id]; !exists {
		cell.values[seat.id] = body.Value
		cell.order = append(cell.order, seat.id)
	}
	room.touchLocked()
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "key": key, "reveal": cell.policy})
}

// GET /v1/net/rooms/{code}/cells/{key}?wait=25 — заглянуть в ящик.
//
// wait включает долгое ожидание: соединение висит, пока ящик не откроется или
// не выйдет срок. Это не оптимизация, а разница в ощущении — при опросе по
// таймеру между «партнёр нажал» и «у меня поехало» всегда стоит интервал, и
// игра ощущается вялой независимо от скорости сети.
func (s *NetService) handleGet(w http.ResponseWriter, r *http.Request, code, key string) {
	s.longPoll(w, r, code, func(room *netRoom, seat *netSeat) (any, bool) {
		return room.viewLocked(key, seat)
	})
}

// longPoll — ДОЛГИЙ ОПРОС: механизм ожидания без того, чего ждут.
//
// Игрок спрашивает «есть ли новое?» и держит запрос открытым, пока новое не
// появится или не выйдет срок. Механизм один на все такие вопросы: взять замок,
// найти место по токену, отметить комнату живой, собрать ответ, ЗАПОМНИТЬ КАНАЛ
// ПЕРЕМЕН ДО СНЯТИЯ ЗАМКА, отпустить и ждать одного из трёх — перемены, срока,
// ухода игрока.
//
// Канал перемен берётся под замком не для красоты: возьми его после — и между
// снятием замка и подпиской успеет пройти уведомление, которого мы уже не
// услышим. Запрос повиснет до срока с устаревшим ответом. Строка стояла в двух
// телах, и это ровно то место, где копии расходятся молча.
//
// Что отдавать и когда считать готовым — дело вызывающего: ящик отдаёт своё
// содержимое и открыт ли он, перекличка — список мест и набралось ли нужное
// число. `build` зовётся ПОД ЗАМКОМ, потому и читает комнату напрямую.
func (s *NetService) longPoll(w http.ResponseWriter, r *http.Request, code string,
	build func(room *netRoom, seat *netSeat) (any, bool)) {

	wait := qtyParam(r, "wait", 0, int(netMaxWait/time.Second))
	deadline := time.Now().Add(time.Duration(wait) * time.Second)

	for {
		s.mu.Lock()
		room, seat, ok := s.seatLocked(code, r)
		if !ok {
			s.mu.Unlock()
			http.Error(w, "unknown seat", http.StatusUnauthorized)
			return
		}
		room.touchLocked()
		resp, ready := build(room, seat)
		ch := room.change
		s.mu.Unlock()

		if ready || wait <= 0 || !time.Now().Before(deadline) {
			writeJSON(w, http.StatusOK, resp)
			return
		}
		select {
		case <-ch: // что-то положили — пересчитываем ответ
		case <-time.After(time.Until(deadline)):
			writeJSON(w, http.StatusOK, resp)
			return
		case <-r.Context().Done(): // игрок свернул приложение
			return
		}
	}
}

// viewLocked собирает ответ по ящику для конкретного места и говорит, открыт ли
// он. Вся секретность игры держится здесь.
func (r *netRoom) viewLocked(key string, me *netSeat) (map[string]any, bool) {
	resp := map[string]any{
		"code": r.code, "key": key,
		"seats": len(r.seats), "seat": me.id,
		"mine": false, "open": false,
	}
	cell := r.cells[key]
	if cell == nil {
		resp["reveal"] = revealAll
		return resp, false
	}
	_, mine := cell.values[me.id]
	resp["mine"] = mine
	resp["reveal"] = cell.policy
	resp["placed"] = len(cell.values)

	open := cell.policy == revealNow
	if cell.policy == revealAll {
		// Открывается, когда положили ВСЕ места И положил сам спрашивающий:
		// иначе можно было бы сесть третьим, ничего не класть и смотреть.
		open = mine && len(cell.values) >= len(r.seats)
	}
	if !open {
		return resp, false
	}

	others := map[string]any{}
	for id, v := range cell.values {
		if id != me.id {
			others[id] = v
		}
	}
	resp["open"] = true
	resp["others"] = others
	// Порядок — единственное, чего клиент не выведет из своей копии игры.
	resp["order"] = cell.order
	return resp, true
}

// ── маршрутизация и мелочи ──────────────────────────────────────────────────

func (s *NetService) handleRoom(w http.ResponseWriter, r *http.Request) {
	rest := strings.Trim(strings.TrimPrefix(r.URL.Path, "/v1/net/rooms/"), "/")
	if rest == "" {
		http.NotFound(w, r)
		return
	}
	parts := strings.SplitN(rest, "/", 3)
	code := strings.ToUpper(parts[0])
	switch {
	case len(parts) == 2 && parts[1] == "join" && r.Method == http.MethodPost:
		s.handleJoin(w, r, code)
	case len(parts) == 3 && parts[1] == "cells" && parts[2] != "":
		key := parts[2]
		if r.Method == http.MethodPost {
			s.handlePut(w, r, code, key)
		} else if r.Method == http.MethodGet {
			s.handleGet(w, r, code, key)
		} else {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		}
	case len(parts) == 1 && r.Method == http.MethodGet:
		s.handleWho(w, r, code)
	default:
		http.NotFound(w, r)
	}
}

// GET /v1/net/rooms/{code} — кто за столом. Нужно, чтобы показать «ждём
// второго» до начала партии.
func (s *NetService) handleWho(w http.ResponseWriter, r *http.Request, code string) {
	need := qtyParam(r, "need", 0, netMaxSeats)
	s.longPoll(w, r, code, func(room *netRoom, seat *netSeat) (any, bool) {
		resp := map[string]any{
			"code": code, "seat": seat.id,
			"seats": len(room.seats), "order": append([]string{}, room.order...),
		}
		return resp, need <= 0 || len(room.seats) >= need
	})
}

// seatLocked находит комнату и место по токену. Токен берём из заголовка, а не
// из адреса: адреса оседают в логах прокси, а токен — право ходить за игрока.
func (s *NetService) seatLocked(code string, r *http.Request) (*netRoom, *netSeat, bool) {
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

// addSeatLocked сажает нового игрока. Места именуются буквами по порядку входа:
// сценарию удобнее «a»/«b», чем случайная строка, а порядок и так хранится.
func (r *netRoom) addSeatLocked() *netSeat {
	id := string(rune('a' + len(r.seats)))
	seat := &netSeat{id: id, token: randomToken()}
	r.seats[id] = seat
	r.order = append(r.order, id)
	return seat
}

// touchLocked отмечает комнату живой и будит всех ожидающих.
func (r *netRoom) touchLocked() {
	r.seen = time.Now()
	close(r.change)
	r.change = make(chan struct{})
}

func (s *NetService) freeCodeLocked() string {
	for i := 0; i < 50; i++ {
		c := randomCode(4)
		if _, busy := s.rooms[c]; !busy {
			return c
		}
	}
	return randomCode(6) // столько совпадений подряд — берём код длиннее
}

func (s *NetService) dropStaleLocked() {
	cutoff := time.Now().Add(-netRoomTTL)
	for code, room := range s.rooms {
		if room.seen.Before(cutoff) {
			delete(s.rooms, code)
		}
	}
}

// sweep убирает заброшенные комнаты. Раз в четверть часа: комнаты живут часами,
// и чаще ходить незачем.
func (s *NetService) sweep() {
	for range time.Tick(15 * time.Minute) {
		s.mu.Lock()
		before := len(s.rooms)
		s.dropStaleLocked()
		if n := before - len(s.rooms); n > 0 {
			log.Printf("[net] убрано заброшенных комнат: %d", n)
		}
		s.mu.Unlock()
	}
}

func randomCode(n int) string {
	b := make([]byte, n)
	for i := range b {
		k, _ := rand.Int(rand.Reader, big.NewInt(int64(len(netAlphabet))))
		b[i] = netAlphabet[k.Int64()]
	}
	return string(b)
}

// randomSeed — 64 бита из криптографического источника. Не time.Now(): две
// комнаты, открытые в одну миллисекунду, получили бы одинаковые броски.
func randomSeed() uint64 {
	n, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 64))
	if err != nil {
		return 0x9E3779B97F4A7C15
	}
	return n.Uint64()
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
