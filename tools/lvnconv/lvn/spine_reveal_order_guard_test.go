package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// СКЕЛЕТ НЕ ПРОЯВЛЯЮТ, ПОКА ОН НЕ ПОДОГНАН.
//
// SkeletonGraphic.MeshScale устаканивается через кадр-два после сборки; до
// этого меш раздут ~в 100×. Показ (LvnSpineFader, альфа 0→1) и подгонка
// (LvnSpineFit, в LateUpdate по готовности MeshScale) — разные компоненты.
// Пока они шли независимо, кто успеет: успел MeshScale к первому видимому
// кадру — чисто, не успел — фигура проявлялась раздутыми прямоугольниками.
// Живой замер 07.09 с устройства: «иногда показывает, иногда нет», белые
// прямоугольники через раз.
//
// Починка связала их: Fader не поднимает альфу к 1, пока Fit не сообщил
// Fitted (ReadyToReveal). Проверить это EditMode-тестом нельзя — spine-unity
// в TestHost нет, Lvn.Engine.Spine там не компилируется (оттого у спайна и
// нет тестов). Поэтому связь стережёт СТРУКТУРНЫЙ страж по исходнику: если
// показ снова начнёт гнать альфу к единице, не дожидаясь подгонки, — красное.
func TestSpineRevealWaitsForFit(t *testing.T) {
	root := repoRoot(t)
	base := filepath.Join(root, "unity", "Packages", "com.lvn.engine.spine", "Runtime")
	fit := read(t, filepath.Join(base, "LvnSpineFit.cs"))

	// ПОКАЗ И ПОДГОНКА — ОДНА ЖЕЛЕЗКА. Раньше их было две (LvnSpineFader +
	// LvnSpineFit) с гейтом ReadyToReveal между ними, и за четыре захода это
	// давало «через раз»: скелет успевал мелькнуть раздутым в сто крат.
	// Теперь гонки нет ПО УСТРОЙСТВУ, и страж держит именно это устройство.

	// 1. Пока ждём устаканивания MeshScale, фигуру прячет МАСШТАБ, а не альфа:
	// погасив альфу, Canvas отсекает детей, меш не рисуется и MeshScale не
	// устаканивается никогда — фигура остаётся невидимой навсегда.
	if !regexp.MustCompile(`localScale\s*=\s*Vector3\.zero`).MatchString(fit) {
		t.Error("ожидание больше не прячет фигуру нулевым масштабом — либо вернулась гонка, " +
			"либо её гасят альфой, и тогда меш не соберётся вовсе")
	}

	// 2. В ТОТ ЖЕ миг, когда подгонка села, альфа ставится в ноль: кадр с
	// верным размером рисуется уже невидимым, вспышки во весь экран нет.
	fitted := strings.Index(fit, "TryFit(")
	if fitted < 0 {
		t.Fatal("в LvnSpineFit не нашлась сама подгонка (TryFit) — страж смотрит не туда")
	}
	after := fit[fitted:]
	if end := strings.Index(after, "return;"); end > 0 {
		after = after[:end]
	}
	if !regexp.MustCompile(`alpha\s*=\s*0f`).MatchString(after) {
		t.Error("после удачной подгонки альфа не обнуляется в том же кадре: " +
			"фигура вспыхнет верным размером до начала фейда")
	}

	// 3. Подъём альфы к единице живёт ОТДЕЛЬНО и только после подгонки —
	// иначе показывать будет нечего, кроме раздутого меша.
	up := strings.Index(fit, "MoveTowards")
	if up < 0 {
		t.Fatal("в LvnSpineFit нет подъёма альфы (MoveTowards) — страж смотрит не туда")
	}
	if !strings.Contains(fit[:up], "_fitted") {
		t.Error("подъём альфы стоит раньше проверки подгонки: гонка «через раз» вернётся")
	}

	// 4. Страховка на случай, когда подгонка не удаётся вовсе (нет холста или
	// границ): фигуру всё равно показывают, иначе она невидима навсегда.
	if !strings.Contains(fit, "FitWaitCap") {
		t.Error("страховка ожидания исчезла — неудачная подгонка оставит фигуру невидимой навсегда")
	}
}

func read(t *testing.T, path string) string {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("не читается %s: %v", path, err)
	}
	return string(b)
}
