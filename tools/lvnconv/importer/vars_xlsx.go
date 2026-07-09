package importer

// vars_xlsx.go parses the game's variable spreadsheet (an .xlsx) into two
// artifacts:
//
//  1. Engine variable declarations — rows whose "Переменная" cell carries an
//     assignment like `Wardrobe.mainCh_Hair = 11;`. Each distinct key becomes a
//     `set default=true key="<Key>" value=<Default>` declaration.
//  2. An emotion legend encoded by CELL BACKGROUND COLORS — on the "Эмоции"
//     sheet each emotion label (Idle/Happy/Sad/…) sits in a cell whose fill is
//     that emotion's signature color. The legend is color(#RRGGBB) → label.
//
// An .xlsx is a ZIP of XML. We parse it with the Go standard library only
// (archive/zip + encoding/xml). The color path is:
//
//	cell.s (style index) → styles.cellXfs[s].fillId → styles.fills[fillId].fgColor
//
// Robustness: missing sheets, blank cells, and cells without styles are all
// tolerated. Emotion swatches are told apart from generic UI backgrounds by the
// observation that a swatch color decorates exactly ONE distinct label, whereas
// generic fills (white, light-green, grey headers) decorate many cells.

import (
	"archive/zip"
	"encoding/xml"
	"fmt"
	"io"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// VarDecl is one engine variable declaration parsed from the spreadsheet.
type VarDecl struct {
	Key         string // e.g. "Wardrobe.mainCh_Hair"
	Default     string // e.g. "11" (already trimmed; numbers normalized)
	Section     string // group heading in column A, e.g. "Причёски" / "Одежда"
	Description string // human note from the "Описание" column, when present
	TechName    string // asset/tech name from the "Тех.название" column
	Sheet       string // sheet the row came from
}

// CharMap is one character row from the «Эмоции» roster sheet — the Rosetta
// Stone linking a story name to its art (tech) name and per-emotion art stems.
type CharMap struct {
	StoryName string            // column A story name, e.g. "Katya", "Matvey"
	TechName  string            // column D tech name / персонажи.zip folder, e.g. "Cold_Main"
	Role      string            // column C role: "ГГ" / "ЛИ" / "ОСН" / "ВТОР"
	Emotions  map[string]string // emotion label (lowercased) → art file stem
}

// WardrobeItem is one wardrobe catalog row from the «Гардеробыч» sheet.
type WardrobeItem struct {
	Variable string // full assignment key, e.g. "Wardrobe.mainCh_Hair"
	Value    string // clean value, e.g. "11" (never "11.0")
	Name     string // human description from the «Описание» column ("[premium]" stripped)
	TechName string // art/tech name from the «Тех.название» column
	Hide     bool   // true when «Не добавлять в гардероб» == 1
	Premium  bool   // true when the «Описание» carried a leading "[premium]" tag
}

// XlsxData is the full parse result.
type XlsxData struct {
	Vars          []VarDecl         // one entry per distinct variable key (first wins)
	EmotionColors map[string]string // "#RRGGBB" → emotion label (e.g. "#00B050" → "Happy")

	Chars       []CharMap                 // «Эмоции» roster, one entry per character row
	Protagonist *CharMap                  // the Role=="ГГ" row, or nil when absent
	Locations   map[string]string         // «Локации» articyName → techName
	Wardrobe    map[string][]WardrobeItem // «Гардеробыч» variable base → catalog items
}

// ---- internal XML shapes -------------------------------------------------

type xlWorkbook struct {
	Sheets struct {
		Sheet []struct {
			Name    string `xml:"name,attr"`
			SheetID string `xml:"sheetId,attr"`
			RID     string `xml:"http://schemas.openxmlformats.org/officeDocument/2006/relationships id,attr"`
		} `xml:"sheet"`
	} `xml:"sheets"`
}

type xlSharedStrings struct {
	SI []xlSI `xml:"si"`
}

type xlSI struct {
	// Both the plain <t> and the rich-text <r><t> runs are captured; we join
	// every text node found under the <si>.
	Runs []string `xml:",any"`
}

// UnmarshalXML flattens all descendant <t> text into a single string.
func (s *xlSI) UnmarshalXML(d *xml.Decoder, start xml.StartElement) error {
	var b strings.Builder
	depth := 0
	inT := false
	for {
		tok, err := d.Token()
		if err != nil {
			if err == io.EOF {
				break
			}
			return err
		}
		switch t := tok.(type) {
		case xml.StartElement:
			depth++
			if t.Name.Local == "t" {
				inT = true
			}
		case xml.EndElement:
			if t.Name.Local == "t" {
				inT = false
			}
			if t.Name.Local == start.Name.Local && depth == 0 {
				s.Runs = []string{b.String()}
				return nil
			}
			depth--
		case xml.CharData:
			if inT {
				b.Write(t)
			}
		}
	}
	s.Runs = []string{b.String()}
	return nil
}

func (s xlSI) text() string {
	if len(s.Runs) == 0 {
		return ""
	}
	return s.Runs[0]
}

type xlStyles struct {
	Fills struct {
		Fill []struct {
			PatternFill struct {
				FgColor struct {
					RGB string `xml:"rgb,attr"`
				} `xml:"fgColor"`
			} `xml:"patternFill"`
		} `xml:"fill"`
	} `xml:"fills"`
	CellXfs struct {
		Xf []struct {
			FillID int `xml:"fillId,attr"`
		} `xml:"xf"`
	} `xml:"cellXfs"`
}

type xlSheet struct {
	Rows []struct {
		R     string `xml:"r,attr"`
		Cells []struct {
			R  string `xml:"r,attr"` // e.g. "B3"
			T  string `xml:"t,attr"` // type: "s" shared, "str", "inlineStr", ""
			S  int    `xml:"s,attr"` // style index
			V  string `xml:"v"`      // value (shared-string index when t="s")
			IS struct {
				T string `xml:"t"`
			} `xml:"is"` // inline string
		} `xml:"c"`
	} `xml:"sheetData>row"`
}

// cell is a resolved spreadsheet cell.
type cell struct {
	col   int    // 0-based column
	row   int    // 1-based row
	text  string // resolved text value
	color string // fill fgColor as raw ARGB (e.g. "FF00B050"), "" if none
}

// ---- public API ----------------------------------------------------------

var assignRe = regexp.MustCompile(`^\s*([A-Za-z_][\w.]*)\s*=\s*(.*?)\s*;?\s*$`)

// ParseVarsXlsx reads the spreadsheet at path and returns the variable
// declarations plus the color→emotion legend.
func ParseVarsXlsx(path string) (XlsxData, error) {
	out := XlsxData{
		EmotionColors: map[string]string{},
		Locations:     map[string]string{},
		Wardrobe:      map[string][]WardrobeItem{},
	}

	zr, err := zip.OpenReader(path)
	if err != nil {
		return out, fmt.Errorf("open xlsx: %w", err)
	}
	defer zr.Close()

	files := map[string]*zip.File{}
	for _, f := range zr.File {
		files[f.Name] = f
	}

	shared, err := readSharedStrings(files["xl/sharedStrings.xml"])
	if err != nil {
		return out, err
	}
	fillColors, styleFill, err := readStyles(files["xl/styles.xml"])
	if err != nil {
		return out, err
	}

	// Collect every sheet part in workbook order (falling back to any sheetN).
	sheetParts := orderedSheetParts(files)

	// color → distinct labels, used to isolate emotion swatches.
	colorLabels := map[string]map[string]bool{}
	seenKeys := map[string]bool{}

	for _, part := range sheetParts {
		cells, err := readSheet(files[part.file], shared, fillColors, styleFill)
		if err != nil {
			return out, err
		}
		// Record color→label associations for the legend.
		for _, c := range cells {
			if c.color == "" || strings.TrimSpace(c.text) == "" {
				continue
			}
			if colorLabels[c.color] == nil {
				colorLabels[c.color] = map[string]bool{}
			}
			colorLabels[c.color][strings.TrimSpace(c.text)] = true
		}
		// Parse variable declarations from this sheet.
		for _, vd := range parseVarsFromSheet(part.name, cells) {
			if vd.Key == "" || seenKeys[vd.Key] {
				continue
			}
			seenKeys[vd.Key] = true
			out.Vars = append(out.Vars, vd)
		}

		// Parse the rich per-sheet mappings. Each parser detects its own sheet
		// by header signature, so it is safe to run all three on every sheet
		// (and independent of the display-name / fallback-name distinction).
		if chars := parseCharsFromSheet(cells); len(chars) > 0 {
			out.Chars = append(out.Chars, chars...)
		}
		for k, v := range parseLocationsFromSheet(cells) {
			if _, ok := out.Locations[k]; !ok {
				out.Locations[k] = v
			}
		}
		for base, items := range parseWardrobeFromSheet(cells) {
			out.Wardrobe[base] = append(out.Wardrobe[base], items...)
		}
	}

	// Pick the protagonist (first Role=="ГГ" row) once the roster is complete.
	for i := range out.Chars {
		if strings.TrimSpace(out.Chars[i].Role) == "ГГ" {
			out.Protagonist = &out.Chars[i]
			break
		}
	}

	// A color is an emotion swatch iff it decorates exactly one distinct label.
	for color, labels := range colorLabels {
		if len(labels) != 1 {
			continue
		}
		var label string
		for l := range labels {
			label = l
		}
		out.EmotionColors[normalizeColor(color)] = label
	}

	return out, nil
}

// SetDefaultLines renders the parsed variables as engine declaration lines:
//
//	set default=true key="Wardrobe.mainCh_Hair" value=11
//
// Numbers are emitted unquoted (with a trailing ".0" collapsed to an int);
// everything else is emitted as a quoted, escaped string.
func (x XlsxData) SetDefaultLines() []string {
	lines := make([]string, 0, len(x.Vars))
	for _, v := range x.Vars {
		lines = append(lines, fmt.Sprintf("set default=true key=%q value=%s", v.Key, formatValue(v.Default)))
	}
	return lines
}

// ---- parsing helpers ------------------------------------------------------

type sheetPart struct {
	name string // display name (e.g. "Гардеробыч")
	file string // e.g. "xl/worksheets/sheet2.xml"
}

// orderedSheetParts returns worksheet parts in workbook (tab) order, resolving
// r:id → target via workbook rels. Falls back to lexical sheetN order.
func orderedSheetParts(files map[string]*zip.File) []sheetPart {
	// Map every worksheet target that actually exists.
	exists := func(name string) bool { _, ok := files[name]; return ok }

	wb := files["xl/workbook.xml"]
	rels := files["xl/_rels/workbook.xml.rels"]
	if wb != nil && rels != nil {
		var book xlWorkbook
		if err := unmarshalFile(wb, &book); err == nil {
			relMap := readRels(rels) // rId → target part
			var parts []sheetPart
			for _, s := range book.Sheets.Sheet {
				target := relMap[s.RID]
				if target == "" {
					continue
				}
				full := normalizePart(target)
				if exists(full) {
					parts = append(parts, sheetPart{name: s.Name, file: full})
				}
			}
			if len(parts) > 0 {
				return parts
			}
		}
	}

	// Fallback: any xl/worksheets/sheetN.xml sorted by N.
	var names []string
	for name := range files {
		if strings.HasPrefix(name, "xl/worksheets/sheet") && strings.HasSuffix(name, ".xml") {
			names = append(names, name)
		}
	}
	sort.Strings(names)
	parts := make([]sheetPart, 0, len(names))
	for i, n := range names {
		parts = append(parts, sheetPart{name: fmt.Sprintf("sheet%d", i+1), file: n})
	}
	return parts
}

func normalizePart(target string) string {
	target = strings.TrimPrefix(target, "/")
	if strings.HasPrefix(target, "xl/") {
		return target
	}
	return "xl/" + target
}

func readRels(f *zip.File) map[string]string {
	out := map[string]string{}
	var rels struct {
		Rel []struct {
			ID     string `xml:"Id,attr"`
			Target string `xml:"Target,attr"`
		} `xml:"Relationship"`
	}
	if err := unmarshalFile(f, &rels); err != nil {
		return out
	}
	for _, r := range rels.Rel {
		out[r.ID] = r.Target
	}
	return out
}

func readSharedStrings(f *zip.File) ([]string, error) {
	if f == nil {
		return nil, nil // no shared strings table is legal (all inline/numeric)
	}
	var sst xlSharedStrings
	if err := unmarshalFile(f, &sst); err != nil {
		return nil, fmt.Errorf("sharedStrings: %w", err)
	}
	out := make([]string, len(sst.SI))
	for i, si := range sst.SI {
		out[i] = si.text()
	}
	return out, nil
}

// readStyles returns fillId→color and styleIndex→fillId maps.
func readStyles(f *zip.File) (fillColors []string, styleFill []int, err error) {
	if f == nil {
		return nil, nil, nil
	}
	var st xlStyles
	if err := unmarshalFile(f, &st); err != nil {
		return nil, nil, fmt.Errorf("styles: %w", err)
	}
	fillColors = make([]string, len(st.Fills.Fill))
	for i, fl := range st.Fills.Fill {
		fillColors[i] = fl.PatternFill.FgColor.RGB
	}
	styleFill = make([]int, len(st.CellXfs.Xf))
	for i, xf := range st.CellXfs.Xf {
		styleFill[i] = xf.FillID
	}
	return fillColors, styleFill, nil
}

func readSheet(f *zip.File, shared []string, fillColors []string, styleFill []int) ([]cell, error) {
	if f == nil {
		return nil, nil
	}
	var sh xlSheet
	if err := unmarshalFile(f, &sh); err != nil {
		return nil, fmt.Errorf("sheet: %w", err)
	}
	var cells []cell
	for _, row := range sh.Rows {
		for _, c := range row.Cells {
			col, rw := parseRef(c.R)
			if col < 0 {
				continue
			}
			text := ""
			switch c.T {
			case "s": // shared string index
				if idx, e := strconv.Atoi(strings.TrimSpace(c.V)); e == nil && idx >= 0 && idx < len(shared) {
					text = shared[idx]
				}
			case "inlineStr":
				text = c.IS.T
			default: // "str", "n", "" — literal value
				text = c.V
			}
			// Resolve fill color via style index → fillId → fgColor.
			color := ""
			if c.S >= 0 && c.S < len(styleFill) {
				fid := styleFill[c.S]
				if fid >= 0 && fid < len(fillColors) {
					color = fillColors[fid]
				}
			}
			cells = append(cells, cell{col: col, row: rw, text: text, color: color})
		}
	}
	return cells, nil
}

// parseVarsFromSheet extracts variable declarations from one sheet's cells.
//
// Preferred path: locate a header row containing "Переменная" and map the
// Значение/Описание/Тех.название columns, tracking the column-A section
// heading. Fallback: any cell whose text is an `ident = value` assignment.
func parseVarsFromSheet(sheetName string, cells []cell) []VarDecl {
	// Index cells by (row → col → cell) for column-aware access.
	byRow := map[int]map[int]cell{}
	maxRow := 0
	for _, c := range cells {
		if byRow[c.row] == nil {
			byRow[c.row] = map[int]cell{}
		}
		byRow[c.row][c.col] = c
		if c.row > maxRow {
			maxRow = c.row
		}
	}

	varCol, valCol, descCol, techCol := -1, -1, -1, -1
	headerRow := -1
	for r := 1; r <= maxRow && headerRow < 0; r++ {
		for col, c := range byRow[r] {
			switch strings.TrimSpace(c.text) {
			case "Переменная":
				varCol = col
				headerRow = r
			}
		}
		if headerRow >= 0 {
			for col, c := range byRow[headerRow] {
				switch strings.TrimSpace(c.text) {
				case "Значение":
					valCol = col
				case "Описание":
					descCol = col
				case "Тех.название", "Техническое название":
					techCol = col
				}
			}
		}
	}

	var out []VarDecl

	if headerRow >= 0 && varCol >= 0 {
		section := ""
		for r := headerRow + 1; r <= maxRow; r++ {
			cols := byRow[r]
			if cols == nil {
				continue
			}
			varText := strings.TrimSpace(cols[varCol].text)
			// A row that fills column A but not the variable column is a
			// section heading (e.g. "Причёски", "Одежда").
			if varText == "" {
				if a := strings.TrimSpace(cols[0].text); a != "" {
					section = a
				}
				continue
			}
			key, def := splitAssignment(varText)
			if key == "" {
				continue
			}
			if def == "" && valCol >= 0 {
				def = strings.TrimSpace(cols[valCol].text)
			}
			vd := VarDecl{
				Key:     key,
				Default: def,
				Section: section,
				Sheet:   sheetName,
			}
			if descCol >= 0 {
				vd.Description = strings.TrimSpace(cols[descCol].text)
			}
			if techCol >= 0 {
				vd.TechName = strings.TrimSpace(cols[techCol].text)
			}
			out = append(out, vd)
		}
		return out
	}

	// Fallback: scan every cell for an assignment expression.
	for _, c := range cells {
		key, def := splitAssignment(strings.TrimSpace(c.text))
		if key == "" {
			continue
		}
		out = append(out, VarDecl{Key: key, Default: def, Sheet: sheetName})
	}
	return out
}

// indexCells builds a (row → col → cell) map and the max row seen.
func indexCells(cells []cell) (byRow map[int]map[int]cell, maxRow int) {
	byRow = map[int]map[int]cell{}
	for _, c := range cells {
		if byRow[c.row] == nil {
			byRow[c.row] = map[int]cell{}
		}
		byRow[c.row][c.col] = c
		if c.row > maxRow {
			maxRow = c.row
		}
	}
	return byRow, maxRow
}

// findHeaderRow scans downward for the first row containing a cell whose
// trimmed text contains needle (case-sensitive substring). Returns -1 if none.
func findHeaderRow(byRow map[int]map[int]cell, maxRow int, needle string) (row, col int) {
	for r := 1; r <= maxRow; r++ {
		for c, cl := range byRow[r] {
			if strings.Contains(strings.TrimSpace(cl.text), needle) {
				return r, c
			}
		}
	}
	return -1, -1
}

// headerColumn returns the column in headerRow whose text contains needle.
func headerColumn(byRow map[int]map[int]cell, headerRow int, needle string) int {
	for c, cl := range byRow[headerRow] {
		if strings.Contains(strings.TrimSpace(cl.text), needle) {
			return c
		}
	}
	return -1
}

// parseCharsFromSheet reads the «Эмоции» roster. It self-detects by the
// «Имя персонажа» header. The emotion legend lives one row below the header
// (row 2 in the real file); its labels are read verbatim per column so the
// emotion order is data-driven, not positional.
func parseCharsFromSheet(cells []cell) []CharMap {
	byRow, maxRow := indexCells(cells)
	headerRow, nameCol := findHeaderRow(byRow, maxRow, "Имя персонажа")
	if headerRow < 0 {
		return nil
	}
	roleCol := headerColumn(byRow, headerRow, "Отношения")
	// The tech-name column header contains "персонаж" but must not be an
	// emotion header (those contain "эмоци").
	techCol := -1
	for c, cl := range byRow[headerRow] {
		t := strings.TrimSpace(cl.text)
		if strings.Contains(t, "нейминг") && strings.Contains(t, "персонаж") && !strings.Contains(t, "эмоци") {
			techCol = c
			break
		}
	}

	// Emotion columns + labels come from the legend row (header + 1). Any
	// non-empty cell there that is not the name/role/tech column is an emotion.
	legendRow := headerRow + 1
	emotionCols := map[int]string{} // col → emotion label (lowercased)
	for c, cl := range byRow[legendRow] {
		if c == nameCol || c == roleCol || c == techCol {
			continue
		}
		label := strings.TrimSpace(cl.text)
		if label == "" {
			continue
		}
		emotionCols[c] = strings.ToLower(label)
	}

	var out []CharMap
	for r := legendRow + 1; r <= maxRow; r++ {
		cols := byRow[r]
		if cols == nil {
			continue
		}
		story := strings.TrimSpace(cols[nameCol].text)
		if story == "" {
			continue
		}
		cm := CharMap{
			StoryName: story,
			Emotions:  map[string]string{},
		}
		if techCol >= 0 {
			cm.TechName = strings.TrimSpace(cols[techCol].text)
		}
		if roleCol >= 0 {
			cm.Role = strings.TrimSpace(cols[roleCol].text)
		}
		for c, label := range emotionCols {
			stem := strings.TrimSpace(cols[c].text)
			if stem == "" {
				continue
			}
			cm.Emotions[label] = stem
		}
		out = append(out, cm)
	}
	return out
}

// parseLocationsFromSheet reads the «Локации» background map. It self-detects
// by the «Название в Артиси» header. Keys and values are whitespace-trimmed;
// values are otherwise kept verbatim (doubled "Cold_Cold_" prefixes and all).
func parseLocationsFromSheet(cells []cell) map[string]string {
	byRow, maxRow := indexCells(cells)
	headerRow, articyCol := findHeaderRow(byRow, maxRow, "Артиси")
	if headerRow < 0 {
		return nil
	}
	techCol := headerColumn(byRow, headerRow, "Техническое название")
	if techCol < 0 {
		return nil
	}
	out := map[string]string{}
	for r := headerRow + 1; r <= maxRow; r++ {
		cols := byRow[r]
		if cols == nil {
			continue
		}
		articy := strings.TrimSpace(cols[articyCol].text)
		tech := strings.TrimSpace(cols[techCol].text)
		if articy == "" || tech == "" {
			continue
		}
		if _, ok := out[articy]; !ok {
			out[articy] = tech
		}
	}
	return out
}

// parseWardrobeFromSheet reads the «Гардеробыч» catalog, grouping items by
// their variable base (e.g. "Wardrobe.mainCh_Hair"). Self-detects by the
// «Переменная» header.
func parseWardrobeFromSheet(cells []cell) map[string][]WardrobeItem {
	byRow, maxRow := indexCells(cells)
	headerRow, varCol := findHeaderRow(byRow, maxRow, "Переменная")
	if headerRow < 0 {
		return nil
	}
	valCol := headerColumn(byRow, headerRow, "Значение")
	descCol := headerColumn(byRow, headerRow, "Описание")
	techCol := headerColumn(byRow, headerRow, "Тех.название")
	if techCol < 0 {
		techCol = headerColumn(byRow, headerRow, "Техническое название")
	}
	hideCol := headerColumn(byRow, headerRow, "Не добавлять")

	out := map[string][]WardrobeItem{}
	for r := headerRow + 1; r <= maxRow; r++ {
		cols := byRow[r]
		if cols == nil {
			continue
		}
		key, def := splitAssignment(strings.TrimSpace(cols[varCol].text))
		if key == "" {
			continue
		}
		item := WardrobeItem{Variable: key}
		// Prefer the explicit «Значение» column, falling back to the value in
		// the assignment. Normalize "11.0" → "11".
		val := def
		if valCol >= 0 {
			if v := strings.TrimSpace(cols[valCol].text); v != "" {
				val = v
			}
		}
		if n, ok := parseNumber(val); ok {
			val = n
		}
		item.Value = val
		if descCol >= 0 {
			name := strings.TrimSpace(cols[descCol].text)
			// A leading "[premium]" tag marks a store-gated wardrobe item; strip it
			// from the display name and flag the row so the wardrobe screen can
			// price it (currency/price/rarity).
			if strings.HasPrefix(name, "[premium]") {
				item.Premium = true
				name = strings.TrimSpace(strings.TrimPrefix(name, "[premium]"))
			}
			item.Name = name
		}
		if techCol >= 0 {
			item.TechName = strings.TrimSpace(cols[techCol].text)
		}
		if hideCol >= 0 {
			item.Hide = isTruthy(strings.TrimSpace(cols[hideCol].text))
		}
		out[key] = append(out[key], item)
	}
	return out
}

// isTruthy reports whether a wardrobe flag cell means "yes" (1 / true).
func isTruthy(s string) bool {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "1", "true", "да", "yes", "y":
		return true
	}
	if n, ok := parseNumber(s); ok && n != "0" {
		return true
	}
	return false
}

