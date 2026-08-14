package main

// Analytics rollups — the derived, queryable half of the append-only log.
//
// The event files stay exactly what they are: one JSONL per UTC day, append
// only, one event per line, greppable with jq. What they are NOT is a query
// engine. A day is capped at analyticsDayMaxSize (256 MiB); re-parsing that on
// every admin refresh — let alone thirty of them for a month view — is not a
// summary, it is a stall.
//
// So every day file gets a ROLLUP: a small fixed-shape aggregate stored beside
// the log under _rollup/<day>.json, together with the byte offset of the
// prefix it was computed from. A query stats the day file and folds only the
// bytes that arrived since. Yesterday's file never grows again, so yesterday's
// rollup is computed exactly once in the lifetime of the server and a 30-day
// range costs 30 small JSON reads. Today's costs whatever arrived since the
// last look.
//
// Why an offset instead of a write-through counter bumped by the ingest path:
// a counter updated after the append silently desyncs the first time the
// process dies between the two, and nothing ever notices. An offset can only
// ever be BEHIND, never wrong, and catching up is the ordinary path. The log
// stays the truth; the rollup is a checkpoint. Delete the whole _rollup
// directory and the next query rebuilds it — that property is what makes the
// cache safe to keep at all.
//
// Every dimension below (title, chapter, op, asset, session) is reported by
// the client, so every dimension is capped. A buggy build that mints a fresh
// chapter id per event must not be able to turn a bounded 256 MiB day into an
// unbounded rollup: overflow lands in one "~other" bucket and raises a
// truncation flag that the reports carry into the answer, so a squeezed number
// is never quietly presented as an exact one.

import (
	"bufio"
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	// Bumping the version invalidates every stored rollup: the on-disk shape
	// is derived data, so a schema change costs one rebuild, never a migration.
	analyticsRollupVersion = 1
	analyticsRollupSubdir  = "_rollup"

	rollupOther = "~other" // the overflow bucket for a capped dimension

	maxRollupNames         = 500
	maxRollupTitles        = 200
	maxRollupChapters      = 500 // per title
	maxRollupUsers         = 20000
	maxRollupSessions      = 20000
	maxRollupOps           = 200
	maxRollupAssets        = 200
	maxRollupExits         = 300 // точек выхода на главу: больше — это уже шум
	maxRollupSlides        = 400 // меток на главу: у самой длинной живой главы их 60
	maxRollupChoices       = 200 // развилок на главу
	maxRollupChoiceOptions = 32  // вариантов в одной развилке
	maxRollupUserTitles    = 8   // titles remembered per player per day
	// Parsed rollups held in memory. The per-player map is the heavy part of a
	// rollup (≈300 B × up to maxRollupUsers), so this constant IS the memory
	// bound of the whole read side: 16 × 20 000 × 300 B ≈ 96 MB at the absolute
	// cap, a few hundred KB at any realistic scale. Days beyond it are re-read
	// from their checkpoint, which is one file read, not a rescan of the log.
	maxRollupCachedDays = 16
	// A checkpoint for TODAY is rewritten as the day grows; past this interval,
	// not on every query. Losing the last few seconds of checkpoint only costs
	// a slightly longer fold after a restart.
	rollupPersistEvery = 30 * time.Second
)

// Event names the rollup understands. Everything else is still counted by
// name and by title — these are only the ones whose SHAPE carries a signal.
const (
	evChapterStart   = "chapter_start"
	evChapterFinish  = "chapter_finish"
	evChapterAbandon = "chapter_abandon" // not sent yet; folded the day it is
	evUnknownOp      = "unknown_op"      // not sent yet; see health.missing_signals
	evAssetFail      = "asset_fail"      // not sent yet
	evBoot           = "boot"
	evFirstScreen    = "first_screen" // первый интерактивный экран после загрузки
	evLabelReach     = "label_reach"  // дошёл до авторской метки — слайд главы
	evChoiceShown    = "choice_shown"
	evChoicePick     = "choice_pick"
)

// rollupEvent is the read-side view of a logged event. Props stay raw: the
// rollup pulls four scalars out of them and must not pay to materialize the
// rest for every one of a million lines.
type rollupEvent struct {
	Name    string                     `json:"name"`
	TS      string                     `json:"ts"`
	User    string                     `json:"user"`
	Title   string                     `json:"title"`
	Author  string                     `json:"author"`
	Chapter string                     `json:"chapter"`
	SID     string                     `json:"sid"`
	Props   map[string]json.RawMessage `json:"props"`
}

