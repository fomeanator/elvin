package main

import (
	"encoding/json"
	"strings"
	"testing"
)

// Drive the server exactly as an MCP client would: one JSON-RPC line in,
// one out. The check tool is the star — it is the agent's quality gate.

func rpc(t *testing.T, method, params string) map[string]any {
	t.Helper()
	msg := `{"jsonrpc":"2.0","id":1,"method":"` + method + `"`
	if params != "" {
		msg += `,"params":` + params
	}
	msg += `}`
	raw := handleMessage([]byte(msg))
	if raw == nil {
		t.Fatalf("%s: no response", method)
	}
	var resp map[string]any
	if err := json.Unmarshal(raw, &resp); err != nil {
		t.Fatalf("%s: bad response json: %v", method, err)
	}
	return resp
}

func toolText(t *testing.T, resp map[string]any) (string, bool) {
	t.Helper()
	res, ok := resp["result"].(map[string]any)
	if !ok {
		t.Fatalf("no result: %v", resp)
	}
	content := res["content"].([]any)
	text := content[0].(map[string]any)["text"].(string)
	isErr, _ := res["isError"].(bool)
	return text, isErr
}

func TestInitializeAndList(t *testing.T) {
	resp := rpc(t, "initialize", `{"protocolVersion":"2024-11-05"}`)
	if resp["error"] != nil {
		t.Fatalf("initialize errored: %v", resp["error"])
	}
	resp = rpc(t, "tools/list", "")
	tools := resp["result"].(map[string]any)["tools"].([]any)
	if len(tools) != 3 {
		t.Fatalf("expected 3 tools, got %d", len(tools))
	}
}

func TestNotificationsAreSilent(t *testing.T) {
	if out := handleMessage([]byte(`{"jsonrpc":"2.0","method":"notifications/initialized"}`)); out != nil {
		t.Fatalf("notification must not be answered: %s", out)
	}
}

func TestCheck_CleanScriptPassesTheGate(t *testing.T) {
	src := `scene t\nПривет!\n- Дальше -> next\n:next\nКонец.\n-> __end`
	resp := rpc(t, "tools/call", `{"name":"lvns_check","arguments":{"source":"`+src+`"}}`)
	text, isErr := toolText(t, resp)
	if isErr {
		t.Fatalf("clean script flagged: %s", text)
	}
	if !strings.Contains(text, `"ok": true`) {
		t.Fatalf("expected ok:true, got: %s", text)
	}
}

func TestCheck_DanglingJumpIsAnError(t *testing.T) {
	src := `scene t\n-> nowhere`
	resp := rpc(t, "tools/call", `{"name":"lvns_check","arguments":{"source":"`+src+`"}}`)
	text, isErr := toolText(t, resp)
	if !isErr {
		t.Fatalf("dangling jump slipped through: %s", text)
	}
	if !strings.Contains(text, "nowhere") {
		t.Fatalf("error message must name the label: %s", text)
	}
}

func TestConvert_EmitsTheContainer(t *testing.T) {
	resp := rpc(t, "tools/call", `{"name":"lvns_convert","arguments":{"source":"scene t\nПривет!"}}`)
	// (the backtick literal above carries a real \n escape inside the JSON string)
	text, isErr := toolText(t, resp)
	if isErr {
		t.Fatalf("convert failed: %s", text)
	}
	if !strings.Contains(text, `"say"`) || !strings.Contains(text, "Привет!") {
		t.Fatalf("container missing the line: %s", text)
	}
}

func TestDoc_ReadsCheatsheet(t *testing.T) {
	t.Setenv("LVN_REPO", "../../../..")
	resp := rpc(t, "tools/call", `{"name":"lvn_doc","arguments":{"name":"cheatsheet"}}`)
	text, isErr := toolText(t, resp)
	if isErr {
		t.Fatalf("doc read failed: %s", text)
	}
	if !strings.Contains(text, ".lvns") {
		t.Fatalf("cheatsheet content unexpected: %.80s", text)
	}
}

func TestDoc_UnknownNameListsOptions(t *testing.T) {
	resp := rpc(t, "tools/call", `{"name":"lvn_doc","arguments":{"name":"nope"}}`)
	text, isErr := toolText(t, resp)
	if !isErr || !strings.Contains(text, "cheatsheet") {
		t.Fatalf("expected the option list, got: %s", text)
	}
}