// splitAssignment parses "Wardrobe.mainCh_Hair = 11;" → ("Wardrobe.mainCh_Hair", "11").
// Returns ("","") when the text is not an assignment.
func splitAssignment(s string) (key, def string) {
	m := assignRe.FindStringSubmatch(s)
	if m == nil {
		return "", ""
	}
	return strings.TrimSpace(m[1]), strings.TrimSpace(m[2])
}

// ---- formatting helpers ---------------------------------------------------

// normalizeColor turns an ARGB "FF00B050" into "#00B050" (strip FF alpha,
// uppercase, add leading '#'). Already-6-digit or unexpected values pass
// through with just uppercasing and a '#'.
func normalizeColor(argb string) string {
	c := strings.ToUpper(strings.TrimSpace(argb))
	c = strings.TrimPrefix(c, "#")
	if len(c) == 8 { // AARRGGBB → drop alpha
		c = c[2:]
	}
	return "#" + c
}

// formatValue renders a declaration value: numeric values unquoted (with a
// trailing ".0" collapsed), everything else quoted and escaped.
func formatValue(v string) string {
	v = strings.TrimSpace(v)
	if v == "" {
		return `""`
	}
	if n, ok := parseNumber(v); ok {
		return n
	}
	if v == "true" || v == "false" {
		return v
	}
	return strconv.Quote(v)
}

