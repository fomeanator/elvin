package importer

// bundle_wire.go is the integration glue for a bundle import: it takes the asset
// entities produced by the bundle asset mappers (bundle_assets.go) and the
// spreadsheet mappings parsed from the game's variable workbook (vars_xlsx.go) and
// stitches them onto a compiled import Result so the copied art actually lines up
// with the story's actors, backgrounds and audio cues.
//
// Every project-specific convention it keys off — the wardrobe flag/variable
// layout, audio-cue prefixes, the protagonist's staging labels and player-name
// template — comes from the import Template (template.go), NOT hardcoded. The
// examples in these comments ("Wardrobe.*", "Music.*", "Главный герой") are the
// built-in DefaultTemplate's values; another project ships its own template.
//
// It runs AFTER the asset mappers have populated res.Sprites (character/heroine
// entities keyed by tech name, e.g. "demo_roman"/"demo_main") and the scripts
// have been compiled. The orchestrator calls PostProcessBundle once, then writes
// the mutated Result. Everything here is a pure in-place mutation of *res.
//
// NOTE for the orchestrator: this pass re-keys the protagonist's heroine entity to
// the protagonist's actor id but deliberately KEEPS the "demo_main" alias, so the
// orchestrator can still point `title.hero` at the resolved actor id
// (Slug(protagonist.StoryName)). There is no Title.Hero / Result.HeroEntity field
// to stash it in here, so setting title.hero remains the orchestrator's job.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"unicode"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// PostProcessBundle wires the spreadsheet mappings into a compiled bundle import
// Result, mutating res in place. In order it:
//
//  1. re-keys character sprite entities from their tech-name key (e.g.
//     "demo_roman") to the story actor id used in the scripts (Slug(StoryName),
//     e.g. "Roman") so an `actor id="Roman"` composes via the emotion art;
//  2. re-keys the layered heroine ("demo_main") to the protagonist's actor id,
//     keeping "demo_main" as an alias (see the package note above re: title.hero);
//  3. stamps `outfit={Wardrobe.<StoryName>}` (heroine also `outfit=`/`hair=` from
//     mainCh_Clothes/mainCh_Hair) onto every `actor` op that left the slot unset,
//     so the outfit follows the story variable at runtime;
//  4. builds each owner entity's `wardrobe` screen block from the spreadsheet
//     catalog (hair/outfit slots), owner matched by StoryName;
//  5. rewrites every `bg` op's sprite_url to the real background copied for its
//     location (via the «Локации» articyName→techName map);
//  6. inserts an `audio` play op after every truthy `Music.*` / `Sound.*` `set`.
//
// Each step is independently defensive: nil maps, missing keys and unparseable
// script data are skipped rather than fatal. contentDir is accepted for symmetry
// with the asset mappers (URLs are content-absolute and don't need it today).
func PostProcessBundle(res *Result, xd XlsxData, contentDir string, tpl *Template) {
	if res == nil {
		return
	}
	tpl = tpl.resolve()
	rekeyCharacters(res, xd)
	rekeyProtagonist(res, xd, tpl)
	replaceWardrobeScenes(res, xd, tpl)  // swap the wardrobe UI + inject the protagonist actor…
	stampWardrobeAxes(res, xd, tpl)      // …BEFORE stamping, so the injected actor gets outfit/hair…
	stampProtagonistMirror(res, xd, tpl) // …and mirror, i.e. shows the live look in the sheet
	buildWardrobes(res, xd, tpl)
	renameProtagonistSpeaker(res, xd, tpl) // show the player-entered name, not the articy label
	applySpeakerNames(res, tpl)            // template speaker_names: latin roster labels → display names

	bgidx := indexBackgrounds(contentDir, xd.Locations, tpl)
	for i := range res.Scripts {
		rewriteScriptOps(&res.Scripts[i], bgidx, tpl)
	}
	// The .lvns sidecar is re-decompiled from the final .lvn once the caller
	// (RunBundle) has ALSO stripped the declared-default boilerplate below —
	// see regenerateLvnsSidecars's call site in bundle.go for why the order
	// matters.

	// The release plans MUST be derived from the scripts as they will ship —
	// the base import collected them before the rewrites above swapped bg urls
	// to the real HD фоны. Live bug this ordering caught: the loading screen
	// proudly warmed 72/72 stale urls while the chapter's actual 5 MB
	// backgrounds cold-loaded mid-scene and lost to the network.
	reconcileChapterAssets(res)
	prefetchLayeredArt(res) // then the layered-cast art on top (no pop-in)
	stampAssetSizes(res, contentDir)
}

// stampAssetSizes writes every release-plan entry's on-disk byte size into the
// manifest, so the client's loading bar progresses by bytes instead of file
// count (count-based bars lurch to 100% when the big files finish last).
func stampAssetSizes(res *Result, contentDir string) {
	if res == nil || contentDir == "" {
		return
	}
	for si := range res.Title.Seasons {
		for ci := range res.Title.Seasons[si].Chapters {
			ch := &res.Title.Seasons[si].Chapters[ci]
			for url, m := range ch.Assets {
				rel := strings.TrimPrefix(url, "/content/")
				if rel == url {
					continue // not a content-absolute url
				}
				if st, err := os.Stat(filepath.Join(contentDir, filepath.FromSlash(rel))); err == nil {
					m.Size = st.Size()
					ch.Assets[url] = m
				}
			}
		}
	}
}

// reconcileChapterAssets rebuilds every chapter's FLAT sprite release set from
// its FINAL compiled script. Non-sprite entries (audio) survive; layered cast
// art is re-added by prefetchLayeredArt, which must run after this pass. It
// also refreshes Chapter.BgURL / Title.CoverURL from that same final script:
// collectArt captured firstBg before this rewrite pass swapped placeholder /
// pre-HD background urls for the real files, so the cover/thumbnail must be
// re-derived here rather than trusted as final (else the title card and
// chapter-1 thumbnail point at a stale, often-broken background).
func reconcileChapterAssets(res *Result) {
	byCid := map[string][]byte{}
	for _, sf := range res.Scripts {
		if strings.HasSuffix(sf.Rel, ".lvns") || !strings.HasSuffix(sf.Rel, ".lvn") {
			continue
		}
		byCid[strings.TrimSuffix(strings.TrimPrefix(sf.Rel, "scripts/"), ".lvn")] = sf.Data
	}
	for si := range res.Title.Seasons {
		for ci := range res.Title.Seasons[si].Chapters {
			ch := &res.Title.Seasons[si].Chapters[ci]
			data := byCid[ch.ID]
			if data == nil {
				continue
			}
			ops, _, ok := decodeScriptOps(data)
			if !ok {
				continue
			}
			fresh := collectAssetsFromOps(ops)
			if ch.Assets == nil {
				ch.Assets = map[string]AssetMeta{}
			}
			for url, m := range ch.Assets {
				if m.Kind == "sprite" {
					delete(ch.Assets, url)
				}
			}
			for url, m := range fresh {
				ch.Assets[url] = m
			}
			if u := firstBgURL(ops); u != "" {
				ch.BgURL = u
				if si == 0 && ci == 0 {
					res.Title.CoverURL = u
				}
			}
		}
	}
}

