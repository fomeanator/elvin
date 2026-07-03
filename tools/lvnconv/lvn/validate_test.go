package lvn

import "testing"

func parse(t *testing.T, s string) *Doc {
	t.Helper()
	d, err := Parse([]byte(s))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	return d
}

// hasError reports whether any error-severity issue mentions sub.
func hasError(issues []Issue, sub string) bool {
	for _, is := range issues {
		if is.Sev == SevError && contains(is.Msg, sub) {
			return true
		}
	}
	return false
}

// hasWarn reports whether any warning-severity issue mentions sub.
func hasWarn(issues []Issue, sub string) bool {
	for _, is := range issues {
		if is.Sev == SevWarning && contains(is.Msg, sub) {
			return true
		}
	}
	return false
}

func TestUnknownFieldWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"fade","too":"black","duration":0.5}]}`)
	issues := Validate(d)
	if !hasWarn(issues, `unknown field "too"`) {
		t.Fatalf("expected unknown-field warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "to"`) {
		t.Fatalf("expected a 'to' suggestion, got %v", issues)
	}
}

func TestEnumValueWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"x","position":"lft","show":true}]}`)
	issues := Validate(d)
	if !hasWarn(issues, `position="lft" is not a known value`) {
		t.Fatalf("expected enum warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "left"`) {
		t.Fatalf("expected a 'left' suggestion, got %v", issues)
	}
}

// Valid values and keys must NOT warn — the checks are only for typos.
func TestValidFieldsAndEnumsDoNotWarn(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"fade","to":"black","duration":0.5},
	 {"op":"actor","id":"x","position":"left","show":true,"emotion":"happy","enter":"fade"},
	 {"op":"audio","channel":"music","url":"/a.mp3","action":"play"},
	 {"op":"camera","action":"shake","duration":0.4},
	 {"op":"particles","type":"rain","on":true},
	 {"op":"set","key":"gold","value":5,"default":true},
	 {"op":"say","who":"X","text":"hi {gold}"}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "unknown field") || contains(is.Msg, "is not a known value") {
			t.Fatalf("false positive on valid content: %s", is.String())
		}
	}
}

func TestUndefinedVarTypoWarned(t *testing.T) {
	// score is set; scoore is read in an expr and an interpolation → both typos.
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"score","value":0},
	 {"op":"if","expr":"scoore >= 10","then":"w","else":"l"},
	 {"op":"say","who":"X","text":"У тебя {scoore} очков"},
	 {"op":"label","id":"w"},{"op":"label","id":"l"}
	]}`)
	issues := Validate(d)
	if !hasWarn(issues, `variable "scoore" is read but never set`) {
		t.Fatalf("expected undefined-var warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "score"`) {
		t.Fatalf("expected a 'score' suggestion, got %v", issues)
	}
}

// A variable that isn't a near-miss of any defined var is treated as seeded
// externally (carried from an earlier chapter / the host), not a typo.
func TestExternalVarNotFlagged(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"gold","value":0},
	 {"op":"if","expr":"player_name_len > 3","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	if hasWarn(Validate(d), "is read but never set") {
		t.Fatalf("a distinct external var must not be flagged as a typo")
	}
}

// String literals inside an expression are not variables and must not be flagged.
func TestStringLiteralNotFlaggedAsVar(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"state","value":"idle"},
	 {"op":"if","expr":"state == \"stat\"","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	// "stat" is a quoted literal that is a near-miss of "state" — but it's a
	// literal, so stripping quotes must prevent a false positive.
	if hasWarn(Validate(d), `variable "stat"`) {
		t.Fatalf("a string literal was mistaken for a variable: %v", Validate(d))
	}
}

// With no vars set at all, the doc is assumed to rely on external seeding and the
// typo check is skipped entirely (no noise).
func TestNoDefinedVarsSkipsCheck(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"if","expr":"anything > 1","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	if hasWarn(Validate(d), "is read but never set") {
		t.Fatalf("undefined-var check should not run when nothing is set")
	}
}

// An unset/absent enum field (e.g. actor with no position) must not warn.
func TestAbsentEnumFieldDoesNotWarn(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"x","show":true}]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "is not a known value") {
			t.Fatalf("absent enum field must not warn: %s", is.String())
		}
	}
}

func contains(s, sub string) bool {
	return len(sub) == 0 || (len(s) >= len(sub) && indexOf(s, sub) >= 0)
}
func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}

func TestValidate_CleanDoc(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"start"},
		{"op":"say","text":"hi"},
		{"op":"goto","label":"start"}
	]}`)
	for _, is := range Validate(d) {
		if is.Sev == SevError {
			t.Errorf("unexpected error issue: %s", is)
		}
	}
}

func TestValidate_DanglingGoto(t *testing.T) {
	d := parse(t, `{"script":[{"op":"goto","label":"nowhere"}]}`)
	if !hasError(Validate(d), "undefined label") {
		t.Fatal("expected dangling-goto error")
	}
}

func TestValidate_BuiltinEndIsFine(t *testing.T) {
	d := parse(t, `{"script":[{"op":"goto","label":"__end"}]}`)
	if hasError(Validate(d), "undefined label") {
		t.Fatal("__end must be an allowed builtin target")
	}
}

func TestValidate_DuplicateLabel(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"a"},
		{"op":"label","id":"a"}
	]}`)
	if !hasError(Validate(d), "duplicate label") {
		t.Fatal("expected duplicate-label error")
	}
}

