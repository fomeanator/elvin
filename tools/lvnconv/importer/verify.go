package importer

// verify.go — the round-trip safety net for generated .lvns sidecars.
//
// Every .lvns this package emits is meant to recompile (via the SAME
// lvns.Convert the Studio panel's WASM uses on "Save to app") into the same
// story the .lvn carries. Historically that property was assumed, not
// checked — and every drift class found in the 2026-07-25 audit (swallowed
// multi-line «…», dropped choice bodies/attrs, unknown host ops, directive-
// word keys) shipped silently because nothing compared the two artifacts at
// import time. VerifyLvnsRoundTrip is that comparison: cheap, structural,
// and LOUD — callers put its findings into Result.Warnings so an import
// whose editable source would corrupt the compiled story on next save says
// so in the report instead of in production.

import (
	"fmt"
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// VerifyLvnsRoundTrip recompiles a generated .lvns and structurally compares
// the op stream against the script it was decompiled from. Returns nil when
// the sidecar is faithful; otherwise a list of human-readable findings.
//
// The comparison is deliberately count-based per op type, EXCLUDING
// label/goto/if: the decompiler's documented control-flow lowering (pruned
// unreferenced labels, fall-through gotos, synthetic if-else labels) changes
// those counts without changing behaviour, and byte-diffing them would flag
// every legitimate file. What must never drift is the story itself: says,
// actors, audio, choices (and their options' priced/gated fields), sets.
func VerifyLvnsRoundTrip(script []articy.Cmd, lvnsData []byte) []string {
	rec, err := lvns.Convert(string(lvnsData))
	if err != nil {
		return []string{fmt.Sprintf("lvns sidecar does not recompile: %v", err)}
	}

	var out []string
	origCounts := opCounts(cmdsAsOps(script))
	recCounts := opCounts(docOps(rec))
	keys := map[string]bool{}
	for k := range origCounts {
		keys[k] = true
	}
	for k := range recCounts {
		keys[k] = true
	}
	var drift []string
	for k := range keys {
		if k == "label" || k == "goto" || k == "if" {
			continue // legitimate control-flow lowering
		}
		if origCounts[k] != recCounts[k] {
			drift = append(drift, fmt.Sprintf("%s %d→%d", k, origCounts[k], recCounts[k]))
		}
	}
	sort.Strings(drift)
	if len(drift) > 0 {
		out = append(out, "lvns sidecar drift (ops lost/gained on recompile): "+strings.Join(drift, ", "))
	}

	// The fields money and progression ride on, counted across all options.
	for _, probe := range []struct{ field, label string }{
		{"wallet_cost", "priced (wallet_cost) choice options"},
		{"body", "choice option bodies"},
	} {
		o := optionFieldCount(cmdsAsOps(script), probe.field)
		r := optionFieldCount(docOps(rec), probe.field)
		if o != r {
			out = append(out, fmt.Sprintf("lvns sidecar drift: %s %d→%d", probe.label, o, r))
		}
	}
	// Counting wallet_cost's PRESENCE is not enough: a price that survives as
	// a bare string ("5 soft coins" — the parser's amount+currency match needs
	// a single-word currency) still counts, and LvnPlayer.Choose spends only a
	// {currency, amount} OBJECT, so the option is silently FREE. Check the
	// shape, which is the part money actually depends on.
	if o, r := pricedOptionCount(cmdsAsOps(script)), pricedOptionCount(docOps(rec)); o != r {
		out = append(out, fmt.Sprintf(
			"lvns sidecar drift: choice options that still CHARGE %d→%d (a wallet_cost that recompiles to a string is not spent — the pick becomes free)", o, r))
	}
	return out
}

// pricedOptionCount counts options carrying a wallet_cost the runtime can
// actually charge: an object with a currency and an amount.
func pricedOptionCount(ops []map[string]any) int {
	n := 0
	for _, c := range ops {
		if c["op"] != "choice" {
			continue
		}
		for _, o := range asAnyList(c["options"]) {
			m, ok := toMap(o)
			if !ok {
				continue
			}
			wc, ok := toMap(m["wallet_cost"])
			if !ok {
				continue
			}
			cur, _ := wc["currency"].(string)
			if cur == "" {
				cur, _ = wc["var"].(string)
			}
			if cur != "" && wc["amount"] != nil {
				n++
			}
		}
	}
	return n
}

func docOps(d *lvns.Doc) []map[string]any {
	out := make([]map[string]any, len(d.Script))
	for i, c := range d.Script {
		out[i] = map[string]any(c)
	}
	return out
}

func opCounts(ops []map[string]any) map[string]int {
	m := map[string]int{}
	for _, c := range ops {
		if op, _ := c["op"].(string); op != "" {
			m[op]++
		}
	}
	return m
}

func optionFieldCount(ops []map[string]any, field string) int {
	n := 0
	for _, c := range ops {
		if c["op"] != "choice" {
			continue
		}
		for _, o := range asAnyList(c["options"]) {
			if m, ok := toMap(o); ok {
				if _, has := m[field]; has {
					n++
				}
			}
		}
	}
	return n
}
