import { afterEach, describe, expect, it, vi } from "vitest";
import { adminFetch, adminUsers, adminDeleteAsset, putAsset } from "../src/lib/api.js";

// fetch is mocked per test — these are contract tests for the thin client:
// auth header, typed 401, JSON/text switching, and path encoding.

function mockFetch(impl) {
  const fn = vi.fn(impl);
  globalThis.fetch = fn;
  return fn;
}

const jsonResponse = (obj, status = 200) =>
  new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });

afterEach(() => {
  delete globalThis.fetch;
  vi.restoreAllMocks();
});

describe("adminFetch", () => {
  it("attaches the bearer token and parses JSON", async () => {
    const fn = mockFetch(async () => jsonResponse({ ok: true }));
    const out = await adminFetch("/v1/admin/users", "tok123");
    expect(out).toEqual({ ok: true });
    const [, opt] = fn.mock.calls[0];
    expect(opt.headers.Authorization).toBe("Bearer tok123");
  });

  it("throws the typed '401' error so the UI can prompt for a token", async () => {
    mockFetch(async () => new Response("unauthorized", { status: 401 }));
    await expect(adminFetch("/v1/admin/users", "bad")).rejects.toThrow(/^401$/);
  });

  it("surfaces the server's error text on non-auth failures", async () => {
    mockFetch(async () => new Response("grant failed: disk full\n", { status: 500 }));
    await expect(adminFetch("/v1/admin/grant", "tok")).rejects.toThrow("grant failed: disk full");
  });

  it("returns text for non-JSON responses", async () => {
    mockFetch(async () => new Response("plain body", { status: 200 }));
    await expect(adminFetch("/x", "tok")).resolves.toBe("plain body");
  });
});

describe("wrappers", () => {
  it("adminUsers unwraps to an array even when the field is missing", async () => {
    mockFetch(async () => jsonResponse({}));
    await expect(adminUsers("tok")).resolves.toEqual([]);
  });

  it("adminDeleteAsset percent-encodes segments but keeps '/' separators", async () => {
    const fn = mockFetch(async () => jsonResponse({ deleted: true }));
    await adminDeleteAsset("bg/night #2.png", "tok");
    const [url, opt] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/assets/bg/night%20%232.png");
    expect(opt.method).toBe("DELETE");
  });

  it("putAsset strips a leading /content/ prefix before uploading", async () => {
    const fn = mockFetch(async () => jsonResponse({ path: "scripts/ch1.lvn", bytes: 2 }));
    await putAsset("/content/scripts/ch1.lvn", "hi", "tok", "text/plain");
    const [url] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/assets/scripts/ch1.lvn");
  });
});