// firstBgURL returns the sprite_url of the first "bg" op — the real, final
// (post-indexBackgrounds) background — used to refresh Chapter.BgURL /
// Title.CoverURL after the rewrite pass.
func firstBgURL(ops []map[string]any) string {
	for _, op := range ops {
		if op == nil {
			continue
		}
		if name, _ := op["op"].(string); name == "bg" {
			if u, _ := op["sprite_url"].(string); u != "" {
				return u
			}
		}
	}
	return ""
}

// indexBackgrounds builds a fuzzy lookup normalized-name → served-url for the real
// HD backgrounds. It keys the actual фон files on disk (the ground truth, robust
// to the «Локации» sheet's doubled-prefix / mis-mapped rows) and also folds in the
// sheet's articy→tech mapping as a fallback. normBg strips the leading "demo_"
// prefixes and punctuation so "Apartment_Mira_evening" matches a file named
// "Demo_Demo_Apartment_Mira_evening.png".
func indexBackgrounds(contentDir string, locs map[string]string, tpl *Template) map[string]string {
	minSize := tpl.resolve().Backgrounds.MinFileSize
	if minSize <= 0 {
		minSize = 200000
	}
	idx := map[string]string{}
	if contentDir != "" {
		if entries, err := os.ReadDir(filepath.Join(contentDir, "bg")); err == nil {
			for _, e := range entries {
				n := e.Name()
				ln := strings.ToLower(n)
				// HD фоны land as .png from the drop or as .jpg when the import
				// transcoded them (transcodeBgJpeg); tiny .jpg articy assets /
				// placeholders are filtered by the size floor below either way.
				if !strings.HasSuffix(ln, ".png") && !strings.HasSuffix(ln, ".jpg") {
					continue
				}
				if info, err := e.Info(); err == nil && info.Size() < minSize {
					continue // a small file is an autostage placeholder, not real art
				}
				// Index under the full name AND under progressively prefix-stripped
				// suffixes (up to 2 leading "word_" segments) so a scene id matches a
				// file with any project prefix ("Demo_", the doubled "Demo_Demo_", or
				// none) — no per-novel prefix hardcoded.
				for _, k := range bgKeys(strings.TrimSuffix(n, filepath.Ext(n))) {
					if _, ok := idx[k]; !ok {
						idx[k] = "/content/bg/" + n
					}
				}
			}
		}
	}
	// Sheet fallback (used when the file scan is unavailable, e.g. in unit
	// tests). When we CAN see the disk, only map to files that exist — the
	// sheet routinely lists art that never shipped, and an unchecked entry
	// rewrote scenes onto dead /content/bg/Demo_*.png URLs (21 broken
	// backgrounds on the live import).
	for articy, tech := range locs {
		k := normBg(strings.TrimSpace(articy))
		if k == "" || tech == "" {
			continue
		}
		if _, ok := idx[k]; ok {
			continue
		}
		name := strings.TrimSpace(tech) + ".png"
		if contentDir != "" {
			if _, err := os.Stat(filepath.Join(contentDir, "bg", name)); err != nil {
				continue
			}
		}
		idx[k] = "/content/bg/" + name
	}
	return idx
}

// normBg reduces a background name to a match key: lowercase, non-alphanumeric
// (Latin+Cyrillic) removed. No prefix assumptions.
func normBg(s string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(strings.TrimSpace(s)) {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(r)
		}
	}
	return b.String()
}

// bgKeys returns match keys for a фон file stem: the full normalized name plus
// versions with the first one or two "_"-separated segments dropped, so a scene
// id resolves regardless of the project's file prefix (e.g. "Demo_", "Demo_Demo_").
func bgKeys(stem string) []string {
	segs := strings.Split(stem, "_")
	var keys []string
	seen := map[string]bool{}
	for drop := 0; drop <= 2 && drop < len(segs); drop++ {
		k := normBg(strings.Join(segs[drop:], "_"))
		if k != "" && !seen[k] {
			seen[k] = true
			keys = append(keys, k)
		}
	}
	return keys
}

// rekeyCharacters connects each character's emotion-art entity (keyed by tech
// name) to the actor id the scripts speak with (Slug of the story name).
func rekeyCharacters(res *Result, xd XlsxData) {
	if res.Sprites == nil {
		return
	}
	for _, c := range xd.Chars {
		if strings.TrimSpace(c.StoryName) == "" {
			continue
		}
		oldKey := strings.ToLower(c.TechName)
		newKey := Slug(c.StoryName)
		// The heroine is the protagonist's roster row too; she is re-keyed (and
		// kept as an alias) by rekeyProtagonist, not here.
		if oldKey == heroKey(xd) {
			continue
		}
		ent, ok := res.Sprites[oldKey]
		if !ok {
			continue
		}
		// Stamp the proper display name from the roster (the tech name is only a
		// key), then move the entity to the actor id the scripts speak with.
		if m, ok := ent.(map[string]any); ok {
			m["name"] = c.StoryName
		}
		if newKey != oldKey {
			res.Sprites[newKey] = ent
			delete(res.Sprites, oldKey)
		}
	}
}

// heroKey is the layered-heroine entity id — derived from the protagonist's tech
// name in the spreadsheet (e.g. "Demo_Main" → "demo_main"), NOT hardcoded, so the
// pipeline works for any novel. Empty when there's no protagonist row.
func heroKey(xd XlsxData) string {
	if xd.Protagonist == nil {
		return ""
	}
	return strings.ToLower(strings.TrimSpace(xd.Protagonist.TechName))
}

// rekeyProtagonist aliases the layered heroine entity to the protagonist's actor
// id, keeping the original tech-name key so title.hero can still resolve.
func rekeyProtagonist(res *Result, xd XlsxData, tpl *Template) {
	hk := heroKey(xd)
	if res.Sprites == nil || xd.Protagonist == nil || hk == "" {
		return
	}
	ent, ok := res.Sprites[hk]
	if !ok {
		return
	}
	// Alias the entity to every on-stage id the protagonist can appear under (her
	// StoryName id and the template's staging-label id), keeping the tech-name key
	// so title.hero still resolves — so both an `actor id="<StoryName>"` and the
	// AutoStage-staged label render her layered composite, not a placeholder.
	for _, id := range tpl.protagonistActorIDs(xd.Protagonist.StoryName) {
		if id != hk {
			res.Sprites[id] = ent
		}
	}
}

