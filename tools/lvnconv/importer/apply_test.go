package importer

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// A crafted rel path (e.g. an id of "../../evil") must never let WriteToContentDir
// write outside the content root — the last line of defence behind id validation.
func TestWriteToContentDirRejectsEscape(t *testing.T) {
	root := t.TempDir()
	outside := filepath.Join(root, "outside-canary")

	res := &Result{
		Scripts: []ScriptFile{
			{Rel: "../outside-canary/pwned.lvn", Data: []byte("x")},
		},
	}
	err := WriteToContentDir(root, res)
	if err == nil {
		t.Fatal("expected an error writing outside the content root")
	}
	if !strings.Contains(err.Error(), "outside content root") {
		t.Fatalf("error = %v, want an 'outside content root' rejection", err)
	}
	if _, statErr := os.Stat(outside); !os.IsNotExist(statErr) {
		t.Fatal("a file escaped the content root")
	}
}

// A normal relative path still writes fine under the root.
func TestWriteToContentDirWritesUnderRoot(t *testing.T) {
	root := t.TempDir()
	res := &Result{
		Scripts: []ScriptFile{{Rel: "scripts/ok-ch01.lvn", Data: []byte("{}")}},
	}
	if err := WriteToContentDir(root, res); err != nil {
		t.Fatalf("write under root failed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "scripts", "ok-ch01.lvn")); err != nil {
		t.Fatalf("expected file written under root: %v", err)
	}
}
