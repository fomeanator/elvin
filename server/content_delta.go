package main

import (
	"encoding/json"
	"net/http"
	"sort"
	"sync"
)

// ЧТО ИМЕННО ИЗМЕНИЛОСЬ — вместо «изменилось хоть что-то, забирай всё заново».
//
// Опрос версии стоит копейки: 79 байт, а при тишине — 304 без тела. Но РЕАКЦИЯ
// на смену версии стоила дорого. Замер на живом проекте (04.09):
//
//	/v1/content/version           79 байт
//	/content/asset-versions.json  282 КБ
//	/v1/content/manifest          435 КБ
//
// Правка одной реплики в одной главе меняет хеш её скрипта, а значит и общую
// версию — и клиент забирал 717 КБ, чтобы применить изменение в сотню байт.
// Игрок платил за это трафиком и ожиданием, сервер — полосой, причём тем
// больше, чем чаще мы опрашиваем. То есть «живое обновление» упиралось не в
// частоту опроса, а в цену ответа.
//
// РЕШЕНИЕ — КОЛЬЦО СНИМКОВ, А НЕ ЖУРНАЛ ИЗМЕНЕНИЙ. Развёртки редки: между
// двумя правками контента проходят часы, а не миллисекунды. Поэтому хватает
// небольшого кольца последних состояний: клиент называет версию, которую видел,
// сервер отдаёт разницу с текущей. Кольцо ограничено сверху — память не растёт;
// не нашли названную версию (клиент спал неделю) — честно просим забрать всё,
// а не выдумываем разницу.
//
// Почему не журнал: журнал надо где-то хранить и чинить после перезапуска, а
// кольцо в памяти восстанавливается само первым же опросом — ценой одного
// полного ответа тому, кто попал в перезапуск.
const deltaRingSize = 16

type versionSnapshot struct {
	hash     string
	versions map[string]string
}

type deltaRing struct {
	mu    sync.Mutex
	items []versionSnapshot // от старых к новым
}

// remember кладёт снимок, если он отличается от последнего. Повторы не копятся:
// опрос идёт раз в две секунды, а содержимое меняется раз в часы.
func (r *deltaRing) remember(hash string, versions map[string]string) {
	if hash == "" || versions == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if n := len(r.items); n > 0 && r.items[n-1].hash == hash {
		return
	}
	cp := make(map[string]string, len(versions))
	for k, v := range versions {
		cp[k] = v
	}
	r.items = append(r.items, versionSnapshot{hash: hash, versions: cp})
	if len(r.items) > deltaRingSize {
		r.items = r.items[len(r.items)-deltaRingSize:]
	}
}

// find возвращает снимок по хешу; ok=false — версия выпала из кольца.
func (r *deltaRing) find(hash string) (map[string]string, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for i := len(r.items) - 1; i >= 0; i-- {
		if r.items[i].hash == hash {
			return r.items[i].versions, true
		}
	}
	return nil, false
}

// contentDelta — ответ «что изменилось». Full=true значит «разницу посчитать
// не от чего, забирай полностью»: единственный честный ответ, когда названной
// версии сервер уже не помнит.
type contentDelta struct {
	Since   string   `json:"since,omitempty"`
	Version string   `json:"version"`
	Full bool `json:"full,omitempty"`
	// Изменившиеся — С НОВЫМИ ХЕШАМИ, а не одними путями. Список путей заставил
	// бы клиента всё равно идти за картой версий целиком (282 КБ), и экономия
	// свелась бы к манифесту. С хешами он правит свою карту на месте и не идёт
	// за ней вовсе.
	Changed map[string]string `json:"changed"`
	Removed []string          `json:"removed"`
}

// diffVersions — что поменялось между двумя картами хешей. Удалённые названы
// отдельно: клиенту мало знать, что файл больше не тот, — ему нужно знать, что
// файла больше нет, иначе он оставит его в кэше навсегда.
func diffVersions(from, to map[string]string) (changed map[string]string, removed []string) {
	changed = map[string]string{}
	for path, h := range to {
		if was, ok := from[path]; !ok || was != h {
			changed[path] = h
		}
	}
	for path := range from {
		if _, ok := to[path]; !ok {
			removed = append(removed, path)
		}
	}
	sort.Strings(removed)
	return
}

// handleContentChanges: GET /v1/content/changes?since=<версия>
//
// Без since — клиент ещё ничего не видел: отвечаем «забирай всё», а не пустой
// разницей, иначе он решит, что у него уже всё есть.
func (s *server) handleContentChanges(w http.ResponseWriter, r *http.Request) {
	cur := s.computeVersionsCached(true)
	now := versionHash(cur)
	s.deltas.remember(now, cur)

	since := r.URL.Query().Get("since")
	out := contentDelta{Since: since, Version: now,
		Changed: map[string]string{}, Removed: []string{}}

	switch {
	case since == "":
		out.Full = true
	case since == now:
		// Ничего не изменилось: пустые списки — это ответ, а не молчание.
	default:
		prev, ok := s.deltas.find(since)
		if !ok {
			out.Full = true
			break
		}
		out.Changed, out.Removed = diffVersions(prev, cur)
		if out.Removed == nil {
			out.Removed = []string{}
		}
	}

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(out)
}