// buildWardrobes builds each owner entity's `wardrobe` screen block from the
// spreadsheet catalog. Every `Wardrobe.<X>` group becomes a slot on the entity
// keyed by StoryName (`Slug(X)`) — NOT tech name (`Wardrobe.Felix` owns the
// `Felix` entity, art prefix `Demo_Felix`); the mainCh_Hair/mainCh_Clothes groups
// are the protagonist's hair/outfit slots. The entity's layers/axes are untouched.
func buildWardrobes(res *Result, xd XlsxData, tpl *Template) {
	if res.Sprites == nil {
		return
	}
	wt := tpl.resolve().Wardrobe
	techByStory := map[string]string{}
	for _, c := range xd.Chars {
		if s := strings.TrimSpace(c.StoryName); s != "" {
			if _, ok := techByStory[s]; !ok {
				techByStory[s] = strings.TrimSpace(c.TechName)
			}
		}
	}
	protoStory, protoTech := "", ""
	if xd.Protagonist != nil {
		protoStory = strings.TrimSpace(xd.Protagonist.StoryName)
		protoTech = strings.TrimSpace(xd.Protagonist.TechName)
	}

	// addSlot attaches one wardrobe slot to its owner entity (looked up by story id),
	// preserving any slot already there (the heroine has both hair and outfit).
	addSlot := func(ownerStory, tech, axis, iconInfix, label, varKey string, removable bool, group []WardrobeItem) {
		if ownerStory == "" || tech == "" {
			return
		}
		ent, ok := res.Sprites[Slug(ownerStory)].(map[string]any)
		if !ok {
			return
		}
		wb, _ := ent["wardrobe"].(map[string]any)
		if wb == nil {
			wb = map[string]any{}
		}
		slot := map[string]any{"name": label, "removable": removable}
		if varKey != "" {
			slot["var"] = varKey // drives write-back: equipping this axis sets the story var
		}
		if items := wardrobeAxisItems(group, label, tech, iconInfix, wt.Premium); len(items) > 0 {
			slot["items"] = items
		}
		wb[axis] = slot
		ent["wardrobe"] = wb
	}

	for key, group := range xd.Wardrobe {
		switch key {
		case wt.ProtagonistHairVar:
			addSlot(protoStory, protoTech, "hair", wt.HairInfix, wt.HairLabel, key, false, group)
		case wt.ProtagonistClothesVar:
			addSlot(protoStory, protoTech, "outfit", wt.OutfitInfix, wt.OutfitLabel, key, true, group)
		default:
			story := strings.TrimPrefix(key, wt.VarPrefix)
			addSlot(story, techByStory[story], "outfit", wt.OutfitInfix, wt.NpcOutfitLabel, key, hasBaseZero(group), group)
		}
	}
}

// hasBaseZero reports whether a wardrobe group carries a value-"0" base state — the
// "undressed" slot that makes an outfit removable.
func hasBaseZero(group []WardrobeItem) bool {
	for _, it := range group {
		if strings.TrimSpace(it.Value) == "0" {
			return true
		}
	}
	return false
}

// replaceWardrobeScenes swaps the novel's in-script wardrobe UI for OUR wardrobe
// screen. Each block runs from `set Open.Wardrobe=true` to the next
// `set Open.Wardrobe=false`; inside it, the "choose clothes/hair" prompts, choices
// and the `set Wardrobe.mainCh_*` ops are stripped and a single `wardrobe_show`
// op is injected — our screen writes those story vars back at runtime on confirm.
// Everything else in the span (audio/bg/actor cues, other sets) is preserved.
func replaceWardrobeScenes(res *Result, xd XlsxData, tpl *Template) {
	if res == nil || xd.Protagonist == nil {
		return
	}
	// Use the protagonist's ON-STAGE actor id (how AutoStage stages her) as the
	// wardrobe char: the in-story sheet mirrors the LIVE actor, so char and the
	// staged actor must be the same id — else a post-wardrobe protagonist op would
	// duplicate her. The entity is aliased to both ids, so the wardrobe resolves.
	char := tpl.protagonistStageID()
	if _, ok := res.Sprites[char]; char == "" || !ok { // no protagonist staged → fall back to StoryName
		char = Slug(strings.TrimSpace(xd.Protagonist.StoryName))
	}
	if char == "" {
		return
	}
	for i := range res.Scripts {
		res.Warnings = append(res.Warnings, replaceWardrobeInScript(&res.Scripts[i], char, tpl)...)
	}
}

// replaceWardrobeInScript performs the swap on one script and returns any
// warnings for the import report. An UNTERMINATED block is the one failure the
// author cannot see from the result: the pass silently leaves the whole span
// alone, so the hand-built articy picker ships as text choices in a novel whose
// every other dressing scene opens the real sheet. Reporting it costs nothing
// and turns "why is this one scene different?" into a line of the import log.
func replaceWardrobeInScript(sf *ScriptFile, char string, tpl *Template) []string {
	ops, rewrap, ok := decodeScriptOps(sf.Data) // ok=false on .lvns sidecars → no-op
	if !ok {
		return nil
	}
	var warnings []string
	out := make([]map[string]any, 0, len(ops))
	changed := false
	dropped := map[string]bool{} // labels removed with the manual picker flow
	for i := 0; i < len(ops); i++ {
		op := ops[i]
		if !isWardrobeFlag(op, true, tpl) {
			out = append(out, op)
			continue
		}
		j := i + 1
		for ; j < len(ops) && !isWardrobeFlag(ops[j], false, tpl); j++ {
		}
		if j >= len(ops) { // unterminated block → leave intact
			out = append(out, op)
			warnings = append(warnings, fmt.Sprintf(
				"%s: `%s = true` at op %d is never closed with `= false` — the wardrobe block "+
					"was left untouched, so this scene ships the source's own picker instead of "+
					"the wardrobe screen", sf.Rel, tpl.resolve().Wardrobe.FlagKey, i))
			continue
		}
		// Auto-stage the protagonist right before the wardrobe so the in-story sheet
		// (which mirrors the LIVE actor) always has someone to dress, even when the
		// beat left the stage empty. Same id as char → no duplicate when the story
		// resumes, and a sticky no-op if she was already on stage.
		out = append(out, op,
			map[string]any{"op": "actor", "id": char, "show": true, "position": "center"},
			map[string]any{"op": "wardrobe_show", "char": char})
		for k := i + 1; k < j; k++ {
			if keepInWardrobeBlock(ops[k], tpl) {
				out = append(out, ops[k])
			} else if n, _ := ops[k]["op"].(string); n == "label" {
				if id, _ := ops[k]["id"].(string); id != "" {
					dropped[id] = true
				}
			}
		}
		out = append(out, ops[j])
		i = j
		changed = true
	}
	if !changed {
		return warnings
	}
	// The manual pickers' BRANCH BODIES live OUTSIDE the flag block (the
	// linearizer appends choice branches at the script tail), so they survive
	// the swap as dead code whose merge-gotos point at labels dropped WITH the
	// block. Retarget any reference to a dropped label to __end: those bodies
	// are unreachable (their choice was removed), this only keeps the script
	// valid — the runtime never walks them.
	if len(dropped) > 0 {
		retarget := func(m map[string]any, key string) {
			if l, _ := m[key].(string); l != "" && dropped[l] {
				m[key] = "__end"
			}
		}
		hasEnd := false
		for _, op := range out {
			switch n, _ := op["op"].(string); n {
			case "goto":
				retarget(op, "label")
			case "if":
				retarget(op, "then")
				retarget(op, "else")
			case "choice":
				if opts, ok := op["options"].([]any); ok {
					for _, o := range opts {
						if oc, ok := o.(map[string]any); ok {
							retarget(oc, "goto")
							if body, ok := oc["body"].([]any); ok {
								for _, b := range body {
									if bc, ok := b.(map[string]any); ok {
										retarget(bc, "label")
									}
								}
							}
						}
					}
				}
			case "label":
				if id, _ := op["id"].(string); id == "__end" {
					hasEnd = true
				}
			}
		}
		if !hasEnd {
			out = append(out, map[string]any{"op": "label", "id": "__end"})
		}
	}
	// The removed pickers' branch bodies are now DEAD CODE at the script tail
	// ({player}: Хвостик. / set Wardrobe.mainCh_Hair=12 / -> __end, ×N) —
	// runtime never walks them, but they dominated the visible end of every
	// chapter's source and read as "broken wardrobe choices" to an author
	// (live partner complaint). Since this pass already made them
	// unreachable, drop everything a control-flow walk can't reach.
	out = removeUnreachableOps(out)
	if b, err := json.Marshal(rewrap(out)); err == nil {
		sf.Data = b
	}
	return warnings
}

