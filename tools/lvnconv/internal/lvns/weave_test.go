package lvns

// weave_test.go — контракт плетения.
//
// Проверяется не «скомпилировалось», а ПОТОК: куда уходит каждая опция, что
// исполняется по дороге и где ветки сходятся. Имена меток тут намеренно нигде
// не сравниваются со строками — они минтованные, и тест, прибитый к ним,
// сломался бы от любой правки схемы имён, ничего не сказав о поведении.

import (
	"strings"
	"testing"
)

// flow — маленький «интерпретатор» скомпилированного документа, ровно
// достаточный, чтобы пройти ветку и увидеть, где она сходится.
type flow struct {
	t      *testing.T
	script []Cmd
	labels map[string]int
}

func newFlow(t *testing.T, src string) *flow {
	t.Helper()
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("не компилируется: %v\n%s", err, src)
	}
	f := &flow{t: t, script: doc.Script, labels: map[string]int{}}
	for i, c := range doc.Script {
		if op, _ := c["op"].(string); op == "label" {
			id, _ := c["id"].(string)
			if _, dup := f.labels[id]; dup {
				t.Fatalf("дубль метки %q — валидатор такое отвергает", id)
			}
			f.labels[id] = i
		}
	}
	return f
}

func (f *flow) choiceAt() (int, []map[string]any) {
	f.t.Helper()
	for i, c := range f.script {
		if op, _ := c["op"].(string); op == "choice" {
			var opts []map[string]any
			for _, o := range c["options"].([]any) {
				opts = append(opts, o.(map[string]any))
			}
			return i, opts
		}
	}
	f.t.Fatal("в документе нет choice")
	return 0, nil
}

// run идёт от индекса вперёд, собирая реплики, пока не упрётся в переход;
// возвращает собранное и индекс метки, на которую ушло управление.
func (f *flow) run(from int) (says []string, landed int) {
	f.t.Helper()
	for i := from; i < len(f.script); i++ {
		op, _ := f.script[i]["op"].(string)
		switch op {
		case "say":
			s, _ := f.script[i]["text"].(string)
			says = append(says, s)
		case "goto":
			lbl, _ := f.script[i]["label"].(string)
			if lbl == "__end" { // встроенная метка рантайма, в скрипте её нет
				return says, -1
			}
			at, ok := f.labels[lbl]
			if !ok {
				f.t.Fatalf("переход на несуществующую метку %q", lbl)
			}
			return says, at
		case "choice":
			f.t.Fatalf("неожиданный вложенный choice на %d", i)
		}
	}
	return says, -1
}

const weaveSrc = `scene t
Нихарис: Выбирай.
- Ударить {
  Ты бьёшь первым.
  репутация = репутация + 3
}
- Уйти {
  Ты уходишь молча.
}
Дальше идём вместе.
-> __end
`

// Главное утверждение: обе ветки исполняют СВОЮ прозу и сходятся в одну точку,
// после которой идёт общий текст. Ровно то, что автор написал бы четырьмя
// метками руками.
func TestWeaveBranchesRunTheirProseAndConverge(t *testing.T) {
	f := newFlow(t, weaveSrc)
	_, opts := f.choiceAt()
	if len(opts) != 2 {
		t.Fatalf("опций %d, ожидалось 2", len(opts))
	}

	var landings []int
	for _, o := range opts {
		lbl, _ := o["goto"].(string)
		if lbl == "" {
			t.Fatalf("сплетённая опция осталась без перехода: %v", o)
		}
		if _, ok := o["body"]; ok {
			t.Errorf("блок с прозой уехал в рантайм-поле body — там он исчезнет при загрузке сейва: %v", o)
		}
		says, landed := f.run(f.labels[lbl] + 1)
		if len(says) != 1 {
			t.Fatalf("ветка %s дала реплики %v, ожидалась ровно одна", lbl, says)
		}
		landings = append(landings, landed)
	}

	if landings[0] != landings[1] {
		t.Fatalf("ветки сошлись в разные точки (%d и %d) — схождения нет", landings[0], landings[1])
	}
	tail, _ := f.run(landings[0] + 1)
	if len(tail) != 1 || tail[0] != "Дальше идём вместе." {
		t.Errorf("после схождения ожидался общий текст, получено %v", tail)
	}
}

// Ветки должны исполнять РАЗНУЮ прозу: тест выше поймал бы и вырожденный
// случай, где обе метки ведут в один блок.
func TestWeaveBranchesAreNotTheSameBlock(t *testing.T) {
	f := newFlow(t, weaveSrc)
	_, opts := f.choiceAt()
	a, _ := f.run(f.labels[opts[0]["goto"].(string)] + 1)
	b, _ := f.run(f.labels[opts[1]["goto"].(string)] + 1)
	if strings.Join(a, "|") == strings.Join(b, "|") {
		t.Fatalf("обе ветки исполняют одно и то же: %v", a)
	}
}

