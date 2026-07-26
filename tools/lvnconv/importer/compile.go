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
//
// No file context, so `include` is refused with a clear error. Use
// CompileLvnsFile for anything that splits a game across files.
func CompileLvns(src string) ([]byte, error) {
	doc, err := lvns.Convert(src)
	if err != nil {
		return nil, err
	}
	return marshalDoc(doc)
}

// CompileLvnsFile compiles .lvns FROM DISK, which is what makes `include` work:
// a path is resolved against the including file's directory, and text alone has
// no directory. A game of more than one chapter always ends up here — the shared
// mechanics file is the whole reason include exists — so an HTTP publish that
// only had CompileLvns hit a wall on the author's second chapter.
func CompileLvnsFile(path string) ([]byte, error) {
	doc, err := lvns.ConvertFile(path)
	if err != nil {
		return nil, err
	}
	return marshalDoc(doc)
}

func marshalDoc(doc any) ([]byte, error) {
	out, err := json.MarshalIndent(doc, "", "  ")
	if err != nil {
		return nil, err
	}
	return append(out, '\n'), nil
}
