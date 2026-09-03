package main

// walk — обход ВСЕХ путей новеллы и отчёт о том, до чего игрок не доберётся.
//
// Соук-бот играет случайными выборами: он отвечает на вопрос «падает ли», но не
// на вопрос «до чего игрок вообще не доберётся». Редкая ветка за гейтом не
// выпадет ни за две тысячи случайных прогонов, и мёртвый контент так и находили
// — глазами, спустя недели (треть главы за сломанным переходом).
//
// Сам обход живёт в пакете языка (lvn.Reach): его же зовёт валидатор при каждом
// сохранении главы, и вторая реализация здесь была бы прямым путём к
// расхождению — в этом проекте оно уже стоило дорого.
//
//	lvnconv walk [-depth N] [-json] [-strict] [-quiet] chapter.lvns [ещё файлы…]

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

type walkReport struct {
	File     string `json:"file"`
	Commands int    `json:"commands"`
	Reached  int    `json:"reached"`
	// Blocks — куски, до которых не ведёт ни один путь.
	Blocks []lvn.DeadBlock `json:"dead_blocks"`
	// Boilerplate — мёртвая разметка ветвей: шов компиляции, а не потеря.
	Boilerplate int `json:"boilerplate_dead"`
	// UncalledFuncs — для главы находка, для файла-библиотеки норма.
	UncalledFuncs []string         `json:"uncalled_funcs"`
	DeadOpts      []lvn.DeadOption `json:"dead_options"`
	// Развилки — то число, которое нужно автору. «Путей» меньше, чем ветвей:
	// сходящиеся ветки памятка отсекает, и глава с двумя выборами честно
	// показывала «путей 1», что читается как «выбор не работает».
	Forks      int `json:"forks"`
	ForksTaken int `json:"forks_taken"`
	Paths      int `json:"paths_completed"`
	CutDepth   int `json:"cut_by_depth"`
	// РАСПИСАНИЕ КАДРА: что обход узнал про сцену, а не только про
	// достижимость. Ходим по главе всё равно — и знать, кто в кадре, стоит тех
	// же шагов; а вопрос «почему герой говорит, когда его нет на экране»
	// иначе всплывает только на живом прогоне, у игрока.
	FrameIssues []lvn.FrameIssue `json:"frame_issues,omitempty"`
	// FrameUncertain — узлы, где кадр зависит от пути. Не беда: в ветвистой
	// главе это норма, и точный ответ там даёт трасса сохранения. Но число
	// полезно видеть — оно говорит, насколько сцена вообще предсказуема.
	FrameUncertain int    `json:"frame_uncertain"`
	Err            string `json:"error,omitempty"`
}

func cmdWalk(args []string) {
	fs := newFlagSet("walk")
	depth := fs.Int("depth", lvn.DefaultReachDepth, "сколько переходов проходить вглубь по одному пути")
	asJSON := fs.Bool("json", false, "отчёт в JSON (для CI)")
	strict := fs.Bool("strict", false, "выход 1, если найден недостижимый контент")
	quiet := fs.Bool("quiet", false, "только сводка, без перечисления мёртвых мест")
	_ = fs.Parse(args)
	if fs.NArg() == 0 {
		die("walk: ожидается хотя бы один <файл.lvn|.lvns>")
	}

	// По файлам — параллельно: главы независимы, а их у продукта двадцать пять.
	reports := make([]walkReport, fs.NArg())
	var wg sync.WaitGroup
	sem := make(chan struct{}, walkJobs())
	for i, path := range fs.Args() {
		wg.Add(1)
		go func(i int, path string) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			reports[i] = walkFile(path, *depth)
		}(i, path)
	}
	wg.Wait()

	if *asJSON {
		out, _ := json.MarshalIndent(reports, "", "  ")
		fmt.Println(string(out))
	} else {
		printWalkReports(reports, *quiet, *depth)
	}

	dead, failed := 0, 0
	for _, r := range reports {
		dead += len(r.Blocks) + len(r.DeadOpts)
		if r.Err != "" {
			failed++
		}
	}
	if failed > 0 || (*strict && dead > 0) {
		os.Exit(1)
	}
}

func walkJobs() int {
	n := 8
	if v := os.Getenv("LVN_WALK_JOBS"); v != "" {
		if k, err := fmt.Sscanf(v, "%d", &n); err != nil || k != 1 || n < 1 {
			n = 8
		}
	}
	return n
}

