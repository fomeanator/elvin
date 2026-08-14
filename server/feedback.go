package main

// Отзывы прямо из игры.
//
// Бета идёт, тестеры пишут в мессенджер — и это теряется. «Тут баг» без главы,
// без позиции в сценарии и без номера сборки нельзя ни воспроизвести, ни даже
// отнести к версии: через неделю неизвестно, о какой сборке шла речь.
//
// Поэтому текст — это лишь половина записи. Вторая половина собирается САМА:
// сборка, новелла, глава, индекс команды, устройство. Просить человека их
// назвать бессмысленно — он их не знает и знать не должен.
//
// Хранение — те же .jsonl по дням, что у событий и логов: отзывов будут
// десятки, а не миллионы, и заводить под них базу значит обслуживать базу
// ради десятков строк.

import (
	"bufio"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

const (
	feedbackMaxText  = 4000 // длиннее человек не пишет, а вот бот — да
	feedbackMaxBytes = 16 << 10
	feedbackDayMax   = 4 << 20 // защита от заливки: день не растёт без предела
)

// feedbackEntry — одна запись. Контекст лежит рядом с текстом, а не в отдельной
// таблице: отзыв без «где это было» бесполезен, и разносить их значит однажды
// потерять половину.
type feedbackEntry struct {
	TS      string `json:"ts"`
	User    string `json:"user,omitempty"` // проставляет сервер, клиенту не верим
	Text    string `json:"text"`
	Kind    string `json:"kind,omitempty"` // bug | idea | other — как выбрал человек
	Build   string `json:"build,omitempty"`
	Title   string `json:"title,omitempty"`
	Chapter string `json:"chapter,omitempty"`
	At      int    `json:"at,omitempty"` // индекс команды: место в сценарии
	Label   string `json:"label,omitempty"`
	Device  string `json:"device,omitempty"`
	// Log — последние строки клиентского лога. Именно они превращают «всё
	// сломалось» в чинибельное сообщение; обрезаются, потому что весь лог
	// целиком никто читать не будет.
	Log string `json:"log,omitempty"`
	// Кадр, восстановленный сервером по скрипту — как в отчёте о выходах.
	// Клиент его не присылает: сервер и так держит главу, а лишние килобайты
	// с телефона стоят батареи.
	Line string `json:"line,omitempty"`
	BG   string `json:"bg,omitempty"`
}

type FeedbackService struct {
	mu         sync.Mutex
	dir        string
	auth       *AuthService
	adminToken string
	// chapters/content — чтобы восстановить кадр по индексу команды.
	chapters *chapterIndex
}

func NewFeedbackService(dir string, auth *AuthService, adminToken string, chapters *chapterIndex) (*FeedbackService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &FeedbackService{dir: dir, auth: auth, adminToken: adminToken, chapters: chapters}, nil
}

func (s *FeedbackService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/feedback", s.handleSubmit)
	mux.HandleFunc("/v1/admin/feedback", s.handleList)
}

// POST /v1/feedback — из игры. Требует сессии: анонимный отзыв нельзя
// уточнить у автора, а половина отзывов требует одного встречного вопроса.
func (s *FeedbackService) handleSubmit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var in feedbackEntry
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, feedbackMaxBytes)).Decode(&in) != nil {
		http.Error(w, `{"text": "…", "build": "…", "title": "…"} required`, http.StatusBadRequest)
		return
	}
	in.Text = strings.TrimSpace(in.Text)
	if in.Text == "" {
		http.Error(w, "пустой отзыв", http.StatusBadRequest)
		return
	}

	e := feedbackEntry{
		TS:      time.Now().UTC().Format(time.RFC3339),
		User:    s.auth.UserFromRequest(r), // пусто у неавторизованного — запись всё равно ценна
		Text:    clip(in.Text, feedbackMaxText),
		Kind:    clip(in.Kind, 16),
		Build:   clip(in.Build, 64),
		Title:   clip(in.Title, 64),
		Chapter: clip(in.Chapter, 64),
		At:      in.At,
		Label:   clip(in.Label, 64),
		Device:  clip(in.Device, 64),
		Log:     clip(in.Log, 4000),
	}
	// Кадр восстанавливаем здесь же: через неделю глава может быть
	// перекомпилирована, и тот же индекс будет указывать в другое место.
	if e.Title != "" && e.Chapter != "" && in.At > 0 {
		if doc := s.loadScript(e.Title, e.Chapter); doc != nil {
			var frame exitPoint
			describeFrame(doc, in.At, &frame)
			e.Line, e.BG = frame.Line, frame.BG
			if e.Label == "" {
				e.Label = frame.Label
			}
		}
	}

	if err := s.append(e); err != nil {
		http.Error(w, "не удалось сохранить", http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *FeedbackService) loadScript(title, chapter string) *lvn.Doc {
	rel := s.chapters.scriptURL(title, chapter)
	root := s.chapters.contentRoot()
	if rel == "" || root == "" {
		return nil
	}
	rel = strings.TrimPrefix(rel, "/content/")
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

func (s *FeedbackService) append(e feedbackEntry) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	path := filepath.Join(s.dir, time.Now().UTC().Format("2006-01-02")+".jsonl")
	if fi, err := os.Stat(path); err == nil && fi.Size() > feedbackDayMax {
		return nil // день переполнен: молча не пишем, но и не рушим клиент
	}
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	line, err := json.Marshal(e)
	if err != nil {
		return err
	}
	_, err = f.Write(append(line, '\n'))
	return err
}

// GET /v1/admin/feedback?days=30&limit=200 — новые сверху.
func (s *FeedbackService) handleList(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	var out []feedbackEntry
	s.mu.Lock()
	for _, day := range win.Days {
		f, err := os.Open(filepath.Join(s.dir, day+".jsonl"))
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 0, 64<<10), 1<<20)
		for sc.Scan() {
			var e feedbackEntry
			if json.Unmarshal(sc.Bytes(), &e) == nil && e.Text != "" {
				out = append(out, e)
			}
		}
		f.Close()
	}
	s.mu.Unlock()

	sort.Slice(out, func(i, j int) bool { return out[i].TS > out[j].TS })
	if n := topN(r); len(out) > n {
		out = out[:n]
	}
	// Сколько отзывов на какую сборку — сразу видно, чинит ли новая версия то,
	// на что жаловались в прошлой.
	byBuild := map[string]int{}
	for _, e := range out {
		b := e.Build
		if b == "" {
			b = "(не назвалась)"
		}
		byBuild[b]++
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"feedback": out, "total": len(out), "by_build": byBuild,
		"from": win.From, "to": win.To,
	})
}
