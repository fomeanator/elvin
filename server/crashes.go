package main

// Падения, сгруппированные: то же, зачем ставят Sentry, на данных, которые у
// нас уже есть.
//
// Сейчас про ошибки известно две вещи: счётчик в отчёте о здоровье («6 из 23
// игроков со сбоем») и 43 МБ сырых строк в /v1/admin/client-logs. Ни то, ни
// другое не отвечает на вопросы, которые задают на самом деле:
//
//	что ломается ЧАЩЕ ВСЕГО · скольких людей это задевает · в какой сборке
//	появилось · как выглядит стек
//
// Сырой лог отвечать на них не может по своей природе: одно падение в цикле
// даёт тысячу строк и выглядит как тысяча проблем, а редкое падение у
// половины игроков тонет между ними.
//
// ГРУППИРОВКА — единственная сложная часть. Одинаковые по сути ошибки почти
// никогда не совпадают дословно: в тексте адреса, индексы, имена файлов. Ключ
// строится из нормализованного сообщения (числа и пути стёрты) и ВЕРХНЕГО
// кадра стека — того места, где упало. Это ровно тот приём, которым живут
// трекеры ошибок, и он не идеален: слишком общий ключ склеит разное, слишком
// точный размножит одно. Поэтому в ответе всегда лежит образец — по нему
// видно, склеилось ли лишнее.