// removeUnreachableOps walks the control-flow graph from the script's entry
// (op 0) across fall-through, goto, if then/else, call, and choice option
// edges, and drops every op no path reaches. Conservative by construction:
// any op type it doesn't model keeps its fall-through edge, and an
// unterminated/odd graph errs on the side of keeping ops.
func removeUnreachableOps(ops []map[string]any) []map[string]any {
	n := len(ops)
	if n == 0 {
		return ops
	}
	labelAt := map[string]int{}
	for i, op := range ops {
		if op["op"] == "label" {
			if id, _ := op["id"].(string); id != "" {
				labelAt[id] = i
			}
		}
	}
	reached := make([]bool, n)
	var visit func(int)
	visit = func(i int) {
		for i >= 0 && i < n && !reached[i] {
			reached[i] = true
			op := ops[i]
			jump := func(label string) {
				if j, ok := labelAt[label]; ok {
					visit(j)
				}
			}
			// Точки входа — клик по объекту, перетаскивание, кнопка в дереве
			// `ui` — уводят так же, как переход, только про них не сказано в
			// тексте. Спрашиваем у того же дома, что и валидатор (lvn):
			// раньше этот обход про них не знал вовсе и вырезал бы ветку, в
			// которую игрок попадает нажатием.
			for _, target := range lvn.HotspotTargets(lvn.Cmd(op)) {
				if target != "" {
					jump(target)
				}
			}
			switch op["op"] {
			case "goto":
				if l, _ := op["label"].(string); l != "" {
					jump(l)
				}
				return // unconditional — no fall-through
			case "return":
				return
			case "if":
				if l, _ := op["then"].(string); l != "" {
					jump(l)
				}
				if l, _ := op["else"].(string); l != "" {
					jump(l)
					return // both arms explicit — no fall-through
				}
				// no else → false path falls through
			case "call":
				if l, _ := op["label"].(string); l != "" {
					jump(l)
				}
				// call returns → fall-through continues
			case "choice":
				// ПРАВИЛО ВЫБОРА БЕРЁТСЯ У РАНТАЙМА, а не сочиняется здесь.
				// LvnPlayer: `if (target != null) Jump(target); else _ip++;` —
				// вариант БЕЗ перехода проваливается на команду после выбора
				// (и тело без goto тоже: `_ip++ // body without a goto`).
				//
				// Здесь стоял безусловный `return`, то есть провал считался
				// невозможным. Для articy это сходилось — тамошний импорт всегда
				// ставит goto (в живой базе 6015 вариантов, из них без перехода
				// НОЛЬ), — но правило всё равно расходилось с игрой, а цена
				// расхождения тут высокая: этот обход не отчёт составляет, а
				// УДАЛЯЕТ команды. Разойдись данные с ожиданием — и вырезанным
				// оказался бы кусок, который рантайм играет.
				fallsThrough := false
				if opts, ok := op["options"].([]any); ok {
					for _, o := range opts {
						oc, ok := o.(map[string]any)
						if !ok {
							continue
						}
						target, _ := oc["goto"].(string)
						bodyJumps := false
						if body, ok := oc["body"].([]any); ok {
							for _, b := range body {
								if bc, ok := b.(map[string]any); ok {
									if l, _ := bc["label"].(string); bc["op"] == "goto" && l != "" {
										jump(l)
										bodyJumps = true
									}
								}
							}
						}
						if target != "" {
							jump(target)
							continue
						}
						if !bodyJumps {
							fallsThrough = true // этот вариант уводит на следующую команду
						}
					}
				}
				if !fallsThrough {
					return // каждый вариант уходит переходом — провала нет
				}
			}
			i++
		}
	}
	visit(0)
	kept := make([]map[string]any, 0, n)
	for i, op := range ops {
		if reached[i] {
			kept = append(kept, op)
		}
	}
	return kept
}

// applySpeakerNames rewrites say `who` labels (and matching entity display
// names) through the template's speaker_names map — the partner's articy
// entities and roster are latin ("Bandit", "Mother"); the nameplate should
// speak the novel's language without waiting on a source-data fix. who_id
// (the staged actor id) is untouched, so speaker highlighting keeps working.
func applySpeakerNames(res *Result, tpl *Template) {
	names := tpl.resolve().SpeakerNames
	if res == nil || len(names) == 0 {
		return
	}
	for i := range res.Scripts {
		sf := &res.Scripts[i]
		ops, rewrap, ok := decodeScriptOps(sf.Data)
		if !ok {
			continue
		}
		if applySpeakerNameOverrides(ops, tpl) {
			if b, err := json.Marshal(rewrap(ops)); err == nil {
				sf.Data = b
			}
		}
	}
	// Entity display names (cast cards, wardrobe headers) follow the same map.
	applySpeakerNameOverridesToSprites(res.Sprites, tpl)
}

// displayNameFor resolves a speaker label through the names map, falling back
// through VARIANT suffixes: "Roman_neardeath_blood" → "Roman_neardeath" →
// "Roman" → «Роман». State-variant sprites (X_black silhouettes, X_dead,
// X_flashback…) speak under their variant label but ARE the base character —
// the nameplate should say the character's name.
func displayNameFor(names map[string]string, who string) string {
	key := who
	for {
		if d, ok := names[key]; ok {
			return d
		}
		i := strings.LastIndex(key, "_")
		if i <= 0 {
			return ""
		}
		key = key[:i]
	}
}

