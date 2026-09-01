package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// КАРТИНКА НАЗЫВАЕТ, ЧЕМ ОНА ПРИШЛА.
//
// Показ картинки был не одним действием, а двумя, и жили они на разных этажах:
// вписывание (LvnPicture.Fit) — в движке, загрузка (ScreenUi.SetBg) — в
// оболочке. Загрузка работает и без вписывания, молча: картинка встаёт
// растянутой под форму своего места. На квадратной плитке этого почти не
// видно, на полноэкранном фоне видно всем — но только на устройстве с другим
// соотношением сторон, чем у того, где проверяли. Так и растягивались фон
// загрузочного экрана, фон подъёма и фон входа: три места из тридцати четырёх,
// найти которые можно было, лишь пересчитав все.
//
// Теперь глагол называет вид: Photo (вписывается), Skin (тянется), Frame
// (девятислойка). Страж следит, чтобы показ не вернулся к безымянному.
func TestКартинкаНазываетСебя(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	pkgs := filepath.Join(root, "unity", "Packages")

	// Ищем ровно показ АРТА ПО URL мимо дома: спрайт, только что взятый у
	// ассетов, и поставленный фоном руками. Спрайт темы, готовая текстура и
	// очистка фона — законные соседи этой строки, и требовать от них дома
	// значило бы утопить стража в ложных находках.
	load := regexp.MustCompile(`LoadSpriteAsync\(`)
	raw := regexp.MustCompile(`\.style\.backgroundImage\s*=\s*new StyleBackground\(`)

	var bad []string
	err := filepath.Walk(pkgs, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		scanned++
		base := filepath.Base(path)
		switch {
		case base == "LvnPicture.cs", // дом
			strings.Contains(path, "/Tests/"),
			strings.Contains(path, "/Samples~/"),
			strings.HasPrefix(base, "VnStage."), // сцена: свой тракт спрайтов
			strings.HasPrefix(base, "WorldStage"):
			return nil
		}
		b, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		lines := strings.Split(string(b), "\n")
		for i, line := range lines {
			if !raw.MatchString(line) {
				continue
			}
			// Спрайт пришёл из ассетов, если загрузка стоит рядом — выше по
			// тому же методу.
			near := strings.Join(lines[max0(i-12):i+1], "\n")
			if !load.MatchString(near) {
				continue
			}
			rel, _ := filepath.Rel(root, path)
			bad = append(bad, rel+":"+itoa(i+1))
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(bad) > 0 {
		t.Errorf("картинка поставлена мимо дома (%d):\n  %s\n\n"+
			"Показывай через Lvn.UI.LvnPicture: Photo — обложка, фон, аватар (вписывается\n"+
			"и не искажается), Skin — рамка, подложка, полоса (тянется по месту),\n"+
			"Frame — девятислойка. Прямое присваивание фона снова делает вписывание\n"+
			"решением вызывающего — а забытое вписывание выглядит как обычная картинка.",
			len(bad), strings.Join(bad, "\n  "))
	}
}

func max0(n int) int {
	if n < 0 {
		return 0
	}
	return n
}