type dayRollup struct {
	V     int    `json:"v"`
	Day   string `json:"day"`
	Bytes int64  `json:"bytes"` // consumed prefix of the day file

	Events int `json:"events"`
	Bad    int `json:"bad"`  // lines that did not parse — a signal of its own
	Anon   int `json:"anon"` // events with no signed-in user

	Names    map[string]int         `json:"names,omitempty"`
	Titles   map[string]*titleRoll  `json:"titles,omitempty"`
	Users    map[string]*playerRoll `json:"users,omitempty"`
	Sessions map[string]uint8       `json:"sessions,omitempty"` // bit0 seen, bit1 failed
	Fails    map[string]int         `json:"fails,omitempty"`
	Ops      map[string]int         `json:"ops,omitempty"`    // unknown/unclaimed ops
	Assets   map[string]int         `json:"assets,omitempty"` // assets that failed to load
	Hours    [24]int                `json:"hours"`
	Trunc    map[string]bool        `json:"trunc,omitempty"`

	persistedAt time.Time // in-memory only: throttles checkpoint writes
}

type titleRoll struct {
	Author   string               `json:"a,omitempty"`
	Events   int                  `json:"e"`
	Starts   int                  `json:"s"`
	Finishes int                  `json:"f"`
	Abandons int                  `json:"ab,omitempty"`
	Fails    int                  `json:"x,omitempty"`
	Chapters map[string]*chapRoll `json:"c,omitempty"`
}

type chapRoll struct {
	Events   int    `json:"e"`
	Starts   int    `json:"s"`
	Finishes int    `json:"f"`
	Abandons int    `json:"ab,omitempty"`
	Fails    int    `json:"x,omitempty"`
	First    string `json:"t,omitempty"` // earliest ts seen — the fallback ordering
	// Exits — где именно бросили: ключ «индекс команды», значение — сколько
	// раз. Половина глав идёт без единого выбора, и там вопрос «почему ушли»
	// решается не развилкой, а тем, ЧТО было на экране: плохой спрайт, не тот
	// фон, зависшая сцена. Индекс сам по себе ничего не говорит — но сервер
	// держит скрипт главы и умеет показать по нему кадр (см. handleExits).
	Exits map[string]int `json:"ex,omitempty"`
	// Slides — сколько раз дошли до авторской метки: ключ «индекс команды».
	// Метка и есть слайд: реплик в главе тысячи, меток десятки, и вопрос
	// «где отваливаются» задают именно про них.
	Slides map[string]int `json:"sl,omitempty"`
	// Choices — как проходили развилки: ключ «индекс команды выбора».
	// Выбор безымянен, поэтому адресуется тем же индексом, что и метки, —
	// так развилка ложится на одну ось с точками выхода.
	Choices map[string]*choiceRoll `json:"ch,omitempty"`
}

// choiceRoll — одна развилка за день.
//
// Показы и выборы считаются раздельно НАМЕРЕННО: их разница и есть «сколько
// человек увидели выбор и ушли, не выбрав». Это самый дорогой сигнал в главе —
// место, где игрок думает и закрывает приложение, — и он исчезает, если
// хранить только распределение вариантов.
type choiceRoll struct {
	Shown   int            `json:"s"`           // сколько раз развилку показали
	Picked  int            `json:"p"`           // сколько раз выбрали
	Written int            `json:"w,omitempty"` // вариантов написано в сценарии
	Visible int            `json:"v,omitempty"` // вариантов реально видно игроку
	Options map[string]int `json:"o,omitempty"` // индекс варианта → сколько раз выбран
	// Seconds — раздумье поштучно, для медианы. Ограничено: среднее по
	// развилке ничего не стоит, а хранить все замеры за месяц незачем.
	Seconds []int `json:"sec,omitempty"`
}

const maxRollupChoiceSeconds = 50

