package adpd

import (
	"encoding/json"
	"fmt"
	"html"
	"math"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// wholeNovelTops are the entry containers for a whole-novel linearization: the
// containers that aren't listed as anyone's pChild.
func wholeNovelTops(fl flow) []uint32 {
	childOf := map[uint32]bool{}
	for _, ch := range fl.childrenOf {
		for _, c := range ch {
			childOf[c] = true
		}
	}
	var tops []uint32
	for n, c := range fl.pg.class {
		if (c == cidStoryFolder || c == cidFlowFrag || c == cidDialog) && !childOf[n] {
			tops = append(tops, n)
		}
	}
	return tops
}

// completeChildren inverts parentOf (child→parent) into parent→children — the REAL
// articy hierarchy (pChild-based childrenOf is incomplete).
func completeChildren(fl flow) map[uint32][]uint32 {
	kids := map[uint32][]uint32{}
	for c, p := range fl.parentOf {
		kids[p] = append(kids[p], c)
	}
	return kids
}

// hierarchyRoot returns the project's Flow root — the parentless node with the most
// FlowFragment children (its children are the chapters).
func hierarchyRoot(fl flow, kids map[uint32][]uint32) (uint32, bool) {
	best := -1
	var root uint32
	for p := range kids {
		if _, hasParent := fl.parentOf[p]; hasParent {
			continue
		}
		ff := 0
		for _, c := range kids[p] {
			if fl.pg.class[c] == cidFlowFrag {
				ff++
			}
		}
		if ff > best {
			best, root = ff, p
		}
	}
	return root, best >= 2
}

var reChapterNum = regexp.MustCompile(`\d+`)

func chapterNum(name string) int {
	if m := reChapterNum.FindString(name); m != "" {
		if v, err := strconv.Atoi(m); err == nil {
			return v
		}
	}
	return 1 << 30
}

type chapterDef struct {
	root uint32
	name string
	num  int
}

// detectChapters returns the project's chapters — the Flow root's FlowFragment
// children, ordered by the number in their name (Эпизод N). Nil when the project
// isn't chaptered (→ single whole-novel export).
func detectChapters(fl flow) []chapterDef {
	kids := completeChildren(fl)
	root, ok := hierarchyRoot(fl, kids)
	if !ok {
		return nil
	}
	var out []chapterDef
	for _, c := range kids[root] {
		if fl.pg.class[c] != cidFlowFrag {
			continue
		}
		out = append(out, chapterDef{root: c, name: strings.TrimSpace(html.UnescapeString(fl.text[c])), num: chapterNum(fl.text[c])})
	}
	if len(out) < 2 {
		return nil
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].num != out[j].num {
			return out[i].num < out[j].num
		}
		return out[i].root < out[j].root
	})
	return out
}

// subtree returns a container plus every transitive descendant (the complete
// hierarchy) — one chapter's scope.
func subtree(kids map[uint32][]uint32, root uint32) map[uint32]bool {
	allowed := map[uint32]bool{}
	var dfs func(uint32)
	dfs = func(n uint32) {
		if allowed[n] {
			return
		}
		allowed[n] = true
		for _, c := range kids[n] {
			dfs(c)
		}
	}
	dfs(root)
	return allowed
}

// ChapterExport is one chapter's articy-export JSON plus its display name.
type ChapterExport struct {
	Name string
	JSON []byte
}

// BuildChaptersJSON splits a chaptered project into one articy-export per chapter
// (the Flow root's FlowFragment children, in episode order). Each chapter is
// linearised with the SAME anchored algorithm as the whole novel, just scoped to
// the chapter's subtree — so within a chapter the flow, choices and reachability are
// identical to the single-file import, and a beat whose flow leaves the chapter
// simply ends it (the next chapter continues the story). This makes each file small
// enough to edit comfortably, unlike one 30k-line script. Global-variable inits go
// into every chapter so each plays standalone. Nil when the project isn't chaptered.
func BuildChaptersJSON(path string) ([]ChapterExport, error) {
	chs, _, err := BuildChaptersJSONReport(path)
	return chs, err
}

// BuildChaptersJSONReport is BuildChaptersJSON plus the linearizer transparency
// report. Chapters that fail to linearize are dropped from the export — the
// report records each one, so the loss is visible instead of silent.
func BuildChaptersJSONReport(path string) ([]ChapterExport, LinearizeReport, error) {
	fl0, proj, err := loadFlow(path)
	if err != nil {
		return nil, LinearizeReport{}, err
	}
	chs := detectChapters(fl0)
	if len(chs) < 2 {
		return nil, LinearizeReport{}, nil
	}
	rep := LinearizeReport{Algorithm: "anchored/chapters"}
	rep.Emittable, rep.Trapped = pinFlowHealth(fl0)
	gvars := globalVars(proj, flowExprs(fl0))
	kids := completeChildren(fl0) // read-only across chapters — hoist out of the loop

	var out []ChapterExport
	for _, ch := range chs {
		// Reuse the decoded flow; reset only the per-chapter MUTABLE state (succ/nodes
		// that linearize rewrites). Reloading + re-decoding the whole Flow partition
		// per chapter was O(chapters × flow size) — 26× a 28 MB decode for a big novel
		// (minutes). Everything else (pin graph, hierarchy, text) is read-only here.
		fl := fl0
		fl.succ = map[uint32][]edge{}
		fl.nodes = map[uint32]bool{}
		allowed := subtree(kids, ch.root)
		entries, ok := linearizeAnchored(fl, ch.root, allowed)
		if !ok || len(entries) == 0 {
			rep.Fallbacks = append(rep.Fallbacks,
				fmt.Sprintf("chapter %q: anchored linearization failed — chapter dropped", ch.name))
			continue
		}
		reach, seen := bfs(fl, entries, math.MaxInt32)
		js, err := json.Marshal(emitModels(fl, reach, seen, entries, gvars))
		if err != nil {
			return nil, rep, err
		}
		out = append(out, ChapterExport{Name: ch.name, JSON: js})
	}
	rep.Chapters = len(out)
	if len(out) < 2 {
		return nil, rep, nil
	}
	return out, rep, nil
}
