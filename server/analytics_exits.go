package main

// «Где бросают и ЧТО там на экране».
//
// Воронка по выборам отвечает на вопрос только там, где выборы есть. Половина
// главы — сплошной текст без единой развилки, и если игроки уходят посреди неё,
// причина не в развилке: плохой спрайт персонажа, не тот фон, сцена, которая
// выглядит поломанной. Событие ухода несёт индекс команды, но «ушли на команде
// 137» само по себе не говорит ничего.
//
// Сервер держит скрипт главы — и это всё, что нужно: по индексу он
// восстанавливает КАДР, который игрок видел последним. Реплика, фон,
// действующие лица со спрайтами. Дальше это уже не догадка, а адрес: открыть и
// посмотреть глазами.

import (
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// exitPoint — одно место выхода с восстановленным кадром.
type exitPoint struct {
	At      int      `json:"at"`               // индекс команды в скрипте
	Players int      `json:"players"`          // сколько раз здесь бросили
	Share   float64  `json:"share"`            // доля от всех выходов из главы
	Label   string   `json:"label,omitempty"`  // ближайшая авторская метка выше
	Line    string   `json:"line,omitempty"`   // реплика, на которой стояли
	Who     string   `json:"who,omitempty"`    // кто её говорил
	BG      string   `json:"bg,omitempty"`     // фон в этот момент
	Actors  []string `json:"actors,omitempty"` // кто на сцене и с каким спрайтом
}

type exitsReport struct {
	Title    string      `json:"title"`
	Chapter  string      `json:"chapter"`
	Starts   int         `json:"starts"`
	Finishes int         `json:"finishes"`
	Abandons int         `json:"abandons"`
	Exits    []exitPoint `json:"exits"`
	Note     string      `json:"note,omitempty"`
}

func (s *AnalyticsService) handleExits(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	title := clip(r.URL.Query().Get("title"), 64)
	chapter := clip(r.URL.Query().Get("chapter"), 64)
	if title == "" || chapter == "" {
		http.Error(w, "нужны title и chapter", http.StatusBadRequest)
		return
	}

	s.rollups.mu.Lock()
	m, _ := s.rollups.window(win.Days)
	var ch *chapRoll
	if t := m.Titles[title]; t != nil {
		ch = t.Chapters[chapter]
	}
	s.rollups.mu.Unlock()

	rep := exitsReport{Title: title, Chapter: chapter}
	if ch == nil {
		rep.Note = "по этой главе в окне нет данных"
		writeJSON(w, http.StatusOK, rep)
		return
	}
	rep.Starts, rep.Finishes, rep.Abandons = ch.Starts, ch.Finishes, ch.Abandons

	total := 0
	for _, n := range ch.Exits {
		total += n
	}
	if total == 0 {
		rep.Note = "выходов посреди главы не зафиксировано"
		writeJSON(w, http.StatusOK, rep)
		return
	}

	script := s.loadChapterScript(title, chapter)
	for key, n := range ch.Exits {
		at, err := strconv.Atoi(key)
		if err != nil {
			continue
		}
		p := exitPoint{At: at, Players: n, Share: ratio(n, total)}
		describeFrame(script, at, &p)
		rep.Exits = append(rep.Exits, p)
	}
	// Самые дорогие места — сверху: там, где бросают чаще всего.
	sort.Slice(rep.Exits, func(i, j int) bool {
		if rep.Exits[i].Players != rep.Exits[j].Players {
			return rep.Exits[i].Players > rep.Exits[j].Players
		}
		return rep.Exits[i].At < rep.Exits[j].At
	})
	if n := topN(r); len(rep.Exits) > n {
		rep.Exits = rep.Exits[:n]
	}
	if script == nil {
		rep.Note = "скрипт главы не найден — кадр не восстановить, только индексы"
	}
	writeJSON(w, http.StatusOK, rep)
}

// loadChapterScript достаёт скомпилированную главу из контента. Имя файла
// выводим из манифеста, а не угадываем: у импортированных глав оно не совпадает
// с идентификатором.
func (s *AnalyticsService) loadChapterScript(title, chapter string) *lvn.Doc {
	rel := s.chapters.scriptURL(title, chapter)
	if rel == "" {
		return nil
	}
	rel = strings.TrimPrefix(rel, "/content/")
	root := s.chapters.contentRoot()
	if root == "" {
		return nil
	}
	data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
	if err != nil {
		return nil
	}
	doc, err := lvn.Parse(data)
	if err != nil {
		return nil
	}
	return doc
}

// describeFrame восстанавливает кадр на индексе: реплика, ближайшая авторская
// метка, фон и действующие лица. Проходим скрипт СВЕРХУ до нужного места:
// сцена — это накопленное состояние, а не одна команда, и «что было на экране»
// иначе не узнать.
func describeFrame(doc *lvn.Doc, at int, out *exitPoint) {
	if doc == nil || at < 0 || at >= len(doc.Script) {
		return
	}
	actors := map[string]string{}
	order := []string{}
	for i := 0; i <= at && i < len(doc.Script); i++ {
		c := doc.Script[i]
		switch c.Op() {
		case "label":
			if id := c.Str("id"); id != "" && !strings.HasPrefix(id, "__") {
				out.Label = id
			}
		case "bg":
			out.BG = c.Str("sprite_url")
		case "clear":
			actors, order = map[string]string{}, nil
		case "actor":
			id := c.Str("id")
			if id == "" {
				continue
			}
			if show, ok := c["show"].(bool); ok && !show {
				delete(actors, id)
				continue
			}
			if _, seen := actors[id]; !seen {
				order = append(order, id)
			}
			// Спрайт может не меняться при смене позы — тогда оставляем прежний.
			if u := c.Str("sprite_url"); u != "" || actors[id] == "" {
				actors[id] = u
			}
		}
	}
	if c := doc.Script[at]; c.Op() == "say" {
		out.Who = c.Str("who")
		out.Line = trimLine(c.Str("text"), 90)
	} else {
		// Ушли не на реплике: ближайшая реплика ВЫШЕ — это то, что человек
		// последним прочитал.
		for i := at; i >= 0; i-- {
			if doc.Script[i].Op() == "say" {
				out.Who = doc.Script[i].Str("who")
				out.Line = trimLine(doc.Script[i].Str("text"), 90)
				break
			}
		}
	}
	for _, id := range order {
		if url, ok := actors[id]; ok {
			if url != "" {
				out.Actors = append(out.Actors, id+" ("+url+")")
			} else {
				out.Actors = append(out.Actors, id)
			}
		}
	}
}

func trimLine(s string, n int) string {
	s = strings.Join(strings.Fields(s), " ")
	if r := []rune(s); len(r) > n {
		return string(r[:n]) + "…"
	}
	return s
}