// playerRoll is one player's day. It is the only per-identity state kept, and
// it exists for one question the aggregate counters cannot answer: WHERE DID
// THIS PLAYER STOP. Counters know how many chapter_starts a chapter got;
// only a per-player last position knows that four hundred people's last act
// of the week was walking into chapter three.
type playerRoll struct {
	First  string          `json:"f,omitempty"`
	Last   string          `json:"l,omitempty"`
	N      int             `json:"n"`
	Fails  int             `json:"x,omitempty"`
	Fin    int             `json:"fin,omitempty"` // chapters finished
	T      string          `json:"t,omitempty"`   // last title seen
	TTS    string          `json:"tts,omitempty"`
	PT     string          `json:"pt,omitempty"` // title of the last CHAPTER seen
	PC     string          `json:"pc,omitempty"` // that chapter
	PTS    string          `json:"pts,omitempty"`
	Titles map[string]bool `json:"ts,omitempty"`
	// Steps — ступени первой сессии, которые игрок прошёл в этот день, и
	// сколько миллисекунд он ждал загрузки. Хранится по игроку, потому что
	// воронка первого дня считается по людям, а не по событиям: десять
	// перезапусков одного человека — это по-прежнему один человек.
	Steps  []string `json:"st,omitempty"`
	BootMs []int    `json:"bms,omitempty"`
}

// noteStep запоминает пройденную ступень без повторов.
func (p *playerRoll) noteStep(name string) {
	for _, s := range p.Steps {
		if s == name {
			return
		}
	}
	if len(p.Steps) < 8 {
		p.Steps = append(p.Steps, name)
	}
}

func newDayRollup(day string) *dayRollup {
	return &dayRollup{
		V: analyticsRollupVersion, Day: day,
		Names: map[string]int{}, Titles: map[string]*titleRoll{},
		Users: map[string]*playerRoll{}, Sessions: map[string]uint8{},
		Fails: map[string]int{}, Ops: map[string]int{}, Assets: map[string]int{},
		Trunc: map[string]bool{},
	}
}

// analyticsIsFailure classifies an event name as "something went wrong".
// The client already names its failures with a _fail suffix (wardrobe_buy_fail,
// ad_reward_fail), so the convention is honoured rather than invented, and the
// handful of explicit names below are recognised for when they start arriving.
func analyticsIsFailure(name string) bool {
	switch name {
	case "error", "exception", "crash", evUnknownOp, "unclaimed_op", evAssetFail:
		return true
	}
	return strings.HasSuffix(name, "_fail") ||
		strings.HasSuffix(name, "_failed") ||
		strings.HasSuffix(name, "_error")
}

// bump increments a capped counter map. Past the cap the key collapses into
// one bucket and the dimension is flagged truncated — a squeezed total stays
// a total, and the caller can see that it was squeezed.
func (r *dayRollup) bump(m map[string]int, dim, key string, n, max int) {
	if _, ok := m[key]; !ok && len(m) >= max {
		key = rollupOther
		r.Trunc[dim] = true
	}
	m[key] += n
}

func (r *dayRollup) title(id string) *titleRoll {
	t, ok := r.Titles[id]
	if ok {
		return t
	}
	if len(r.Titles) >= maxRollupTitles {
		r.Trunc["titles"] = true
		id = rollupOther
		if t, ok = r.Titles[id]; ok {
			return t
		}
	}
	t = &titleRoll{Chapters: map[string]*chapRoll{}}
	r.Titles[id] = t
	return t
}

func (t *titleRoll) chapter(r *dayRollup, id string) *chapRoll {
	if t.Chapters == nil {
		t.Chapters = map[string]*chapRoll{}
	}
	c, ok := t.Chapters[id]
	if ok {
		return c
	}
	if len(t.Chapters) >= maxRollupChapters {
		r.Trunc["chapters"] = true
		id = rollupOther
		if c, ok = t.Chapters[id]; ok {
			return c
		}
	}
	c = &chapRoll{}
	t.Chapters[id] = c
	return c
}

// player returns the per-player record, or nil once the daily cap is hit.
// Users are the one dimension with no "~other" bucket: folding strangers into
// a shared key would corrupt every unique count that reads this map, so past
// the cap a player contributes to the counters but not to the identity view,
// and users_truncated says so out loud.
func (r *dayRollup) player(id string) *playerRoll {
	p, ok := r.Users[id]
	if ok {
		return p
	}
	if len(r.Users) >= maxRollupUsers {
		r.Trunc["users"] = true
		return nil
	}
	p = &playerRoll{}
	r.Users[id] = p
	return p
}

