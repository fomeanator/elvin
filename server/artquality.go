package main

// Разрешение арта: единственный дефект, который не ловит ни один тест.
//
// Скрипт компилируется, ассет на месте, ошибок нет — а игрок видит мыло.
// Живой случай: в первом эпизоде Time Romance на сцену выходил портрет
// 209x229 (голова, аватарка из articy), движок ставил его во весь рост, и
// именно на этом кадре бросали игру чаще всего. Из 459 мелких картинок в
// показе у 455 нормальной версии не нашлось нигде: это не сбой конвейера, а
// не доехавший арт — но узнали мы об этом только через отчёт о выходах.
//
// Поэтому проверка живёт в страже: любой путь, которым контент попадает в
// игру (сохранение из студии, импорт из articy), теперь говорит про мыло
// вслух. Предупреждение, не ошибка — заглушки и иконки имеют право быть
// мелкими, а решать, стоит ли эпизод такого арта, автору.

import (
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"os"
	"strings"
)

// Пороги — по вертикали, потому что и фон, и актёр тянутся по высоте экрана.
// Телефон — это ~2400px в высоту при плотности 3x. Требовать столько нельзя
// (вес), но фон ниже 900 и персонаж ниже 700 на таком экране уже видно
// глазом: у первого плывут детали, у второго рвётся контур лица.
const (
	minBackgroundHeight = 900
	minActorHeight      = 700
)

func minArtHeight(op string) int {
	if op == "bg" {
		return minBackgroundHeight
	}
	return minActorHeight
}

func artKindName(op string) string {
	switch op {
	case "bg":
		return "фон"
	case "actor":
		return "персонаж"
	default:
		return "объект"
	}
}

// imageSize читает только заголовок файла: раскодировать картинку целиком ради
// двух чисел — это сотни мегабайт на импорте большой новеллы.
func imageSize(path string) (w, h int, ok bool) {
	if !isImagePath(path) {
		return 0, 0, false
	}
	f, err := os.Open(path)
	if err != nil {
		return 0, 0, false
	}
	defer f.Close()
	cfg, _, err := image.DecodeConfig(f)
	if err != nil {
		// Формат, который стандартная библиотека не знает (webp, ktx2), —
		// не повод ругаться: молчим, а не выдумываем размер.
		return 0, 0, false
	}
	return cfg.Width, cfg.Height, true
}

func isImagePath(path string) bool {
	switch strings.ToLower(path[strings.LastIndex(path, ".")+1:]) {
	case "png", "jpg", "jpeg", "gif":
		return true
	}
	return false
}
