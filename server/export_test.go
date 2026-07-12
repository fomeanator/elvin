package main

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

const sampleManifest = `{
  "dependencies": {
    "com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main",
    "com.lvn.engine": "file:../../unity/Packages/com.lvn.engine",
    "com.lvn.engine.shell": "file:../../unity/Packages/com.lvn.engine.shell",
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}`

func enginePkgURL(name string) string {
	return engineRepoURL + "?path=/unity/Packages/" + name
}

// An exported manifest pins the engine to the release tag: updates are the
// project owner's explicit choice, not a side effect of our next push.
func TestPatchManifestPinsTheReleaseTag(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), "v0.5.0"))
	want := `"com.lvn.engine": "` + enginePkgURL("com.lvn.engine") + `#v0.5.0"`
	if !strings.Contains(out, want) {
		t.Fatalf("engine not pinned:\n%s", out)
	}
	// every engine-family package rides the same repo + tag
	wantShell := `"com.lvn.engine.shell": "` + enginePkgURL("com.lvn.engine.shell") + `#v0.5.0"`
	if !strings.Contains(out, wantShell) {
		t.Fatalf("shell package not pinned:\n%s", out)
	}
	if strings.Contains(out, "unity-mcp") {
		t.Fatal("dev-only unity-mcp package must be stripped from exports")
	}
}

// Without a resolvable tag the URL stays unpinned (dev fallback) — but still
// valid JSON with the dev package stripped.
func TestPatchManifestUnpinnedFallback(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), ""))
	if !strings.Contains(out, `"com.lvn.engine": "`+enginePkgURL("com.lvn.engine")+`"`) {
		t.Fatalf("unpinned URL malformed:\n%s", out)
	}
	if strings.Contains(out, "#") {
		t.Fatalf("no tag requested, but got a pin:\n%s", out)
	}
}

// The tag is the version of the engine package the template's file: entry
// points at — hermetic fixture, mirrors the sandbox layout.
func TestEngineReleaseTagDerivation(t *testing.T) {
	tmpl := t.TempDir()
	if err := os.MkdirAll(filepath.Join(tmpl, "Packages"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(tmpl, "engine"), 0o755); err != nil {
		t.Fatal(err)
	}
	must := func(p, s string) {
		if err := os.WriteFile(p, []byte(s), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	must(filepath.Join(tmpl, "Packages", "manifest.json"),
		`{"dependencies":{"com.lvn.engine":"file:../engine"}}`)
	must(filepath.Join(tmpl, "engine", "package.json"), `{"version":"1.2.3"}`)

	if got := engineReleaseTag(tmpl); got != "v1.2.3" {
		t.Fatalf("engineReleaseTag = %q, want v1.2.3", got)
	}
	if got := engineReleaseTag(t.TempDir()); got != "" {
		t.Fatalf("no manifest must mean no pin, got %q", got)
	}
}

// Against the REAL local template (gitignored — skipped in CI): the release
// process tags every published version as vX.Y.Z.
func TestEngineReleaseTagFromSandboxTemplate(t *testing.T) {
	if _, err := os.Stat(filepath.Join("..", "sandbox", "Packages", "manifest.json")); err != nil {
		t.Skip("local sandbox template not present")
	}
	tag := engineReleaseTag("../sandbox")
	if !regexp.MustCompile(`^v\d+\.\d+\.\d+`).MatchString(tag) {
		t.Fatalf("engineReleaseTag(../sandbox) = %q, want vX.Y.Z", tag)
	}
}