// parseNumber returns a normalized numeric string when v is a number.
func parseNumber(v string) (string, bool) {
	if i, err := strconv.ParseInt(v, 10, 64); err == nil {
		return strconv.FormatInt(i, 10), true
	}
	if f, err := strconv.ParseFloat(v, 64); err == nil {
		// Collapse integral floats (11.0 → 11).
		if f == float64(int64(f)) {
			return strconv.FormatInt(int64(f), 10), true
		}
		return strconv.FormatFloat(f, 'g', -1, 64), true
	}
	return "", false
}

// ---- xml plumbing ---------------------------------------------------------

func parseRef(ref string) (col, row int) {
	col = 0
	i := 0
	for ; i < len(ref); i++ {
		ch := ref[i]
		if ch >= 'A' && ch <= 'Z' {
			col = col*26 + int(ch-'A'+1)
		} else if ch >= 'a' && ch <= 'z' {
			col = col*26 + int(ch-'a'+1)
		} else {
			break
		}
	}
	if col == 0 || i >= len(ref) {
		return -1, -1
	}
	r, err := strconv.Atoi(ref[i:])
	if err != nil {
		return -1, -1
	}
	return col - 1, r
}

func unmarshalFile(f *zip.File, v any) error {
	if f == nil {
		return fmt.Errorf("missing part")
	}
	rc, err := f.Open()
	if err != nil {
		return err
	}
	defer rc.Close()
	data, err := io.ReadAll(rc)
	if err != nil {
		return err
	}
	return xml.Unmarshal(data, v)
}