// renameProtagonistSpeaker rewrites the player character's say `who` to the
// template's PlayerTemplate ("{player}") so the dialogue shows the name the player
// chose, not the articy label (the engine interpolates say `who` against Vars —
// LvnPlayer sets Vars[PlayerVar] from the name-input screen). The on-stage actor id
// is unchanged; only the visible speaker name is. A default (the xlsx StoryName) is
// seeded so it's never blank when name-input is skipped.
func renameProtagonistSpeaker(res *Result, xd XlsxData, tpl *Template) {
	if res == nil {
		return
	}
	st := tpl.resolve().Staging
	playerTmpl := st.PlayerTemplate
	if playerTmpl == "" {
		playerTmpl = "{player}"
	}
	playerVar := st.PlayerVar
	if playerVar == "" {
		playerVar = "player"
	}
	def := ""
	if xd.Protagonist != nil {
		def = strings.TrimSpace(xd.Protagonist.StoryName)
	}
	for i := range res.Scripts {
		renameSpeakerInScript(&res.Scripts[i], tpl, playerTmpl)
	}
	// Seed a default player name at the top of the first chapter (default=true → the
	// name-input value, set earlier at novel start, wins; this is only the fallback).
	if def != "" && len(res.Scripts) > 0 {
		seedPlayerDefault(&res.Scripts[0], playerVar, def)
	}
}

func renameSpeakerInScript(sf *ScriptFile, tpl *Template, _ string) {
	ops, rewrap, ok := decodeScriptOps(sf.Data)
	if !ok {
		return
	}
	if applyProtagonistSpeakerRename(ops, tpl) {
		if b, err := json.Marshal(rewrap(ops)); err == nil {
			sf.Data = b
		}
	}
}

// seedPlayerDefault prepends `set <playerVar>=<name> default=true` so the player
// template always resolves; the name-input screen (run at novel start) overrides it.
func seedPlayerDefault(sf *ScriptFile, playerVar, name string) {
	ops, rewrap, ok := decodeScriptOps(sf.Data)
	if !ok {
		return
	}
	seed := map[string]any{"op": "set", "key": playerVar, "value": name, "default": true}
	ops = append([]map[string]any{seed}, ops...)
	if b, err := json.Marshal(rewrap(ops)); err == nil {
		sf.Data = b
	}
}

// axisTokenRe matches a catalog layer's axis placeholder, e.g. `{emotion}`/`{outfit}`.
var axisTokenRe = regexp.MustCompile(`\{(\w+)\}`)

// prefetchLayeredArt adds every art file the chapter's layered cast can show to the
// chapter's prefetch set (critical), so the whole chapter is preloaded at its start
// and nothing pops in. Needed because collectChapterAssets only sees the actor ops'
// flat sprite_url — the real layered composite (body + {outfit}/{hair}/{emotion}
// layers, resolved via the catalog) never lands in the release set otherwise.
func prefetchLayeredArt(res *Result) {
	if res == nil || res.Sprites == nil {
		return
	}
	byCid := map[string][]byte{} // chapter id → compiled .lvn bytes
	for _, sf := range res.Scripts {
		if strings.HasSuffix(sf.Rel, ".lvns") || !strings.HasSuffix(sf.Rel, ".lvn") {
			continue
		}
		cid := strings.TrimSuffix(strings.TrimPrefix(sf.Rel, "scripts/"), ".lvn")
		byCid[cid] = sf.Data
	}
	for si := range res.Title.Seasons {
		for ci := range res.Title.Seasons[si].Chapters {
			ch := &res.Title.Seasons[si].Chapters[ci]
			if data := byCid[ch.ID]; data != nil {
				addLayeredChapterAssets(ch, data, res.Sprites)
			}
		}
	}
}

func addLayeredChapterAssets(ch *Chapter, scriptData []byte, sprites map[string]any) {
	ops, _, ok := decodeScriptOps(scriptData)
	if !ok {
		return
	}
	if ch.Assets == nil {
		ch.Assets = map[string]AssetMeta{}
	}
	// Critical gates the Play button — it must cover the OPENING scene only.
	// Marking the whole chapter's cast critical made the gate download every
	// outfit/emotion of every character up front (live case: 72 files,
	// 69 seconds on a slow link before the first line). Later scenes' art
	// streams as deferred while the player reads; the stage self-heals any
	// miss.
	bgCount := 0
	done := map[string]bool{} // entity ids already expanded
	for _, op := range ops {
		if op == nil {
			continue
		}
		if n, _ := op["op"].(string); n == "bg" {
			bgCount++
			continue
		} else if n != "actor" {
			continue
		}
		id, _ := op["id"].(string)
		if id == "" || done[id] {
			continue
		}
		done[id] = true
		ent, ok := sprites[id].(map[string]any)
		if !ok {
			continue
		}
		firstScene := bgCount <= 1
		crit := map[string]bool{}
		if firstScene {
			// The gate needs the actor's OPENING look, live: body, every
			// emotion (they swap per line), the DEFAULT outfit/hair. The
			// rest of the wardrobe streams deferred like everything else.
			for _, u := range resolveEntityCriticalURLs(ent) {
				crit[u] = true
			}
		}
		for _, url := range resolveEntityArtURLs(ent) {
			if _, exists := ch.Assets[url]; !exists {
				ch.Assets[url] = AssetMeta{Kind: "sprite", Scope: "chapter", Critical: crit[url]}
			}
		}
	}
}

// resolveEntityCriticalURLs is resolveEntityArtURLs trimmed to the entity's
// opening look: static layers, every EMOTION value (emotions change per line
// of dialogue) and only the DEFAULT value of every other axis (outfits/hair
// swap rarely and can stream).
func resolveEntityCriticalURLs(ent map[string]any) []string {
	layers, _ := ent["layers"].([]any)
	axes, _ := ent["axes"].(map[string]any)
	defaults, _ := ent["defaults"].(map[string]any)
	var out []string
	for _, l := range layers {
		lm, ok := l.(map[string]any)
		if !ok {
			continue
		}
		url, _ := lm["url"].(string)
		if url == "" {
			continue
		}
		m := axisTokenRe.FindStringSubmatch(url)
		if m == nil {
			out = append(out, url)
			continue
		}
		axis := m[1]
		if axis == "emotion" {
			if vals, ok := axes[axis].([]any); ok {
				for _, v := range vals {
					out = append(out, strings.Replace(url, "{"+axis+"}", fmt.Sprint(v), 1))
				}
			}
			continue
		}
		def := defaults[axis]
		if def == nil {
			if vals, ok := axes[axis].([]any); ok && len(vals) > 0 {
				def = vals[0]
			}
		}
		if def != nil {
			out = append(out, strings.Replace(url, "{"+axis+"}", fmt.Sprint(def), 1))
		}
	}
	return out
}

// resolveEntityArtURLs expands every layer of a catalog entity over ALL its axis
// values → the full set of concrete art URLs the entity can ever display. A static
// layer (no `{axis}`) yields its url as-is; a templated layer yields one url per
// value of that axis (so a whole wardrobe / all emotions are covered).
func resolveEntityArtURLs(ent map[string]any) []string {
	layers, _ := ent["layers"].([]any)
	axes, _ := ent["axes"].(map[string]any)
	var out []string
	for _, l := range layers {
		lm, ok := l.(map[string]any)
		if !ok {
			continue
		}
		url, _ := lm["url"].(string)
		if url == "" {
			continue
		}
		m := axisTokenRe.FindStringSubmatch(url)
		if m == nil { // static layer
			out = append(out, url)
			continue
		}
		axis := m[1]
		vals, _ := axes[axis].([]any)
		for _, v := range vals {
			out = append(out, strings.ReplaceAll(url, "{"+axis+"}", fmt.Sprint(v)))
		}
	}
	return out
}

