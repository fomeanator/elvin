package lvn

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// КОНТРАКТ ДИСПЕТЧЕРИЗАЦИИ ПРОВЕРЯЕТСЯ У КАЖДОЙ РЕАЛИЗАЦИИ, а не у одной.
//
// Правило языка одно на все рантаймы: операцию ПОТОКА плеер потребляет сам,
// ПОСТАНОВОЧНУЮ пересылает сцене дословно. В C# это стережёт
// OpDispatchContractTests — по одной пробе на каждую строку таблицы владения,
// и он же падает, если для новой операции пробу забыли завести.
//
// У браузерного плеера такого стража не было. Корпус его проверяет, но лишь на
// тех операциях, которые в корпусе встречаются: из тридцати восьми — восемнадцать.
// Оставшиеся двадцать (anim, audio, camera, fx, particles, portal, save,
// wardrobe_show и прочая постановка) не проходили через core.js НИ РАЗУ. Пока
// его default-ветка пересылает всё подряд, это верно случайно; первая же
// попытка «обработать» такую операцию внутри плеера пройдёт незамеченной.
//
// Проба намеренно тупая: один документ, одна команда, вопрос ровно один —
// ушла она на сцену или нет.
func TestBrowserPlayerDispatchesLikeTheTable(t *testing.T) {
	node := requireNode(t, "контракт диспетчеризации браузерного плеера")
	root := repoRoot(t)
	core := filepath.Join(root, filepath.FromSlash("panel/public/play/core.js"))
	runner := filepath.Join(root, filepath.FromSlash("conformance/dispatch-probe.mjs"))
	for _, f := range []string{core, runner} {
		if _, err := os.Stat(f); err != nil {
			t.Fatalf("%s не найден (%v)", f, err)
		}
	}

	raw, err := os.ReadFile(filepath.Join(root, "conformance", "ops-owners.json"))
	if err != nil {
		t.Fatalf("таблица владения: %v", err)
	}
	var table struct {
		Ops map[string]struct {
			Owner  string `json:"owner"`
			CSharp string `json:"csharp"`
		} `json:"ops"`
	}
	if err := json.Unmarshal(raw, &table); err != nil {
		t.Fatalf("таблица владения: %v", err)
	}

	// Документ на одну команду: label держит поток на месте, проба идёт следом.
	type probe struct {
		Op     string           `json:"op"`
		Script []map[string]any `json:"script"`
	}
	var probes []probe
	var names []string
	for op := range table.Ops {
		names = append(names, op)
	}
	sort.Strings(names)
	for _, op := range names {
		probes = append(probes, probe{Op: op, Script: []map[string]any{{"op": op}}})
	}

	tmp, err := os.CreateTemp("", "lvn-dispatch-*.json")
	if err != nil {
		t.Fatalf("временный файл: %v", err)
	}
	defer os.Remove(tmp.Name())
	if err := json.NewEncoder(tmp).Encode(probes); err != nil {
		t.Fatalf("запись проб: %v", err)
	}
	tmp.Close()

	cmd := exec.Command(node, runner, core, tmp.Name())
	cmd.Dir = root
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("прогон проб не удался: %v\n%s", err, out)
	}
	var got []struct {
		Op        string `json:"op"`
		Forwarded bool   `json:"forwarded"`
		Stop      string `json:"stop"`
		Error     string `json:"error"`
	}
	if err := json.Unmarshal(out, &got); err != nil {
		t.Fatalf("ответ раннера: %v\n%s", err, out)
	}

	var bad []string
	for _, g := range got {
		row := table.Ops[g.Op]
		if g.Error != "" {
			bad = append(bad, g.Op+": плеер упал — "+g.Error)
			continue
		}
		switch row.CSharp {
		case "player":
			// Поток: плеер обязан обработать сам, на сцену такое не уходит.
			if g.Forwarded {
				bad = append(bad, g.Op+": операция потока ушла на сцену")
			}
		case "player+stage":
			// РАЗНАЯ ПОДАЧА ОДНОЙ ОПЕРАЦИИ ЗАКОННА. Ввод и ожидание C# рисует
			// СЦЕНОЙ (иначе оверлей некому нарисовать), а браузерный плеер
			// отдаёт СОБЫТИЕМ — playground рисует их сам. Требовать пересылки
			// значило бы объявить чужую верную подачу ошибкой. Нельзя другое:
			// чтобы операция пропала совсем — ни сцене, ни событием.
			if !g.Forwarded && g.Stop == "" {
				bad = append(bad, g.Op+": пропала — ни на сцену, ни событием")
			}
		default:
			// Постановка: плеер её не трактует, а пересылает дословно.
			if !g.Forwarded {
				bad = append(bad, g.Op+": постановочная операция не дошла до сцены — рисовать нечем")
			}
		}
	}
	sort.Strings(bad)
	if len(bad) > 0 {
		t.Fatalf("браузерный плеер расходится с таблицей владения:\n  %s\n"+
			"правило одно на все рантаймы: поток потребляем, постановку пересылаем дословно",
			strings.Join(bad, "\n  "))
	}
}
