package lvn

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Run the real mutator on a disposable repository, with a fake test process
// making an independent edit while the mutation is in flight. Restoring an
// old backup over that edit reproduces the shared-worktree data loss.
func TestMutationRunnerDoesNotOverwriteConcurrentEdits(t *testing.T) {
	for _, tc := range []struct{ mode, failure string }{
		{"", "test"}, {"-bite", "test"}, {"", "build"}, {"-bite", "build"},
		{"", "empty"}, {"-bite", "empty"},
	} {
		t.Run("mode="+tc.mode+"/failure="+tc.failure, func(t *testing.T) {
			for _, name := range []string{"bash", "git", "rsync", "python3"} {
				if _, err := exec.LookPath(name); err != nil {
					t.Skipf("%s unavailable: %v", name, err)
				}
			}
			root := t.TempDir()
			write := func(path, body string, mode os.FileMode) {
				t.Helper()
				if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(path, []byte(body), mode); err != nil {
					t.Fatal(err)
				}
			}
			script, err := os.ReadFile(filepath.Join(repoRoot(t), "qa", "mutation-check.sh"))
			if err != nil {
				t.Fatal(err)
			}
			write(filepath.Join(root, "qa", "mutation-check.sh"), string(script), 0755)
			write(filepath.Join(root, "server", "wallet.go"), "\t\tif req.OpID != \"\" {\n", 0644)
			write(filepath.Join(root, "server", "content_delta.go"), "\t\tout.Changed, out.Removed = diffVersions(prev, cur)\n", 0644)
			if out, err := exec.Command("git", "-C", root, "init", "-q").CombinedOutput(); err != nil {
				t.Fatalf("git init: %v\n%s", err, out)
			}
			bin := t.TempDir()
			write(filepath.Join(bin, "go"), `#!/bin/bash
if [ "$MUTATION_FAILURE_MODE" = empty ]; then
  printf '%s\n' '{"Action":"pass","Package":"server"}'
  exit 0
fi
if ! grep -q 'мутация' wallet.go content_delta.go; then
  printf '%s\n' '{"Action":"pass","Test":"Invariant"}'
  exit 0
fi
printf '%s\n' "$PWD" >> "$MUTATION_VISITS"
printf '%s\n' 'independent author edit' > "$MUTATION_ORIGINAL_ROOT/server/wallet.go"
if [ "$MUTATION_FAILURE_MODE" = build ]; then
  printf '%s\n' '{"Action":"fail","Package":"server"}'
else
  printf '%s\n' '{"Action":"fail","Package":"server","Test":"Invariant"}'
fi
exit 1
`, 0755)
			visits := filepath.Join(t.TempDir(), "visits")
			ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
			defer cancel()
			args := []string{filepath.Join(root, "qa", "mutation-check.sh")}
			if tc.mode != "" {
				args = append(args, tc.mode)
			}
			cmd := exec.CommandContext(ctx, "bash", args...)
			cmd.Env = append(os.Environ(), "PATH="+bin+string(os.PathListSeparator)+os.Getenv("PATH"),
				"MUTATION_ORIGINAL_ROOT="+root, "MUTATION_VISITS="+visits, "MUTATION_FAILURE_MODE="+tc.failure)
			out, runErr := cmd.CombinedOutput()
			if tc.failure == "test" && runErr != nil {
				t.Fatalf("mutation runner: %v\n%s", runErr, out)
			}
			if tc.failure != "test" && runErr == nil {
				t.Errorf("%s was incorrectly accepted as a caught mutation:\n%s", tc.failure, out)
			}
			got, err := os.ReadFile(filepath.Join(root, "server", "wallet.go"))
			if tc.failure == "empty" {
				if err != nil || string(got) != "\t\tif req.OpID != \"\" {\n" {
					t.Errorf("an empty baseline must not change source files: %q (%v)", got, err)
				}
				if !strings.Contains(string(out), "ни одного теста") {
					t.Errorf("empty test selection was not diagnosed: %s", out)
				}
				return
			}
			if err != nil || string(got) != "independent author edit\n" {
				t.Errorf("mutator overwrote the author's edit: %q (%v)", got, err)
			}
			paths, err := os.ReadFile(visits)
			if err != nil {
				t.Fatal(err)
			}
			lines := strings.Split(strings.TrimSpace(string(paths)), "\n")
			if len(lines) != 2 {
				t.Fatalf("expected both mutation test runs, got %q", paths)
			}
			for _, path := range lines {
				if path == filepath.Join(root, "server") {
					t.Error("tests observed a mutation in the live repository")
				}
			}
		})
	}
}
