import { describe, expect, it } from "vitest";
import {
  parseUnifiedDiff, isImagePath, kindOf, titleOf, groupByTitle, countByTitle,
  sizeDelta, canKeepMine, previewUrl, shortSha, summarizeWrite,
} from "../src/lib/conflicts.js";

// The fixtures below are REAL responses: captured from a running server
// (server/import_conflicts.go) against a planted conflict pair, not invented.
const textConflict = {
  rel: "scripts/novel/ch1.lvn",
  incoming_rel: "scripts/novel/ch1.lvn.incoming",
  mine: { exists: true, size: 41, modified: "2026-07-26T05:38:45Z", sha: "3996e27a683469e2c589f4eaa769221c539fd81e6b7fbd707e387e87632c29f4" },
  incoming: { exists: true, size: 37, modified: "2026-07-26T05:38:45Z", sha: "13498bd611b4e6e72e7908dee6e8bb3931773c2d6364ca6dd56a44968bc05304" },
  text: true,
  titles: ["novel"],
  diff: '--- scripts/novel/ch1.lvn (mine)\n+++ scripts/novel/ch1.lvn.incoming (incoming)\n@@ -1,2 +1,2 @@\n label start\n-say Hero "мой текст"\n+say Hero "incoming text"\n',
  undoable: true,
};

const binaryConflict = {
  rel: "art/bg.png",
  incoming_rel: "art/bg.png.incoming",
  mine: { exists: true, size: 75, modified: "2026-07-26T05:38:45Z", sha: "57cda64cead0869c" },
  incoming: { exists: true, size: 73, modified: "2026-07-26T05:38:45Z", sha: "5c1940856c064a85" },
  text: false,
  titles: ["novel"],
  diff_note: "binary content — compare by size and time (mine 75 bytes, incoming 73 bytes)",
  undoable: false,
};

describe("parseUnifiedDiff", () => {
  it("splits the server's diff into typed rows", () => {
    const d = parseUnifiedDiff(textConflict.diff);
    expect(d.rows.map((r) => r.kind)).toEqual(["file", "file", "hunk", "ctx", "del", "add"]);
    expect(d.rows[4].text).toBe('say Hero "мой текст"');
    expect(d.rows[5].text).toBe('say Hero "incoming text"');
  });

  it("does NOT count the +++/--- header lines as additions/deletions", () => {
    // The header starts with the same characters as a real change; counting it
    // would report "+1 −1" on a diff that changed nothing.
    const d = parseUnifiedDiff(textConflict.diff);
    expect(d.added).toBe(1);
    expect(d.removed).toBe(1);
  });

  it("is safe on empty/absent diffs (binary conflicts carry none)", () => {
    expect(parseUnifiedDiff("").rows).toEqual([]);
    expect(parseUnifiedDiff(undefined).added).toBe(0);
  });

  it("keeps blank context lines renderable", () => {
    const d = parseUnifiedDiff("@@ -1,2 +1,2 @@\n \n+x\n");
    expect(d.rows[1]).toEqual({ kind: "ctx", text: "" });
    expect(d.added).toBe(1);
  });
});

describe("classification", () => {
  it("recognises previewable images by the author-side extension", () => {
    expect(isImagePath("art/bg.png")).toBe(true);
    expect(isImagePath("art/bg.PNG")).toBe(true);
    expect(isImagePath("scripts/a.lvn")).toBe(false);
  });
  it("labels the file kinds the screen shows", () => {
    expect(kindOf("scripts/x/ch1.lvn")).toBe("script");
    expect(kindOf("scripts/x/ch1.lvns")).toBe("source");
    expect(kindOf("manifest.json")).toBe("manifest");
    expect(kindOf("art/bg.png")).toBe("art");
    expect(kindOf("audio/x.ogg")).toBe("binary");
  });
});

describe("grouping", () => {
  it("prefers the server's baseline-derived title over the path", () => {
    expect(titleOf(textConflict)).toBe("novel");
  });
  it("falls back to scripts/<id>/ for a path no import tracked", () => {
    expect(titleOf({ rel: "scripts/other/ch2.lvn", titles: [] })).toBe("other");
    expect(titleOf({ rel: "ui/logo.png" })).toBe("");
  });
  it("groups and counts by title, untitled last", () => {
    const rows = [textConflict, binaryConflict, { rel: "ui/logo.png", titles: [] }];
    const g = groupByTitle(rows);
    expect(g.map((x) => x.title)).toEqual(["novel", ""]);
    expect(g[0].rows).toHaveLength(2);
    expect(countByTitle(rows)).toEqual({ novel: 2, "": 1 });
  });
});

describe("row helpers", () => {
  it("reports the size delta signed towards the incoming version", () => {
    expect(sizeDelta(textConflict)).toBe(-4);
    expect(sizeDelta({})).toBe(0);
  });
  it("refuses 'keep mine' when the author's file is gone", () => {
    expect(canKeepMine(textConflict)).toBe(true);
    expect(canKeepMine({ mine: { exists: false }, incoming: { exists: true } })).toBe(false);
  });
  it("builds cache-busted preview URLs for both sides", () => {
    expect(previewUrl(binaryConflict, "mine")).toBe("/content/art/bg.png?v=57cda64cead0");
    expect(previewUrl(binaryConflict, "incoming")).toBe("/content/art/bg.png.incoming?v=5c1940856c06");
  });
  it("percent-encodes path segments but keeps separators", () => {
    expect(previewUrl({ rel: "art/night #2.png", mine: {} }, "mine")).toBe("/content/art/night%20%232.png");
  });
  it("shortens a sha for the table", () => {
    expect(shortSha(textConflict.mine.sha)).toBe("3996e27a");
    expect(shortSha(undefined)).toBe("");
  });
});

describe("summarizeWrite", () => {
  const report = {
    files: [
      { rel: "scripts/n/ch1.lvn", status: "conflict", incoming: "scripts/n/ch1.lvn.incoming" },
      { rel: "scripts/n/ch2.lvn", status: "updated" },
      { rel: "scripts/n/ch3.lvn", status: "unchanged" },
      { rel: "scripts/n/ch4.lvn", status: "kept_local" },
      { rel: "art/a.png", status: "new" },
    ],
    conflicts: ["scripts/n/ch1.lvn"],
  };
  it("counts every status the importer can return", () => {
    const s = summarizeWrite(report);
    expect(s.counts).toEqual({ new: 1, updated: 1, unchanged: 1, kept_local: 1, conflict: 1 });
    expect(s.total).toBe(5);
  });
  it("uses the report's own conflict list", () => {
    expect(summarizeWrite(report).conflicts).toEqual(["scripts/n/ch1.lvn"]);
  });
  it("derives conflicts from statuses when the list is absent", () => {
    const s = summarizeWrite({ files: report.files });
    expect(s.conflicts).toEqual(["scripts/n/ch1.lvn"]);
  });
  it("survives an import response with no write report at all", () => {
    expect(summarizeWrite(undefined).total).toBe(0);
    expect(summarizeWrite(undefined).conflicts).toEqual([]);
  });
});
