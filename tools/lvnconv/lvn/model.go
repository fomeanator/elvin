// Package lvn defines the .lvn container model and a source-agnostic validator.
//
// .lvn is the "universal narrative container": every authoring front-end
// (ink, articy, …) compiles down to the same flat command list, and the
// runtime plays that list. Validation therefore lives here, decoupled from
// any producer — a hand-edited or third-party .lvn is checked the same way as
// a freshly converted one.
package lvn

import (
	"encoding/json"
	"fmt"
)

// Cmd is a single command: an "op" plus op-specific fields.
type Cmd map[string]any

// Op returns the command's op name ("say", "goto", …) or "" if absent.
func (c Cmd) Op() string {
	s, _ := c["op"].(string)
	return s
}

// Str returns a string field, or "" if missing / not a string.
func (c Cmd) Str(key string) string {
	s, _ := c[key].(string)
	return s
}

// Doc is the .lvn document shape: an optional scene id and the command list.
type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`
}

// Parse decodes a .lvn document from JSON bytes.
func Parse(data []byte) (*Doc, error) {
	var d Doc
	if err := json.Unmarshal(data, &d); err != nil {
		return nil, fmt.Errorf("invalid .lvn JSON: %w", err)
	}
	return &d, nil
}

// Marshal encodes the document as indented .lvn JSON with a trailing newline.
func (d *Doc) Marshal() ([]byte, error) {
	data, err := json.MarshalIndent(d, "", "  ")
	if err != nil {
		return nil, err
	}
	return append(data, '\n'), nil
}
