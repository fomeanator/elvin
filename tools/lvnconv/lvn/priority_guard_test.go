package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ФОНОВЫЙ ПРОГРЕВ ХОДИТ ПО ЛЕСТНИЦЕ.
//
// «Положить в очередь» — половина работы; вторая половина порядок. Очередь без
// него забивается, и первым не доезжает как раз то, чего игрок ждёт: вводная
// (глава ноль с агентом и фаворитами) в манифесте не первая, и её арт вставал
// за спиной у сотни картинок, которых никто не ждал. Живой случай 01.09.
//
// Здесь закреплены две вещи, каждая из которых по отдельности выглядит
// мелочью: прогрев начинает с ВВОДНОЙ и раскладывает части главы ПО СТУПЕНЯМ
// (критичное — то, что рисует первый кадр, — раньше прочего).
func TestLibraryWarmFollowsThePriorityLadder(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine.shell", "Runtime", "NovelApp.Chapter.cs"))))

	i := strings.Index(src, "WarmLibraryAsync(")
	if i < 0 {
		t.Fatal("прогрева библиотеки нет — якорь стража промахнулся")
	}
	body := src[i:]

	if !strings.Contains(body, "LvnIntro.Is(") {
		t.Error("прогрев не выделяет вводную.\n\n" +
			"Глава ноль — первое, что видит игрок после установки, а в манифесте она не первая.\n" +
			"Без явного порядка её агент и фавориты встают за спиной у всей библиотеки.")
	}
	if !strings.Contains(body, "LvnPriority.ByRung(") {
		t.Error("прогрев не раскладывает части главы по ступеням.\n\n" +
			"Критичное рисует ПЕРВЫЙ КАДР, и качать его надо раньше прочего. Какая поза первая —\n" +
			"решает автор (признак критичности в манифесте): движок не знает, чем откроется сцена.")
	}
}

// ЛЕСТНИЦА НАЗЫВАЕТ ВСЕ КЛАССЫ АССЕТОВ.
//
// Ступень по классу — ответ для всего, что не принадлежит главе. Появится
// новый класс, а ступени у него не будет — он молча уедет в «прочее», то есть
// в самый хвост очереди, и никто не заметит, пока игрок не подождёт лишнего.
func TestEveryAssetClassHasARung(t *testing.T) {
	root := repoRoot(t)
	policy := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine", "Runtime", "Content", "DownloadPolicy.cs"))))
	prio := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine", "Runtime", "Content", "LvnPriority.cs"))))

	i := strings.Index(policy, "enum AssetClass")
	if i < 0 {
		t.Fatal("списка классов нет — якорь стража промахнулся")
	}
	body := policy[i:]
	if j := strings.Index(body, "}"); j > 0 {
		body = body[:j]
	}
	var missing []string
	seen := 0
	for _, line := range strings.Split(body, "\n") {
		name := strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(line), ","))
		if name == "" || strings.HasPrefix(name, "enum") || strings.HasPrefix(name, "{") {
			continue
		}
		if strings.ContainsAny(name, " \t") {
			continue
		}
		seen++
		if !strings.Contains(prio, "AssetClass."+name) && !strings.Contains(prio, "case "+name) {
			missing = append(missing, name)
		}
	}
	atLeast(t, seen, 5, "разобранных классов ассетов")
	if len(missing) > 0 {
		t.Errorf("классы без ступени: %s\n\n"+
			"Класс без ступени уезжает в «прочее» — в самый хвост очереди. Заметит это не автор\n"+
			"класса, а игрок, подождавший лишнего.", strings.Join(missing, ", "))
	}
}

// «Я ЖИВОЙ — УСТУПИТЕ» СЧИТАЕТСЯ В ОДНОМ МЕСТЕ.
//
// Фоновый прогрев ждёт, пока счётчик живых загрузок не станет нулём. Правило
// поднимали ПО МЕСТУ — и подняли у двух дверей из семи: спрайта и звука.
// Скрипт главы, префаб, объёмный набор и файл на диск шли мимо счёта, и
// прогрев им не уступал. Первый запуск 01.09 вставал именно так: вводной нужен
// был её СКРИПТ, а он грузился дверью, которой гейт не видел.
func TestLivePressureIsCountedInOnePlace(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine", "Runtime", "UI", "CachingAssets.cs"))))

	inc := strings.Count(src, "Interlocked.Increment(ref _livePressure)")
	if inc != 1 {
		t.Errorf("счётчик живых загрузок поднимают в %d местах (ожидалось одно — LiveAsync).\n\n"+
			"Правило, написанное по месту, поднимут не у всех дверей: так скрипт главы и оказался\n"+
			"невидим для гейта, а фоновый прогрев не уступил тому, чего ждал игрок.", inc)
	}
	if !strings.Contains(src, "LiveAsync") {
		t.Error("общего правила LiveAsync нет — счёт снова разойдётся по дверям")
	}
	// Двери, которых игрок ЖДЁТ, обязаны идти через правило.
	for _, door := range []string{"LoadSpriteAsync", "LoadAudioAsync", "LoadTextAsync", "EnsureCachedFileAsync"} {
		i := strings.Index(src, door)
		if i < 0 {
			t.Fatalf("двери %s нет — якорь стража промахнулся", door)
		}
		tail := src[i:]
		if j := strings.Index(tail, "\n        public "); j > 0 {
			tail = tail[:j]
		}
		if !strings.Contains(tail, "LiveAsync") {
			t.Errorf("дверь %s идёт мимо правила «я живой»: прогрев ей не уступит", door)
		}
	}
}
