package main

// Граница между игрой и авторской кухней.
//
// Список в toolingRel будут править: появится новый формат исходников, кто-то
// захочет добавить «.txt» целиком. Цена ошибки несимметрична: лишний файл в
// наборе — это трафик и лишняя тревога играющих, а ОТРЕЗАННЫЙ игровой файл —
// игра, которая офлайн не открывается. Поэтому обе стороны проверяются
// именами из живого каталога, а не выдуманными.

import "testing"

func TestToolingRelKnowsTheKitchen(t *testing.T) {
	kitchen := []string{
		"scripts/chapter-01.lvns",            // исходник главы
		"scripts/lvns_packages/duel.lvns",    // исходник пакета
		"manifest.json.bak-predeploy-014142", // бэкап деплоя с меткой времени
		"manifest.json.bak-basis-013551",     //   их в живом каталоге девять
		"cold.json.bak-20260724191900",       // бэкап рядом с настройками
		"kenney_toon-characters.zip",         // присланный архив ассетов
		"Knight Spine Animation Asset.zip",   //   с пробелами в имени
		"bg/room.psd",                        // исходник графики
		"scripts/chapter-01.lvn~",            // черновик редактора
		"README-studio.md",                   // заметка студии
		"bg/room.jpg.orig",                   // след слияния
		".DS_Store",                          // след файлового менеджера
		"bg/.DS_Store",
	}
	for _, rel := range kitchen {
		if !toolingRel(rel) {
			t.Errorf("%q сочтён игрой — уедет игроку в набор и будет тревожить его сменой версии", rel)
		}
	}

	game := []string{
		"scripts/chapter-01.lvn",     // то, что исполняет плеер
		"scripts/chapter-01.en.json", // каталог перевода
		"manifest.json",              // каталог игр
		"bg/room.jpg", "bg/room.png", "bg/room.webp",
		"bg/hero.atlas.txt", // атлас Spine — .txt, и это ИГРА
		"bg/hero.skel.bytes",
		"fonts/text.ttf", "fonts/text.otf",
		"audio/theme.ogg", "audio/step.wav", "audio/voice.mp3",
		"video/intro.mp4",
		"ext-grammar.json", "experiments.json", "daily-rewards.json",
		"bg/room@2k.jpg", "bg/room.ktx2", // производные: их отсекает другое правило
	}
	for _, rel := range game {
		if toolingRel(rel) {
			t.Errorf("%q сочтён кухней — игра без него офлайн не откроется", rel)
		}
	}
}