func (r *dayRollup) foldLine(line []byte) {
	line = bytes.TrimSpace(line)
	if len(line) == 0 {
		return
	}
	var ev rollupEvent
	if json.Unmarshal(line, &ev) != nil || ev.Name == "" {
		r.Bad++ // a line the writer could not have produced: corruption, surfaced
		return
	}
	name := clip(ev.Name, 64)
	// Attribution is stamped at write time (attribution.go); props are read as
	// a fallback for lines written before the stamp existed, and because the
	// Unity helper spells the chapter both ways ("chapter" in NovelApp, "ch"
	// in the documented example).
	title := clip(firstNonEmptyStr(ev.Title, rollupProp(ev.Props, "title")), 64)
	chapter := clip(firstNonEmptyStr(ev.Chapter, rollupProp(ev.Props, "chapter", "ch")), 64)
	sid := clip(firstNonEmptyStr(ev.SID, rollupProp(ev.Props, "sid", "session")), 64)
	ts := normalizeAnalyticsTS(ev.TS, r.Day)
	fail := analyticsIsFailure(name)

	r.Events++
	r.bump(r.Names, "names", name, 1, maxRollupNames)
	if fail {
		r.bump(r.Fails, "names", name, 1, maxRollupNames)
	}
	if h := analyticsHour(ts); h >= 0 {
		r.Hours[h]++
	}

	t := r.title(title)
	t.Events++
	if ev.Author != "" {
		t.Author = ev.Author
	}
	if fail {
		t.Fails++
	}
	switch name {
	case evChapterStart:
		t.Starts++
	case evChapterFinish:
		t.Finishes++
	case evChapterAbandon:
		t.Abandons++
	}
	if chapter != "" {
		c := t.chapter(r, chapter)
		c.Events++
		if ts != "" && (c.First == "" || ts < c.First) {
			c.First = ts
		}
		if fail {
			c.Fails++
		}
		switch name {
		case evChapterStart:
			c.Starts++
		case evChapterFinish:
			c.Finishes++
		case evChapterAbandon:
			c.Abandons++
			// Именно ok, а не «at >= 0»: отсутствующее поле читается как ноль,
			// и без проверки уход без адреса записался бы точкой выхода на
			// первой команде главы — выдуманное место, на котором никто не был.
			if at, ok := rollupPropIntOK(ev.Props, "at"); ok && at >= 0 {
				if c.Exits == nil {
					c.Exits = map[string]int{}
				}
				// Ключ — строка: карта уезжает в JSON чекпоинта, а числовые
				// ключи там всё равно станут строками.
				r.bump(c.Exits, "exits", strconv.Itoa(at), 1, maxRollupExits)
			}
		case evLabelReach:
			// Метка = слайд: место в истории, размеченное автором. Индекс
			// команды, а не имя — так метки, развилки и точки выхода ложатся
			// на одну ось, и «ушли на 420» можно сопоставить со «сюда дошло
			// столько-то».
			if at, ok := rollupPropIntOK(ev.Props, "at"); ok && at >= 0 {
				if c.Slides == nil {
					c.Slides = map[string]int{}
				}
				r.bump(c.Slides, "slides", strconv.Itoa(at), 1, maxRollupSlides)
			}
		case evChoiceShown, evChoicePick:
			at, ok := rollupPropIntOK(ev.Props, "at")
			if !ok || at < 0 {
				break
			}
			if c.Choices == nil {
				c.Choices = map[string]*choiceRoll{}
			}
			key := strconv.Itoa(at)
			ch := c.Choices[key]
			if ch == nil {
				if len(c.Choices) >= maxRollupChoices {
					r.Trunc["choices"] = true
					break
				}
				ch = &choiceRoll{}
				c.Choices[key] = ch
			}
			if name == evChoiceShown {
				ch.Shown++
				// Написано/видно перезаписываем, а не копим: это свойство
				// развилки, а не счётчик. Последнее виденное значение —
				// самое свежее состояние гейтов.
				if n, ok := rollupPropIntOK(ev.Props, "written"); ok && n > 0 {
					ch.Written = n
				}
				if n, ok := rollupPropIntOK(ev.Props, "shown"); ok && n > 0 {
					ch.Visible = n
				}
				break
			}
			ch.Picked++
			if opt, ok := rollupPropIntOK(ev.Props, "option"); ok && opt >= 0 {
				if ch.Options == nil {
					ch.Options = map[string]int{}
				}
				r.bump(ch.Options, "options", strconv.Itoa(opt), 1, maxRollupChoiceOptions)
			}
			if sec, ok := rollupPropIntOK(ev.Props, "seconds"); ok && sec >= 0 &&
				len(ch.Seconds) < maxRollupChoiceSeconds {
				ch.Seconds = append(ch.Seconds, sec)
			}
		}
	}

	if ev.User == "" {
		r.Anon++
	} else if p := r.player(clip(ev.User, 64)); p != nil {
		p.N++
		if fail {
			p.Fails++
		}
		if name == evChapterFinish {
			p.Fin++
		}
		switch name {
		case evBoot, evFirstScreen, evChapterStart, evChapterFinish:
			p.noteStep(name)
		}
		// Время загрузки собираем поштучно: медиана честнее среднего, а
		// посчитать её можно только по отдельным замерам.
		if name == evFirstScreen {
			if ms := rollupPropInt(ev.Props, "boot_ms"); ms > 0 && len(p.BootMs) < 20 {
				p.BootMs = append(p.BootMs, ms)
			}
		}
		if p.First == "" || (ts != "" && ts < p.First) {
			p.First = ts
		}
		if ts >= p.Last {
			p.Last = ts
		}
		if title != "" && ts >= p.TTS {
			p.T, p.TTS = title, ts
		}
		if chapter != "" && ts >= p.PTS {
			p.PT, p.PC, p.PTS = title, chapter, ts
		}
		if title != "" {
			if p.Titles == nil {
				p.Titles = map[string]bool{}
			}
			if _, ok := p.Titles[title]; !ok && len(p.Titles) >= maxRollupUserTitles {
				r.Trunc["user_titles"] = true
			} else {
				p.Titles[title] = true
			}
		}
	}

	if sid != "" {
		f, ok := r.Sessions[sid]
		if !ok && len(r.Sessions) >= maxRollupSessions {
			r.Trunc["sessions"] = true
		} else {
			f |= 1
			if fail {
				f |= 2
			}
			r.Sessions[sid] = f
		}
	}

	switch name {
	case evUnknownOp, "unclaimed_op":
		// One op per event, or a whole session's LvnPlayer.UnclaimedOps as a
		// {op: count} object — both shapes fold to the same counter.
		if op := rollupProp(ev.Props, "op", "name"); op != "" {
			n := rollupPropInt(ev.Props, "count", "n")
			if n <= 0 {
				n = 1
			}
			r.bump(r.Ops, "ops", clip(op, 64), n, maxRollupOps)
		}
		if raw, ok := ev.Props["ops"]; ok {
			var m map[string]int
			if json.Unmarshal(raw, &m) == nil {
				for op, n := range m {
					if n <= 0 {
						n = 1
					}
					r.bump(r.Ops, "ops", clip(op, 64), n, maxRollupOps)
				}
			}
		}
	case evAssetFail, "asset_error", "download_fail":
		if a := rollupProp(ev.Props, "asset", "url", "path", "file"); a != "" {
			r.bump(r.Assets, "assets", clip(a, 160), 1, maxRollupAssets)
		}
	}
}

