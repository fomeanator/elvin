package main

import (
	"testing"
)

// Разбор — на сервере, поэтому он обязан принимать всё, чем реально размечают
// ссылки: полный адрес, голую строку параметров (так отдаёт Play Install
// Referrer), сокращённые имена и русские значения.
func TestParseAttributionShapes(t *testing.T) {
	cases := []struct {
		name, raw string
		want      playerAttribution
	}{
		{"полный адрес",
			"https://timeromance.ru/?utm_source=telegram&utm_medium=post&utm_campaign=aug_beta",
			playerAttribution{Source: "telegram", Medium: "post", Campaign: "aug_beta"}},
		{"голая строка меток",
			"utm_source=vk&utm_campaign=test",
			playerAttribution{Source: "vk", Campaign: "test"}},
		{"своя схема с якорем",
			"lvn://open?utm_source=tg&ref=aram#frag",
			playerAttribution{Source: "tg", Ref: "aram"}},
		{"сокращения",
			"?src=yandex&campaign=cpc1",
			playerAttribution{Source: "yandex", Campaign: "cpc1"}},
		{"кириллица и пробелы",
			"https://x/?utm_source=%D1%82%D0%B5%D0%BB%D0%B5%D0%B3%D0%B0&utm_campaign=%20%D0%BB%D0%B5%D1%82%D0%BE%20",
			playerAttribution{Source: "телега", Campaign: "лето"}},
		{"без меток вовсе",
			"https://timeromance.ru/", playerAttribution{}},
	}
	for _, c := range cases {
		got := parseAttribution(c.raw)
		if got.Source != c.want.Source || got.Medium != c.want.Medium ||
			got.Campaign != c.want.Campaign || got.Ref != c.want.Ref {
			t.Errorf("%s: получено %+v, ожидалось %+v", c.name, got, c.want)
		}
		// Оригинал сохраняется всегда: разметку пишут люди, и то, чего мы
		// сегодня не разобрали, завтра ещё можно прочитать.
		if c.raw != "" && got.Raw == "" {
			t.Errorf("%s: исходная строка потеряна", c.name)
		}
	}
}

// Битую строку не выбрасываем — иначе теряется единственный след кампании.
func TestParseAttributionKeepsBrokenRaw(t *testing.T) {
	got := parseAttribution("https://x/?utm_source=%zz&broken")
	if got.Raw == "" {
		t.Error("сломанная разметка обязана сохраниться целиком")
	}
}

// Главное правило: первое касание неизменяемо. Переустановка по прямой ссылке
// не должна задним числом обнулять кампанию, которая привела игрока.
func TestAttributionFirstTouchIsImmutable(t *testing.T) {
	s, err := NewAuthService(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	s.mu.Lock()
	s.users["u1"] = &authUser{Created: "2026-08-01T00:00:00Z"}
	s.mu.Unlock()

	first, wrote := s.SetAttributionFirstTouch("u1", parseAttribution("?utm_campaign=реклама_март"))
	if !wrote || first.Campaign != "реклама_март" {
		t.Fatalf("первая запись не прошла: %+v %v", first, wrote)
	}
	// Вторая установка, прямая ссылка с другой кампанией.
	again, wrote := s.SetAttributionFirstTouch("u1", parseAttribution("?utm_campaign=прямой_май"))
	if wrote {
		t.Error("канал переписан — результат сработавшей кампании обнулён задним числом")
	}
	if again.Campaign != "реклама_март" {
		t.Errorf("канал должен остаться первым: %+v", again)
	}
	if s.AttributionOf("u1").Campaign != "реклама_март" {
		t.Error("на диске тоже должен остаться первый канал")
	}
}

// Запуск без меток НЕ занимает место первого касания: иначе первый же вход
// напрямую навсегда закроет игроку возможность быть атрибутированным.
func TestAttributionEmptyDoesNotClaimFirstTouch(t *testing.T) {
	s, err := NewAuthService(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	s.mu.Lock()
	s.users["u1"] = &authUser{Created: "2026-08-01T00:00:00Z"}
	s.mu.Unlock()

	if _, wrote := s.SetAttributionFirstTouch("u1", parseAttribution("https://timeromance.ru/")); wrote {
		t.Error("пустая строка заняла место первого касания")
	}
	got, wrote := s.SetAttributionFirstTouch("u1", parseAttribution("?utm_campaign=позже"))
	if !wrote || got.Campaign != "позже" {
		t.Errorf("после пустого запуска атрибуция обязана записаться: %+v %v", got, wrote)
	}
}

// В отчёте канал называется так, чтобы по нему можно было принять решение:
// вопрос «что окупилось» задают про кампанию, источник — её свойство.
func TestChannelNaming(t *testing.T) {
	cases := []struct {
		attr playerAttribution
		want string
	}{
		{playerAttribution{Source: "tg", Campaign: "aug"}, "tg/aug"},
		{playerAttribution{Campaign: "aug"}, "aug"},
		{playerAttribution{Source: "tg"}, "tg"},
		{playerAttribution{Ref: "aram"}, "aram"},
		{playerAttribution{}, ""},
	}
	for _, c := range cases {
		if got := c.attr.Channel(); got != c.want {
			t.Errorf("%+v → %q, ожидалось %q", c.attr, got, c.want)
		}
	}
}

// Сохранённый канал обязан пережить перезапуск сервера — это половина
// условия приёмки «переживает перезапуск приложения».
func TestAttributionSurvivesRestart(t *testing.T) {
	dir := t.TempDir()
	s, err := NewAuthService(dir)
	if err != nil {
		t.Fatal(err)
	}
	s.mu.Lock()
	s.users["u1"] = &authUser{Created: "2026-08-01T00:00:00Z"}
	s.mu.Unlock()
	if _, wrote := s.SetAttributionFirstTouch("u1", parseAttribution("?utm_source=tg&utm_campaign=test")); !wrote {
		t.Fatal("не записалось")
	}

	again, err := NewAuthService(dir)
	if err != nil {
		t.Fatal(err)
	}
	if got := again.AttributionOf("u1").Channel(); got != "tg/test" {
		t.Errorf("после перезапуска канал %q, ожидалось tg/test", got)
	}
	if again.Channels()["u1"] != "tg/test" {
		t.Error("карта каналов для отчётов не восстановилась")
	}
}
