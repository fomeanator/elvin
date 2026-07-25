package importer

import "strings"

// applyVarAliases rewrites variable-name prefixes through the template's
// VarAliases map, on the fields where variable names live: set/inc `key`,
// and the expression strings of set/if/choice-options. Never touches spoken
// text. A prefix ending in "." can't accidentally hit its own longer form
// ("Relationship." never matches inside "Relationships." — the next rune
// there is 's', not the dot the prefix requires).
func applyVarAliases(ops []map[string]any, tpl *Template) {
	aliases := tpl.resolve().VarAliases
	if len(aliases) == 0 {
		return
	}
	fix := func(s string) string {
		for from, to := range aliases {
			s = strings.ReplaceAll(s, from, to)
		}
		return s
	}
	for _, op := range ops {
		if op == nil {
			continue
		}
		switch op["op"] {
		case "set", "inc":
			if k, _ := op["key"].(string); k != "" {
				op["key"] = fix(k)
			}
			if e, _ := op["expr"].(string); e != "" {
				op["expr"] = fix(e)
			}
		case "if":
			if e, _ := op["expr"].(string); e != "" {
				op["expr"] = fix(e)
			}
		case "choice":
			for _, o := range asAnyList(op["options"]) {
				if m, ok := toMap(o); ok {
					if e, _ := m["expr"].(string); e != "" {
						m["expr"] = fix(e)
					}
				}
			}
		}
	}
}

// applySpeakerAliasesToCast resolves every declared Template.SpeakerAliases
// entry against the cast map: an alias whose canonical target HAS art gets
// the same sprite stem, so AutoStage stages her under EITHER label with the
// real art (a roster nickname like "ГГ" for a script label like "Главный
// герой"). Never overwrites an alias that already has its OWN cast entry —
// an author-declared alias only fills a gap, it doesn't clobber real data.
func applySpeakerAliasesToCast(cast map[string]string, tpl *Template) {
	tpl = tpl.resolve()
	for alias, canon := range tpl.SpeakerAliases {
		alias = strings.TrimSpace(alias)
		canon = strings.TrimSpace(canon)
		if alias == "" || canon == "" || alias == canon {
			continue
		}
		if _, exists := cast[alias]; exists {
			continue
		}
		if spr, ok := cast[canon]; ok {
			cast[alias] = spr
		}
	}
}