// mergeFrom folds another day into this aggregate. Counters add; identities
// union; a player's last position is the later of the two, which is what makes
// "where did they stop" meaningful across a week rather than per calendar day.
func (r *dayRollup) mergeFrom(o *dayRollup) {
	if o == nil {
		return
	}
	r.Events += o.Events
	r.Bad += o.Bad
	r.Anon += o.Anon
	for i := range r.Hours {
		r.Hours[i] += o.Hours[i]
	}
	for k, v := range o.Trunc {
		if v {
			r.Trunc[k] = true
		}
	}
	for k, v := range o.Names {
		r.bump(r.Names, "names", k, v, maxRollupNames)
	}
	for k, v := range o.Fails {
		r.bump(r.Fails, "names", k, v, maxRollupNames)
	}
	for k, v := range o.Ops {
		r.bump(r.Ops, "ops", k, v, maxRollupOps)
	}
	for k, v := range o.Assets {
		r.bump(r.Assets, "assets", k, v, maxRollupAssets)
	}
	for id, ot := range o.Titles {
		t := r.title(id)
		t.Events += ot.Events
		t.Starts += ot.Starts
		t.Finishes += ot.Finishes
		t.Abandons += ot.Abandons
		t.Fails += ot.Fails
		if ot.Author != "" {
			t.Author = ot.Author
		}
		for cid, oc := range ot.Chapters {
			c := t.chapter(r, cid)
			c.Events += oc.Events
			c.Starts += oc.Starts
			c.Finishes += oc.Finishes
			c.Abandons += oc.Abandons
			c.Fails += oc.Fails
			if oc.First != "" && (c.First == "" || oc.First < c.First) {
				c.First = oc.First
			}
			// Точки выхода тоже складываются по дням: без этого свёртка на
			// диске правильная, а отчёт за окно пустой — ровно так оно и
			// вышло при первом прогоне.
			for at, n := range oc.Exits {
				if c.Exits == nil {
					c.Exits = map[string]int{}
				}
				r.bump(c.Exits, "exits", at, n, maxRollupExits)
			}
			for at, n := range oc.Slides {
				if c.Slides == nil {
					c.Slides = map[string]int{}
				}
				r.bump(c.Slides, "slides", at, n, maxRollupSlides)
			}
			for at, och := range oc.Choices {
				if c.Choices == nil {
					c.Choices = map[string]*choiceRoll{}
				}
				ch := c.Choices[at]
				if ch == nil {
					if len(c.Choices) >= maxRollupChoices {
						continue
					}
					ch = &choiceRoll{}
					c.Choices[at] = ch
				}
				ch.Shown += och.Shown
				ch.Picked += och.Picked
				if och.Written > 0 {
					ch.Written = och.Written
				}
				if och.Visible > 0 {
					ch.Visible = och.Visible
				}
				for opt, n := range och.Options {
					if ch.Options == nil {
						ch.Options = map[string]int{}
					}
					r.bump(ch.Options, "options", opt, n, 32)
				}
				for _, sec := range och.Seconds {
					if len(ch.Seconds) >= maxRollupChoiceSeconds {
						break
					}
					ch.Seconds = append(ch.Seconds, sec)
				}
			}
		}
	}
	for id, op := range o.Users {
		p := r.player(id)
		if p == nil {
			continue
		}
		p.N += op.N
		p.Fails += op.Fails
		p.Fin += op.Fin
		if op.First != "" && (p.First == "" || op.First < p.First) {
			p.First = op.First
		}
		if op.Last > p.Last {
			p.Last = op.Last
		}
		if op.TTS >= p.TTS && op.T != "" {
			p.T, p.TTS = op.T, op.TTS
		}
		if op.PTS >= p.PTS && op.PC != "" {
			p.PT, p.PC, p.PTS = op.PT, op.PC, op.PTS
		}
		// Ступени и замеры загрузки тоже складываем: без этого воронка первой
		// сессии за окно из нескольких дней теряет данные — ровно так уже
		// вышло с точками выхода.
		for _, st := range op.Steps {
			p.noteStep(st)
		}
		for _, ms := range op.BootMs {
			if len(p.BootMs) < 20 {
				p.BootMs = append(p.BootMs, ms)
			}
		}
		for tt := range op.Titles {
			if p.Titles == nil {
				p.Titles = map[string]bool{}
			}
			if _, ok := p.Titles[tt]; !ok && len(p.Titles) >= maxRollupUserTitles {
				r.Trunc["user_titles"] = true
			} else {
				p.Titles[tt] = true
			}
		}
	}
	for sid, f := range o.Sessions {
		cur, ok := r.Sessions[sid]
		if !ok && len(r.Sessions) >= maxRollupSessions {
			r.Trunc["sessions"] = true
			continue
		}
		r.Sessions[sid] = cur | f
	}
}