// Развилка работает в ОБЕ стороны: блок из set/inc остаётся рантайм-телом и не
// стоит ни одной метки. Иначе конструкция чинила бы многословность выбора и
// тут же возвращала её другим путём.
func TestBodySafeBlockStillRidesAsBodyWithNoLabels(t *testing.T) {
	f := newFlow(t, "scene t\n- Взять -> дальше {\n  gold = gold + 1\n}\n:дальше\nтекст\n-> __end\n")
	_, opts := f.choiceAt()
	body, ok := opts[0]["body"].([]any)
	if !ok || len(body) == 0 {
		t.Fatalf("безопасный блок перестал быть body: %v", opts[0])
	}
	for id := range f.labels {
		if strings.HasPrefix(id, "__weave") || strings.HasPrefix(id, "__wend") {
			t.Errorf("на блоке из set/inc сминтована метка %q — плетение сработало там, где не нужно", id)
		}
	}
}

// Стрелка и блок вместе: блок исполняется, потом управление уходит ПО СТРЕЛКЕ,
// а не в схождение — иначе явный переход, написанный автором, молча терялся бы.
func TestWeaveWithAnArrowJumpsToTheArrowNotTheConvergence(t *testing.T) {
	f := newFlow(t, "scene t\n- Уйти -> финал {\n  Ты уходишь.\n}\n- Остаться {\n  Ты остаёшься.\n}\nобщий хвост\n-> __end\n:финал\nконец\n-> __end\n")
	_, opts := f.choiceAt()
	says, landed := f.run(f.labels[opts[0]["goto"].(string)] + 1)
	if len(says) != 1 || says[0] != "Ты уходишь." {
		t.Fatalf("блок при стрелке не исполнился: %v", says)
	}
	if got := f.script[landed]["id"]; got != "финал" {
		t.Errorf("ушли в %v, а стрелка вела в «финал»", got)
	}
}

// ЗАЧЕМ переход пишется сразу за choice. Опции нужна стрелка ИЛИ блок —
// провалиться просто так язык не даёт. Но блок из set/inc без стрелки стрелки
// не имеет: он отрабатывает как рантайм-body, и управление продолжается с
// команды ЗА выбором. Если в том же выборе есть сплетённая ветка, эта команда —
// уже начало чужой ветки. Переход в схождение закрывает ровно этот случай, и
// это самый тихий из возможных багов: тело исполнилось, игрок поехал не туда.
func TestBodyOnlyOptionLandsOnTheConvergenceNotTheFirstBranch(t *testing.T) {
	f := newFlow(t, "scene t\n- Взять {\n  gold = gold + 1\n}\n- Ответить {\n  Ты отвечаешь.\n}\nхвост\n-> __end\n")
	at, opts := f.choiceAt()
	if _, ok := opts[0]["body"]; !ok {
		t.Fatalf("блок из set перестал быть body: %v", opts[0])
	}
	says, landed := f.run(at + 1) // путь опции без стрелки: команда за выбором
	if len(says) != 0 {
		t.Fatalf("опция с body въехала в чужую прозу: %v", says)
	}
	tail, _ := f.run(landed + 1)
	if len(tail) == 0 || tail[0] != "хвост" {
		t.Errorf("опция с body пришла не в схождение, а в %v", tail)
	}
}

// Вложенность: выбор внутри сплетённой ветки. Плоский сбор блока закончил бы
// внешний блок на ВНУТРЕННЕЙ `}` и молча превратил его остаток в скрипт.
func TestWeaveHandlesAChoiceInsideAWovenBlock(t *testing.T) {
	src := "scene t\n- Спросить {\n  Ты спрашиваешь.\n  - Резко {\n    Резко.\n  }\n  - Мягко {\n    Мягко.\n  }\n}\nхвост\n-> __end\n"
	f := newFlow(t, src)
	at, opts := f.choiceAt()
	if len(opts) != 1 {
		t.Fatalf("внешний выбор разобран как %d опций — внутренние опции утекли наружу", len(opts))
	}
	_ = at
	if _, ok := opts[0]["body"]; ok {
		t.Error("блок с вложенным выбором уехал в body")
	}
}

// Минтованные имена — часть формата сейва (якорь = ближайшая метка сверху).
// Правка в другом месте главы не должна их переименовывать.
func TestWeaveNamesSurviveAnEditElsewhere(t *testing.T) {
	before := newFlow(t, "scene t\n:глава\n- Ударить {\n  Бьёшь.\n}\nхвост\n-> __end\n")
	after := newFlow(t, "scene t\n:пролог\nдобавленная строка\n-> глава\n:глава\n- Ударить {\n  Бьёшь.\n}\nхвост\n-> __end\n")
	pick := func(f *flow) string {
		for id := range f.labels {
			if strings.HasPrefix(id, "__weave") {
				return id
			}
		}
		f.t.Fatal("метка ветки не сминтована")
		return ""
	}
	if a, b := pick(before), pick(after); a != b {
		t.Errorf("вставка сцены выше переименовала ветку: %q → %q — это сдвигает якорь каждого сейва внутри неё", a, b)
	}
}
