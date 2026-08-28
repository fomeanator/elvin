package lvn

import (
	"strings"
	"testing"
)

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

// ОТКРЫТЫЙ НАБОР ПОЛЕЙ — НЕ ПОВОД МОЛЧАТЬ О ЯВНОЙ ОПЕЧАТКЕ.
//
// У `actor`/`obj` набор закрыть нельзя: сверх грамматики там живут оси
// гардероба, которые называет автор (в живом контенте `outfit=` и `hair=`
// встречаются десятки тысяч раз). Из-за этого опечатка в САМОЙ ЧАСТОЙ команде
// языка не ловилась вовсе — компилировалась молча и молча же не действовала.
func TestActorTypoWarnedButWardrobeAxesAreNot(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"actor","id":"m","sprite_ulr":"/a.png","postion":"left"},
	 {"op":"actor","id":"m","outfit":"school","hair":"long","armor":"plate"}
	]}`)
	issues := Validate(d)

	if !hasWarn(issues, `"sprite_ulr"`) || !hasWarn(issues, `"postion"`) {
		t.Fatalf("описки в actor должны быть замечены: %+v", issues)
	}
	for _, axis := range []string{`"outfit"`, `"hair"`, `"armor"`} {
		if hasWarn(issues, axis) {
			t.Fatalf("ось гардероба %s — не опечатка, предупреждать о ней нельзя: %+v", axis, issues)
		}
	}
}

// Короткие имена не судим: у осей вроде `w`/`h` расстояние до `x`/`y` тоже
// единица, и подсказка была бы шумом ровно там, где автор прав.
func TestShortAxisNamesAreLeftAlone(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"m","w":1,"h":2,"ear":"x"}]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "looks like a typo") {
			t.Fatalf("короткое имя не должно давать подсказку: %s", is.Msg)
		}
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

func TestValidate_DropMapTargets(t *testing.T) {
	// on_drop "target:label" pairs and on_drop_miss are jump references: a
	// typo must error, and a label reached only via a drop is NOT dead.
	d := parse(t, `{"script":[
		{"op":"obj","id":"apple","draggable":true,"on_drop":"bag:in_bag, box:nowhere","on_drop_miss":"missed"},
		{"op":"label","id":"in_bag"},
		{"op":"label","id":"missed"}
	]}`)
	iss := Validate(d)
	if !hasError(iss, `undefined label "nowhere"`) {
		t.Fatal("expected error for the typo'd drop label")
	}
	if hasWarn(iss, `"in_bag" is never targeted`) || hasWarn(iss, `"missed" is never targeted`) {
		t.Fatal("drop-reached labels must not read as dead")
	}
}

func TestValidate_MalformedDropPair(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"obj","id":"apple","draggable":true,"on_drop":"baglabel"}
	]}`)
	if !hasWarn(Validate(d), "not target:label") {
		t.Fatal("expected malformed-pair warning")
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
	// Ошибка, а не предупреждение: такая строка молча уезжает игроку.
	if !hasError(Validate(d), "не разобрался") {
		t.Fatal("строка-команда, ставшая репликой, обязана быть ОШИБКОЙ")
	}
}

func TestValidate_PlainNarration_NoFalsePositive(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"She said hello."},{"op":"say","who":"Mara","text":"set the mood"}]}`)
	if hasError(Validate(d), "не разобрался") {
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

func TestValidate_InputNeedsVar(t *testing.T) {
	d := parse(t, `{"script":[{"op":"input","prompt":"Name?"}]}`)
	if !hasError(Validate(d), "input needs var") {
		t.Fatal("expected missing-var error")
	}
	d2 := parse(t, `{"script":[{"op":"input","var":"name","prompt":"Name?"}]}`)
	if hasError(Validate(d2), "input") {
		t.Fatal("valid input must pass")
	}
}

func TestValidate_TimedChoicePairing(t *testing.T) {
	// timeout_goto is a jump reference; either half without the other warns.
	d := parse(t, `{"script":[
		{"op":"choice","options":[{"text":"a","goto":"x"}],"timeout":5,"timeout_goto":"nowhere"},
		{"op":"label","id":"x"}
	]}`)
	if !hasError(Validate(d), `undefined label "nowhere"`) {
		t.Fatal("expected error for the typo'd timeout branch")
	}
	half := parse(t, `{"script":[
		{"op":"choice","options":[{"text":"a","goto":"x"}],"timeout":5},
		{"op":"label","id":"x"}
	]}`)
	if !hasWarn(Validate(half), "nowhere to go when time runs out") {
		t.Fatal("expected timeout-without-goto warning")
	}
}

// A call to a function no evaluator implements is the failure the runtime hides:
// the expression throws, the variable reads 0, nothing on screen says so. The
// validator has to be the one that speaks up — this is the check that would have
// caught `func` being a phantom feature years earlier.
func TestValidate_UnknownExprFunction(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"x","expr":"add(2,3)"},
		{"op":"if","expr":"flor(x) > 1","then":"a","else":"a"},
		{"op":"say","text":"you have {tally(x)} left"},
		{"op":"label","id":"a"}
	]}`)
	is := Validate(d)
	if !hasWarn(is, "unknown function add()") {
		t.Fatalf("expected a warning for add() in a set expr: %v", is)
	}
	if !hasWarn(is, "did you mean floor()") {
		t.Fatalf("expected a spelling suggestion for flor(): %v", is)
	}
	if !hasWarn(is, "unknown function tally()") {
		t.Fatalf("expected a warning for a call in an interpolation: %v", is)
	}
}