// ---------------------------------------------------------------- the store

// rollupStore owns the derived files and the parsed cache. It has its OWN
// mutex and never takes the ingest mutex: an admin asking for a month must not
// be able to stall the event firehose, and a tailer that only ever reads a
// prefix of an append-only file needs no coordination with the appender.
type rollupStore struct {
	dir string // the analytics directory (day files live here)

	mu    sync.Mutex
	cache map[string]*dayRollup
	used  map[string]int64
	tick  int64

	// Observability for the health report and the tests: proof that the
	// second query did not re-read the first query's bytes.
	statBytesRead int64
	statFolds     int
	statRebuilds  int
}

func newRollupStore(dir string) *rollupStore {
	return &rollupStore{dir: dir, cache: map[string]*dayRollup{}, used: map[string]int64{}}
}

func (st *rollupStore) dayPath(day string) string { return filepath.Join(st.dir, day+".jsonl") }
func (st *rollupStore) rollPath(day string) string {
	return filepath.Join(st.dir, analyticsRollupSubdir, day+".json")
}

// day returns the rollup for one UTC day, brought up to date with the log.
// The caller must hold st.mu — reports read the returned pointer, and holding
// the lock for the whole request is what keeps that read race-free.
func (st *rollupStore) day(day string) *dayRollup {
	r, ok := st.cache[day]
	if !ok {
		r = st.load(day)
		st.cache[day] = r
	}
	st.tick++
	st.used[day] = st.tick
	st.advance(r)
	st.evict()
	return r
}

