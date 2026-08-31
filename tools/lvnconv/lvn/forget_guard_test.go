package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// Личное игрока обязано быть забываемым.
//
// Игрок вправе попросить «начать заново» и «удалить аккаунт», и оба обряда
// ведёт один дом — LvnForget. Пока список того, что стереть, жил в вызывающем
// коде, три хранилища из него выпали молча: галерея открытых кадров и
// прочитанные реплики не стирались НИКОГДА (методы сброса были написаны и не
// позваны), а миниатюры сейвов оставались PNG-файлами на диске.
//
// Промах здесь особенный: он не ломает игру и не виден в логе. Его видит ровно
// один человек — тот, кто попросил себя забыть, а игра помнит. Поэтому файл,
// который пишет личное в записную книжку устройства, обязан быть назван в
// LvnForget.cs: либо он забывается, либо там сказано, почему нет.
func TestPersonalDataIsForgettable(t *testing.T) {
	root := repoRoot(t)

	home := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "UI", "LvnForget.cs")
	forget, err := os.ReadFile(home)
	if err != nil {
		t.Fatalf("дом забвения не найден: %v", err)
	}
	known := string(forget)

	// Не личное: настройки устройства, служебные очереди и кэши. Каждое
	// исключение — с причиной, иначе список бы тихо разросся до «всё исключено».
	notPersonal := map[string]string{
		"LvnPrefs.cs":           "настройки устройства: громкость, язык, скорость текста — не то, что игра узнала об игроке",
		"LvnKeep.cs":            "сама записная книжка",
		"LvnForget.cs":          "дом забвения",
		"LvnExperiments.cs":     "раскладка экспериментов приходит с сервера и переживает игрока",
		"LvnLogShip.cs":         "очередь недоставленных логов: обезличена и уходит с ближайшей отправкой",
		"LvnBackend.cs":         "стирает свои ключи сам, в ответе на удаление аккаунта",
		"LvnStateStore.cs":      "хранилище переменных: забывается через LocalStateStore.Forget",
		"VnStage.Background.cs": "последний фон — подсказка для мгновенного показа, не история игрока",
		"VnStage.SaveLoad.cs":   "сейв команды скрипта живёт в слотах, которые забываются целиком",
		"ProgressVault.cs":      "регистрируется в забвении из оболочки (Engine не видит Shell)",
		"LvnProgress.cs":        "регистрируется в забвении из оболочки (Engine не видит Shell)",
		"NovelApp.Player.cs":    "идентификатор устройства: регистрируется в забвении из оболочки",
		"NovelApp.Manifest.cs":  "кэш манифеста: содержимое каталога, а не сведения об игроке",
		"LvnWallet.cs":          "офлайн-зеркало кошелька: стирается в LvnBackend вместе с учёткой (ForgetLocal)",
		"LvnAnalytics.cs":       "очередь недоставленных событий: уходит с ближайшей отправкой, как логи",
		"LvnAttribution.cs":     "откуда пришла УСТАНОВКА: свойство устройства и кампании, а не игрока",
		"LvnOutbox.cs":          "очередь на отправку: служебный буфер владельца, забывается вместе с ним",
	}

	writeRe := regexp.MustCompile(`LvnKeep\.(Put|Jot)\(`)
	var missing []string

	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			if !writeRe.Match(b) {
				return nil
			}
			base := filepath.Base(path)
			if _, ok := notPersonal[base]; ok {
				return nil
			}
			// Назван в доме забвения — по имени класса без расширения и без
			// части после точки (LvnSaveStore.Slots.cs → LvnSaveStore).
			class := strings.SplitN(strings.TrimSuffix(base, ".cs"), ".", 2)[0]
			if !strings.Contains(known, class) {
				missing = append(missing, base)
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	sort.Strings(missing)
	if len(missing) > 0 {
		t.Fatalf("пишут личное в записную книжку, но забвение о них не знает: %s\n"+
			"назовите хранилище в LvnForget.cs (или, если это не личное игрока, "+
			"добавьте его в notPersonal этого стража — с причиной)",
			strings.Join(missing, ", "))
	}
}