// isWardrobeFlag matches `set <FlagKey>=<want>` — the block's open/close markers.
func isWardrobeFlag(op map[string]any, want bool, tpl *Template) bool {
	if op == nil {
		return false
	}
	if n, _ := op["op"].(string); n != "set" {
		return false
	}
	if k, _ := op["key"].(string); k != tpl.resolve().Wardrobe.FlagKey {
		return false
	}
	b, _ := op["value"].(bool)
	return b == want
}

// keepInWardrobeBlock reports whether an op inside a wardrobe block survives the
// swap: the choose-clothes/hair prompts, their choices and the protagonist outfit
// sets are dropped (our screen replaces them); everything else (audio/bg/actor,
// other sets) is preserved in order.
func keepInWardrobeBlock(op map[string]any, tpl *Template) bool {
	wt := tpl.resolve().Wardrobe
	switch n, _ := op["op"].(string); n {
	case "say", "choice", "label":
		return false
	case "goto", "if", "return", "call":
		// The manual picker's CONTROL FLOW must not survive the swap: the
		// runtime wardrobe (wardrobe_show) replaces the whole try-on loop,
		// and a kept re-open branch tail (`goto n44`) sat as the last op
		// before the closing Open.Wardrobe=false — an unconditional jump
		// that CUT the path to the closing marker and everything after it.
		// Live-hit: a third of one partner chapter (the Felix/Oscar scene, 1190
		// unique lines) plus chunks of ch14/17/18/21-23 were unreachable on
		// prod. The block must stay a straight line into its closing op.
		return false
	case "set":
		k, _ := op["key"].(string)
		return k != wt.ProtagonistClothesVar && k != wt.ProtagonistHairVar
	default:
		return true // audio, bg, actor, obj, …
	}
}

// wardrobeAxisItems turns catalog rows into wardrobe items, dropping hidden rows and
// rows with no art (empty tech). A blank name falls back to "<label> <value>"; the
// icon is the canonical served art URL; a `[premium]` row is priced in crystals.
func wardrobeAxisItems(src []WardrobeItem, label, tech, iconInfix string, premium PremiumTemplate) []any {
	var items []any
	for _, it := range src {
		if it.Hide || strings.TrimSpace(it.Value) == "" || strings.TrimSpace(it.TechName) == "" {
			continue
		}
		name := strings.TrimSpace(it.Name)
		if name == "" {
			name = label + " " + it.Value
		}
		item := map[string]any{
			"value": it.Value,
			"name":  name,
			"icon":  "/content/art/" + tech + "_" + iconInfix + "_" + it.Value + ".png",
		}
		if it.Premium {
			item["currency"] = premium.Currency
			item["price"] = premium.Price
			item["rarity"] = premium.Rarity
		}
		items = append(items, item)
	}
	return items
}

// stampWardrobeAxes makes each character's outfit follow its story variable: for
// every character with a `Wardrobe.<StoryName>` group it walks the `actor` ops and
// sets `outfit={Wardrobe.<StoryName>}` where the author left it unset (the heroine
// additionally gets `outfit={Wardrobe.mainCh_Clothes}` and `hair={Wardrobe.mainCh_Hair}`).
// At runtime AxesOf interpolates the variable to the current index; `defaults` is the
// floor for any op this pass didn't reach.
func stampWardrobeAxes(res *Result, xd XlsxData, tpl *Template) {
	if res == nil || len(res.Scripts) == 0 || len(xd.Wardrobe) == 0 {
		return
	}
	wt := tpl.resolve().Wardrobe
	protoStory := ""
	if xd.Protagonist != nil {
		protoStory = strings.TrimSpace(xd.Protagonist.StoryName)
	}
	protoIDs := tpl.protagonistActorIDs(protoStory) // her StoryName id + the staging label
	stamp := map[string]map[string]string{}         // actor id → {axisField: "{var}"}
	add := func(actorID, field, tmpl string) {
		if actorID == "" {
			return
		}
		if stamp[actorID] == nil {
			stamp[actorID] = map[string]string{}
		}
		stamp[actorID][field] = tmpl
	}
	for key := range xd.Wardrobe {
		switch key {
		case wt.ProtagonistHairVar:
			// The protagonist is staged under the dialogue label too (AutoStage), so
			// stamp every actor id she can appear under.
			for _, id := range protoIDs {
				add(id, "hair", "{"+key+"}")
			}
		case wt.ProtagonistClothesVar:
			for _, id := range protoIDs {
				add(id, "outfit", "{"+key+"}")
			}
		default:
			story := strings.TrimPrefix(key, wt.VarPrefix)
			add(Slug(story), "outfit", "{"+key+"}")
		}
	}
	if len(stamp) == 0 {
		return
	}
	for i := range res.Scripts {
		stampScriptOps(&res.Scripts[i], stamp)
	}
}

// stampProtagonistMirror flips the bundle protagonist to face INTO the scene.
// AutoStage places her on the LEFT (stage.go) but her exported art faces left, so
// unflipped she looks off-screen; mirror=true faces her right. Scoped to the
// protagonist's actor ids only — NPCs stage right and keep their orientation. Only
// stamps where the author left `mirror` unset.
func stampProtagonistMirror(res *Result, xd XlsxData, tpl *Template) {
	if res == nil || len(res.Scripts) == 0 || !tpl.resolve().Staging.ProtagonistMirror {
		return
	}
	protoStory := ""
	if xd.Protagonist != nil {
		protoStory = xd.Protagonist.StoryName
	}
	stamp := map[string]map[string]string{}
	for _, id := range tpl.protagonistActorIDs(protoStory) { // StoryName id + staging label
		stamp[id] = map[string]string{"mirror": "true"}
	}
	if len(stamp) == 0 {
		return
	}
	for i := range res.Scripts {
		stampScriptOps(&res.Scripts[i], stamp)
	}
}

// stampScriptOps decodes a script, stamps the wardrobe axis templates onto matching
// `actor` ops (only where the field is unset), and re-marshals in place.
func stampScriptOps(sf *ScriptFile, stamp map[string]map[string]string) {
	ops, rewrap, ok := decodeScriptOps(sf.Data)
	if !ok {
		return
	}
	changed := false
	for _, op := range ops {
		if op == nil {
			continue
		}
		if name, _ := op["op"].(string); name != "actor" {
			continue
		}
		id, _ := op["id"].(string)
		fields := stamp[id]
		if fields == nil {
			continue
		}
		for f, tmpl := range fields {
			if _, exists := op[f]; !exists {
				op[f] = tmpl
				changed = true
			}
		}
	}
	if changed {
		if b, err := json.Marshal(rewrap(ops)); err == nil {
			sf.Data = b
		}
	}
}

