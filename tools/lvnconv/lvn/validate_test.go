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
	d := parse(t, `{"script":[{"op":"saay","text":"typo"}]}`)
	if !hasError(Validate(d), "unknown op") {
		t.Fatal("expected unknown-op error")
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
	if !hasError(iss, "unknown op") {
		t.Fatal("unknown op should be an error")
	}
	if !hasWarn(iss, "never targeted") {
		t.Fatal("an untargeted label should be a warning")
	}
}
