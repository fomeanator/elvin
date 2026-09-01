package adpd

import (
	"fmt"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/textcut"
	"sort"
	"strings"
)

// LinearizeReport says which linearizer produced the export, why every earlier
// (more faithful) stage fell through, and how healthy the raw pin-flow graph
// was. The cascade degrades gracefully — anchored → faithful → components →
// hierarchy → structural fan-outs — but a downgrade trades reconvergence and
// loop fidelity for coverage, and the author must see that instead of finding
// out from a subtly-wrong script. Surfaced by `lvnconv import/convert` and the
// server's import endpoint.
type LinearizeReport struct {
	// Algorithm that produced the export: "start" (explicit -start node),
	// "anchored", "anchored/chapters", "faithful", "components", "hierarchy"
	// or "structural-fanouts" (coarsest).
	Algorithm string `json:"algorithm"`
	// Fallbacks lists, in cascade order, why each earlier stage was skipped.
	Fallbacks []string `json:"fallbacks,omitempty"`
	// Emittable is the number of pin-flow nodes that produce script output
	// (dialogue fragments, conditions, outcomes).
	Emittable int `json:"emittable"`
	// Trapped counts emittable nodes that cannot reach any leaf in the raw
	// forward pin-flow — content stuck in cycles that every linearizer has to
	// break or abandon. A high trapped share means the export leans on
	// heuristics; see reach_test.go for the per-node drill-down.
	Trapped int `json:"trapped"`
	// Chapters is the number of chapters emitted by a chaptered import.
	Chapters int `json:"chapters,omitempty"`

	// ── связность: сколько сюжета линеаризатор ПРОЧИТАЛ, а сколько ПРИДУМАЛ ──
	//
	// Trapped выше отвечает на вопрос «есть ли контент, застрявший в циклах», и
	// на живых проектах он всегда был 0 — из-за чего импорт выглядел здоровым,
	// пока граф был разорван. Не хватало именно этих счётчиков: доходит ли поток
	// articy до каждой реплики САМ. Мерить надо это, а не отсутствие циклов.

	// Reachable — emittable-узлы, до которых форвардный pin-flow доходит от
	// настоящего входа своей главы без всякой штопки.
	Reachable int `json:"reachable,omitempty"`
	// Stitched — узлы, до которых поток НЕ доходит: линеаризатор дотягивает их
	// синтетическим goto. Каждый такой узел — место, где порядок и ветвление
	// придуманы нами, а не прочитаны из проекта. >0 = импорт опирается на
	// эвристику, и автор должен это знать до плейтеста.
	Stitched int `json:"stitched,omitempty"`
	// Orphans — реплики с текстом, не попавшие НИ В ОДНУ главу: полная потеря
	// для игрока. Худший класс дефекта этого пайплайна — поэтому отдельное поле.
	Orphans int `json:"orphans,omitempty"`
	// Jumps / JumpsResolved — узлы Jump и сколько из них раскодировано. Jump без
	// цели = поток обрывается на переходе.
	Jumps         int `json:"jumps,omitempty"`
	JumpsResolved int `json:"jumps_resolved,omitempty"`
	// InferredParents — узлы, чей контейнер восстановлен по инцидентным связям, а
	// не прочитан из pParent (см. inferMissingParents). Мера догадки в иерархии.
	InferredParents int `json:"inferred_parents,omitempty"`

	// Warnings — человекочитаемые предупреждения по перечисленному выше, с
	// указанием узлов. Печатаются импортом и уезжают в ответ серверного /import.
	Warnings []string `json:"warnings,omitempty"`
}

