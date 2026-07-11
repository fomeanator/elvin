package optimize

import (
	"os"
	"path/filepath"
	"strings"
)

// URLFor converts a filesystem path under contentRoot into the "/content/..."
// URL form manifest.json and .lvns scripts reference (e.g.
// ".../server/content/art/Foo.png" → "/content/art/Foo.png").
func URLFor(contentRoot, path string) (string, bool) {
	rel, err := filepath.Rel(contentRoot, path)
	if err != nil || strings.HasPrefix(rel, "..") {
		return "", false
	}
	return "/content/" + filepath.ToSlash(rel), true
}

// RewriteRefs replaces every old→new URL substring (as produced by URLFor)
// across manifest.json and every .lvns script under contentRoot/scripts, and
// returns the files it actually changed. .lvns are the SOURCE of truth for
// scripts — the compiled .lvn is regenerated from them (see cmd/optimize.go),
// never patched directly, or the next edit-and-recompile would silently
// revert the rename.
func RewriteRefs(contentRoot string, renames map[string]string) ([]string, error) {
	if len(renames) == 0 {
		return nil, nil
	}
	var candidates []string
	if manifest := filepath.Join(contentRoot, "manifest.json"); fileExists(manifest) {
		candidates = append(candidates, manifest)
	}
	scripts, err := filepath.Glob(filepath.Join(contentRoot, "scripts", "*.lvns"))
	if err != nil {
		return nil, err
	}
	candidates = append(candidates, scripts...)

	var touched []string
	for _, path := range candidates {
		data, err := os.ReadFile(path)
		if err != nil {
			return touched, err
		}
		text := string(data)
		changed := false
		for oldURL, newURL := range renames {
			if strings.Contains(text, oldURL) {
				text = strings.ReplaceAll(text, oldURL, newURL)
				changed = true
			}
		}
		if !changed {
			continue
		}
		if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
			return touched, err
		}
		touched = append(touched, path)
	}
	return touched, nil
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
