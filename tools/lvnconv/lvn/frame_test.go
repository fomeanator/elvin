package lvn

import "testing"

// Расписание кадра: кто в каком месте чем является — для каждой команды главы.
//
// Проверки держат три вещи: кадр читается как данные; на ветвлениях расписание
// честно признаёт неоднозначность вместо того, чтобы выбрать один путь молча;
// и — главное для ветвистых игр — инвариант («что верно на всех путях»)
// остаётся верным, сколько бы ветвей ни сходилось в точке.

func doc(cmds ...Cmd) *Doc { return &Doc{Script: cmds} }

// issuesOf — находки одного вида. Виды разные и мешать их нельзя: «говорит вне
// кадра» — про одну реплику, «остался в кадре» — про конец главы.
func issuesOf(r FrameReport, kind string) []FrameIssue {
	var out []FrameIssue
	for _, is := range r.Issues {
		if is.Kind == kind {
			out = append(out, is)
		}
	}
	return out
}

func actor(id string, show bool) Cmd {
	return Cmd{"op": "actor", "id": id, "show": show, "position": "center"}
}
// Реплика: `who` — отображаемое имя, `who_id` — актёр на сцене. Именно вторым
// связаны говорящий и фигура в кадре; на живом контенте проверка молчала, пока
// сравнивала имена.
func say(who, text string) Cmd {
	return Cmd{"op": "say", "who": who, "who_id": who, "text": text}
}
func label(id string) Cmd      { return Cmd{"op": "label", "id": id} }
func gotoL(id string) Cmd      { return Cmd{"op": "goto", "label": id} }

func TestScheduleLinearKnowsEveryStop(t *testing.T) {
	d := doc(
		Cmd{"op": "bg", "sprite_url": "/hall.jpg"},
		actor("agent", true),
		say("agent", "здравствуй"),
		actor("agent", false),
	)
	r := Schedule(d, 0)

	if !r.Stops[1].Frame.Actors["agent"].Visible {
		t.Fatal("после показа агент обязан быть в кадре")
	}
	if !r.Stops[2].Frame.Actors["agent"].Visible {
		t.Fatal("реплика не должна убирать людей из кадра")
	}
	if r.Stops[3].Frame.Actors["agent"].Visible {
		t.Fatal("после hide агента в кадре быть не должно")
	}
	if r.Uncertain != 0 {
		t.Fatalf("линейная глава однозначна вся, а неопределённых узлов %d", r.Uncertain)
	}
}

// Грим живёт отдельно от позы и переживает уход: иначе вернувшийся актёр
// оказывается другим человеком — ровно это мы и видели живьём.
func TestScheduleKeepsTheLookThroughHiding(t *testing.T) {
	d := doc(
		actor("agent", true),
		Cmd{"op": "sfx", "id": "agent", "dark": 0.88},
		actor("agent", false),
	)
	r := Schedule(d, 0)
	a := r.Stops[2].Frame.Actors["agent"]
	if a.Visible {
		t.Fatal("ушёл — значит не виден")
	}
	if a.Fx == nil {
		t.Fatal("грим стёрся вместе с уходом — вернётся другой человек")
	}
}

// Ветви сошлись с ОДИНАКОВОЙ сценой — знание подтверждено, а не потеряно.
func TestScheduleBranchesThatAgreeStayCertain(t *testing.T) {
	d := doc(
		actor("agent", true),                      // 0
		Cmd{"op": "if", "then": "A", "else": "B"}, // 1
		label("A"),                                // 2
		gotoL("END"),                              // 3
		label("B"),                                // 4
		gotoL("END"),                              // 5
		label("END"),                              // 6
		say("agent", "вместе"),                    // 7
	)
	r := Schedule(d, 0)
	if !r.Stops[7].Reached {
		t.Fatal("обход не дошёл до схождения")
	}
	if !r.Stops[7].Certain {
		t.Fatal("ветви привели одинаковую сцену — узел обязан остаться однозначным")
	}
}

// Ветви сошлись с РАЗНОЙ сценой: расписание обязано это признать. Одна ложь в
// расписании дороже сотни честных пропусков.
func TestScheduleBranchesThatDisagreeAreUncertain(t *testing.T) {
	d := doc(
		actor("agent", true),                      // 0
		Cmd{"op": "if", "then": "A", "else": "B"}, // 1
		label("A"),                                // 2
		actor("hero", true),                       // 3
		gotoL("END"),                              // 4
		label("B"),                                // 5
		actor("agent", false),                     // 6
		gotoL("END"),                              // 7
		label("END"),                              // 8
		say("agent", "вместе"),                    // 9
	)
	r := Schedule(d, 0)
	if r.Stops[9].Certain {
		t.Fatal("кадр зависит от пути, а расписание уверяет, что знает его")
	}
	if r.Uncertain == 0 {
		t.Fatal("неопределённость не посчитана")
	}
}