// trimmedLocations returns a lookup keyed by both the raw and space-trimmed
// articy location name → tech name.
func trimmedLocations(in map[string]string) map[string]string {
	if len(in) == 0 {
		return nil
	}
	out := make(map[string]string, len(in)*2)
	for k, v := range in {
		out[k] = v
		out[strings.TrimSpace(k)] = v
	}
	return out
}

// rewriteScriptOps parses a compiled script's ops, rewrites background urls and
// inserts audio ops, then re-marshals it back into the same JSON shape it had.
func rewriteScriptOps(sf *ScriptFile, bgidx map[string]string, tpl *Template) {
	ops, rewrap, ok := decodeScriptOps(sf.Data)
	if !ok {
		return
	}
	ops = transformOps(ops, bgidx, tpl)
	if b, err := json.Marshal(rewrap(ops)); err == nil {
		sf.Data = b
	}
}

// transformOps applies the per-op background rewrite and audio insertion.
func transformOps(ops []map[string]any, bgidx map[string]string, tpl *Template) []map[string]any {
	out := make([]map[string]any, 0, len(ops))
	// The background showing right now, so a cutscene can hand the scene back
	// when it ends. Nil until the chapter sets its first background.
	var sceneBg map[string]any
	// Set by a "[timer]" line and consumed by the choice it introduces.
	armTimer := false
	for _, op := range ops {
		if op == nil {
			continue
		}
		name, _ := op["op"].(string)
		if name == "bg" {
			if id, ok := op["id"].(string); ok {
				// Fuzzy-match the scene id to a real HD фон file; only swap when one
				// is found (else keep the working articy background).
				if u := bgidx[normBg(id)]; u != "" {
					op["sprite_url"] = u
				}
			}
			if !isCutsceneBg(op) {
				sceneBg = op
			}
		}
		if name == "say" {
			if stripTimerTag(op) {
				armTimer = true
			}
		}
		if name == "choice" && armTimer {
			armChoiceTimer(op, tpl)
			armTimer = false
		}
		out = append(out, op)
		if name == "set" {
			if audio := audioOpForSet(op, tpl); audio != nil {
				out = append(out, audio)
			}
			if cut := cutsceneOpForSet(op, tpl, sceneBg); cut != nil {
				out = append(out, cut)
			}
			if cam := cameraOpForSet(op, tpl); cam != nil {
				out = append(out, cam)
			}
		}
	}
	return out
}

// timerTag matches the author's countdown direction anywhere in a line.
var timerTag = regexp.MustCompile(`(?i)\[\s*timer\s*\]\s*`)

// stripTimerTag removes a "[timer]" direction from a line and reports whether
// one was there. The tag is a note to the importer; leaving it in would show
// the player "[timer] Что делать?!".
func stripTimerTag(op map[string]any) bool {
	found := false
	for _, k := range []string{"text", "line"} {
		s, ok := op[k].(string)
		if !ok || !timerTag.MatchString(s) {
			continue
		}
		op[k] = strings.TrimSpace(timerTag.ReplaceAllString(s, ""))
		found = true
	}
	return found
}

// armChoiceTimer gives a choice its countdown and the branch an expired one
// takes. Without options (or with the timer already set by the author) it does
// nothing.
func armChoiceTimer(op map[string]any, tpl *Template) {
	if _, exists := op["timeout"]; exists {
		return
	}
	t := tpl.resolve().Timer
	if t.Seconds <= 0 {
		return
	}
	opts, _ := op["options"].([]any)
	if len(opts) == 0 {
		return
	}
	pick := opts[0]
	if strings.EqualFold(t.Branch, "last") {
		pick = opts[len(opts)-1]
	}
	target := ""
	switch o := pick.(type) {
	case map[string]any:
		target, _ = o["goto"].(string)
	case articy.Cmd:
		target, _ = o["goto"].(string)
	}
	if target == "" {
		return
	}
	op["timeout"] = t.Seconds
	op["timeout_goto"] = target
}

// cameraOpForSet turns a camera direction into a camera op: a focus phrase
// ("зум, слева, 70%") into a pan/zoom, a shake code into a shake. Returns nil
// for anything else, and for an empty direction (the author clearing it).
func cameraOpForSet(op map[string]any, tpl *Template) map[string]any {
	key, _ := op["key"].(string)
	if key == "" {
		return nil
	}
	c := tpl.resolve().Camera
	dur := c.Duration
	if dur <= 0 {
		dur = 0.6
	}
	val := strings.TrimSpace(fmt.Sprint(op["value"]))
	if val == "" || val == "<nil>" || val == "false" {
		return nil
	}
	switch key {
	case c.ShakeVar:
		// Two-digit code: the second digit is the strength the author asked for.
		amp := 0.02
		if n := lastDigit(val); n > 0 {
			amp = 0.01 * float64(n)
		}
		return map[string]any{"op": "camera", "action": "shake", "amplitude": amp, "duration": dur}
	case c.ZoomVar, c.FocusVar:
		factor, x, y, ok := parseFocusPhrase(val)
		if !ok {
			return nil
		}
		out := map[string]any{"op": "camera", "action": "zoom", "factor": factor, "duration": dur}
		if x >= 0 {
			out["x"], out["y"] = x, y
		}
		return out
	}
	return nil
}

// parseFocusPhrase reads the author's camera phrase. It names how close the
// camera gets ("волосы" tightest, "полностью" all the way out, a percentage) and
// optionally where it looks ("слева"/"справа"/"по центру"). Returns ok=false for
// a phrase that asks for nothing (e.g. "по умолчанию" — the default framing).
func parseFocusPhrase(s string) (factor, x, y float64, ok bool) {
	l := strings.ToLower(s)
	x, y = -1, -1
	switch {
	case strings.Contains(l, "по умолчанию"), strings.Contains(l, "сброс"):
		return 1, -1, -1, true
	case strings.Contains(l, "волос"):
		factor = 1.8
	case strings.Contains(l, "лиц"):
		factor = 1.6
	case strings.Contains(l, "поясн"), strings.Contains(l, "пояс"):
		factor = 1.3
	case strings.Contains(l, "полностью"), strings.Contains(l, "полн"):
		factor = 1
	}
	if m := percentRe.FindStringSubmatch(l); m != nil {
		if p, err := strconv.ParseFloat(m[1], 64); err == nil && p > 0 {
			// "70%" means the frame keeps 70% of the scene → zoom in by its inverse.
			factor = 100 / p
		}
	}
	if factor == 0 {
		return 0, -1, -1, false
	}
	switch {
	case strings.Contains(l, "слева"), strings.Contains(l, "лев"):
		x, y = 0.28, 0.5
	case strings.Contains(l, "справа"), strings.Contains(l, "прав"):
		x, y = 0.72, 0.5
	case strings.Contains(l, "центр"):
		x, y = 0.5, 0.5
	}
	return factor, x, y, true
}

var percentRe = regexp.MustCompile(`(\d{1,3})\s*%`)

// lastDigit returns the final digit of a code like "23", or 0 when there is none.
func lastDigit(s string) int {
	for i := len(s) - 1; i >= 0; i-- {
		if s[i] >= '0' && s[i] <= '9' {
			return int(s[i] - '0')
		}
	}
	return 0
}

