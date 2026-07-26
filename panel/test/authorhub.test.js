import { describe, expect, it } from "vitest";
import {
  relOf, chapterFilesOf, chapterCountOf, buildFeed, importedAtOf, novelRows,
  stateOf, runPool, sourceOfLastChange,
} from "../src/lib/authorhub.js";

const title = {
  id: "novel", name: "Новелла", author: "aram",
  seasons: [{ chapters: [
    { id: "ch1", name: "Глава 1", script_url: "/content/scripts/novel/ch1.lvn" },
    { id: "ch2", name: "Глава 2", script_url: "/content/scripts/novel/ch2.lvn" },
  ] }],
};

describe("manifest → files", () => {
  it("strips the /content/ prefix the manifest carries", () => {
    expect(relOf("/content/scripts/novel/ch1.lvn")).toBe("scripts/novel/ch1.lvn");
    expect(relOf("scripts/x.lvn")).toBe("scripts/x.lvn");
    expect(relOf(undefined)).toBe("");
  });

  it("lists both the compiled chapter and its .lvns source", () => {
    const files = chapterFilesOf(title);
    expect(files.map((f) => f.rel)).toEqual([
      "scripts/novel/ch1.lvn", "scripts/novel/ch1.lvns",
      "scripts/novel/ch2.lvn", "scripts/novel/ch2.lvns",
    ]);
    expect(files[1].chapter).toBe("Глава 1");
  });

  it("skips paths the server has no history for", () => {
    // historyEligible() only covers scripts/**.lvn|.lvns — asking for art
    // would just 404, so the cabinet must not ask.
    const files = chapterFilesOf({ seasons: [{ chapters: [
      { script_url: "/content/art/bg.png" },
      { script_url: "/content/scripts/a.lvn" },
    ] }] });
    expect(files.map((f) => f.rel)).toEqual(["scripts/a.lvn", "scripts/a.lvns"]);
  });

  it("counts chapters across seasons", () => {
    expect(chapterCountOf(title)).toBe(2);
    expect(chapterCountOf({})).toBe(0);
  });
});

describe("buildFeed", () => {
  const history = {
    "scripts/novel/ch1.lvn": [{ ts: "1785044405381", size: 208 }, { ts: "1785044000000", size: 190 }],
    "scripts/novel/ch2.lvn": [{ ts: "1785044500000", size: 300 }],
  };
  it("merges every file into one newest-first timeline", () => {
    const feed = buildFeed(history, chapterFilesOf(title));
    expect(feed.map((r) => r.ts)).toEqual(["1785044500000", "1785044405381", "1785044000000"]);
  });
  it("flags the newest snapshot per file as the one-click undo", () => {
    const feed = buildFeed(history, chapterFilesOf(title));
    expect(feed.filter((r) => r.undoLast).map((r) => r.rel))
      .toEqual(["scripts/novel/ch2.lvn", "scripts/novel/ch1.lvn"]);
  });
  it("carries the previous snapshot's size so the row shows a direction", () => {
    const feed = buildFeed(history, chapterFilesOf(title));
    const newest = feed.find((r) => r.rel === "scripts/novel/ch1.lvn");
    expect(newest.size).toBe(208);
    expect(newest.prevSize).toBe(190);
    expect(feed[feed.length - 1].prevSize).toBe(null); // oldest has nothing before it
  });
  it("labels rows with the chapter name, not just a path", () => {
    expect(buildFeed(history, chapterFilesOf(title))[0].chapter).toBe("Глава 2");
  });
  it("is safe on an empty history", () => {
    expect(buildFeed({}, [])).toEqual([]);
  });
});

describe("sourceOfLastChange", () => {
  // An editorial write snapshots then writes (same instant); an import writes
  // through and never snapshots. That is the whole inference.
  it("calls a change editorial when mtime matches the newest snapshot", () => {
    expect(sourceOfLastChange("2026-07-26T05:40:05Z", Date.parse("2026-07-26T05:40:05Z"))).toBe("editor");
  });
  it("calls it an import when mtime is well past the newest snapshot", () => {
    expect(sourceOfLastChange("2026-07-26T09:00:00Z", Date.parse("2026-07-26T05:40:05Z"))).toBe("import");
  });
  it("calls a file with no history at all import-only", () => {
    expect(sourceOfLastChange("2026-07-26T09:00:00Z", "")).toBe("import");
  });
  it("does not guess when the mtime is unknown", () => {
    expect(sourceOfLastChange("", "")).toBe("unknown");
    expect(sourceOfLastChange("", 1785044405381)).toBe("editor");
  });
});

describe("novel rows", () => {
  const baselines = [{ name: "novel.json", dir: false, modified: "2026-07-26T05:38:45Z" }];
  it("joins manifest, conflict counts and the import baseline", () => {
    const [r] = novelRows([title], { novel: 3 }, baselines);
    expect(r).toMatchObject({ id: "novel", author: "aram", chapters: 2, conflicts: 3, importedAt: "2026-07-26T05:38:45Z" });
  });
  it("reports no baseline for a novel that was never imported", () => {
    expect(importedAtOf(baselines, "other")).toBe("");
    expect(importedAtOf(undefined, "novel")).toBe("");
  });
  it("lets conflicts outrank every other state", () => {
    expect(stateOf({ conflicts: 1, chapters: 0, author: "" })).toBe("conflicts");
    expect(stateOf({ conflicts: 0, chapters: 0 })).toBe("empty");
    expect(stateOf({ conflicts: 0, chapters: 2, author: "" })).toBe("unattributed");
    expect(stateOf({ conflicts: 0, chapters: 2, author: "aram" })).toBe("ok");
  });
});

describe("runPool", () => {
  it("keeps results in input order regardless of completion order", async () => {
    const out = await runPool([30, 10, 20, 0], 2, (ms) =>
      new Promise((res) => setTimeout(() => res(ms), ms)));
    expect(out).toEqual([30, 10, 20, 0]);
  });
  it("never exceeds the concurrency limit", async () => {
    let inFlight = 0, peak = 0;
    await runPool([1, 2, 3, 4, 5, 6, 7, 8], 3, async () => {
      inFlight++; peak = Math.max(peak, inFlight);
      await new Promise((r) => setTimeout(r, 1));
      inFlight--;
    });
    expect(peak).toBeLessThanOrEqual(3);
  });
  it("handles an empty list without hanging", async () => {
    expect(await runPool([], 5, async () => 1)).toEqual([]);
  });
});
