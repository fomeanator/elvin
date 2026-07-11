import { describe, expect, it } from "vitest";
import { fmt, dt, authMsg } from "../src/components/adminShared.jsx";

describe("fmt", () => {
  it("formats numbers with ru-RU grouping", () => {
    // The group separator is a (narrow) no-break space depending on ICU.
    expect(fmt(1234)).toMatch(/^1[\s\u00a0\u202f]234$/u);
    expect(fmt(7)).toBe("7");
  });
  it("treats null/undefined as zero", () => {
    expect(fmt(null)).toBe("0");
    expect(fmt(undefined)).toBe("0");
  });
});

describe("dt", () => {
  it("trims an ISO timestamp to a readable date-time", () => {
    expect(dt("2026-07-11T09:15:42Z")).toBe("2026-07-11 09:15:42");
  });
  it("is safe on empty input", () => {
    expect(dt(null)).toBe("");
  });
});

describe("authMsg", () => {
  it("maps the typed 401 to the token hint", () => {
    expect(authMsg(new Error("401"))).toMatch(/токен/i);
  });
  it("passes other messages through", () => {
    expect(authMsg(new Error("boom"))).toBe("boom");
  });
  it("falls back on empty errors", () => {
    expect(authMsg(null)).toBe("ошибка");
  });
});