// ВЕТВИСТАЯ ИГРА: точный кадр в точке схождения неизвестен, но инвариант —
// «кто здесь есть на всех путях» и «кого здесь нет ни на одном» — остаётся
// верным. Именно на нём и стоит строить проверки: он не растёт с числом
// комбинаций.
func TestScheduleInvariantSurvivesBranching(t *testing.T) {
	d := doc(
		actor("agent", true),                      // 0 — есть у всех путей
		Cmd{"op": "if", "then": "A", "else": "B"}, // 1
		label("A"),                                // 2
		actor("hero", true),                       // 3 — только на этом пути
		gotoL("END"),                              // 4
		label("B"),                                // 5
		actor("cat", true),                        // 6 — только на этом
		actor("cat", false),                       // 7 — и сразу уходит
		gotoL("END"),                              // 8
		label("END"),                              // 9
		say("agent", "вместе"),                    // 10
	)
	r := Schedule(d, 0)
	st := r.Stops[10]

	if !st.AlwaysVisible["agent"] {
		t.Fatal("агент стоит на обоих путях — это и есть то, на что можно опереться")
	}
	if st.AlwaysVisible["hero"] {
		t.Fatal("героиня есть лишь на одной ветви — «всегда» про неё неверно")
	}
	if !st.NeverVisible["cat"] {
		t.Fatal("кота нет ни на одном пути: на одном не показывали, на другом убрали")
	}
	if st.NeverVisible["agent"] {
		t.Fatal("агент в кадре — «никогда» про него ложь")
	}
}

// Находка строится на инварианте: говорит тот, кого нет в кадре НИ НА ОДНОМ
// пути. Иначе на ветвистой игре отчёт полон тревог, верных лишь для одной ветви.
func TestScheduleReportsSpeakingOffstage(t *testing.T) {
	d := doc(
		actor("agent", true),  // 0
		actor("agent", false), // 1
		say("agent", "я здесь"), // 2 — но его нет
	)
	got := issuesOf(Schedule(d, 0), "speaks-offstage")
	if len(got) != 1 {
		t.Fatalf("ожидалась одна находка «говорит вне кадра», получено %d", len(got))
	}
	if got[0].Who != "agent" {
		t.Fatalf("не тот говорящий: %+v", got[0])
	}
}

// Рассказчик и системные голоса не показываются никогда — и находкой быть не
// должны: ложная тревога в ежедневном отчёте убивает доверие ко всему отчёту.
func TestScheduleIgnoresVoicesThatNeverAppear(t *testing.T) {
	d := doc(
		actor("agent", true),
		say("system", "СВЯЗЬ УСТАНОВЛЕНА"),
		say("agent", "слышу"),
	)
	if got := issuesOf(Schedule(d, 0), "speaks-offstage"); len(got) != 0 {
		t.Fatalf("система голосом за кадром — это норма, а отчёт нашёл %d", len(got))
	}
}

// `clear` скрывает всех, но кадр помнит, чем их вернуть: показанный снова без
// position обязан встать на прежнее место.
func TestScheduleClearHidesButRemembers(t *testing.T) {
	d := doc(
		actor("agent", true),
		Cmd{"op": "clear"},
	)
	r := Schedule(d, 0)
	a, known := r.Stops[1].Frame.Actors["agent"]
	if !known {
		t.Fatal("кадр забыл человека — вернуть его будет нечем")
	}
	if a.Visible {
		t.Fatal("clear обязан скрыть")
	}
	if a.Pose == nil {
		t.Fatal("поза потеряна — «встать как стоял» станет невозможно")
	}
}

// Кто остался стоять к концу главы — того унесёт в меню или в следующую главу.
// Этот класс дефектов мы неделю ловили руками: «агент остался», «героиня не
// уходит». В тексте главы он виден за секунду.
func TestScheduleReportsWhoIsLeftOnStage(t *testing.T) {
	d := doc(
		actor("agent", true),
		actor("hero", true),
		actor("hero", false),
		say("agent", "конец"),
	)
	got := issuesOf(Schedule(d, 0), "left-on-stage")
	if len(got) != 1 || got[0].Who != "agent" {
		t.Fatalf("забытый в кадре не найден: %+v", got)
	}
}

// Глава, которая за собой убрала, находкой быть не должна.
func TestScheduleSaysNothingWhenTheChapterCleansUp(t *testing.T) {
	d := doc(
		actor("agent", true),
		say("agent", "прощай"),
		actor("agent", false),
	)
	if got := issuesOf(Schedule(d, 0), "left-on-stage"); len(got) != 0 {
		t.Fatalf("глава убрала за собой, а отчёт нашёл %d", len(got))
	}
}
