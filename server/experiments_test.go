package main

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"
)

func expSvc(t *testing.T, exps []experiment) *ExperimentsService {
	t.Helper()
	dir := t.TempDir()
	path := filepath.Join(dir, "experiments.json")
	data, _ := json.Marshal(exps)
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	return NewExperimentsService(path, nil, "t")
}

// Доля трафика — то, ради чего конфиг вынесен на сервер: «вариант Б на 10%»
// должно означать десять процентов, а не половину.
func TestExperimentRespectsWeights(t *testing.T) {
	s := expSvc(t, []experiment{{
		Name: "первая_сцена", Enabled: true,
		Variants: []experimentVariant{{ID: "a", Weight: 90}, {ID: "b", Weight: 10}},
	}})
	counts := map[string]int{}
	const n = 4000
	for i := 0; i < n; i++ {
		counts[s.assign("u_" + itoa(i))["первая_сцена"]]++
	}
	share := float64(counts["b"]) / n
	if math.Abs(share-0.10) > 0.02 {
		t.Errorf("доля варианта b = %.3f, ожидалось ≈0.10 (%v)", share, counts)
	}
}

// Назначение обязано быть устойчивым: игрок не должен менять группу от
// запроса к запросу — иначе история переписывается у него под руками.
func TestExperimentAssignmentIsStable(t *testing.T) {
	s := expSvc(t, []experiment{{
		Name: "x", Enabled: true,
		Variants: []experimentVariant{{ID: "a"}, {ID: "b"}},
	}})
	first := s.assign("игрок")["x"]
	for i := 0; i < 50; i++ {
		if got := s.assign("игрок")["x"]; got != first {
			t.Fatalf("группа поехала: %q вместо %q", got, first)
		}
	}
}

// Внутри слоя игрок участвует ровно в ОДНОМ эксперименте: иначе два теста
// накладываются и эффект одного неотличим от эффекта другого.
func TestExperimentLayerIsMutuallyExclusive(t *testing.T) {
	s := expSvc(t, []experiment{
		{Name: "цена", Layer: "экономика", Enabled: true,
			Variants: []experimentVariant{{ID: "a"}, {ID: "b"}}},
		{Name: "витрина", Layer: "экономика", Enabled: true,
			Variants: []experimentVariant{{ID: "a"}, {ID: "b"}}},
		{Name: "сцена", Layer: "сюжет", Enabled: true,
			Variants: []experimentVariant{{ID: "a"}, {ID: "b"}}},
	})
	both, layers := 0, 0
	for i := 0; i < 500; i++ {
		a := s.assign("u_" + itoa(i))
		if _, ok := a["цена"]; ok {
			if _, ok2 := a["витрина"]; ok2 {
				both++
			}
		}
		// Слой «сюжет» независим — в нём игрок должен быть всегда.
		if _, ok := a["сцена"]; ok {
			layers++
		}
	}
	if both != 0 {
		t.Errorf("%d игроков попали в два эксперимента одного слоя", both)
	}
	if layers != 500 {
		t.Errorf("эксперимент другого слоя должен доставаться всем: %d из 500", layers)
	}
}

// Выключенный эксперимент не «выключает сцену»: все идут первым вариантом,
// история продолжает играться.
func TestExperimentDisabledFallsToFirst(t *testing.T) {
	s := expSvc(t, []experiment{{
		Name: "x", Enabled: false,
		Variants: []experimentVariant{{ID: "как_было"}, {ID: "новое"}},
	}})
	for i := 0; i < 100; i++ {
		if got := s.assign("u_" + itoa(i))["x"]; got != "как_было" {
			t.Fatalf("выключенный эксперимент отдал %q", got)
		}
	}
}

// Самая дорогая ошибка: поменять доли и не поднять версию. Часть игроков молча
// переедет между группами, старые и новые данные смешаются, а отчёт будет
// выглядеть нормально.
func TestExperimentRefusesSilentWeightChange(t *testing.T) {
	prev := []experiment{{Name: "x", Version: 1, Enabled: true,
		Variants: []experimentVariant{{ID: "a", Weight: 50}, {ID: "b", Weight: 50}}}}
	same := []experiment{{Name: "x", Version: 1, Enabled: true,
		Variants: []experimentVariant{{ID: "a", Weight: 90}, {ID: "b", Weight: 10}}}}
	if err := validateExperiments(same, prev); err == nil {
		t.Error("смена долей без версии принята — данные испортятся молча")
	}
	bumped := []experiment{{Name: "x", Version: 2, Enabled: true,
		Variants: []experimentVariant{{ID: "a", Weight: 90}, {ID: "b", Weight: 10}}}}
	if err := validateExperiments(bumped, prev); err != nil {
		t.Errorf("с поднятой версией менять доли можно: %v", err)
	}
	// Версия входит в хеш: после её поднятия деление другое.
	e1, e2 := prev[0], bumped[0]
	moved := 0
	for i := 0; i < 200; i++ {
		u := "u_" + itoa(i)
		if e1.pick(u) != e2.pick(u) {
			moved++
		}
	}
	if moved == 0 {
		t.Error("после смены версии деление обязано стать другим")
	}
}

// Меньше двух вариантов — это не эксперимент.
func TestExperimentValidationBasics(t *testing.T) {
	bad := [][]experiment{
		{{Name: "", Variants: []experimentVariant{{ID: "a"}, {ID: "b"}}}},
		{{Name: "x", Variants: []experimentVariant{{ID: "a"}}}},
		{{Name: "x", Variants: []experimentVariant{{ID: "a"}, {ID: "a"}}}},
		{{Name: "x", Variants: []experimentVariant{{ID: "a"}, {ID: "b", Weight: -1}}}},
	}
	for i, b := range bad {
		if err := validateExperiments(b, nil); err == nil {
			t.Errorf("случай %d принят, а не должен", i)
		}
	}
}
