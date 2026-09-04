package main

import (
	"os"
	"regexp"
	"strings"
	"testing"
)

// РЕЖИМ, ОТКЛЮЧАЮЩИЙ ПРОВЕРКУ, ОБЯЗАН О СЕБЕ КРИЧАТЬ.
//
// Самые громкие флаги были самыми тихими. Сервер предупреждал про открытый
// /v1/wallet/earn и про непривязанный bundle id — а про два режима, снимающих
// доверие ЦЕЛИКОМ, молчал. Хуже: -auth-dev в том блоке уже участвовал, но лишь
// чтобы ПРИГЛУШИТЬ меньшее замечание.
//
// Замерено живым сервером:
//
//	-iap-dev    один и тот же чек трижды → 500 + 500 + 500 = 1500
//	-auth-dev   любая строка становится личностью, одна и та же — всегда одним
//	            и тем же игроком: узнал чужую строку — стал этим человеком
//
// Страж ищет флаги ПО ПРИЗНАКУ, а не по списку имён: список стареет молча (за
// ночь это уже случалось с числом тестов в комментарии CI и с копией граммати-
// ки). Признак — сама справка флага: «test builds only», «never production»,
// «test mode». Завёл такой флаг — назови его в строке запуска.
func TestFlagsThatDisableChecksAnnounceThemselves(t *testing.T) {
	raw, err := os.ReadFile("main.go")
	if err != nil {
		t.Fatalf("запуск сервера не прочитан: %v", err)
	}
	src := string(raw)

	// Все объявления флагов вместе с их справкой.
	decl := regexp.MustCompile(`flag\.\w+\("([a-z0-9-]+)",[^,]*,\s*"([^"]*)"\)`)
	// Строки, которые сервер печатает при запуске.
	logged := strings.Builder{}
	for _, m := range regexp.MustCompile(`log\.Printf\("([^"]*)"`).FindAllStringSubmatch(src, -1) {
		logged.WriteString(m[1])
		logged.WriteString("\n")
	}
	announced := logged.String()

	risky := regexp.MustCompile(`(?i)test builds?|never production|test.mode`)
	seen, checked := 0, 0
	for _, m := range decl.FindAllStringSubmatch(src, -1) {
		seen++
		name, help := m[1], m[2]
		if !risky.MatchString(help) {
			continue
		}
		checked++
		if !strings.Contains(announced, name) {
			t.Errorf("флаг -%s снимает проверку (%q), но при запуске о нём ни слова — "+
				"на проде это заметят по счетам, а не по логу", name, help)
		}
	}

	// Пороги на ПРОСМОТРЕННОЕ: ноль разобранных флагов означал бы, что разбор
	// промахнулся, а «нарушений нет» — пустоту, а не порядок.
	if seen < 10 {
		t.Fatalf("разобрано всего %d флагов — якорь разбора промахнулся", seen)
	}
	if checked < 2 {
		t.Fatalf("опасных флагов найдено %d — признак перестал их узнавать", checked)
	}
}
