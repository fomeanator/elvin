package main

// СКЛЕЙКА ЛОГОВ (TR-86, Илья 16.09: «инкременторы, которые считают логи и
// потом кладут в архив — сжатый, только важные данные, серую массу вырезают»).
//
// Сырой дневник устройства живёт двое суток — столько его читают сводка
// кадров и хвост дня. Дальше день СКЛЕИВАЕТСЯ: счётчики (строки по уровням,
// по тегам, по шаблонам строк, сессии с fps и отклонениями) ложатся в
// <день>.summary.json, важные строки — ошибки, исключения, предупреждения,
// отклонения с хвостом, заголовки устройств, куски кольца и подробный лог по
// запросу — в <день>.keep.jsonl.gz, а серая масса (окна и кадры замеров,
// прочие info) вырезается. Сжатый архив живёт два месяца, сырьё — двое суток.
// Хвост дня и сессии читаются из архива так же, как из сырья.

import (
	"bufio"
	"compress/gzip"
	"encoding/json"
	"io"
	"log"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

const (
	clientLogRawDays  = 2  // сырой дневник живёт столько суток, потом склеивается
	clientLogKeepDays = 60 // сжатый архив и сводка — столько суток
	summaryTemplates  = 200
)

var reDigits = regexp.MustCompile(`\d+([.,]\d+)?`)

// logSessionRow — сессия дня: кто, на чём, сколько строк и какие, кадры.
type logSessionRow struct {
	Session    string         `json:"session"`
	Device     string         `json:"device"`
	App        string         `json:"app,omitempty"`
	Model      string         `json:"model,omitempty"`
	OS         string         `json:"os,omitempty"`
	First      string         `json:"first"`
	Last       string         `json:"last"`
	Lines      int            `json:"lines"`
	Levels     map[string]int `json:"levels"`
	Deviations int            `json:"deviations"`
	Windows    int            `json:"windows"`
	FPS        float64        `json:"fps,omitempty"`
	WorstMs    float64        `json:"worst_ms,omitempty"`
	Janky      int            `json:"janky_windows"`
}

type logDaySummary struct {
	Day        string           `json:"day"`
	Lines      int              `json:"lines"`
	Kept       int              `json:"kept"`
	RawBytes   int64            `json:"raw_bytes"`
	KeptBytes  int64            `json:"kept_bytes"`
	Devices    int              `json:"devices"`
	Deviations int              `json:"deviations"`
	Levels     map[string]int   `json:"levels"`
	Tags       map[string]int   `json:"tags"`
	Templates  []nameCount      `json:"templates"` // самые частые строки (числа → #)
	Sessions   []*logSessionRow `json:"sessions"`
	CompactAt  string           `json:"compacted_at"`
}

// logLineKept — что достойно архива: всё, кроме серой массы. Серая масса —
// окна и кадры замеров (они уже в сводке сессий) и прочие info без хвоста.
func logLineKept(ln *clientLogLine) bool {
	switch ln.Level {
	case "info":
		return ln.Tail != "" || strings.HasPrefix(ln.Msg, "[lvn-deviation]") || strings.HasPrefix(ln.Msg, "[lvn-perf] session")
	default:
		return true // device, warning, error, exception, trace, ring
	}
}

// logTag — приставка строки «[lvn-…]» или пусто.
func logTag(msg string) string {
	if !strings.HasPrefix(msg, "[") {
		return ""
	}
	if i := strings.IndexByte(msg, ']'); i > 0 && i < 40 {
		return msg[:i+1]
	}
	return ""
}

// sessionFold — одна строка дня в таблицу сессий (общая для сводки и для
// живого ответа по сырью).
func sessionFold(rows map[string]*logSessionRow, order *[]string, ln *clientLogLine, model, osName string) {
	if ln.Session == "" {
		return
	}
	row := rows[ln.Session]
	if row == nil {
		row = &logSessionRow{Session: ln.Session, Device: ln.Dev, App: ln.App, First: ln.TS, Levels: map[string]int{}}
		rows[ln.Session] = row
		*order = append(*order, ln.Session)
	}
	if ln.TS != "" {
		if row.First == "" || ln.TS < row.First {
			row.First = ln.TS
		}
		if ln.TS > row.Last {
			row.Last = ln.TS
		}
	}
	if ln.Level == "device" {
		row.Model, row.OS = model, osName
		if ln.App != "" {
			row.App = ln.App
		}
		return
	}
	row.Lines++
	row.Levels[ln.Level]++
	if ln.Tail != "" {
		row.Deviations++
	}
	if strings.HasPrefix(ln.Msg, "[lvn-perf] W ") || strings.HasPrefix(ln.Msg, "[lvn-perf] window ") {
		fld := performanceFields(ln.Msg)
		if fps, ok := performanceNumber(fld, "fps"); ok {
			row.FPS = (row.FPS*float64(row.Windows) + fps) / float64(row.Windows+1)
		}
		if worst, ok := performanceNumber(fld, "max_ms"); ok && worst > row.WorstMs {
			row.WorstMs = worst
		}
		if o50, ok := performanceNumber(fld, "over50"); ok && o50 > 0 {
			row.Janky++
		}
		row.Windows++
	}
}

func sessionsSorted(rows map[string]*logSessionRow, order []string) []*logSessionRow {
	list := make([]*logSessionRow, 0, len(order))
	for _, id := range order {
		list = append(list, rows[id])
	}
	sort.Slice(list, func(i, j int) bool { return list[i].First > list[j].First })
	return list
}

// openDayLines — сырой дневник, а если он уже склеен — сжатый архив.
func (s *ClientLogService) openDayLines(day string) (io.ReadCloser, error) {
	if f, err := os.Open(filepath.Join(s.dir, day+".jsonl")); err == nil {
		return f, nil
	}
	f, err := os.Open(filepath.Join(s.dir, day+".keep.jsonl.gz"))
	if err != nil {
		return nil, err
	}
	gz, err := gzip.NewReader(f)
	if err != nil {
		f.Close()
		return nil, err
	}
	return struct {
		io.Reader
		io.Closer
	}{gz, f}, nil
}

func (s *ClientLogService) summaryPath(day string) string {
	return filepath.Join(s.dir, day+".summary.json")
}

func (s *ClientLogService) loadSummary(day string) *logDaySummary {
	data, err := os.ReadFile(s.summaryPath(day))
	if err != nil {
		return nil
	}
	var sum logDaySummary
	if json.Unmarshal(data, &sum) != nil {
		return nil
	}
	return &sum
}

// compactDay — склеить один день (под замком сервиса). Идемпотентно: если
// сырья нет, делать нечего.
func (s *ClientLogService) compactDay(day string) error {
	raw := filepath.Join(s.dir, day+".jsonl")
	st, err := os.Stat(raw)
	if err != nil {
		return nil
	}
	in, err := os.Open(raw)
	if err != nil {
		return err
	}
	defer in.Close()
	keepPath := filepath.Join(s.dir, day+".keep.jsonl.gz")
	tmp := keepPath + ".tmp"
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	gz := gzip.NewWriter(out)
	sum := logDaySummary{Day: day, RawBytes: st.Size(), Levels: map[string]int{}, Tags: map[string]int{}}
	templates := map[string]int{}
	devices := map[string]bool{}
	rows := map[string]*logSessionRow{}
	var order []string
	sc := bufio.NewScanner(in)
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
		sum.Lines++
		sum.Levels[ln.Level]++
		if ln.Dev != "" {
			devices[ln.Dev] = true
		}
		if ln.Tail != "" {
			sum.Deviations++
		}
		if tag := logTag(ln.Msg); tag != "" {
			sum.Tags[tag]++
		}
		if ln.Level != "device" && ln.Msg != "" {
			templates[clip(reDigits.ReplaceAllString(ln.Msg, "#"), 96)]++
		}
		sessionFold(rows, &order, &ln.clientLogLine, ln.Model, ln.OS)
		if logLineKept(&ln.clientLogLine) {
			if _, err := gz.Write(append(append([]byte(nil), sc.Bytes()...), '\n')); err != nil {
				gz.Close()
				out.Close()
				os.Remove(tmp)
				return err
			}
			sum.Kept++
		}
	}
	if err := gz.Close(); err != nil {
		out.Close()
		os.Remove(tmp)
		return err
	}
	if err := out.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, keepPath); err != nil {
		os.Remove(tmp)
		return err
	}
	if kst, err := os.Stat(keepPath); err == nil {
		sum.KeptBytes = kst.Size()
	}
	sum.Devices = len(devices)
	sum.Templates = topCounts(templates, summaryTemplates)
	sum.Sessions = sessionsSorted(rows, order)
	sum.CompactAt = time.Now().UTC().Format(time.RFC3339)
	data, _ := json.Marshal(sum)
	if err := os.WriteFile(s.summaryPath(day), data, 0o600); err != nil {
		return err
	}
	if err := os.Remove(raw); err != nil {
		return err
	}
	log.Printf("[client-logs] день %s склеен: строк %d, в архиве %d, %d КБ → %d КБ",
		day, sum.Lines, sum.Kept, sum.RawBytes>>10, sum.KeptBytes>>10)
	return nil
}

// compactOldDays — под замком: склеить сырые дни старше clientLogRawDays,
// удалить архивы и сводки старше clientLogKeepDays.
func (s *ClientLogService) compactOldDays(now time.Time) {
	rawCut := now.AddDate(0, 0, -clientLogRawDays).Format("2006-01-02")
	keepCut := now.AddDate(0, 0, -clientLogKeepDays).Format("2006-01-02")
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return
	}
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() {
			continue
		}
		day := name
		for _, suf := range []string{".keep.jsonl.gz", ".summary.json", ".jsonl"} {
			day = strings.TrimSuffix(day, suf)
		}
		if !reDay.MatchString(day) {
			continue
		}
		switch {
		case day < keepCut:
			if os.Remove(filepath.Join(s.dir, name)) == nil {
				log.Printf("[client-logs] %s старше %d суток — удалён", name, clientLogKeepDays)
			}
		case strings.HasSuffix(name, ".jsonl") && day <= rawCut:
			if err := s.compactDay(day); err != nil {
				log.Printf("[client-logs] склейка %s не удалась: %v", day, err)
			}
		}
	}
}
