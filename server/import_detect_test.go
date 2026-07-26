package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// The mapper reads report.speakers and report.xlsx_* off the SAME object, so
// the embedded report's fields must stay flat on the wire — an accidental
// nesting would blank the whole table without an error anywhere.
func TestDetectResponseKeepsReportFlat(t *testing.T) {
	rep := &importer.DetectReport{
		ProjectDir: "/p", Chapters: 3,
		Speakers:              []importer.SpeakerDetect{{Who: "Аня", LineCount: 7, Role: "npc"}},
		ProtagonistWithoutArt: []string{"Главный герой"},
	}
	data, err := json.Marshal(detectResponse{
		DetectReport: rep,
		xlsxPreview:  xlsxPreview{XlsxProtagonist: "cold_main", XlsxEmotionColors: 11},
	})
	if err != nil {
		t.Fatal(err)
	}
	var got map[string]any
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"speakers", "chapters", "protagonist_without_art", "xlsx_protagonist", "xlsx_emotion_colors"} {
		if _, ok := got[key]; !ok {
			t.Fatalf("key %q missing from the detect response: %s", key, data)
		}
	}
	if sp, _ := got["speakers"].([]any); len(sp) != 1 {
		t.Fatalf("speakers did not survive embedding: %s", data)
	}
}

// The whole point of threading the spreadsheet through is that a preview
// computed WITHOUT it silently disagrees with the import. A sheet that won't
// parse must therefore say so, not fall back to a quiet half-answer.
func TestApplyXlsxToPreviewSurfacesParseFailure(t *testing.T) {
	bad := filepath.Join(t.TempDir(), "vars.xlsx")
	if err := os.WriteFile(bad, []byte("this is not a spreadsheet"), 0o644); err != nil {
		t.Fatal(err)
	}
	tpl := importer.DefaultTemplate()
	before := len(tpl.EmotionColors)
	out := applyXlsxToPreview(tpl, bad)
	if out.XlsxError == "" {
		t.Fatal("a broken -vars.xlsx was swallowed silently")
	}
	if out.XlsxProtagonist != "" || out.XlsxEmotionColors != 0 {
		t.Fatalf("failed parse still reported data: %+v", out)
	}
	if len(tpl.EmotionColors) != before {
		t.Fatalf("failed parse mutated the template legend (%d → %d)", before, len(tpl.EmotionColors))
	}
}

// A missing file is the same class of failure — reported, never silent.
func TestApplyXlsxToPreviewMissingFile(t *testing.T) {
	out := applyXlsxToPreview(importer.DefaultTemplate(), filepath.Join(t.TempDir(), "nope.xlsx"))
	if !strings.Contains(strings.ToLower(out.XlsxError), "no such file") && out.XlsxError == "" {
		t.Fatalf("want an error for a missing sheet, got %+v", out)
	}
}
