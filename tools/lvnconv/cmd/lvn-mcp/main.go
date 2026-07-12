// lvn-mcp — an MCP (Model Context Protocol) server over stdio that gives any
// AI agent the LVN toolchain: compile .lvns, validate the result, and read
// the engine's authoring docs. This is the distribution seam of the "AI
// writes — you read, edit and own" strategy: connect the server to an agent
// and it can build a narrative game without cloning knowledge of the repo.
//
//	claude mcp add lvn -- lvn-mcp            # or: go run ./cmd/lvn-mcp
//
// Transport: newline-delimited JSON-RPC 2.0 on stdin/stdout (the MCP stdio
// framing). No network, no state — every call is pure.
package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

const protocolVersion = "2024-11-05"

type request struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method"`
	Params  json.RawMessage `json:"params,omitempty"`
}

type response struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Result  any             `json:"result,omitempty"`
	Error   *rpcError       `json:"error,omitempty"`
}

type rpcError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

func main() {
	sc := bufio.NewScanner(os.Stdin)
	sc.Buffer(make([]byte, 1<<20), 16<<20) // whole chapters arrive as one line
	out := bufio.NewWriter(os.Stdout)
	for sc.Scan() {
		line := sc.Bytes()
		if len(line) == 0 {
			continue
		}
		if resp := handleMessage(line); resp != nil {
			out.Write(resp)
			out.WriteByte('\n')
			out.Flush()
		}
	}
}

// handleMessage processes one JSON-RPC message and returns the serialized
// response, or nil for notifications (which never get one).
func handleMessage(raw []byte) []byte {
	var req request
	if err := json.Unmarshal(raw, &req); err != nil {
		return marshal(response{JSONRPC: "2.0", Error: &rpcError{Code: -32700, Message: "parse error: " + err.Error()}})
	}
	if strings.HasPrefix(req.Method, "notifications/") {
		return nil
	}

	var result any
	var rerr *rpcError
	switch req.Method {
	case "initialize":
		result = map[string]any{
			"protocolVersion": protocolVersion,
			"capabilities":    map[string]any{"tools": map[string]any{}},
			"serverInfo":      map[string]any{"name": "lvn-mcp", "version": "0.1.0"},
		}
	case "ping":
		result = map[string]any{}
	case "tools/list":
		result = map[string]any{"tools": toolList()}
	case "tools/call":
		result, rerr = callTool(req.Params)
	default:
		rerr = &rpcError{Code: -32601, Message: "method not found: " + req.Method}
	}

	return marshal(response{JSONRPC: "2.0", ID: req.ID, Result: result, Error: rerr})
}

func marshal(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}

// ── tools ────────────────────────────────────────────────────────────────

func toolList() []map[string]any {
	src := map[string]any{
		"type":     "object",
		"required": []string{"source"},
		"properties": map[string]any{
			"source": map[string]any{"type": "string", "description": "The .lvns script text"},
		},
	}
	check := map[string]any{
		"type":     "object",
		"required": []string{"source"},
		"properties": map[string]any{
			"source": map[string]any{"type": "string", "description": "The .lvns script text"},
			"ext_grammar": map[string]any{"type": "string", "description": "Optional ext-grammar.json content declaring the project's host ops (`ext …`) " +
				"so they validate like built-ins instead of warning as unknown"},
		},
	}
	return []map[string]any{
		{
			"name": "lvns_check",
			"description": "Compile a .lvns script and run the structural validator. " +
				"Returns ok/errors/warnings — the same quality gate every shipped example passes " +
				"(the bar is ok=true with zero warnings).",
			"inputSchema": check,
		},
		{
			"name": "lvns_convert",
			"description": "Compile a .lvns script to its runnable .lvn JSON container. " +
				"Fails with the compile error when the script doesn't parse.",
			"inputSchema": src,
		},
		{
			"name": "lvn_doc",
			"description": "Read an LVN authoring doc. Names: tutorial, cheatsheet, capabilities, " +
				"language, recipes, agents, format, embedding.",
			"inputSchema": map[string]any{
				"type":     "object",
				"required": []string{"name"},
				"properties": map[string]any{
					"name": map[string]any{"type": "string", "description": "Doc name (see the list above)"},
				},
			},
		},
	}
}

