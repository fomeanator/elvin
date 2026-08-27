package main

import (
	"fmt"
	"os"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/figure"
)

// cmdFigure measures where each paper doll actually stands inside its canvas
// and records it in the manifest, so the runtime can size the CHARACTER instead
// of the file. See internal/figure for why that distinction matters.
//
//	lvnconv figure -i server/content           # measure and report
//	lvnconv figure -i server/content -apply    # write `content` into manifest.json
func cmdFigure(args []string) {
	fs := newFlagSet("figure")
	in := fs.String("i", "server/content", "content root (the folder holding manifest.json)")
	apply := fs.Bool("apply", false, "write the measurements into manifest.json (default: report only)")
	_ = fs.Parse(args)

	results, err := figure.Scan(*in)
	if err != nil {
		die("figure: " + err.Error())
	}

	measured, changed, skipped := 0, 0, 0
	for _, r := range results {
		switch {
		case r.Err != nil:
			fmt.Fprintf(os.Stderr, "  ОШИБКА %s: %v\n", r.Entity, r.Err)
		case r.Skipped != "":
			skipped++
		default:
			measured++
			mark := "="
			if r.Changed() {
				mark = "→"
				changed++
			}
			foreign := ""
			if r.Foreign > 0 {
				foreign = fmt.Sprintf(", %d чужих кадра(ов) мимо", r.Foreign)
			}
			fmt.Printf("  %s %-24s %2d кадр(ов)  фигура %.0f%%×%.0f%% холста, поля: слева %.0f%%, сверху %.0f%%%s\n",
				mark, r.Entity, r.Files, 100*r.Box.W, 100*r.Box.H, 100*r.Box.X, 100*r.Box.Y, foreign)
		}
	}

	if !*apply {
		fmt.Fprintf(os.Stderr, "\nfigure: измерено %d, обновило бы %d, пропущено %d (не куклы) — примерка вхолостую, пиши -apply\n",
			measured, changed, skipped)
		return
	}
	written, err := figure.Apply(*in, results)
	if err != nil {
		die("figure: " + err.Error())
	}
	fmt.Fprintf(os.Stderr, "\nfigure: измерено %d, записано %d, пропущено %d (не куклы)\n", measured, written, skipped)
}