// load reads the stored rollup, or starts a fresh one. A rollup written by an
// older schema is not migrated — it is thrown away and recomputed, which is
// the whole advantage of keeping the log as the source of truth.
func (st *rollupStore) load(day string) *dayRollup {
	body, err := os.ReadFile(st.rollPath(day))
	if err != nil {
		return newDayRollup(day)
	}
	var r dayRollup
	if json.Unmarshal(body, &r) != nil || r.V != analyticsRollupVersion || r.Day != day {
		return newDayRollup(day)
	}
	// Maps omitted when empty come back nil; the fold path assumes they exist.
	if r.Names == nil {
		r.Names = map[string]int{}
	}
	if r.Titles == nil {
		r.Titles = map[string]*titleRoll{}
	}
	if r.Users == nil {
		r.Users = map[string]*playerRoll{}
	}
	if r.Sessions == nil {
		r.Sessions = map[string]uint8{}
	}
	if r.Fails == nil {
		r.Fails = map[string]int{}
	}
	if r.Ops == nil {
		r.Ops = map[string]int{}
	}
	if r.Assets == nil {
		r.Assets = map[string]int{}
	}
	if r.Trunc == nil {
		r.Trunc = map[string]bool{}
	}
	return &r
}

// advance folds every complete line that appeared after the stored offset.
//
// Three cases, and the third is the one that matters: if the file is SHORTER
// than the offset it is not the file we counted — rotated, restored from a
// backup, truncated by a full disk — and the only honest move is to rebuild
// from zero rather than to keep adding to a total that describes a file that
// no longer exists.
func (st *rollupStore) advance(r *dayRollup) {
	path := st.dayPath(r.Day)
	fi, err := os.Stat(path)
	if err != nil {
		if os.IsNotExist(err) && r.Bytes > 0 {
			*r = *newDayRollup(r.Day) // the day was deleted; so are its numbers
			st.statRebuilds++
		}
		return
	}
	if fi.Size() < r.Bytes {
		*r = *newDayRollup(r.Day)
		st.statRebuilds++
	}
	if fi.Size() == r.Bytes {
		return // nothing new: the cheap path, and the common one
	}
	f, err := os.Open(path)
	if err != nil {
		return
	}
	defer f.Close()
	if _, err := f.Seek(r.Bytes, 0); err != nil {
		return
	}
	br := bufio.NewReaderSize(f, 256<<10)
	consumed := r.Bytes
	for {
		line, err := br.ReadBytes('\n')
		if err != nil {
			// A trailing fragment is a half-written event: leave the offset
			// short of it so the next pass folds the whole line, once.
			break
		}
		consumed += int64(len(line))
		r.foldLine(line)
	}
	st.statBytesRead += consumed - r.Bytes
	st.statFolds++
	r.Bytes = consumed
	// A closed day is checkpointed once — its next advance() returns at the
	// size check above and never comes back here.
	if time.Since(r.persistedAt) >= rollupPersistEvery {
		st.persist(r)
		r.persistedAt = time.Now()
	}
}

// persist writes the checkpoint beside the log, atomically. Failure is not an
// error the caller needs: the answer is already computed, and the only cost of
// a missing checkpoint is that the next cold read folds the day again.
func (st *rollupStore) persist(r *dayRollup) {
	dir := filepath.Join(st.dir, analyticsRollupSubdir)
	if os.MkdirAll(dir, 0o755) != nil {
		return
	}
	body, err := json.Marshal(r)
	if err != nil {
		return
	}
	tmp := filepath.Join(dir, "."+r.Day+".tmp")
	if os.WriteFile(tmp, body, 0o600) != nil {
		return
	}
	if os.Rename(tmp, st.rollPath(r.Day)) != nil {
		_ = os.Remove(tmp)
	}
}