func callTool(params json.RawMessage) (any, *rpcError) {
	var p struct {
		Name      string `json:"name"`
		Arguments struct {
			Source     string `json:"source"`
			Name       string `json:"name"`
			ExtGrammar string `json:"ext_grammar"`
		} `json:"arguments"`
	}
	if err := json.Unmarshal(params, &p); err != nil {
		return nil, &rpcError{Code: -32602, Message: "bad params: " + err.Error()}
	}

	text, isErr := runTool(p.Name, p.Arguments.Source, p.Arguments.Name, p.Arguments.ExtGrammar)
	return map[string]any{
		"content": []map[string]any{{"type": "text", "text": text}},
		"isError": isErr,
	}, nil
}

// runTool executes a tool and returns its text payload; isErr marks tool-level
// failures (the MCP way — protocol errors are reserved for malformed calls).
func runTool(tool, source, docName, extGrammar string) (text string, isErr bool) {
	switch tool {
	case "lvns_check":
		return checkSource(source, extGrammar)
	case "lvns_convert":
		doc, err := lvns.Convert(source)
		if err != nil {
			return "compile error: " + err.Error(), true
		}
		b, _ := json.MarshalIndent(doc, "", "  ")
		return string(b), false
	case "lvn_doc":
		return readDoc(docName)
	default:
		return "unknown tool: " + tool, true
	}
}

func checkSource(source, extGrammar string) (string, bool) {
	type report struct {
		OK       bool     `json:"ok"`
		Commands int      `json:"commands"`
		Errors   []string `json:"errors"`
		Warnings []string `json:"warnings"`
	}
	rep := report{Errors: []string{}, Warnings: []string{}}

	var ext *lvn.ExtGrammar
	if extGrammar != "" {
		g, gerr := lvn.ParseExtGrammar([]byte(extGrammar))
		if gerr != nil {
			rep.Errors = append(rep.Errors, "ext-grammar: "+gerr.Error())
			b, _ := json.MarshalIndent(rep, "", "  ")
			return string(b), true
		}
		ext = g
	}

	compiled, err := lvns.Convert(source)
	if err != nil {
		rep.Errors = append(rep.Errors, "compile: "+err.Error())
		b, _ := json.MarshalIndent(rep, "", "  ")
		return string(b), true
	}

	// Bridge to the runtime-format validator through the container JSON —
	// exactly what the CLI's convert→validate pipeline does.
	raw, _ := json.Marshal(compiled)
	doc, perr := lvn.Parse(raw)
	if perr != nil {
		rep.Errors = append(rep.Errors, "container: "+perr.Error())
		b, _ := json.MarshalIndent(rep, "", "  ")
		return string(b), true
	}
	rep.Commands = len(doc.Script)
	for _, is := range lvn.ValidateExt(doc, ext) {
		if is.Sev == lvn.SevError {
			rep.Errors = append(rep.Errors, is.String())
		} else {
			rep.Warnings = append(rep.Warnings, is.String())
		}
	}
	rep.OK = len(rep.Errors) == 0
	b, _ := json.MarshalIndent(rep, "", "  ")
	return string(b), !rep.OK
}

// ── docs ─────────────────────────────────────────────────────────────────

var docFiles = map[string]string{
	"tutorial":     "howto/TUTORIAL.md",
	"cheatsheet":   "howto/CHEATSHEET.md",
	"capabilities": "howto/CAPABILITIES.md",
	"language":     "howto/LANGUAGE.md",
	"recipes":      "howto/recipes.md",
	"agents":       "howto/AGENTS.md",
	"format":       "docs/lvn-format.md",
	"embedding":    "docs/embedding.md",
}

func readDoc(name string) (string, bool) {
	rel, ok := docFiles[strings.ToLower(strings.TrimSpace(name))]
	if !ok {
		names := make([]string, 0, len(docFiles))
		for n := range docFiles {
			names = append(names, n)
		}
		return "unknown doc — pick one of: " + strings.Join(names, ", "), true
	}
	root, err := repoRoot()
	if err != nil {
		return err.Error(), true
	}
	data, err := os.ReadFile(filepath.Join(root, rel))
	if err != nil {
		return "doc unreadable: " + err.Error(), true
	}
	return string(data), false
}

// repoRoot finds the LVN repository: LVN_REPO wins, otherwise walk up from
// the working directory looking for the docs landmark.
func repoRoot() (string, error) {
	if env := os.Getenv("LVN_REPO"); env != "" {
		return env, nil
	}
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "howto", "CHEATSHEET.md")); err == nil {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", fmt.Errorf("LVN repo not found — set LVN_REPO to the repository root")
		}
		dir = parent
	}
}
