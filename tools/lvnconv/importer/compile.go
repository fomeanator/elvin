package importer

// compile.go re-exports the .lvns compiler for callers OUTSIDE the lvnconv
// module tree.
//
// The compiler lives in internal/lvns, which Go's internal rule limits to
// packages rooted at tools/lvnconv/. The server is not one of them, and it
// needs exactly this: an author (or an AI writing on the author's behalf) sends
// .lvns source over HTTP, and the server must turn it into the .lvn the runtime
// plays. Without this door the only way to publish is a local Go toolchain,
// which is precisely the barrier the agent bundle exists to remove.
//
// This is a re-export, not a second implementation: the same Convert every
// other path uses, so there is nothing here that can drift.

import (
	"encoding/json"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// CompileLvns compiles .lvns source into the indented .lvn JSON container —
// byte-for-byte what `lvnconv convert` writes, so a script published over HTTP
// and one compiled on a laptop are the same file.
func CompileLvns(src string) ([]byte, error) {
	doc, err := lvns.Convert(src)
	if err != nil {
		return nil, err
	}
	out, err := json.MarshalIndent(doc, "", "  ")
	if err != nil {
		return nil, err
	}
	return append(out, '\n'), nil
}
