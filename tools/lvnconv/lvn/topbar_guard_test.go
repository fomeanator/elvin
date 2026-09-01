package lvn

import (
	"fmt"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// СОСТАВ ВЕРХНЕГО БАРА РЕШАЕТСЯ В ОДНОМ МЕСТЕ.
//
// Поверхностей пять: полный ряд, ряд игровых кнопок, баблики валют, полоса
// прогресса и ловушка тапа. Видно их или нет — следствие ТРЁХ признаков разом
// (тишина воронки, игровой режим, развёрнут ли игровой бар), и решали про это
// три места, каждое со своим набором: тишина перечисляла пять поверхностей,
// смена режима — четыре, открытие и закрытие бара — по-своему в каждой ветке.
//
// Так и вышел живой дефект: выход из главы при ОТКРЫТОМ баре. Смена режима
// показывала верхний ряд, а через 200 мс конец анимации скрытия прятал его
// обратно и возвращал только в игре — в меню верхняя панель исчезала.
//
// Порог считает только УМОЛЧАНИЯ ПОСТРОЙКИ (элемент рождается скрытым). Любое
// решение о видимости обязано жить в ApplyBarVisibility.
func TestTopBarVisibilityHasOneAuthor(t *testing.T) {
	const budget = 4 // 01.09: четыре «рождается скрытым» при постройке

	root := repoRoot(t)
	path := filepath.Join(root, "unity", "Packages", "com.lvn.engine.shell", "Runtime", "LvnTopBar.cs")
	src := stripComments(string(mustRead(t, path)))
	surface := regexp.MustCompile(`_(row|gameRow|miniPills|miniProgress|tapCatcher)\.style\.display\s*=`)

	// Тело ApplyBarVisibility — единственное законное место решений.
	lines := strings.Split(src, "\n")
	inHome := false
	depth := 0
	count := 0
	var where []string
	for i, l := range lines {
		if strings.Contains(l, "private void ApplyBarVisibility()") {
			inHome, depth = true, 0
		}
		if inHome {
			depth += strings.Count(l, "{") - strings.Count(l, "}")
			if depth == 0 && strings.Contains(l, "}") {
				inHome = false
			}
			continue
		}
		if surface.MatchString(l) {
			count++
			where = append(where, fmt.Sprintf("LvnTopBar.cs:%d", i+1))
		}
	}
	atLeast(t, strings.Count(src, "_miniPills"), 3, "упоминаний поверхностей бара")
	if count > budget {
		t.Errorf("решений о видимости бара мимо ApplyBarVisibility: %d при пороге %d\n  %s\n\n"+
			"Видно поверхность или нет — следствие ТРЁХ признаков сразу (тишина, игра, развёрнут ли\n"+
			"бар). Перечисляя набор по месту, легко получить то, что уже было: в меню верхняя\n"+
			"панель исчезала, потому что конец анимации возвращал баблики только в игре.",
			count, budget, strings.Join(where, "\n  "))
	}
}

// ЛЕНТА ГАРДЕРОБА ВИДНА ПО КАРТОЧКАМ, А НЕ ПО ДАННЫМ.
//
// Правило стояло двумя написаниями: вкладка «Моё» считала показанное, обычная
// спрашивала длину списка ДО сборки. Ответы совпадали, но вопросы разные —
// «сколько вышло» и «сколько было данных». Разошлись бы в первый же день,
// когда сборка начнёт что-нибудь пропускать: лента показалась бы пустой
// полосой. Решение живёт в AdoptStripCards и спрашивает после сборки.
func TestWardrobeStripAsksAboutCards(t *testing.T) {
	root := repoRoot(t)
	path := filepath.Join(root, "unity", "Packages", "com.lvn.engine.shell", "Runtime", "WardrobeSheet.Strip.cs")
	src := stripComments(string(mustRead(t, path)))
	if !strings.Contains(src, "private void AdoptStripCards()") {
		t.Fatal("дома AdoptStripCards нет — якорь стража промахнулся")
	}
	n := strings.Count(src, "_strip.style.display")
	if n != 1 {
		t.Errorf("решений о видимости ленты: %d (ожидалось одно, в AdoptStripCards).\n\n"+
			"Спрашивать надо после сборки и про КАРТОЧКИ: «получилось ли что-нибудь», а не\n"+
			"«было ли из чего». Иначе лента показывается пустой полосой там, где данные были,\n"+
			"а карточек не вышло.", n)
	}
}

// ОБЕ ВЕТКИ ПЕРЕСБОРКИ ЛЕНТЫ КОНЧАЮТСЯ ПОДСВЕТКОЙ.
//
// `RebuildStrip` ветвится: «Моё» (лента из разных осей) и обычная вкладка.
// Подсветку рисует `StyleStrip`, и он ОБЕ ветки умеет — на «Моё» отмечает
// надетое, а не k-ю карточку. Но ветка «Моё» выходила раньше, чем до него
// доходило: после «Выбрать» лента оставалась серой, и надетое было ничем не
// отмечено. Живой скрин Ильи 01.09.
func TestBothStripBranchesStyle(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine.shell", "Runtime", "WardrobeSheet.Strip.cs"))))
	i := strings.Index(src, "RebuildStrip(")
	if i < 0 {
		t.Fatal("пересборки ленты нет — якорь стража промахнулся")
	}
	// Каждый `return;` внутри пересборки обязан иметь StyleStrip() выше себя
	// в пределах ветки: считаем, что вызовов подсветки не меньше, чем выходов.
	body := src[i:]
	if j := strings.Index(body, "\n        private "); j > 0 {
		body = body[:j]
	}
	returns := strings.Count(body, "return;")
	styles := strings.Count(body, "StyleStrip();")
	if styles < returns {
		t.Errorf("в пересборке ленты %d выходов и только %d вызовов подсветки.\n\n"+
			"Ветка, вышедшая без StyleStrip, оставляет ленту серой: надетое ничем не отмечено.\n"+
			"На «Моё» это заметнее всего — там подсветка и есть единственный ответ на вопрос\n"+
			"«что на мне сейчас».", returns, styles)
	}
}
