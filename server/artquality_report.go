package main

// «Что в игре выглядит мылом» — по уже опубликованному контенту.
//
// Страж ловит мыло на входе, но контент, который уже лежит на проде, через
// стража не проходил. А именно он сейчас и играется: 84% показов фона и 69%
// показов персонажей в партнёрской новелле — картинки мельче порога.
//
// Отчёт отвечает на два разных вопроса и потому считает две величины:
//   - сколько ФАЙЛОВ переделать — это заказ художнику;
//   - сколько ПОКАЗОВ они дают — это цена вопроса для игрока. Одна миниатюра
//     главного героя в кадре 5430 раз важнее сорока редких фонов.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

type artOffender struct {
	URL     string `json:"url"`
	Kind    string `json:"kind"` // bg | actor | obj
	W       int    `json:"w"`
	H       int    `json:"h"`
	Shows   int    `json:"shows"` // сколько раз команда показа встречается в скриптах
	Titles  string `json:"titles,omitempty"`
	Missing bool   `json:"missing,omitempty"` // файла нет вовсе
}

type artQualityReport struct {
	Files     int           `json:"files"`     // сколько разных картинок используется
	Shows     int           `json:"shows"`     // сколько всего показов
	BadFiles  int           `json:"bad_files"` // из них мелких
	BadShows  int           `json:"bad_shows"` // показов мелкими
	Missing   int           `json:"missing"`   // ссылок на несуществующие файлы
	MinBG     int           `json:"min_bg"`    // применённые пороги
	MinActor  int           `json:"min_actor"`
	Offenders []artOffender `json:"offenders"`
	Note      string        `json:"note,omitempty"`
}

// GET /v1/admin/art-quality?title=<id>&limit=50 — параметр среза называется
// limit, как во всей остальной аналитике.
func (s *server) handleArtQuality(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	only := strings.TrimSpace(r.URL.Query().Get("title"))

	type stat struct {
		kind   string
		shows  int
		titles map[string]bool
	}
	used := map[string]*stat{}

	// Обходим скомпилированные главы: играется именно .lvn, а не исходник.
	_ = filepath.WalkDir(s.content, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(path, ".lvn") {
			return nil
		}
		rel, _ := filepath.Rel(s.content, path)
		title := strings.SplitN(filepath.ToSlash(rel), "/", 2)[0]
		if only != "" && title != only {
			return nil
		}
		raw, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		doc, err := lvn.Parse(raw)
		if err != nil {
			return nil
		}
		for _, c := range doc.Script {
			op := c.Op()
			if op != "bg" && op != "actor" && op != "obj" {
				continue
			}
			u := c.Str("sprite_url")
			if u == "" || strings.HasPrefix(u, "http") || strings.ContainsAny(u, "{}") {
				continue
			}
			st := used[u]
			if st == nil {
				st = &stat{kind: op, titles: map[string]bool{}}
				used[u] = st
			}
			st.shows++
			st.titles[title] = true
		}
		return nil
	})

	rep := artQualityReport{MinBG: minBackgroundHeight, MinActor: minActorHeight}
	for u, st := range used {
		rep.Files++
		rep.Shows += st.shows
		rel := strings.TrimPrefix(u, "/content/")
		abs := filepath.Join(s.content, filepath.Clean("/" + filepath.FromSlash(rel))[1:])
		titles := make([]string, 0, len(st.titles))
		for t := range st.titles {
			titles = append(titles, t)
		}
		sort.Strings(titles)
		if _, err := os.Stat(abs); err != nil {
			rep.Missing++
			rep.Offenders = append(rep.Offenders, artOffender{URL: u, Kind: st.kind,
				Shows: st.shows, Titles: strings.Join(titles, ", "), Missing: true})
			continue
		}
		width, height, ok := imageSize(abs)
		if !ok || height >= minArtHeight(st.kind) {
			continue
		}
		rep.BadFiles++
		rep.BadShows += st.shows
		rep.Offenders = append(rep.Offenders, artOffender{URL: u, Kind: st.kind,
			W: width, H: height, Shows: st.shows, Titles: strings.Join(titles, ", ")})
	}

	// Самое дорогое сверху: сортируем по показам, а не по размеру. Переделывать
	// начинают с того, что игрок видит чаще всего.
	sort.Slice(rep.Offenders, func(i, j int) bool {
		if rep.Offenders[i].Shows != rep.Offenders[j].Shows {
			return rep.Offenders[i].Shows > rep.Offenders[j].Shows
		}
		return rep.Offenders[i].URL < rep.Offenders[j].URL
	})
	if n := topN(r); len(rep.Offenders) > n {
		rep.Offenders = rep.Offenders[:n]
	}
	if rep.Files == 0 {
		rep.Note = "скомпилированных глав не найдено — проверять нечего"
	} else if rep.BadFiles == 0 && rep.Missing == 0 {
		rep.Note = "весь показываемый арт проходит по разрешению"
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(rep)
}
