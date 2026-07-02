package main

import (
	"regexp"
	"strings"
	"testing"
)

const sampleManifest = `{
  "dependencies": {
    "com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main",
    "com.lvn.engine": "file:../../unity/Packages/com.lvn.engine",
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}`

// An exported manifest pins the engine to the release tag: updates are the
// project owner's explicit choice, not a side effect of our next push.
func TestPatchManifestPinsTheReleaseTag(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), "v0.5.0"))
	want := `"com.lvn.engine": "` + engineGitURL + `#v0.5.0"`
	if !strings.Contains(out, want) {
		t.Fatalf("engine not pinned:\n%s", out)
	}
	if strings.Contains(out, "unity-mcp") {
		t.Fatal("dev-only unity-mcp package must be stripped from exports")
	}
}

// Without a resolvable tag the URL stays unpinned (dev fallback) — but still
// valid JSON with the dev package stripped.
func TestPatchManifestUnpinnedFallback(t *testing.T) {
	out := string(patchManifest([]byte(sampleManifest), ""))
	if !strings.Contains(out, `"com.lvn.engine": "`+engineGitURL+`"`) {
		t.Fatalf("unpinned URL malformed:\n%s", out)
	}
	if strings.Contains(out, "#") {
		t.Fatalf("no tag requested, but got a pin:\n%s", out)
	}
}

// The tag comes from the version of the engine package the REAL template
// points at — the release process tags every published version as vX.Y.Z.
func TestEngineReleaseTagFromSandboxTemplate(t *testing.T) {
	tag := engineReleaseTag("../sandbox")
	if !regexp.MustCompile(`^v\d+\.\d+\.\d+`).MatchString(tag) {
		t.Fatalf("engineReleaseTag(../sandbox) = %q, want vX.Y.Z", tag)
	}
}