func (st *rollupStore) evict() {
	for len(st.cache) > maxRollupCachedDays {
		oldest, at := "", int64(1<<62)
		for d, u := range st.used {
			if u < at {
				oldest, at = d, u
			}
		}
		if oldest == "" {
			return
		}
		delete(st.cache, oldest)
		delete(st.used, oldest)
	}
}

// window folds a list of days into one aggregate and, in the same pass, the
// tiny per-day line the "by day" cut needs.
//
// It deliberately does NOT hand back the day rollups themselves: holding
// ninety-two of them alive at once would defeat maxRollupCachedDays and make a
// quarter-wide query the biggest allocation in the process. One day at a time,
// folded and released.
func (st *rollupStore) window(days []string) (*dayRollup, []dayReport) {
	merged := newDayRollup("")
	out := make([]dayReport, 0, len(days))
	for _, d := range days {
		r := st.day(d)
		out = append(out, dayReport{
			Day: r.Day, Events: r.Events, Players: len(r.Users),
			Starts: r.Names[evChapterStart], Finishes: r.Names[evChapterFinish],
			Fails: sumCounts(r.Fails),
		})
		merged.mergeFrom(r)
	}
	return merged, out
}

// ------------------------------------------------------------- small helpers

func firstNonEmptyStr(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}

// rollupProp pulls a scalar out of the raw props. Objects and arrays are
// refused rather than stringified: a dimension key must never be able to carry
// a whole document into a map key.
func rollupProp(props map[string]json.RawMessage, keys ...string) string {
	for _, k := range keys {
		raw, ok := props[k]
		if !ok || len(raw) == 0 {
			continue
		}
		switch raw[0] {
		case '"':
			var s string
			if json.Unmarshal(raw, &s) == nil && s != "" {
				return s
			}
		case '{', '[', 'n':
			// object / array / null — not a dimension
		default:
			if s := strings.TrimSpace(string(raw)); s != "" {
				return s
			}
		}
	}
	return ""
}

// rollupPropIntOK — то же, но с ответом «поле вообще было». Разница не
// косметическая: отсутствующее поле возвращается нулём, а ноль — законный
// индекс команды и законное число секунд, так что без этого признака
// «события без адреса» неотличимы от «событий в самом начале главы».
func rollupPropIntOK(props map[string]json.RawMessage, keys ...string) (int, bool) {
	for _, k := range keys {
		raw, ok := props[k]
		if !ok {
			continue
		}
		var n float64
		if json.Unmarshal(raw, &n) == nil {
			return int(n), true
		}
	}
	return 0, false
}

func rollupPropInt(props map[string]json.RawMessage, keys ...string) int {
	for _, k := range keys {
		raw, ok := props[k]
		if !ok {
			continue
		}
		var n float64
		if json.Unmarshal(raw, &n) == nil {
			return int(n)
		}
	}
	return 0
}

// normalizeAnalyticsTS returns an RFC3339 UTC stamp. Everything downstream
// compares timestamps as strings — which is only valid when they all share one
// zone and one layout, so any client offset is converted here rather than
// trusted. An unparsable stamp falls back to the day itself, which keeps it
// ordered inside its own day and behind every real event in it.
func normalizeAnalyticsTS(ts, day string) string {
	if ts != "" {
		if t, err := time.Parse(time.RFC3339, ts); err == nil {
			return t.UTC().Format(time.RFC3339)
		}
	}
	if day != "" {
		return day + "T00:00:00Z"
	}
	return ""
}

func analyticsHour(ts string) int {
	if len(ts) < 13 || ts[10] != 'T' {
		return -1
	}
	h := int(ts[11]-'0')*10 + int(ts[12]-'0')
	if h < 0 || h > 23 {
		return -1
	}
	return h
}

func ratio(a, b int) float64 {
	if b <= 0 {
		return 0
	}
	return round4(float64(a) / float64(b))
}

func round4(f float64) float64 {
	return float64(int64(f*10000+0.5)) / 10000
}

type nameCount struct {
	Name  string `json:"name"`
	Count int    `json:"count"`
}

// topCounts sorts a counter map into a stable, truncated top-N list. Ties
// break by name so two identical days produce two identical answers.
func topCounts(m map[string]int, n int) []nameCount {
	out := make([]nameCount, 0, len(m))
	for k, v := range m {
		out = append(out, nameCount{Name: k, Count: v})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].Name < out[j].Name
	})
	if n > 0 && len(out) > n {
		out = out[:n]
	}
	return out
}