// Every built-in, a bare variable, a nested built-in call and an ink-style text
// alternative must stay silent — a false positive here would break the 0-warning
// gate on real content.
func TestValidate_KnownExprFunctionsAreSilent(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"x","expr":"floor(sum(list(1,2,3)) / max(1, len(inv)))"},
		{"op":"set","key":"y","expr":"get(m, \"put(x)\", 0)"},
		{"op":"if","expr":"has(inv, \"key\") and chance(0.5)","then":"a","else":"a"},
		{"op":"say","text":"{keys(m)} and {mood: good|bad} and {a|b|c}"},
		{"op":"label","id":"a"}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "unknown function") {
			t.Fatalf("false positive: %s", is.String())
		}
	}
}

// Two statements sharing one line inside a block leave the second one INSIDE the
// first expression, where the evaluator throws and `set` swallows the throw — the
// variable keeps its old value and nothing on screen says so (the howto/rpg shop
// shipped like this and still gated at 0 warnings).
func TestValidate_StrayAssignInExpr(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"gold","expr":"gold - 5  potions = potions + 1"}
	]}`)
	if !hasWarn(Validate(d), "stray `=`") {
		t.Fatalf("expected a stray-assignment warning: %v", Validate(d))
	}
	clean := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"a","expr":"gold >= 5 and hp != 0 and flag == 1"},
		{"op":"set","key":"b","expr":"get(m, \"k=v\", 0)"},
		{"op":"set","key":"c","expr":"put(m, \"k\", x <= 2)"}
	]}`)
	for _, is := range Validate(clean) {
		if contains(is.Msg, "stray") {
			t.Fatalf("false positive: %s", is.String())
		}
	}
}

// The op-typo lint: a mistyped command must not reach the player as dialogue.
// Shapes prose never has (`=`, `->`, a /path argument) are flagged; a
// positional-only slip is knowingly left alone — see commandLike.
//
// Это ОШИБКА, а не предупреждение: предупреждение через API публикации никто
// не читает, и глава с `bbg /content/…` уезжала игроку с ok:true.
func TestMistypedCommandLint(t *testing.T) {
	flagged := func(text string) bool {
		doc := &Doc{Script: []Cmd{{"op": "say", "text": text}, {"op": "goto", "label": "__end"}}}
		for _, is := range Validate(doc) {
			if is.Sev != SevError {
				continue
			}
			if strings.Contains(is.Msg, "опечаткой") || strings.Contains(is.Msg, "не разобрался") {
				return true
			}
		}
		return false
	}
	for _, tc := range []struct {
		text string
		want bool
		why  string
	}{
		{"sett gold = 1", true, "key=value slip"},
		{"iff gold > 1 -> rich", true, "swallows a whole branch, and no if op is left for the dangling-target check to see"},
		{"bbg /content/bg/x.jpg", true, "content url as an argument"},
		{"actro id=hill", true, "near-miss with key=value"},
		{"shwo mara", false, "positional-only: indistinguishable from prose, deliberately not flagged"},
		{"Она открыла дверь.", false, "ordinary narration"},
		{"wave after wave of them", false, "prose whose first word is one edit from an op"},
		{"Мы дошли до развилки — налево или направо?", false, "prose with a dash"},
	} {
		if got := flagged(tc.text); got != tc.want {
			t.Errorf("%q: flagged=%v, want %v (%s)", tc.text, got, tc.want, tc.why)
		}
	}
}

