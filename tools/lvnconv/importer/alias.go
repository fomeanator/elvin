package importer

import "strings"

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
