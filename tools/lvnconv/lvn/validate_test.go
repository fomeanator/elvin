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

// hasError reports whether any fatal (script-level) issue mentions sub.
func hasError(issues []Issue, sub string) bool {
	for _, is := range issues {
		if is.Index >= 0 && contains(is.Msg, sub) {
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
		if is.Index >= 0 {
			t.Errorf("unexpected fatal issue: %s", is)
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