// ГОВОРЯЩИЙ, ПОХОЖИЙ НА ПРОЗУ, — это проза, разрезанная двоеточием.
//
// В языке `Имя: текст` — реплика, поэтому строка «Комната-побег. Кликай по
// предметам: осмотри стол» превращается в реплику говорящего «Комната-побег.
// Кликай по предметам». Тихо: на экране появляется подпись, которой автор не
// писал. В живом контенте таких нашлось шесть.
func TestProseSpeakerWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","who":"Комната-побег. Кликай по предметам","text":"осмотри стол"},
	 {"op":"say","who":"Матвей и Валера","text":"мы вдвоём"},
	 {"op":"say","who":"...","text":"пауза"},
	 {"op":"say","who":"Анна","text":"привет"}
	]}`)
	issues := Validate(d)

	if !hasWarn(issues, "looks like prose cut by a colon") {
		t.Fatalf("проза, разрезанная двоеточием, должна быть замечена: %+v", issues)
	}
	for _, ok := range []string{"Матвей и Валера", "Анна", `"..."`} {
		for _, is := range issues {
			if contains(is.Msg, ok) && contains(is.Msg, "looks like prose") {
				t.Fatalf("законный говорящий %q не должен предупреждать: %s", ok, is.Msg)
			}
		}
	}
}

// ДИАГНОСТИКА ГОВОРИТ О ТОМ, ЧТО ПИСАЛ АВТОР.
//
// В импортированной главе метку получает КАЖДАЯ нода графа articy (форма
// `n17_000000`); в базе Time Romance таких 5329 против 38 авторских. Две
// проверки — «провал в метку» и «метка никем не нужна» — ругались на них
// 4343 раза, и пять настоящих находок тонули в четырёх с половиной тысячах
// строк вывода.
func TestGeneratedLabelsDoNotDrownTheRealFindings(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","text":"раз"},
	 {"op":"label","id":"n17_000000"},
	 {"op":"say","text":"два"},
	 {"op":"label","id":"развилка"},
	 {"op":"goto","label":"n17_000000"},
	 {"op":"goto","label":"развилка"}
	]}`)
	issues := Validate(d)

	// Молчать надо о ШУМЕ РАЗМЕТКИ — «провал в метку» и «метка никем не нужна».
	// Дефекты ПОТОКА (петля без выхода, недостижимость) машинная метка не
	// оправдывает: в главах Cold именно на таких метках замкнут гардероб, и
	// промолчать о софтлоке было бы хуже, чем шуметь.
	for _, is := range issues {
		if is.Sev != SevWarning || !contains(is.Msg, "n17_000000") {
			continue
		}
		if contains(is.Msg, "fall-through") || contains(is.Msg, "never targeted") {
			t.Fatalf("шум разметки на метке импортёра: %s", is.Msg)
		}
	}
	// Авторская метка с тем же провалом — предупреждение на месте.
	if !hasWarn(issues, `"развилка"`) {
		t.Fatalf("на авторской метке проверка обязана работать: %+v", issues)
	}
}

// Подавление объясняется вслух: иначе оно неотличимо от сломанной проверки.
// Заметка НЕ предупреждение — `-strict` валит сборку на любом предупреждении,
// и честное объяснение стало бы стеной.
func TestSuppressionIsSaidOutLoudAsANote(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","text":"раз"},
	 {"op":"label","id":"n1_000000"},
	 {"op":"label","id":"n2_000000"}
	]}`)
	issues := Validate(d)

	var note *Issue
	for i := range issues {
		if issues[i].Sev == SevNote {
			note = &issues[i]
		}
	}
	if note == nil {
		t.Fatalf("подавленное должно быть названо заметкой: %+v", issues)
	}
	if !contains(note.Msg, "generated label") {
		t.Fatalf("заметка должна объяснять, ЧТО именно не показано: %s", note.Msg)
	}
	if note.Sev == SevWarning || note.Sev == SevError {
		t.Fatal("заметка не должна валить -strict")
	}
}

// ПЕТЛЯ БЕЗ ВЫХОДА — игрок застрянет навсегда.
//
// Находка не теоретическая: в главах Cold так замкнут гардероб — открыть,
// поставить флаг, вернуться на метку, открыть снова (тридцать таких мест в
// восемнадцати файлах). Прежняя диагностика видела лишь СЛЕДСТВИЕ («дальше
// недостижимо»), да и то не всегда: если в хвост главы вёл ещё один переход,
// петля проходила молча — ровно так молчал cold-ch01.
func TestLoopWithoutExitWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"label","id":"ловушка"},
	 {"op":"say","text":"крутимся"},
	 {"op":"goto","label":"ловушка"},
	 {"op":"say","text":"сюда не попасть"}
	]}`)
	if !hasWarn(Validate(d), "has no way out") {
		t.Fatalf("вечная петля должна быть замечена: %+v", Validate(d))
	}
}

// Игровой цикл — норма: из него уводит выбор, ветвление или возврат.
func TestGameLoopsAreNotFlagged(t *testing.T) {
	cases := map[string]string{
		"выбор уводит наружу": `{"scene":"t","script":[
		 {"op":"label","id":"loop"},
		 {"op":"choice","options":[{"text":"ещё","goto":"loop"},{"text":"хватит","goto":"end"}]},
		 {"op":"goto","label":"loop"},
		 {"op":"label","id":"end"},
		 {"op":"say","text":"конец"}]}`,
		"ветвление уводит наружу": `{"scene":"t","script":[
		 {"op":"label","id":"loop"},
		 {"op":"inc","key":"n"},
		 {"op":"if","expr":"n > 3","then":"end","else":"loop"},
		 {"op":"goto","label":"loop"},
		 {"op":"label","id":"end"},
		 {"op":"say","text":"конец"}]}`,
		"возврат из вызова": `{"scene":"t","script":[
		 {"op":"label","id":"sub"},
		 {"op":"say","text":"работа"},
		 {"op":"return"},
		 {"op":"goto","label":"sub"}]}`,
	}
	for name, src := range cases {
		d := parse(t, src)
		if hasWarn(Validate(d), "has no way out") {
			t.Fatalf("%s: законный цикл не должен предупреждать: %+v", name, Validate(d))
		}
	}
}
