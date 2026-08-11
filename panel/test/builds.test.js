import { afterEach, describe, expect, it, vi } from "vitest";
import { adminBuilds, adminRegisterBuild, adminDeleteBuild, downloadBuild } from "../src/lib/api.js";

// Контракт тонкого клиента сборок: токен в заголовке, путь с id, и — главное —
// скачивание через fetch, а не ссылкой: у входа по токену нет cookie, и
// простой <a href> вернул бы 401 вместо файла.

const jsonResponse = (obj, status = 200) =>
  new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json" } });

afterEach(() => {
  delete globalThis.fetch;
  vi.restoreAllMocks();
});

describe("сборки", () => {
  it("список приходит с бирером", async () => {
    const fn = vi.fn(async () => jsonResponse({ builds: [], latest: null }));
    globalThis.fetch = fn;
    await expect(adminBuilds("tok")).resolves.toEqual({ builds: [], latest: null });
    const [url, opt] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/builds");
    expect(opt.headers.Authorization).toBe("Bearer tok");
  });

  it("регистрация шлёт путь залитого файла и версию", async () => {
    const fn = vi.fn(async () => jsonResponse({ id: "android-1.4.2-1.apk" }));
    globalThis.fetch = fn;
    await adminRegisterBuild({ path: "/srv/uploads/app.apk", version: "1.4.2", notes: "правки" }, "tok");
    const [, opt] = fn.mock.calls[0];
    expect(opt.method).toBe("POST");
    expect(JSON.parse(opt.body)).toEqual({ path: "/srv/uploads/app.apk", version: "1.4.2", notes: "правки" });
  });

  it("удаление кодирует id в пути", async () => {
    const fn = vi.fn(async () => jsonResponse({ ok: true }));
    globalThis.fetch = fn;
    await adminDeleteBuild("android-1.4 бета.apk", "tok");
    expect(fn.mock.calls[0][0]).toBe("/v1/admin/builds/" + encodeURIComponent("android-1.4 бета.apk"));
    expect(fn.mock.calls[0][1].method).toBe("DELETE");
  });

  it("скачивание идёт с заголовком авторизации и кладёт файл под своим именем", async () => {
    const fn = vi.fn(async () => new Response(new Blob(["apk"]), { status: 200 }));
    globalThis.fetch = fn;
    const url = "blob:test";
    globalThis.URL.createObjectURL = vi.fn(() => url);
    globalThis.URL.revokeObjectURL = vi.fn();
    // Тесты идут без DOM (jsdom в проекте не подключён), поэтому document —
    // заглушка ровно на те три вызова, которые делает downloadBuild.
    const clicked = [];
    globalThis.document = {
      createElement: () => ({
        click() { clicked.push({ href: this.href, download: this.download }); },
        remove() {},
      }),
      body: { appendChild() {} },
    };

    await downloadBuild("android-1.4.2-99.apk", "android-1.4.2.apk", "tok");
    delete globalThis.document;

    expect(fn.mock.calls[0][1].headers.Authorization).toBe("Bearer tok");
    expect(clicked).toHaveLength(1);
    expect(clicked[0].download).toBe("android-1.4.2.apk");
    expect(globalThis.URL.revokeObjectURL).toHaveBeenCalledWith(url);
  });

  it("отказ сервера доходит до вызывающего, а не молча качает пустоту", async () => {
    globalThis.fetch = vi.fn(async () => new Response("нет такой сборки", { status: 404 }));
    await expect(downloadBuild("нет", "f.apk", "tok")).rejects.toThrow(/404/);
  });
});