func walkFile(path string, depth int) walkReport {
	rep := walkReport{File: path}
	doc, err := loadForWalk(path)
	if err != nil {
		rep.Err = err.Error()
		return rep
	}
	r := lvn.Reach(doc, depth)
	rep.Commands = len(doc.Script)
	rep.Reached = r.Reached()
	rep.Blocks, rep.Boilerplate = r.Blocks, r.Boilerplate
	rep.UncalledFuncs, rep.DeadOpts = r.UncalledFuncs, r.DeadOptions
	rep.Forks, rep.ForksTaken = r.Forks, r.ForksTaken
	rep.Paths, rep.CutDepth = r.Paths, r.CutByDepth

	f := lvn.Schedule(doc, depth)
	rep.FrameIssues, rep.FrameUncertain = f.Issues, f.Uncertain
	return rep
}

// loadForWalk принимает и скомпилированный .lvn, и авторский .lvns — обход
// нужен ровно там, где новеллу пишут.
func loadForWalk(path string) (*lvn.Doc, error) {
	if strings.EqualFold(filepath.Ext(path), ".lvns") {
		src, err := lvns.ConvertFile(path)
		if err != nil {
			return nil, err
		}
		raw, err := json.Marshal(src)
		if err != nil {
			return nil, err
		}
		return lvn.Parse(raw)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return lvn.Parse(data)
}

func printWalkReports(reports []walkReport, quiet bool, depth int) {
	totalCmds, totalReached, totalDead := 0, 0, 0
	for _, r := range reports {
		if r.Err != "" {
			fmt.Fprintf(os.Stderr, "%s: НЕ РАЗОБРАН — %s\n", r.File, r.Err)
			continue
		}
		dead := len(r.Blocks) + len(r.DeadOpts)
		totalCmds += r.Commands
		totalReached += r.Reached
		totalDead += dead

		pct := 100.0
		if r.Commands > 0 {
			pct = float64(r.Reached) / float64(r.Commands) * 100
		}
		fmt.Printf("%s: %d/%d команд достижимо (%.1f%%), развилок %d/%d",
			filepath.Base(r.File), r.Reached, r.Commands, pct, r.ForksTaken, r.Forks)
		if r.CutDepth > 0 {
			fmt.Printf(", обрублено по глубине %d", r.CutDepth)
		}
		fmt.Println()
		if r.CutDepth > 0 {
			// Иначе неполный обход не отличить от мёртвого контента — а это
			// разные новости.
			fmt.Printf("  ⚠ обход не дошёл до конца %d раз(а): покрытие НЕПОЛНОЕ, повторите с -depth больше %d\n",
				r.CutDepth, depth)
		}
		if len(r.FrameIssues) > 0 {
			fmt.Printf("  сцена (%d):\n", len(r.FrameIssues))
			for _, is := range r.FrameIssues {
				fmt.Printf("    #%d %s\n", is.Cmd, is.Note)
			}
		}
		if r.FrameUncertain > 0 {
			fmt.Printf("  кадр зависит от пути в %d команд(ах) — там сцену знает только трасса\n",
				r.FrameUncertain)
		}
		if len(r.UncalledFuncs) > 0 {
			fmt.Printf("  функции без вызова (%d): %s\n", len(r.UncalledFuncs), strings.Join(r.UncalledFuncs, ", "))
		}
		if r.Boilerplate > 0 {
			fmt.Printf("  служебной разметки недостижимо: %d команд(ы) — это норма компиляции\n", r.Boilerplate)
		}
		if quiet || dead == 0 {
			continue
		}
		blocks := append([]lvn.DeadBlock(nil), r.Blocks...)
		sort.Slice(blocks, func(i, j int) bool { return blocks[i].Len > blocks[j].Len })
		for _, b := range blocks {
			fmt.Printf("  МЁРТВО #%d…#%d (%d команд): %s\n", b.Start, b.End, b.Len, b.Sample)
			if len(b.Labels) > 0 {
				fmt.Printf("      метки: %s\n", strings.Join(b.Labels, ", "))
			}
		}
		if len(r.DeadOpts) > 0 {
			fmt.Printf("  недостижимые варианты выбора (%d):\n", len(r.DeadOpts))
			for _, o := range r.DeadOpts {
				fmt.Printf("    #%d вариант %d %q\n", o.Cmd, o.Option, o.Text)
			}
		}
	}
	if len(reports) > 1 {
		pct := 100.0
		if totalCmds > 0 {
			pct = float64(totalReached) / float64(totalCmds) * 100
		}
		fmt.Printf("\nвсего: %d/%d команд достижимо (%.1f%%), мёртвых мест %d\n",
			totalReached, totalCmds, pct, totalDead)
	}
}