// cutsceneOpForSet lowers an illustration trigger into a background swap: the
// truthy write shows the CG (which also unlocks it in the gallery), the falsy
// one restores the scene's own background. Returns nil when the set is not a
// cutscene trigger, or when a "hide" has no background to go back to.
func cutsceneOpForSet(op map[string]any, tpl *Template, sceneBg map[string]any) map[string]any {
	key, _ := op["key"].(string)
	if key == "" {
		return nil
	}
	for _, cue := range tpl.resolve().Cutscenes {
		if cue.VarPrefix == "" || !strings.HasPrefix(key, cue.VarPrefix) {
			continue
		}
		name := strings.Trim(key[len(cue.VarPrefix):], "_")
		if name == "" {
			continue
		}
		if truthyValue(op["value"]) {
			return map[string]any{"op": "bg", "sprite_url": cue.PathPrefix + name + cue.Ext,
				"cutscene": true}
		}
		// Ending the cutscene: hand the scene back its own background. Without a
		// known one there is nothing to restore — leave the CG up rather than
		// blanking the screen.
		if sceneBg == nil {
			return nil
		}
		restore := map[string]any{"op": "bg"}
		for _, k := range []string{"id", "sprite_url", "url"} {
			if v, ok := sceneBg[k]; ok {
				restore[k] = v
			}
		}
		if len(restore) == 1 {
			return nil
		}
		return restore
	}
	return nil
}

// isCutsceneBg reports whether a bg op is one this pass inserted, so a cutscene
// never becomes the background a later cutscene restores.
func isCutsceneBg(op map[string]any) bool {
	v, ok := op["cutscene"].(bool)
	return ok && v
}

// lookupLocation resolves an articy location name to its tech name, tolerating
// surrounding whitespace on the op's id.
func lookupLocation(loc map[string]string, id string) string {
	if loc == nil {
		return ""
	}
	if t, ok := loc[id]; ok {
		return t
	}
	if t, ok := loc[strings.TrimSpace(id)]; ok {
		return t
	}
	return ""
}

// audioOpForSet returns the audio op to insert after a truthy audio-cue set, or
// nil when the set isn't an audio cue (or its value is falsy). The template's Audio
// rules map a variable prefix (e.g. "Music.") to a channel + served-url layout.
func audioOpForSet(op map[string]any, tpl *Template) map[string]any {
	key, _ := op["key"].(string)
	if key == "" || !truthyValue(op["value"]) {
		return nil
	}
	for _, cue := range tpl.resolve().Audio {
		if cue.VarPrefix == "" || !strings.HasPrefix(key, cue.VarPrefix) {
			continue
		}
		name := key[len(cue.VarPrefix):]
		if name == "" {
			continue
		}
		return map[string]any{"op": "audio", "channel": cue.Channel, "action": "play",
			"url": cue.PathPrefix + name + cue.Ext}
	}
	return nil
}

// truthyValue reports whether a set value means "on": bool true, the strings
// "true"/"1", or the number 1.
func truthyValue(v any) bool {
	switch t := v.(type) {
	case bool:
		return t
	case string:
		switch strings.TrimSpace(t) {
		case "true", "1":
			return true
		}
	case float64:
		return t == 1
	case int:
		return t == 1
	case json.Number:
		return t.String() == "1"
	}
	return false
}

// regenerateLvnsSidecars re-decompiles every .lvn's FINAL script back into its
// .lvns sidecar, once all the rewrites above have run — see the call site's
// comment for why this matters (the sidecar otherwise drifts silently stale).
// A chapter with no matching sidecar, or a .lvn that fails to parse (should
// never happen — it's this same process's own JSON), is left untouched rather
// than aborting the whole import over a cosmetic source-view file.
func regenerateLvnsSidecars(res *Result) {
	if res == nil {
		return
	}
	// regen decompiles one final .lvn into fresh .lvns bytes AND verifies the
	// result recompiles into the same story (VerifyLvnsRoundTrip) — a sidecar
	// that would corrupt the .lvn on the panel's next "Save to app" must be
	// loud in the import report, never a silent landmine.
	regen := func(rel string, lvnData []byte) ([]byte, bool) {
		var doc articy.Doc
		if err := json.Unmarshal(lvnData, &doc); err != nil {
			return nil, false
		}
		out := ToLvns(&doc)
		for _, w := range VerifyLvnsRoundTrip(doc.Script, out) {
			res.Warnings = append(res.Warnings, rel+": "+w)
		}
		return out, true
	}

	lvnsIndex := map[string]int{}
	for i, sf := range res.Scripts {
		if strings.HasSuffix(sf.Rel, ".lvns") {
			lvnsIndex[strings.TrimSuffix(sf.Rel, ".lvns")] = i
		}
	}
	for i := range res.Scripts {
		if !strings.HasSuffix(res.Scripts[i].Rel, ".lvn") {
			continue
		}
		j, ok := lvnsIndex[strings.TrimSuffix(res.Scripts[i].Rel, ".lvn")]
		if !ok {
			continue
		}
		if out, ok := regen(res.Scripts[i].Rel, res.Scripts[i].Data); ok {
			res.Scripts[j].Data = out
		}
	}
	// Single-chapter form: Run() puts the script in ScriptRel/Lvn and the
	// sidecar in LvnsRel/Lvns — res.Scripts stays empty, and skipping this
	// branch left the ORIGINAL stale-sidecar bug alive for every one-chapter
	// project (and for multi-chapter projects that fell back to one).
	if res.LvnsRel != "" && len(res.Lvn) > 0 {
		if out, ok := regen(res.ScriptRel, res.Lvn); ok {
			res.Lvns = out
		}
	}
}

// decodeScriptOps parses a compiled script's Data into a flat op slice and
// returns a rewrap that reproduces the ORIGINAL top-level shape. Two shapes are
// supported: a bare op array (`[ {op..}, ... ]`) and the .lvn document object
// (`{ "scene":.., "script":[..] }`). Returns ok=false for empty/unparseable data.
func decodeScriptOps(data []byte) (ops []map[string]any, rewrap func([]map[string]any) any, ok bool) {
	trimmed := bytes.TrimSpace(data)
	if len(trimmed) == 0 {
		return nil, nil, false
	}
	switch trimmed[0] {
	case '[':
		if err := json.Unmarshal(trimmed, &ops); err != nil {
			return nil, nil, false
		}
		return ops, func(o []map[string]any) any { return o }, true
	case '{':
		var obj map[string]any
		if err := json.Unmarshal(trimmed, &obj); err != nil {
			return nil, nil, false
		}
		raw, _ := obj["script"].([]any)
		ops = make([]map[string]any, 0, len(raw))
		for _, r := range raw {
			if m, ok := r.(map[string]any); ok {
				ops = append(ops, m)
			}
		}
		return ops, func(o []map[string]any) any {
			arr := make([]any, len(o))
			for i, m := range o {
				arr[i] = m
			}
			obj["script"] = arr
			return obj
		}, true
	}
	return nil, nil, false
}
