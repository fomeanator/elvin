package articy

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Stage-direction tags — the SAME `# cmd: args` syntax ink2lvn uses, so a
// writer moving between Inky and articy keeps one staging vocabulary. In
// articy they live in the StageDirections field of a DialogueFragment, one
// or more tags, newline- or inline-separated.

var reTagStart = regexp.MustCompile(`(?:^|\s)#\s*[A-Za-zА-Яа-я_]+\s*:`)

func (g *gen) handleTags(s string, sayExtras Cmd) error {
	for _, line := range strings.Split(s, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		idx := reTagStart.FindAllStringIndex(line, -1)
		if len(idx) == 0 {
			return fmt.Errorf("cannot parse stage direction %q (expected `# команда: аргументы`)", line)
		}
		for i, loc := range idx {
			end := len(line)
			if i+1 < len(idx) {
				end = idx[i+1][0]
			}
			part := strings.TrimSpace(line[loc[0]:end])
			kv := strings.SplitN(part, ":", 2)
			name := strings.ToLower(strings.TrimSpace(strings.TrimPrefix(kv[0], "#")))
			args := ""
			if len(kv) == 2 {
				args = strings.TrimSpace(kv[1])
			}
			if err := g.handleTag(name, args, sayExtras); err != nil {
				return err
			}
		}
	}
	return nil
}

func (g *gen) handleTag(name, args string, sayExtras Cmd) error {
	switch name {
	case "todo":
		return nil

	case "style":
		sayExtras["style"] = args
		return nil
	case "say":
		applyKV(sayExtras, splitArgs(args))
		return nil

	case "bg", "actor":
		fields := splitArgs(args)
		c := Cmd{"op": name}
		if len(fields) > 0 && !strings.Contains(fields[0], "=") {
			c["id"] = fields[0]
			fields = fields[1:]
		}
		applyKV(c, fields)
		g.emit(c)
		return nil

	case "set":
		fields := splitArgs(args)
		if len(fields) < 2 {
			return fmt.Errorf("set: expected `key value`")
		}
		g.emit(Cmd{"op": "set", "key": fields[0], "value": coerce(fields[1])})
		return nil

	case "inc":
		fields := splitArgs(args)
		if len(fields) < 2 {
			return fmt.Errorf("inc: expected `key by`")
		}
		n, err := strconv.ParseInt(fields[1], 10, 64)
		if err != nil {
			return fmt.Errorf("inc: by must be integer")
		}
		g.emit(Cmd{"op": "inc", "key": fields[0], "by": n})
		return nil

	case "wait":
		fields := splitArgs(args)
		ms := int64(500)
		if len(fields) > 0 {
			n, err := strconv.ParseInt(fields[0], 10, 64)
			if err != nil {
				return fmt.Errorf("wait: ms must be integer")
			}
			ms = n
		}
		g.emit(Cmd{"op": "wait", "ms": ms})
		return nil

	case "fade":
		fields := splitArgs(args)
		c := Cmd{"op": "fade", "to": "black", "duration": 0.5}
		if len(fields) > 0 {
			c["to"] = fields[0]
		}
		if len(fields) > 1 {
			if d, err := strconv.ParseFloat(fields[1], 64); err == nil {
				c["duration"] = d
			}
		}
		g.emit(c)
		return nil

	case "audio":
		fields := splitArgs(args)
		c := Cmd{"op": "audio"}
		if len(fields) > 0 {
			c["channel"] = fields[0]
		}
		if len(fields) > 1 {
			c["action"] = fields[1]
		}
		rest := fields[2:]
		if len(rest) > 0 && !strings.Contains(rest[0], "=") {
			c["url"] = rest[0]
			rest = rest[1:]
		}
		applyKV(c, rest)
		g.emit(c)
		return nil

	case "dim":
		fields := splitArgs(args)
		c := Cmd{"op": "dim", "alpha": 0.4, "duration": 0.5}
		if len(fields) > 0 {
			if v, err := strconv.ParseFloat(fields[0], 64); err == nil {
				c["alpha"] = v
			}
		}
		if len(fields) > 1 {
			if v, err := strconv.ParseFloat(fields[1], 64); err == nil {
				c["duration"] = v
			}
		}
		g.emit(c)
		return nil

	case "camera":
		fields := splitArgs(args)
		if len(fields) == 0 {
			return fmt.Errorf("camera tag expects an action (shake/zoom)")
		}
		c := Cmd{"op": "camera", "action": fields[0]}
		applyKV(c, fields[1:])
		g.emit(c)
		return nil

	case "particles":
		fields := splitArgs(args)
		if len(fields) == 0 {
			return fmt.Errorf("particles tag expects a type")
		}
		on := true
		if len(fields) > 1 && (fields[1] == "off" || fields[1] == "false") {
			on = false
		}
		g.emit(Cmd{"op": "particles", "type": fields[0], "on": on})
		return nil

	case "preload":
		var assets []any
		for _, u := range splitArgs(args) {
			kind := "sprite"
			low := strings.ToLower(u)
			if strings.HasSuffix(low, ".ogg") || strings.HasSuffix(low, ".mp3") || strings.HasSuffix(low, ".wav") {
				kind = "audio"
			}
			assets = append(assets, map[string]any{"url": u, "kind": kind})
		}
		if len(assets) == 0 {
			return fmt.Errorf("preload tag expects at least one URL")
		}
		g.emit(Cmd{"op": "preload", "assets": assets})
		return nil

	case "hint":
		if args == "off" {
			g.emit(Cmd{"op": "hint", "show": false})
		} else {
			g.emit(Cmd{"op": "hint", "text": args, "show": true})
		}
		return nil
	}
	return fmt.Errorf("unknown tag #%s (typo?)", name)
}

func splitArgs(s string) []string {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil
	}
	return regexp.MustCompile(`\s+`).Split(s, -1)
}

func applyKV(c Cmd, fields []string) {
	for _, f := range fields {
		eq := strings.Index(f, "=")
		if eq <= 0 {
			continue
		}
		c[f[:eq]] = coerce(f[eq+1:])
	}
}

func coerce(s string) any {
	switch s {
	case "true":
		return true
	case "false":
		return false
	case "null":
		return nil
	}
	if n, err := strconv.ParseInt(s, 10, 64); err == nil {
		return n
	}
	if f, err := strconv.ParseFloat(s, 64); err == nil {
		return f
	}
	if len(s) >= 2 && (s[0] == '"' && s[len(s)-1] == '"' || s[0] == '\'' && s[len(s)-1] == '\'') {
		return s[1 : len(s)-1]
	}
	return s
}
