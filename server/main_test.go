package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestVersionHashChangesWithContent(t *testing.T) {
	dir := t.TempDir()
	write := func(rel, body string) {
		p := filepath.Join(dir, rel)
		_ = os.MkdirAll(filepath.Dir(p), 0o755)
		if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	write("manifest.json", `{"titles":[]}`)
	write("scripts/ch1.lvn", `{"script":[]}`)

	s := &server{content: dir}

	v1 := versionHash(s.computeVersions(true))
	if v1 == "" {
		t.Fatal("empty version hash")
	}
	// stable: same content → same hash
	if v2 := versionHash(s.computeVersions(true)); v1 != v2 {
		t.Fatalf("version not stable: %s vs %s", v1, v2)
	}
	// changed: editing a script must change the hash
	write("scripts/ch1.lvn", `{"script":[{"op":"say","text":"hi"}]}`)
	if v3 := versionHash(s.computeVersions(true)); v1 == v3 {
		t.Fatal("version did not change after editing a script")
	}
	// editing the manifest must also change it
	prev := versionHash(s.computeVersions(true))
	write("manifest.json", `{"titles":[{"id":"t1"}]}`)
	if cur := versionHash(s.computeVersions(true)); prev == cur {
		t.Fatal("version did not change after editing the manifest")
	}
}

func TestAssetVersionsExcludesManifest(t *testing.T) {
	dir := t.TempDir()
	_ = os.WriteFile(filepath.Join(dir, "manifest.json"), []byte(`{}`), 0o644)
	_ = os.WriteFile(filepath.Join(dir, "a.png"), []byte("img"), 0o644)
	s := &server{content: dir}
	v := s.computeVersions(false)
	if _, ok := v["manifest.json"]; ok {
		t.Fatal("asset-versions must not include manifest.json")
	}
	if _, ok := v["a.png"]; !ok {
		t.Fatal("asset-versions must include assets")
	}
}