// connectivity наполняет счётчики связности и предупреждения. Считает по
// НЕТРОНУТОМУ форвардному pin-flow (forwardEdges), не по fl.succ: succ к этому
// моменту уже перезаписан линеаризатором вместе со синтетическими рёбрами, и по
// нему «достижимо всё» всегда.
func (rep *LinearizeReport) connectivity(fl flow, scopes []scopeDef) {
	pg := fl.pg
	if pg == nil {
		return
	}
	rep.Jumps, rep.JumpsResolved = pg.jumpStats()
	rep.InferredParents = fl.inferred
	inScope := map[uint32]bool{}
	for _, sc := range scopes {
		emit, reached, worst := rawReach(fl, sc.root, sc.allowed)
		rep.Reachable += reached
		rep.Stitched += len(emit) - reached
		for _, n := range emit {
			inScope[n] = true
		}
		if len(emit)-reached > 0 {
			rep.Warnings = append(rep.Warnings, fmt.Sprintf(
				"%s: поток articy сам доходит до %d из %d реплик — остальные %d линеаризатор дотягивает синтетическим переходом (первая: %q)",
				sc.name, reached, len(emit), len(emit)-reached, textcut.Runes(fl.text[worst], 60)))
		}
	}
	// Сироты: реплика с текстом, не попавшая ни в один scope, в экспорт не
	// попадает вообще. Это молчаливая потеря сюжета — самый дорогой дефект здесь.
	var orphans []uint32
	for n := range pg.class {
		if pg.isEmittable(n) && !inScope[n] && strings.TrimSpace(fl.text[n]) != "" {
			orphans = append(orphans, n)
		}
	}
	sort.Slice(orphans, func(i, j int) bool { return orphans[i] < orphans[j] })
	rep.Orphans = len(orphans)
	if len(orphans) > 0 {
		rep.Warnings = append(rep.Warnings, fmt.Sprintf(
			"ПОТЕРЯ: %d реплик(и) не принадлежат ни одной главе и в экспорт не попали — у их контейнера не раскодирован узел. Первая: узел %d %q",
			len(orphans), orphans[0], textcut.Runes(fl.text[orphans[0]], 60)))
	}
	if rep.Jumps > rep.JumpsResolved {
		rep.Warnings = append(rep.Warnings, fmt.Sprintf(
			"%d из %d узлов Jump не раскодированы — на них поток обрывается", rep.Jumps-rep.JumpsResolved, rep.Jumps))
	}
}

// scopeDef — одна область линеаризации (глава или весь роман целиком).
type scopeDef struct {
	name    string
	root    uint32
	allowed map[uint32]bool
}

// rawReach прогоняет форвардный pin-flow articy от настоящего входа scope'а и
// возвращает его emittable-узлы, число достигнутых и самый ранний недостигнутый
// узел (для предупреждения — по нему автор найдёт место обрыва в articy).
func rawReach(fl flow, root uint32, allowed map[uint32]bool) (emit []uint32, reached int, worst uint32) {
	pg := fl.pg
	for n := range pg.class {
		if pg.isEmittable(n) && (allowed == nil || allowed[n]) {
			emit = append(emit, n)
		}
	}
	sort.Slice(emit, func(i, j int) bool { return emit[i] < emit[j] })
	if len(emit) == 0 {
		return nil, 0, 0
	}
	succ := make(map[uint32][]uint32, len(emit))
	for _, n := range emit {
		for _, e := range forwardEdges(pg, n, allowed) {
			succ[n] = append(succ[n], e.dst)
		}
	}
	seen := map[uint32]bool{}
	q := pg.rootEntry([]uint32{root}, allowed)
	for _, e := range q {
		seen[e] = true
	}
	for i := 0; i < len(q); i++ {
		for _, d := range succ[q[i]] {
			if !seen[d] {
				seen[d] = true
				q = append(q, d)
			}
		}
	}
	found := false
	for _, n := range emit {
		if seen[n] {
			reached++
		} else if !found {
			worst, found = n, true
		}
	}
	return emit, reached, worst
}

// pinFlowDiag builds the raw forward pin-flow over emittable nodes (no
// stitching) and marks which of them can reach a leaf. Shared by the
// production health counters and the TestReach drill-down.
func pinFlowDiag(fl flow) (emit []uint32, canEnd map[uint32]bool) {
	pg := fl.pg
	if pg == nil { // synthetic graphs (tests, -start over raw 0x02 flow) have no pin graph
		return nil, map[uint32]bool{}
	}
	succ := map[uint32][]uint32{}
	for n := range pg.class {
		if !pg.isEmittable(n) {
			continue
		}
		emit = append(emit, n)
		var outs []uint32
		seen := map[uint32]bool{}
		for _, p := range pg.outPins(n) {
			for _, t := range pg.reachFromPin(p, nil) {
				if !seen[t] {
					seen[t] = true
					outs = append(outs, t)
				}
			}
		}
		succ[n] = outs
	}
	preds := map[uint32][]uint32{}
	canEnd = map[uint32]bool{}
	var q []uint32
	for _, n := range emit {
		if len(succ[n]) == 0 {
			canEnd[n] = true
			q = append(q, n)
		}
		for _, d := range succ[n] {
			preds[d] = append(preds[d], n)
		}
	}
	for len(q) > 0 {
		x := q[0]
		q = q[1:]
		for _, p := range preds[x] {
			if !canEnd[p] {
				canEnd[p] = true
				q = append(q, p)
			}
		}
	}
	return emit, canEnd
}

// pinFlowHealth reduces pinFlowDiag to the two numbers worth reporting on
// every import: emittable node count and how many of them are trapped.
func pinFlowHealth(fl flow) (emittable, trapped int) {
	emit, canEnd := pinFlowDiag(fl)
	for _, n := range emit {
		if !canEnd[n] {
			trapped++
		}
	}
	return len(emit), trapped
}
