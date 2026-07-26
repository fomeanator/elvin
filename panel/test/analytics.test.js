import { describe, expect, it } from "vitest";
import {
  windowQuery, windowLabel, pct, share, severity, funnelMax, biggestLeak,
  gapsPending, dropKindLabel,
} from "../src/lib/analytics.js";

describe("windowQuery", () => {
  // The three shapes parseAnalyticsWindow (server/analytics_report.go) accepts.
  it("renders a single day", () => {
    expect(windowQuery({ day: "2026-07-26" })).toBe("day=2026-07-26");
  });
  it("renders a rolling window", () => {
    expect(windowQuery({ days: 7 })).toBe("days=7");
  });
  it("renders an explicit range", () => {
    expect(windowQuery({ from: "2026-07-01", to: "2026-07-26" })).toBe("from=2026-07-01&to=2026-07-26");
  });
  it("sends nothing for the server default (today)", () => {
    expect(windowQuery({})).toBe("");
    expect(windowQuery(null)).toBe("");
  });
  it("does not send days=1 — that is the single-day shape", () => {
    expect(windowQuery({ days: 1, day: "2026-07-26" })).toBe("day=2026-07-26");
  });
  it("labels the window for the header", () => {
    expect(windowLabel({ days: 30 })).toBe("последние 30 дн.");
    expect(windowLabel({ day: "2026-07-26" })).toBe("2026-07-26");
    expect(windowLabel({ from: "2026-07-01", to: "2026-07-26" })).toBe("2026-07-01 — 2026-07-26");
  });
});

describe("pct", () => {
  it("renders the server's 0..1 ratios", () => {
    expect(pct(0.7791)).toBe("78%");
    expect(pct(0.0585, 1)).toBe("5.9%");
    expect(pct(1)).toBe("100%");
  });
  it("keeps 'no data' distinct from zero", () => {
    expect(pct(null)).toBe("—");
    expect(pct(undefined)).toBe("—");
    expect(pct(0)).toBe("0%");
  });
});

describe("share", () => {
  it("clamps to the track", () => {
    expect(share(5, 10)).toBe(0.5);
    expect(share(15, 10)).toBe(1);
    expect(share(-1, 10)).toBe(0);
  });
  it("returns 0 rather than NaN on an empty total", () => {
    expect(share(3, 0)).toBe(0);
  });
});

describe("severity", () => {
  // Both factors are required: a big rate on a tiny base is noise, a small
  // rate on a huge base is still a crowd.
  it("needs a real share AND real bodies for 'high'", () => {
    expect(severity({ rate: 0.5, lost: 3 })).toBe("med");   // rate only
    expect(severity({ rate: 0.5, lost: 30 })).toBe("high");
  });
  it("flags a big absolute loss even at a low rate", () => {
    expect(severity({ rate: 0.05, lost: 40 })).toBe("med");
  });
  it("leaves small leaks alone", () => {
    expect(severity({ rate: 0.1, lost: 4 })).toBe("low");
    expect(severity({})).toBe("low");
  });
});

describe("funnel helpers", () => {
  const steps = [{ starts: 40, finishes: 31 }, { starts: 26, finishes: 22 }, { starts: 14, finishes: 11 }];
  it("scales every bar against the widest step", () => {
    expect(funnelMax(steps)).toBe(40);
  });
  it("never divides by zero on an empty funnel", () => {
    expect(funnelMax([])).toBe(1);
  });
  it("summarises the worst leak from the server's ranked list", () => {
    const leak = biggestLeak({ dropoffs: [
      { chapter: "ch1", name: "Глава 1", kind: "in_chapter", lost: 9, rate: 0.225 },
      { chapter: "ch2", kind: "after_chapter", lost: 8, rate: 0.3636 },
    ] });
    expect(leak.chapter).toBe("Глава 1");
    expect(leak.text).toContain("9 игроков потеряно");
    expect(leak.text).toContain(dropKindLabel("in_chapter"));
  });
  it("returns null when nothing cleared the sample threshold", () => {
    expect(biggestLeak({ dropoffs: [] })).toBe(null);
    expect(biggestLeak({})).toBe(null);
  });
});

describe("gapsPending", () => {
  it("keeps only the blind spots that are still blind", () => {
    const gaps = [
      { event: "chapter_abandon", seen: 0 },
      { event: "props.sid", seen: 12 },
    ];
    expect(gapsPending(gaps).map((g) => g.event)).toEqual(["chapter_abandon"]);
  });
  it("is safe when the report carries no gaps", () => {
    expect(gapsPending(undefined)).toEqual([]);
  });
});
