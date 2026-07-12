package lvn

// Localization catalog extraction. The runtime resolves reader-facing strings
// through a per-language catalog (LvnPlayer.Strings): a say/option that carries
// a stable "text_id" (articy imports) is keyed by that id; everything else —
// the normal .lvns-authored path — is keyed by the source string itself,
// gettext-style. ExtractStrings walks a compiled document and returns those
// keys, so `lvnconv locale` can build/refresh the `<script>.<lang>.json`
// sidecars the runtime loads beside the script.

// ExtractStrings returns the localizable catalog keys of a document in first-
// appearance order, deduplicated: say text and speaker names, choice option
// text, input prompts and floating `text` labels. When a command carries a
// stable "text_id", that id is the key (its inline text, if any, is the source
// rendering); otherwise the source string is.
func ExtractStrings(d *Doc) []string {
	var keys []string
	seen := map[string]bool{}
	add := func(s string) {
		if s == "" || seen[s] {
			return
		}
		seen[s] = true
		keys = append(keys, s)
	}
	textKey := func(c Cmd) {
		if id, _ := c["text_id"].(string); id != "" {
			add(id)
			return
		}
		if t, _ := c["text"].(string); t != "" {
			add(t)
		}
	}

	var walk func(c Cmd)
	walk = func(c Cmd) {
		op, _ := c["op"].(string)
		switch op {
		case "say":
			if who, _ := c["who"].(string); who != "" {
				add(who)
			}
			textKey(c)
		case "text":
			textKey(c)
		case "input":
			if p, _ := c["prompt"].(string); p != "" {
				add(p)
			}
		case "choice":
			opts, _ := c["options"].([]any)
			for _, o := range opts {
				oc, ok := o.(map[string]any)
				if !ok {
					continue
				}
				textKey(Cmd(oc))
				if body, ok := oc["body"].([]any); ok {
					for _, b := range body {
						if bm, ok := b.(map[string]any); ok {
							walk(Cmd(bm))
						}
					}
				}
			}
		}
	}
	for _, c := range d.Script {
		walk(c)
	}
	return keys
}

// CatalogStats describes a catalog against the strings a script needs.
type CatalogStats struct {
	Total        int      // keys the script needs
	Missing      []string // needed but absent from the catalog
	Stale        []string // in the catalog but no longer in the script
	Untranslated int      // present with value == key (extract's prefill)
}

// MergeCatalog folds the needed keys into an existing catalog: present
// translations are kept verbatim, new keys are prefilled with themselves (the
// source text — a loaded catalog must never render an empty line), and stale
// keys are dropped only when prune is set. Returns the merged catalog and the
// stats of the existing one.
func MergeCatalog(existing map[string]string, keys []string, prune bool) (map[string]string, CatalogStats) {
	st := CatalogStats{Total: len(keys)}
	need := map[string]bool{}
	merged := map[string]string{}
	for _, k := range keys {
		need[k] = true
		if v, ok := existing[k]; ok {
			merged[k] = v
			if v == k {
				st.Untranslated++
			}
		} else {
			merged[k] = k
			st.Missing = append(st.Missing, k)
		}
	}
	for k, v := range existing {
		if !need[k] {
			st.Stale = append(st.Stale, k)
			if !prune {
				merged[k] = v
			}
		}
	}
	return merged, st
}