func TestValidate_UnknownOp(t *testing.T) {
	// Unknown ops are a WARNING, not an error: they may be host-defined
	// (authored via `ext`, handled by the game through LvnOps.Register).
	d := parse(t, `{"script":[{"op":"saay","text":"typo"}]}`)
	iss := Validate(d)
	if hasError(iss, "unknown op") {
		t.Fatal("unknown op must not be an error (host-defined ops are legal)")
	}
	if !hasWarn(iss, "unknown op") {
		t.Fatal("expected unknown-op warning")
	}
}

func TestValidate_IfBranchTargets(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"if","cond":{},"then":"yes","else":"no"},
		{"op":"label","id":"yes"}
	]}`)
	if !hasError(Validate(d), `label "no"`) {
		t.Fatal("expected error for missing else target")
	}
}

func TestValidate_ChoiceOptionGoto(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"choice","options":[
			{"text":"go","goto":"missing"},
			{"text":"stay","goto":"here"}
		]},
		{"op":"label","id":"here"}
	]}`)
	if !hasError(Validate(d), `label "missing"`) {
		t.Fatal("expected error for missing choice target")
	}
}

func TestValidate_NestedOptionBody(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"choice","options":[
			{"text":"x","body":[{"op":"goto","label":"ghost"}]}
		]}
	]}`)
	if !hasError(Validate(d), `label "ghost"`) {
		t.Fatal("expected error for dangling goto inside option body")
	}
}

func TestValidate_FallThroughIntoJumpTarget(t *testing.T) {
	// The button-screen footgun: a say screen falls through into a label that is
	// also a jump target → tapping slides the chapter forward unexpectedly.
	d := parse(t, `{"script":[
		{"op":"say","text":"hub — tap a hotspot"},
		{"op":"label","id":"weather"},
		{"op":"say","text":"rain"},
		{"op":"goto","label":"weather"}
	]}`)
	iss := Validate(d)
	if !hasWarn(iss, "fall-through") {
		t.Fatal("expected a fall-through warning for ':weather'")
	}
	if hasError(iss, "fall-through") {
		t.Fatal("fall-through must be a warning, not an error")
	}
}

func TestValidate_NoFallThroughWarnAfterGoto(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"a"},
		{"op":"say","text":"x"},
		{"op":"goto","label":"b"},
		{"op":"label","id":"b"},
		{"op":"say","text":"y"},
		{"op":"goto","label":"a"}
	]}`)
	if hasWarn(Validate(d), "fall-through") {
		t.Fatal("a label reached only after a goto must not warn")
	}
}

func TestValidate_UnbalancedBraces(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"hello {name"}]}`)
	if !hasWarn(Validate(d), "unbalanced") {
		t.Fatal("expected unbalanced-brace warning")
	}
}

func TestValidate_EscapedBracesAreFine(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"a {{literal}} and {name}"}]}`)
	if hasWarn(Validate(d), "unbalanced") {
		t.Fatal("escaped braces and a plain {var} must not warn")
	}
}

func TestValidate_ChoiceOptionWithoutTarget(t *testing.T) {
	d := parse(t, `{"script":[{"op":"choice","options":[{"text":"dead end"}]}]}`)
	if !hasWarn(Validate(d), "no goto and no body") {
		t.Fatal("expected warning for a choice option with no goto/body")
	}
}

func TestValidate_MissingScene(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"hi"}]}`)
	iss := Validate(d)
	if !hasWarn(iss, "scene") {
		t.Fatal("expected a missing-scene warning")
	}
	if hasError(iss, "scene") {
		t.Fatal("missing scene is a warning, not an error")
	}
}

func TestValidate_ScenePresent_NoWarn(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"hi"}]}`)
	if hasWarn(Validate(d), "no `scene`") {
		t.Fatal("a present scene header must not warn")
	}
}

func TestValidate_EmptyScript(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[]}`)
	if !hasWarn(Validate(d), "empty") {
		t.Fatal("expected an empty-script warning")
	}
}

func TestValidate_FailedCommandAsNarration(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"fade to=\"black\"3 duration=0.8"}]}`)
	if !hasWarn(Validate(d), "didn't parse") {
		t.Fatal("expected a warning for an op-looking narration line")
	}
}

func TestValidate_PlainNarration_NoFalsePositive(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"She said hello."},{"op":"say","who":"Mara","text":"set the mood"}]}`)
	if hasWarn(Validate(d), "didn't parse") {
		t.Fatal("plain narration / dialogue must not warn")
	}
}

func TestValidate_SeverityClassification(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"saay","text":"typo"},
		{"op":"label","id":"orphan"}
	]}`)
	iss := Validate(d)
	if !hasWarn(iss, "unknown op") {
		t.Fatal("unknown op should be a warning (may be host-defined)")
	}
	if !hasWarn(iss, "never targeted") {
		t.Fatal("an untargeted label should be a warning")
	}
}
