package lvn

import (
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// КОРПУС ПРОВЕРЯЕТ ВСЕХ, КТО ИГРАЕТ.
//
// Язык исполняют трое: C#-рантайм (приложение), Go (обход и проверки) и
// `expr.js` — вычислитель браузерного playground. Корпус `conformance/cases`
// объявлял себя только для C# (`"runtimes": ["csharp"]`), поэтому третья
// реализация не сверялась ни с кем: её держал в узде комментарий «the same
// surface the engine's LvnExpression covers».
//
// Сверка списков функций ничего не доказывает — их у обоих ровно тридцать, и
// имена совпадают. Расходятся не имена, а ПОВЕДЕНИЕ: что считать истиной, как
// сравнивать строку с числом, во что превращается неизвестная переменная. Для
// автора, который пробует новеллу в браузере, а потом играет её в приложении,
// это разница между «работает» и «у меня по-другому».
//
// Поэтому здесь корпус прогоняется через настоящий `expr.js` в node. Нет node —
// тест пропускается: это машина без браузерного тракта, а не поломка.
func TestBrowserExpressionsAgreeWithTheCorpus(t *testing.T) {
	node, err := exec.LookPath("node")
	if err != nil {
		t.Skip("node не установлен — браузерный вычислитель не проверяется на этой машине")
	}
	root := repoRoot(t)
	exprJS := filepath.Join(root, filepath.FromSlash("server/website/play/expr.js"))
	if _, err := os.Stat(exprJS); err != nil {
		t.Skipf("expr.js не найден: %v", err)
	}

	type expectation struct {
		Vars      map[string]any `json:"vars"`
		ExprTrue  []string       `json:"expr_true"`
		ExprFalse []string       `json:"expr_false"`
	}
	type doc struct {
		Script []map[string]any `json:"script"`
	}
	type kase struct {
		ID       string      `json:"id"`
		Runtimes []string    `json:"runtimes"`
		Doc      doc         `json:"doc"`
		Expect   expectation `json:"expect"`
	}

	files, err := filepath.Glob(filepath.Join(root, "conformance", "cases", "*.json"))
	if err != nil || len(files) == 0 {
		t.Fatalf("корпус не найден: %v", err)
	}

	// Состояние случая приходит ДВУМЯ путями: готовым набором `expect.vars`
	// или скриптом, который его создаёт (`set l expr="list(2,2,3)"`). Второй
	// путь пришлось учесть отдельно: подставив пустые переменные, тест сначала
	// «нашёл» двадцать пять расхождений там, где расходился он сам.
	type probe struct {
		Case   string           `json:"case"`
		Expr   string           `json:"expr"`
		Want   bool             `json:"want"`
		Vars   any              `json:"vars"`
		Script []map[string]any `json:"script"`
	}
	var probes []probe
	skipped := 0
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		var k kase
		if json.Unmarshal(raw, &k) != nil {
			continue
		}
		vars := k.Expect.Vars
		if vars == nil {
			vars = map[string]any{}
		}
		// ЧТО ЭТОТ ТЕСТ МЕРЯЕТ — только вычисление выражений. Если состояние
		// случая рождается ПОТОКОМ (goto перепрыгивает часть set, call/return
		// уводят и возвращают) или зависит от чужих правил (`default` — это
		// Чтец «да-нет»), то, исполняя скрипт линейно, тест мерил бы СВОЮ
		// модель плеера, а не браузерный вычислитель. Такие случаи честнее
		// пропустить, чем зачесть расхождением: ровно на них первая версия
		// «нашла» десять несуществующих ошибок.
		// КОРПУС САМ ГОВОРИТ, КОГО ПРОВЕРЯЕТ. Случай, обязательный для
		// браузера, помечен «js» в runtimes; остальные тест не трогает — не
		// потому что они неважны, а потому что их состояние рождается потоком
		// (goto перепрыгивает set, call уводит и возвращает) или правилами
		// соседних домов (`default` — это Чтец «да-нет»). Исполняя такой
		// скрипт линейно, тест мерил бы СВОЮ модель плеера: ровно так первая
		// версия «нашла» десять несуществующих ошибок.
		declared := false
		for _, r := range k.Runtimes {
			if r == "js" {
				declared = true
			}
		}
		if !declared || scriptNeedsARealPlayer(k.Doc.Script) {
			skipped++
			continue
		}
		for _, e := range k.Expect.ExprTrue {
			probes = append(probes, probe{k.ID, e, true, vars, k.Doc.Script})
		}
		for _, e := range k.Expect.ExprFalse {
			probes = append(probes, probe{k.ID, e, false, vars, k.Doc.Script})
		}
	}
	if len(probes) == 0 {
		t.Fatal("в корпусе не нашлось ни одного выражения — разбор сломался, а не корпус")
	}

	payload, err := json.Marshal(probes)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	inPath := filepath.Join(dir, "probes.json")
	if err := os.WriteFile(inPath, payload, 0o644); err != nil {
		t.Fatal(err)
	}
	script := filepath.Join(dir, "run.mjs")
	src := fmt.Sprintf(`
import { readFileSync } from 'node:fs';
import { evalBool, evalExpr } from %q;
const probes = JSON.parse(readFileSync(%q, 'utf8'));

// Состояние случая: сперва объявленные переменные, потом то, что создаёт сам
// скрипт (set/inc) — ровно как их применяет плеер.
function stateOf(p) {
  const vars = JSON.parse(JSON.stringify(p.vars || {}));
  for (const c of (p.script || [])) {
    const op = c.op, key = c.key;
    if (!key || (op !== 'set' && op !== 'inc')) continue;
    if (op === 'set' && c.default && vars[key] !== undefined) continue;
    const value = op === 'inc'
      ? ((Number.isFinite(Number(vars[key])) ? Number(vars[key]) : 0)
         + (c.by === undefined ? 1 : Number(evalExpr(String(c.by), vars))))
      : (c.expr !== undefined ? evalExpr(String(c.expr), vars) : c.value);
    if (key.includes('.')) {
      const [root, ...rest] = key.split('.');
      let node = (vars[root] = vars[root] || {});
      while (rest.length > 1) node = (node[rest.shift()] ||= {});
      node[rest[0]] = value;
    } else {
      vars[key] = value;
    }
  }
  return vars;
}

const bad = [];
for (const p of probes) {
  let got, err = null;
  try { got = evalBool(p.expr, stateOf(p)); } catch (e) { err = String(e && e.message || e); }
  if (err !== null || got !== p.want) bad.push({ case: p.case, expr: p.expr, want: p.want, got: got ?? null, err });
}
process.stdout.write(JSON.stringify(bad));
`, exprJS, inPath)
	if err := os.WriteFile(script, []byte(src), 0o644); err != nil {
		t.Fatal(err)
	}

	out, err := exec.Command(node, script).CombinedOutput()
	if err != nil {
		t.Fatalf("node не смог прогнать корпус: %v\n%s", err, out)
	}
	var bad []struct {
		Case string `json:"case"`
		Expr string `json:"expr"`
		Want bool   `json:"want"`
		Got  *bool  `json:"got"`
		Err  string `json:"err"`
	}
	if err := json.Unmarshal(out, &bad); err != nil {
		t.Fatalf("непонятный ответ node: %v\n%s", err, out)
	}

	t.Logf("сверено %d выражений; %d случая(ев) пропущено — их состояние рождается потоком или правилами других домов",
		len(probes), skipped)

	if len(bad) > 0 {
		var lines []string
		for _, b := range bad {
			got := "ошибка: " + b.Err
			if b.Got != nil {
				got = fmt.Sprintf("%v", *b.Got)
			}
			lines = append(lines, fmt.Sprintf("%s: %q — корпус ждёт %v, браузер дал %s", b.Case, b.Expr, b.Want, got))
		}
		sort.Strings(lines)
		t.Fatalf("браузерный вычислитель разошёлся с корпусом (%d из %d):\n  %s\n\n"+
			"Автор пробует новеллу в playground, а играет её в приложении: расхождение здесь — "+
			"это «у меня работало по-другому». Правьте server/website/play/expr.js под корпус, "+
			"а не корпус под него: источник правды — язык, а не одна из его реализаций.",
			len(bad), len(probes), strings.Join(lines, "\n  "))
	}
}

// scriptNeedsARealPlayer: состояние такого случая нельзя воспроизвести линейным
// проходом по set/inc — нужен настоящий плеер с переходами либо правила
// соседних домов (Чтец «да-нет» для `default`).
func scriptNeedsARealPlayer(script []map[string]any) bool {
	for _, c := range script {
		switch fmt.Sprint(c["op"]) {
		case "goto", "call", "return", "if", "choice", "label":
			return true
		}
		if _, ok := c["default"]; ok {
			return true
		}
	}
	return false
}
