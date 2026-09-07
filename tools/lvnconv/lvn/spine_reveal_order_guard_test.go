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
	// Подгонка обязана сообщать о своей готовности — иначе показу нечего ждать.
	if !strings.Contains(fit, "Fitted") {
		t.Fatal("LvnSpineFit больше не сообщает Fitted — показу нечего ждать, гонка вернётся")
	}
	if !regexp.MustCompile(`Fitted\s*=\s*true`).MatchString(fit) {
		t.Error("LvnSpineFit нигде не выставляет Fitted=true — флаг всегда ложь, показ застрянет невидимым")
	}

	fader := read(t, filepath.Join(base, "LvnSpineFader.cs"))
	// В ветке РЕАЛЬНОГО показа (после сброса _warmLeft = 0) подъём альфы к
	// единице обязан стоять ПОСЛЕ проверки готовности. Вырезаем эту ветку и
	// смотрим, что подъёму предшествует гейт.
	i := strings.Index(fader, "_warmLeft = 0")
	if i < 0 {
		t.Fatal("в LvnSpineFader не нашлась ветка реального показа (_warmLeft = 0) — страж смотрит не туда")
	}
	branch := fader[i:]
	up := strings.Index(branch, "MoveTowards")
	if up < 0 {
		t.Fatal("в ветке показа нет подъёма альфы (MoveTowards) — страж смотрит не туда")
	}
	before := branch[:up]
	gate := strings.Contains(before, "ReadyToReveal") ||
		strings.Contains(before, "Fitted")
	if !gate {
		t.Error("показ поднимает альфу к 1, не дождавшись подгонки: скелет мелькнёт раздутыми " +
			"прямоугольниками (гонка «через раз»). Верните гейт ReadyToReveal/Fitted перед MoveTowards")
	}
	// ПОВТОРНЫЙ показ обязан ждать подгонку так же, как первый. После
	// скрытия меш отбрасывается и пересобирается с MeshScale=1, а Fitted
	// держит прошлый успех — без перевзвода показ проходит гейт мгновенно и
	// снова мелькает раздутым (замер 07.09: «после выхода в меню»). Значит
	// Fit умеет Rearm, а показ его зовёт.
	if !strings.Contains(fit, "Rearm") {
		t.Error("LvnSpineFit не умеет Rearm — повторный показ не сможет заново дождаться подгонки")
	}
	if !strings.Contains(fader, "Rearm") {
		t.Error("Fader нигде не перевзводит подгонку (Rearm): первый показ чист, а повторный " +
			"после скрытия мелькнёт раздутым — MeshScale сброшен, а Fitted держит прошлый раз")
	}

	// Гейт обязан рисовать почти-невидимый меш, пока ждёт: только живое
	// рисование качает MeshScale, иначе подгонка не наступит никогда.
	if !strings.Contains(before, "WarmAlpha") {
		t.Error("во время ожидания подгонки альфа не держится на WarmAlpha — меш не рисуется, " +
			"MeshScale не раскачается, и ожидание станет вечным")
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
