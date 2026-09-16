package main

// Client log intake — field diagnostics without adb. Devices batch their
// warnings, errors, exceptions (with stack traces) and the engine's
// "[lvn-boot]"/"[lvn-perf]" timing marks to /v1/log/client; each day lands in
// its own JSONL file next to analytics, one line per entry, enriched with the
// device header the client sends once per batch. The admin reads any device's
// tail with a curl — the answer to "it crashes on the partner's phone".
//
// Same trust model as analytics: anonymous by design, so the same token
// bucket rate limit and a hard per-day size cap bound a hostile writer.

import (
	"bufio"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type ClientLogService struct {
	mu         sync.Mutex
	dir        string
	adminToken string
	// ПУЛЬТ ПОДРОБНОГО ЛОГА (TR-86): устройство → до какого момента слать
	// Trace. Указание уезжает в ответе на каждую пачку этого устройства;
	// хранится в файле рядом с дневниками и переживает рестарт.
	directives map[string]*logDirective
	// pruned — за какой день уборка уже прошла. Пустая строка значит «в этой
	// жизни процесса ещё не прибирались».
	pruned string
	// headed — сессия → день, за который заголовок устройства уже записан.
	// Клиент шлёт свою визитку с каждой пачкой (раз в 15 с), а нужна она
	// одна на сессию: в живом логе 16.09 каждая десятая строка была ею.
	headed map[string]string
}

const headedSessionsMax = 20000

// clientLogKeepDays — сколько суток диагностики держим.
//
// Это ДНЕВНИК ОТЛАДКИ, а не история продукта: по нему отвечают на «почему у
// партнёра падает на этой сборке», и вопрос этот всегда про недавнее. Файлы
// при этом крупные — сутки живого тестирования дают 37 и 49 МБ (замер на
// проде 03.09.2026), а уборки не было НИКАКОЙ: 189 МБ накопилось за неполный
// месяц и продолжало расти, пока не кончился бы диск. Диск на маленьком
// боксе кончается тихо и разом: первым перестаёт писаться не лог, а кошелёк.
//
// Две недели — с запасом на «вернусь к этому после выходных».

func NewClientLogService(dir, adminToken string) (*ClientLogService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &ClientLogService{dir: dir, adminToken: adminToken}, nil
}

func (s *ClientLogService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/log/client", s.handleIngest)
	mux.HandleFunc("/v1/admin/client-logs", s.handleTail)
	mux.HandleFunc("/v1/admin/performance", s.handlePerformance)
	// Падения, сгруппированные по сути, а не по строкам (crashes.go): то же,
	// зачем ставят Sentry, на данных, которые уже собираются.
	mux.HandleFunc("/v1/admin/crashes", s.handleCrashes)
	mux.HandleFunc("/v1/admin/log-level", s.handleLogLevel)            // подробный лог с устройства по указанию (TR-86)
	mux.HandleFunc("/v1/admin/log-fetch", s.handleLogFetch)            // кусок кольца за период задним числом (TR-86, этап 2)
	mux.HandleFunc("/v1/admin/client-logs/sessions", s.handleSessions) // что прислало устройство (этап 3)
	mux.HandleFunc("/v1/admin/client-logs/summary", s.handleSummary)   // сводка склеенного дня
}

// logDirective — что просим у устройства: слать Trace до срока и/или выслать
// куски кольца за периоды (этап 2: «запрос задним числом»).
type logDirective struct {
	Until time.Time  `json:"until,omitempty"`
	Fetch []logRange `json:"fetch,omitempty"`
}

type logRange struct {
	From string `json:"from"` // RFC3339 UTC
	To   string `json:"to"`
}

func (d *logDirective) alive(now time.Time) bool {
	return d != nil && (d.Until.After(now) || len(d.Fetch) > 0)
}

func (s *ClientLogService) directivesPath() string {
	return filepath.Join(s.dir, "..", "log-directives.json")
}

// loadDirectives — под замком; просроченные сроки снимаются, пустые указания
// выбрасываются.
func (s *ClientLogService) loadDirectives(now time.Time) {
	if s.directives != nil {
		return
	}
	s.directives = map[string]*logDirective{}
	data, err := os.ReadFile(s.directivesPath())
	if err != nil {
		return
	}
	var raw map[string]*logDirective
	if json.Unmarshal(data, &raw) != nil {
		return
	}
	for dev, d := range raw {
		if d != nil && !d.Until.After(now) {
			d.Until = time.Time{}
		}
		if d.alive(now) {
			s.directives[dev] = d
		}
	}
}

func (s *ClientLogService) saveDirectives() {
	data, _ := json.Marshal(s.directives)
	_ = os.WriteFile(s.directivesPath(), data, 0o600)
}

// directive — указание устройства для правки (заводится пустым).
func (s *ClientLogService) directive(dev string, now time.Time) *logDirective {
	s.loadDirectives(now)
	d := s.directives[dev]
	if d == nil {
		d = &logDirective{}
		s.directives[dev] = d
	}
	return d
}

// directiveFor — живое указание для устройства (под замком); просроченный
// срок снимается, пустое указание удаляется.
func (s *ClientLogService) directiveFor(dev string, now time.Time) (*logDirective, bool) {
	s.loadDirectives(now)
	d, ok := s.directives[dev]
	if !ok {
		return nil, false
	}
	if !d.Until.After(now) {
		d.Until = time.Time{}
	}
	if !d.alive(now) {
		delete(s.directives, dev)
		s.saveDirectives()
		return nil, false
	}
	return d, true
}

// ackFetched — устройство прислало кусок за период: снять его из очереди.
func (s *ClientLogService) ackFetched(dev string, r logRange, now time.Time) {
	d, ok := s.directiveFor(dev, now)
	if !ok {
		return
	}
	kept := d.Fetch[:0]
	for _, f := range d.Fetch {
		if f.From != r.From || f.To != r.To {
			kept = append(kept, f)
		}
	}
	d.Fetch = kept
	if !d.alive(now) {
		delete(s.directives, dev)
	}
	s.saveDirectives()
}

func (s *ClientLogService) directiveList(now time.Time) []map[string]any {
	list := []map[string]any{}
	for dev, d := range s.directives {
		if !d.alive(now) {
			continue
		}
		row := map[string]any{"device": dev}
		if d.Until.After(now) {
			row["until"] = d.Until.UTC().Format(time.RFC3339)
		}
		if len(d.Fetch) > 0 {
			row["fetch"] = d.Fetch
		}
		list = append(list, row)
	}
	return list
}

// PUT /v1/admin/log-fetch {"device","from","to"} — попросить кусок кольца за
// период (RFC3339 UTC, не длиннее суток). Устройство высылает его при
// следующем выходе в сеть; DELETE с теми же полями снимает запрос.
func (s *ClientLogService) handleLogFetch(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	if r.Method != http.MethodPut && r.Method != http.MethodDelete {
		http.Error(w, "PUT or DELETE", http.StatusMethodNotAllowed)
		return
	}
	var req struct {
		Device string `json:"device"`
		From   string `json:"from"`
		To     string `json:"to"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil || req.Device == "" {
		http.Error(w, `{"device", "from", "to"} required`, http.StatusBadRequest)
		return
	}
	from, err1 := time.Parse(time.RFC3339, req.From)
	to, err2 := time.Parse(time.RFC3339, req.To)
	if err1 != nil || err2 != nil || !to.After(from) || to.Sub(from) > 24*time.Hour {
		http.Error(w, "from/to — RFC3339, from < to, не длиннее суток", http.StatusBadRequest)
		return
	}
	rng := logRange{From: from.UTC().Format(time.RFC3339), To: to.UTC().Format(time.RFC3339)}
	now := time.Now().UTC()
	s.mu.Lock()
	defer s.mu.Unlock()
	dev := clip(req.Device, 64)
	if r.Method == http.MethodDelete {
		s.ackFetched(dev, rng, now)
	} else {
		d := s.directive(dev, now)
		dup := false
		for _, f := range d.Fetch {
			if f == rng {
				dup = true
			}
		}
		if !dup {
			d.Fetch = append(d.Fetch, rng)
		}
		s.saveDirectives()
	}
	writeJSON(w, http.StatusOK, map[string]any{"directives": s.directiveList(now)})
}

// GET /v1/admin/log-level — живые указания; PUT {"device","hours"} — слать
// Trace с устройства столько часов (0 — снять). Устройство узнаёт об этом с
// первой же пачкой логов и шлёт всё до срока.
func (s *ClientLogService) handleLogLevel(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	now := time.Now().UTC()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.loadDirectives(now)
	switch r.Method {
	case http.MethodGet:
	case http.MethodPut:
		var req struct {
			Device string  `json:"device"`
			Hours  float64 `json:"hours"`
		}
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil || req.Device == "" {
			http.Error(w, `{"device": id, "hours": 24} required`, http.StatusBadRequest)
			return
		}
		dev := clip(req.Device, 64)
		d := s.directive(dev, now)
		if req.Hours <= 0 {
			d.Until = time.Time{}
		} else {
			if req.Hours > 24*7 {
				req.Hours = 24 * 7
			}
			d.Until = now.Add(time.Duration(req.Hours * float64(time.Hour)))
		}
		if !d.alive(now) {
			delete(s.directives, dev)
		}
		s.saveDirectives()
	default:
		http.Error(w, "GET or PUT", http.StatusMethodNotAllowed)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"directives": s.directiveList(now)})
}

type clientLogBatch struct {
	Device map[string]string `json:"device"` // id, model, os, app, session — informational
	Lines  []clientLogLine   `json:"lines"`
	// Кусок кольца за период (этап 2): строки уровня ring с исходным ts; после
	// приёма запрос снимается с устройства.
	Fetched *logRange `json:"fetched,omitempty"`
}

type clientLogLine struct {
	TS    string `json:"ts,omitempty"`
	Level string `json:"level,omitempty"` // exception | error | warning | info
	Msg   string `json:"msg"`
	Stack string `json:"stack,omitempty"`
	Tail  string `json:"tail,omitempty"` // хвост чёрного ящика при отклонении (TR-86)
	N     int    `json:"n,omitempty"`    // collapse count for repeated lines
	// Server-stamped:
	Dev     string `json:"dev,omitempty"`
	Session string `json:"session,omitempty"`
	// App — версия сборки. Клиент присылает её один раз на пачку, а нужна она
	// НА СТРОКЕ: без этого нельзя ответить на «в какой сборке это появилось»,
	// а это первый вопрос к любому падению.
	App string `json:"app,omitempty"`
}

const clientLogDayMaxSize = 256 << 20

func (s *ClientLogService) handleIngest(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	if !analyticsAllow("log:"+clientIP(r), time.Now()) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
		return
	}
	var batch clientLogBatch
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyDoc)).Decode(&batch); err != nil {
		http.Error(w, "JSON {device, lines:[{ts,level,msg,stack?}]} required", http.StatusBadRequest)
		return
	}
	if len(batch.Lines) == 0 || len(batch.Lines) > 200 {
		http.Error(w, "1..200 lines per batch", http.StatusBadRequest)
		return
	}
	dev := clip(batch.Device["id"], 64)
	session := clip(batch.Device["session"], 64)
	now := time.Now().UTC()

	s.mu.Lock()
	defer s.mu.Unlock()
	s.pruneOldDays(now)
	path := filepath.Join(s.dir, now.Format("2006-01-02")+".jsonl")
	if st, err := os.Stat(path); err == nil && st.Size() > clientLogDayMaxSize {
		http.Error(w, "daily volume cap reached", http.StatusTooManyRequests)
		return
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		http.Error(w, "storage error", http.StatusInternalServerError)
		return
	}
	defer f.Close()

	// The device header rides with every batch, but is written once per
	// session and day — that first line documents the hardware the rest of
	// the session's lines ran on.
	day := now.Format("2006-01-02")
	if len(batch.Device) > 0 && (session == "" || s.headed[session] != day) {
		if session != "" {
			if len(s.headed) >= headedSessionsMax || s.headed == nil {
				s.headed = map[string]string{}
			}
			s.headed[session] = day
		}
		hdr := map[string]any{"ts": now.Format(time.RFC3339), "level": "device", "dev": dev, "session": session}
		for k, v := range batch.Device {
			if k != "id" && k != "session" {
				hdr[k] = clip(v, 128)
			}
		}
		line, _ := json.Marshal(hdr)
		_, _ = f.Write(append(line, '\n'))
	}
	accepted := 0
	for _, ln := range batch.Lines {
		if ln.Msg == "" {
			continue
		}
		ln.Msg = clip(ln.Msg, 4096)
		ln.Stack = clip(ln.Stack, 8192)
		ln.Tail = clip(ln.Tail, 48<<10)
		ln.Level = clip(ln.Level, 16)
		if ln.TS == "" || len(ln.TS) > 40 {
			ln.TS = now.Format(time.RFC3339)
		}
		ln.Dev = dev
		ln.Session = session
		ln.App = clip(batch.Device["app"], 64)
		line, _ := json.Marshal(ln)
		if _, err := f.Write(append(line, '\n')); err == nil {
			accepted++
		}
	}
	resp := map[string]any{"accepted": accepted}
	if batch.Fetched != nil {
		s.ackFetched(dev, *batch.Fetched, now)
	}
	if d, ok := s.directiveFor(dev, now); ok {
		log := map[string]any{}
		if d.Until.After(now) {
			log["until"] = d.Until.UTC().Format(time.RFC3339)
			log["level"] = "trace"
		}
		if len(d.Fetch) > 0 {
			log["fetch"] = d.Fetch
		}
		resp["log"] = log
	}
	writeJSON(w, http.StatusOK, resp)
}

// GET /v1/admin/client-logs/sessions?day=&device= — ЧТО ПРИСЛАЛО УСТРОЙСТВО
// (этап 3): сессии дня с моделью и сборкой, счётом строк по уровням, числом
// отклонений (с хвостом) и сводкой кадров по окнам «W»: средний fps, худший
// кадр, окна с кадрами дольше 50 мс.
func (s *ClientLogService) handleSessions(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	day := r.URL.Query().Get("day")
	if day == "" {
		day = time.Now().UTC().Format("2006-01-02")
	}
	if !reDay.MatchString(day) {
		http.Error(w, "day=YYYY-MM-DD", http.StatusBadRequest)
		return
	}
	device := r.URL.Query().Get("device")
	s.mu.Lock()
	defer s.mu.Unlock()
	// Склеенный день — сессии уже посчитаны в сводке.
	if _, err := os.Stat(filepath.Join(s.dir, day+".jsonl")); err != nil {
		if sum := s.loadSummary(day); sum != nil {
			list := sum.Sessions
			if device != "" {
				list = nil
				for _, row := range sum.Sessions {
					if strings.HasPrefix(row.Device, device) {
						list = append(list, row)
					}
				}
			}
			writeJSON(w, http.StatusOK, map[string]any{"day": day, "sessions": list, "compacted": true})
			return
		}
	}
	rows := map[string]*logSessionRow{}
	var order []string
	if f, err := s.openDayLines(day); err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		for sc.Scan() {
			var ln struct {
				clientLogLine
				Model string `json:"model"`
				OS    string `json:"os"`
			}
			if json.Unmarshal(sc.Bytes(), &ln) != nil {
				continue
			}
			if device != "" && !strings.HasPrefix(ln.Dev, device) {
				continue
			}
			sessionFold(rows, &order, &ln.clientLogLine, ln.Model, ln.OS)
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{"day": day, "sessions": sessionsSorted(rows, order)})
}

// GET /v1/admin/client-logs/summary?day= — сводка склеенного дня: строки по
// уровням и тегам, самые частые строки, сессии; для несклеенного дня пусто.
func (s *ClientLogService) handleSummary(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	day := r.URL.Query().Get("day")
	if !reDay.MatchString(day) {
		http.Error(w, "day=YYYY-MM-DD", http.StatusBadRequest)
		return
	}
	s.mu.Lock()
	sum := s.loadSummary(day)
	s.mu.Unlock()
	if sum == nil {
		writeJSON(w, http.StatusOK, map[string]any{"day": day, "compacted": false})
		return
	}
	writeJSON(w, http.StatusOK, sum)
}

func clip(s string, max int) string {
	if len(s) > max {
		return s[:max]
	}
	return s
}

// GET /v1/admin/client-logs?day=YYYY-MM-DD&device=<prefix>&level=error&n=200 —
// the last n matching lines of a day, newest last. The files are also plain
// JSONL on disk for jq when the query outgrows this.
// pruneOldDays — раз в день: склеить старые дни и убрать совсем старые
// (см. logs_compact.go). Зовётся с приёма пачки под замком.
func (s *ClientLogService) pruneOldDays(now time.Time) {
	day := now.Format("2006-01-02")
	if s.pruned == day {
		return
	}
	s.pruned = day
	s.compactOldDays(now)
}

func (s *ClientLogService) handleTail(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	day := r.URL.Query().Get("day")
	if day == "" {
		day = time.Now().UTC().Format("2006-01-02")
	}
	if !reDay.MatchString(day) {
		http.Error(w, "day=YYYY-MM-DD", http.StatusBadRequest)
		return
	}
	device := r.URL.Query().Get("device")
	level := r.URL.Query().Get("level")
	session := clip(r.URL.Query().Get("session"), 64)
	tag := clip(r.URL.Query().Get("tag"), 64) // приставка строки: [lvn-perf], [lvn-deviation]…
	n := qtyParam(r, "n", 200, 2000)

	s.mu.Lock()
	defer s.mu.Unlock()
	var tail []json.RawMessage
	if f, err := s.openDayLines(day); err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		for sc.Scan() {
			var ln clientLogLine
			if json.Unmarshal(sc.Bytes(), &ln) != nil {
				continue
			}
			if device != "" && !strings.HasPrefix(ln.Dev, device) {
				continue
			}
			if level != "" && ln.Level != level {
				continue
			}
			if session != "" && ln.Session != session {
				continue
			}
			if tag != "" && !strings.HasPrefix(ln.Msg, tag) {
				continue
			}
			tail = append(tail, json.RawMessage(append([]byte(nil), sc.Bytes()...)))
			if len(tail) > n {
				tail = tail[1:]
			}
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{"day": day, "lines": tail})
}