import (
	"bufio"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// crashGroup — одна проблема, а не одна строка лога.
type crashGroup struct {
	Signature string `json:"signature"` // нормализованный ключ
	Title     string `json:"title"`     // как показать человеку
	Level     string `json:"level"`
	Events    int    `json:"events"`  // сколько раз случилось
	Devices   int    `json:"devices"` // скольких устройств коснулось — важнее числа событий
	Sessions  int    `json:"sessions"`
	FirstSeen string `json:"first_seen"`
	LastSeen  string `json:"last_seen"`
	// Builds — в каких сборках встречалось. Если проблема есть только в
	// последней, её принесла последняя; если во всех — она старая.
	Builds map[string]int `json:"builds,omitempty"`
	// Sample — живой образец со стеком: по нему видно, не склеила ли
	// группировка разные вещи в одну.
	Sample      string `json:"sample,omitempty"`
	SampleStack string `json:"sample_stack,omitempty"`
}

type crashesReport struct {
	From    string       `json:"from"`
	To      string       `json:"to"`
	Groups  []crashGroup `json:"groups"`
	Events  int          `json:"events"`  // всего строк уровня ошибки в окне
	Devices int          `json:"devices"` // сколько разных устройств пострадало
	Note    string       `json:"note,omitempty"`
}

var (
	// Всё, что делает два одинаковых падения разными строками.
	reCrashNum  = regexp.MustCompile(`\b\d+\b`)
	reCrashHex  = regexp.MustCompile(`0x[0-9a-fA-F]+`)
	reCrashPath = regexp.MustCompile(`(?:[A-Za-z]:)?[\\/][^\s:'"]+`)
	reCrashGUID = regexp.MustCompile(`[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}`)
	reCrashWS   = regexp.MustCompile(`\s+`)
)

// crashSignature стирает из сообщения всё переменное. Порядок важен: пути
// вычищаются до чисел, иначе от пути останется мусор из разделителей.
func crashSignature(msg, stack string) string {
	s := msg
	s = reCrashGUID.ReplaceAllString(s, "<id>")
	s = reCrashHex.ReplaceAllString(s, "<addr>")
	s = reCrashPath.ReplaceAllString(s, "<path>")
	s = reCrashNum.ReplaceAllString(s, "<n>")
	s = strings.TrimSpace(reCrashWS.ReplaceAllString(s, " "))
	s = clip(s, 200)
	// Верхний кадр стека — то место, где упало. Одно и то же сообщение из
	// двух разных мест это две разные проблемы, и чинят их по-разному.
	if top := topStackFrame(stack); top != "" {
		s += " @ " + top
	}
	return s
}

func topStackFrame(stack string) string {
	for _, line := range strings.Split(stack, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		line = reCrashPath.ReplaceAllString(line, "")
		line = reCrashNum.ReplaceAllString(line, "")
		line = strings.TrimSpace(reCrashWS.ReplaceAllString(line, " "))
		if line != "" {
			return clip(line, 120)
		}
	}
	return ""
}

func crashIsFailure(level string) bool {
	switch strings.ToLower(level) {
	case "exception", "error", "assert", "fatal":
		return true
	}
	return false
}

// GET /v1/admin/crashes?days=7&limit=50
func (s *ClientLogService) handleCrashes(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	type agg struct {
		g        crashGroup
		devices  map[string]bool
		sessions map[string]bool
	}
	groups := map[string]*agg{}
	allDevices := map[string]bool{}
	total := 0

	for _, day := range win.Days {
		f, err := os.Open(filepath.Join(s.dir, day+".jsonl"))
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 0, 64<<10), 1<<20)
		for sc.Scan() {
			var ln clientLogLine
			if json.Unmarshal(sc.Bytes(), &ln) != nil || ln.Msg == "" || !crashIsFailure(ln.Level) {
				continue
			}
			// N — счётчик схлопнутых повторов на клиенте: одна строка может
			// означать сотню случаев, и не учесть его значит недосчитать
			// ровно те падения, которые бьют чаще всего.
			n := ln.N
			if n <= 0 {
				n = 1
			}
			total += n
			if ln.Dev != "" {
				allDevices[ln.Dev] = true
			}

			sig := crashSignature(ln.Msg, ln.Stack)
			a := groups[sig]
			if a == nil {
				a = &agg{
					g: crashGroup{Signature: sig, Title: clip(strings.TrimSpace(ln.Msg), 200),
						Level: ln.Level, FirstSeen: ln.TS, Builds: map[string]int{},
						Sample: clip(ln.Msg, 500), SampleStack: clip(ln.Stack, 2000)},
					devices: map[string]bool{}, sessions: map[string]bool{},
				}
				groups[sig] = a
			}
			a.g.Events += n
			if ln.TS != "" {
				if a.g.FirstSeen == "" || ln.TS < a.g.FirstSeen {
					a.g.FirstSeen = ln.TS
				}
				if ln.TS > a.g.LastSeen {
					a.g.LastSeen = ln.TS
				}
			}
			if ln.Dev != "" {
				a.devices[ln.Dev] = true
			}
			if ln.Session != "" {
				a.sessions[ln.Session] = true
			}
			build := ln.App
			if build == "" {
				build = "(не назвалась)"
			}
			a.g.Builds[build] += n
			// Образец со стеком лучше образца без: чинить по стеку можно,
			// по сообщению — гадать.
			if a.g.SampleStack == "" && ln.Stack != "" {
				a.g.Sample, a.g.SampleStack = clip(ln.Msg, 500), clip(ln.Stack, 2000)
			}
		}
		f.Close()
	}

	rep := crashesReport{From: win.From, To: win.To, Events: total, Devices: len(allDevices)}
	for _, a := range groups {
		a.g.Devices, a.g.Sessions = len(a.devices), len(a.sessions)
		rep.Groups = append(rep.Groups, a.g)
	}
	// По ЛЮДЯМ, а не по событиям. Падение в цикле даёт тысячу строк у одного
	// человека и выглядит страшнее, чем редкое падение у половины игроков —
	// а чинить в первую очередь надо второе.
	sort.Slice(rep.Groups, func(i, j int) bool {
		if rep.Groups[i].Devices != rep.Groups[j].Devices {
			return rep.Groups[i].Devices > rep.Groups[j].Devices
		}
		return rep.Groups[i].Events > rep.Groups[j].Events
	})
	if n := topN(r); len(rep.Groups) > n {
		rep.Groups = rep.Groups[:n]
	}
	if len(rep.Groups) == 0 {
		rep.Note = "ошибок в окне нет"
	}
	writeJSON(w, http.StatusOK, rep)
}
