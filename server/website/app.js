// app.js — loads the Go WASM build of the lvnconv pipeline (the SAME converter +
// validator the CLI uses, one source of truth), wires the chapter playground +
// "Save to app", and the sprite/entity manager (catalog of ids the manifest &
// scripts reference; the client resolves an id to its layers and composites).

let wasmReady = false;
async function initWasm() {
    try {
        const go = new Go();
        const res = await WebAssembly.instantiateStreaming(fetch("lvns.wasm"), go.importObject);
        go.run(res.instance);
        wasmReady = true;
    } catch (e) { console.error("wasm load failed", e); }
}

document.addEventListener("DOMContentLoaded", async () => {
    const editor = document.getElementById("editor");
    const output = document.getElementById("output");
    const statusBadge = document.getElementById("status-badge");
    let lastJson = "";

    function compile() {
        if (!wasmReady || typeof window.lvnsCompile !== "function") { output.textContent = "Loading compiler…"; return; }
        const r = window.lvnsCompile(editor.value);
        if (!r.ok) {
            output.textContent = r.errors || "Compilation error";
            output.classList.add("error-text");
            statusBadge.textContent = "✗ " + (r.errors ? r.errors.split("\n")[0] : "Error");
            statusBadge.className = "badge error";
            lastJson = "";
            return;
        }
        lastJson = r.json;
        output.textContent = r.json;
        output.classList.remove("error-text");
        if (r.warnings) { statusBadge.textContent = "⚠ " + r.warnings.split("\n").length + " warning(s)"; statusBadge.className = "badge warn"; statusBadge.title = r.warnings; }
        else { statusBadge.textContent = "✓ Compiled"; statusBadge.className = "badge success"; statusBadge.title = ""; }
    }
    editor.addEventListener("input", compile);

    document.getElementById("copy-btn")?.addEventListener("click", () => navigator.clipboard.writeText(output.textContent));
    document.getElementById("copy-ai-btn")?.addEventListener("click", () => navigator.clipboard.writeText(document.getElementById("ai-prompt-text").textContent));
    document.querySelectorAll(".try-btn").forEach((btn) => btn.addEventListener("click", () => {
        const codeEl = document.getElementById(btn.getAttribute("data-code-id"));
        if (codeEl) { editor.value = codeEl.textContent.trim(); compile(); document.getElementById("playground")?.scrollIntoView({ behavior: "smooth" }); }
    }));
    document.getElementById("docs-toggle")?.addEventListener("click", () => document.getElementById("sidebar")?.classList.toggle("hidden"));

    function setSave(msg, cls) {
        const s = document.getElementById("save-status");
        if (!s) return;
        s.textContent = msg;
        s.className = "save-status " + (cls || "");
    }

    // ── Chapter: Save to app (PUT compiled .lvn via admin → live reload) ──
    {
        const pathInput = document.getElementById("save-path");
        const tokenInput = document.getElementById("admin-token");
        if (pathInput) pathInput.value = localStorage.getItem("lvn_save_path") || "scripts/ch1.lvn";
        if (tokenInput) tokenInput.value = localStorage.getItem("lvn_admin_token") || "";
        document.getElementById("save-btn")?.addEventListener("click", async () => {
            if (!lastJson) { setSave("Nothing compiled to save.", "save-err"); return; }
            const path = (pathInput.value || "scripts/ch1.lvn").trim().replace(/^\/+/, "");
            const token = (tokenInput.value || "").trim();
            localStorage.setItem("lvn_save_path", path);
            localStorage.setItem("lvn_admin_token", token);
            setSave("Saving…", "");
            try {
                const resp = await fetch("/v1/admin/assets/" + path, { method: "PUT", headers: { "Authorization": "Bearer " + token, "Content-Type": "application/json" }, body: lastJson });
                if (resp.ok) { const d = await resp.json(); setSave(`✓ Saved ${d.path} (${d.bytes} B) — live in the app within ~2s`, "save-ok"); }
                else setSave(`✗ ${resp.status}: ${(await resp.text()).trim()}`, "save-err");
            } catch (e) { setSave("✗ " + e.message, "save-err"); }
        });
    }

    // ── Sprite / entity manager ──────────────────────────────────────────────
    {
        const chapterView = document.getElementById("chapter-view");
        const spritesView = document.getElementById("sprites-view");
        const vChapter = document.getElementById("view-chapter");
        const vSprites = document.getElementById("view-sprites");
        let catalog = {};
        let currentId = null;
        let entityAxes = {};   // axis -> [allowed values]
        let axisValues = {};   // axis -> currently picked value
        let loaded = false;
        let previewBust = Date.now();

        function showView(sprites) {
            spritesView.hidden = !sprites;
            chapterView.style.display = sprites ? "none" : "";
            const sb = document.getElementById("sidebar"); if (sb) sb.style.display = sprites ? "none" : "";
            vSprites.classList.toggle("active", sprites);
            vChapter.classList.toggle("active", !sprites);
            if (sprites && !loaded) loadCatalog();
        }
        vChapter?.addEventListener("click", () => showView(false));
        vSprites?.addEventListener("click", () => showView(true));

        async function loadCatalog() {
            loaded = true;
            try { catalog = (await (await fetch("/v1/content/manifest", { cache: "no-store" })).json()).sprites || {}; }
            catch { catalog = {}; }
            selectEntity(Object.keys(catalog)[0] || null);
        }

        function renderList() {
            const items = document.getElementById("sp-items");
            items.innerHTML = "";
            Object.keys(catalog).forEach((id) => {
                const e = catalog[id] || {};
                const kind = (e.layers || []).some((l) => (typeof l === "string" ? l : (l.url || "")).includes("{")) ? "cast" : "sprite";
                const div = document.createElement("div");
                div.className = "sp-item" + (id === currentId ? " active" : "");
                div.innerHTML = id + '<span class="sp-kind">' + kind + "</span>";
                div.onclick = () => selectEntity(id);
                items.appendChild(div);
            });
        }

        function layerRows() {
            return [...document.querySelectorAll("#sp-layers .sp-layer")]
                .map((r) => ({ url: r.querySelector(".sp-url").value.trim(), when: r.querySelector(".sp-when").value.trim() }))
                .filter((l) => l.url);
        }
        function tokens() {
            const set = new Set();
            layerRows().forEach((l) => (l.url.match(/\{([^}]+)\}/g) || []).forEach((t) => set.add(t.slice(1, -1))));
            return [...set];
        }
        function ensureAxesForTokens() { tokens().forEach((ax) => { if (!entityAxes[ax]) entityAxes[ax] = []; }); }

        function applyEntity(e) {
            document.getElementById("sp-id").value = currentId || e.id || "";
            document.getElementById("sp-name").value = e.name || "";
            document.getElementById("sp-color").value = e.color || "";
            entityAxes = {};
            Object.keys(e.axes || {}).forEach((ax) => entityAxes[ax] = (e.axes[ax] || []).slice());
            axisValues = {};
            Object.keys(e.defaults || {}).forEach((k) => axisValues[k] = e.defaults[k]);
            const box = document.getElementById("sp-layers"); box.innerHTML = "";
            const parts = e.parts || (e.layers && e.layers.length ? e.layers : [{}]);
            parts.forEach((l) => addLayerCard(typeof l === "string" ? { url: l } : l));
            ensureAxesForTokens();
            renderAxesEditor();
            renderPreviewControls();
            renderAllSlots();
            renderPreview();
            renderList();
        }
        function selectEntity(id) {
            currentId = id;
            applyEntity(id && catalog[id] ? catalog[id] : { layers: [{}], defaults: {}, axes: {} });
        }

        // Guided templates — start from a ready skeleton, never a blank form.
        const TEMPLATES = {
            simple: { parts: [{ name: "image" }] },
            character: {
                axes: { pose: ["standing"], emotion: ["neutral", "happy", "sad"] },
                defaults: { pose: "standing", emotion: "neutral" },
                parts: [{ name: "body", axis: "pose" }, { name: "face", axis: "emotion" }],
            },
        };
        function newEntity(kind) {
            currentId = null;
            applyEntity(JSON.parse(JSON.stringify(TEMPLATES[kind] || TEMPLATES.simple)));
            document.getElementById("sp-name").focus();
        }

        // axes editor — manage each axis's allowed values as chips
        function renderAxesEditor() {
            ensureAxesForTokens();
            const box = document.getElementById("sp-axes-edit"); box.innerHTML = "";
            Object.keys(entityAxes).forEach((ax) => {
                const row = document.createElement("div"); row.className = "sp-axis-edit";
                const head = document.createElement("div"); head.className = "sp-axis-edit-head";
                const nm = document.createElement("input"); nm.className = "sp-axis-name"; nm.value = ax; nm.title = "axis name (use as {" + ax + "} in a layer path)";
                nm.onchange = () => {
                    const v = nm.value.trim();
                    if (v && v !== ax) { entityAxes[v] = entityAxes[ax]; delete entityAxes[ax]; if (axisValues[ax] !== undefined) { axisValues[v] = axisValues[ax]; delete axisValues[ax]; } renderAxesEditor(); renderPreviewControls(); }
                };
                const del = document.createElement("button"); del.className = "rm"; del.textContent = "✕"; del.title = "remove axis";
                del.onclick = () => { delete entityAxes[ax]; delete axisValues[ax]; renderAxesEditor(); renderPreviewControls(); renderPreview(); };
                head.append(nm, del); row.appendChild(head);

                const chips = document.createElement("div"); chips.className = "sp-chips";
                (entityAxes[ax] || []).forEach((val) => {
                    const chip = document.createElement("span"); chip.className = "chip"; chip.textContent = val;
                    const x = document.createElement("button"); x.textContent = "×";
                    x.onclick = () => { entityAxes[ax] = entityAxes[ax].filter((v) => v !== val); renderAxesEditor(); renderPreviewControls(); renderPreview(); };
                    chip.appendChild(x); chips.appendChild(chip);
                });
                const add = document.createElement("input"); add.className = "sp-chip-add"; add.placeholder = "+ value, Enter";
                add.onkeydown = (ev) => { if (ev.key === "Enter") { const v = add.value.trim(); if (v && !entityAxes[ax].includes(v)) { entityAxes[ax].push(v); add.value = ""; renderAxesEditor(); renderPreviewControls(); } } };
                chips.appendChild(add);
                row.appendChild(chips);
                box.appendChild(row);
            });
        }
        const PRESETS = { pose: ["standing", "sitting"], emotion: ["neutral", "happy", "sad", "angry"] };
        function addState(name, values) {
            if (!entityAxes[name]) entityAxes[name] = [];
            (values || []).forEach((v) => { if (!entityAxes[name].includes(v)) entityAxes[name].push(v); });
            renderAxesEditor(); renderPreviewControls(); renderAllSlots();
        }
        document.querySelectorAll(".sp-preset-state").forEach((b) => {
            if (b.id === "sp-add-axis") b.onclick = () => { let n = "state", i = 1; while (entityAxes[n]) n = "state" + (++i); addState(n, []); };
            else b.onclick = () => addState(b.dataset.preset, PRESETS[b.dataset.preset]);
        });

        // preview controls — a dropdown per axis (pick a state)
        function renderPreviewControls() {
            ensureAxesForTokens();
            const box = document.getElementById("sp-axes"); box.innerHTML = "";
            Object.keys(entityAxes).forEach((ax) => {
                const wrap = document.createElement("div"); wrap.className = "sp-axis";
                const lab = document.createElement("label"); lab.textContent = ax; wrap.appendChild(lab);
                const vals = entityAxes[ax] || [];
                if (vals.length) {
                    const sel = document.createElement("select");
                    vals.forEach((v) => { const o = document.createElement("option"); o.value = v; o.textContent = v; sel.appendChild(o); });
                    if (axisValues[ax] === undefined || !vals.includes(axisValues[ax])) axisValues[ax] = vals[0];
                    sel.value = axisValues[ax];
                    sel.onchange = () => { axisValues[ax] = sel.value; renderPreview(); };
                    wrap.appendChild(sel);
                } else {
                    const inp = document.createElement("input"); inp.placeholder = "add values ←"; inp.value = axisValues[ax] || "";
                    inp.oninput = () => { axisValues[ax] = inp.value; renderPreview(); };
                    wrap.appendChild(inp);
                }
                box.appendChild(wrap);
            });
            renderAllSlots();
        }

        function axisOptionsHtml(selected) {
            let h = '<option value="">— doesn’t change —</option>';
            Object.keys(entityAxes).forEach((ax) => { h += `<option value="${ax}"${ax === selected ? " selected" : ""}>${ax}</option>`; });
            return h;
        }

        // A layer = one part of the character. Pick what it "varies by" (an axis)
        // and whether it's always shown or conditional; then click the per-value
        // slots to upload each image. The path is kept editable underneath.
        function slug(s) { return (s || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "") || "part"; }
        function deriveName(path) { const f = (path || "").split("/").pop() || ""; return f.replace(/_?\{[^}]+\}/, "").replace(/\.[^.]+$/, ""); }

        // Auto-build the file path for a part from its name + which state it changes
        // with — the editor never types a path. Keeps the existing folder/extension
        // when editing; defaults to /content/sprites/<id>/ for new parts.
        function regenPath(card) {
            const id = slug(document.getElementById("sp-id").value) || "entity";
            const name = slug(card.querySelector(".sp-partname").value);
            const axis = card.querySelector(".sp-vary").value;
            const cur = card.querySelector(".sp-url").value;
            let dir = "/content/sprites/" + id, ext = ".png";
            const slash = cur.lastIndexOf("/");
            // keep a custom folder for hand-authored content; default parts follow the id
            if (slash > 0 && !cur.startsWith("/content/sprites/")) { dir = cur.slice(0, slash); const dot = cur.lastIndexOf("."); if (dot > slash) ext = cur.slice(dot); }
            card.querySelector(".sp-url").value = dir + "/" + name + (axis ? "_{" + axis + "}" : "") + ext;
        }
        function regenAllPaths() { document.querySelectorAll("#sp-layers .sp-layer").forEach(regenPath); renderAllSlots(); renderPreview(); }

        // A part = one piece of the character's art (Body, Face, Blush…). The editor
        // names it, picks what it changes with, and drops images into the slots.
        function addLayerCard(layer) {
            layer = layer || {};
            const box = document.getElementById("sp-layers");
            const card = document.createElement("div"); card.className = "sp-layer";
            const tok = (layer.url || "").match(/\{([^}]+)\}/);

            const top = document.createElement("div"); top.className = "sp-part-top";
            const name = document.createElement("input"); name.className = "sp-partname";
            name.placeholder = "Part — Body, Face, Blush…"; name.value = layer.name || (layer.url ? deriveName(layer.url) : "");
            const varyWrap = document.createElement("label"); varyWrap.className = "sp-inline"; varyWrap.textContent = "changes with ";
            const vary = document.createElement("select"); vary.className = "sp-vary"; vary.innerHTML = axisOptionsHtml(layer.axis || (tok ? tok[1] : ""));
            varyWrap.appendChild(vary);
            const cond = document.createElement("button"); cond.className = "sp-cond-btn"; cond.textContent = layer.when ? "◆ condition" : "+ condition";
            cond.title = "show this part only on a condition (advanced)";
            const rm = document.createElement("button"); rm.className = "rm"; rm.textContent = "✕"; rm.title = "remove part";
            top.append(name, varyWrap, cond, rm);
            card.appendChild(top);

            const url = document.createElement("input"); url.className = "sp-url"; url.type = "hidden"; url.value = layer.url || "";
            const when = document.createElement("input"); when.className = "save-input sp-when"; when.value = layer.when || "";
            when.placeholder = "only when… e.g. warmth >= 1"; when.style.display = layer.when ? "" : "none";
            card.append(url, when);

            const slots = document.createElement("div"); slots.className = "sp-slots"; card.appendChild(slots);

            name.addEventListener("input", () => { regenPath(card); refreshAfterLayerChange(); });
            vary.onchange = () => { regenPath(card); refreshAfterLayerChange(); };
            cond.onclick = () => { const hidden = when.style.display === "none"; when.style.display = hidden ? "" : "none"; cond.textContent = hidden ? "◆ condition" : "+ condition"; if (!hidden) when.value = ""; renderPreview(); };
            when.addEventListener("input", renderPreview);
            rm.onclick = () => { card.remove(); refreshAfterLayerChange(); };

            if (!layer.url && name.value) regenPath(card);
            box.appendChild(card);
            renderSlots(card);
        }
        document.getElementById("sp-add-layer").onclick = () => addLayerCard({});

        function refreshAfterLayerChange() { renderAxesEditor(); renderPreviewControls(); renderPreview(); renderAllSlots(); }

        function renderAllSlots() { document.querySelectorAll("#sp-layers .sp-layer").forEach(renderSlots); }

        function renderSlots(card) {
            const url = card.querySelector(".sp-url").value.trim();
            const box = card.querySelector(".sp-slots"); box.innerHTML = "";
            const m = url.match(/\{([^}]+)\}/);
            if (m) {
                const axis = m[1], vals = entityAxes[axis] || [];
                if (!vals.length) { const n = document.createElement("span"); n.className = "sp-slot-note"; n.textContent = "add values to “" + axis + "” above to get upload slots"; box.appendChild(n); return; }
                vals.forEach((v) => box.appendChild(makeSlot(url.replace(/\{[^}]+\}/, v), v)));
            } else if (url) {
                box.appendChild(makeSlot(url, "image"));
            }
        }

        function makeSlot(resolvedUrl, label) {
            const slot = document.createElement("div"); slot.className = "sp-slot empty"; slot.title = "click to upload  " + resolvedUrl;
            const img = document.createElement("img"); img.src = resolvedUrl + "?v=" + previewBust;
            img.onload = () => slot.classList.remove("empty");
            img.onerror = () => slot.classList.add("empty");
            const lab = document.createElement("span"); lab.className = "sp-slot-lab"; lab.textContent = label;
            slot.append(img, lab);
            slot.onclick = () => {
                const rel = resolvedUrl.replace(/^\/+content\/+/, "");
                const picker = document.createElement("input"); picker.type = "file"; picker.accept = "image/*";
                picker.onchange = async () => { const f = picker.files && picker.files[0]; if (f) { await uploadImage(rel, f); renderAllSlots(); renderPreview(); } };
                picker.click();
            };
            return slot;
        }

        function fill(template) {
            let missing = false;
            const out = template.replace(/\{([^}]+)\}/g, (_, k) => { const v = axisValues[k]; if (!v) { missing = true; return "{" + k + "}"; } return v; });
            return missing ? null : out;
        }

        function renderPreview() {
            const stage = document.getElementById("sp-stage"); stage.innerHTML = "";
            const notes = [];
            layerRows().forEach((layer) => {
                const url = fill(layer.url);
                if (!url) { notes.push("pick a value for: " + layer.url); return; }
                const img = document.createElement("img"); img.className = "sp-layer-img";
                img.src = url + (url.includes("?") ? "&" : "?") + "v=" + previewBust;
                img.title = url + (layer.when ? "  ·  when " + layer.when : "");
                if (layer.when) { img.style.opacity = "0.6"; notes.push("conditional (when " + layer.when + ")"); }
                stage.appendChild(img);
            });
            if (notes.length) { const m = document.createElement("div"); m.className = "sp-miss"; m.textContent = notes.join("\n"); stage.appendChild(m); }
        }

        function pickAndUpload(urlInput) {
            const target = fill((urlInput.value || "").trim());
            if (!target) { setSave("Pick the state (axes) so the path resolves, then upload.", "save-err"); return; }
            const picker = document.createElement("input"); picker.type = "file"; picker.accept = "image/*";
            picker.onchange = async () => { const f = picker.files && picker.files[0]; if (f) await uploadImage(target.replace(/^\/+content\/+/, ""), f); };
            picker.click();
        }
        async function uploadImage(relPath, file) {
            const token = (document.getElementById("admin-token").value || "").trim();
            setSave("Uploading " + relPath + " …", "");
            try {
                const resp = await fetch("/v1/admin/assets/" + relPath, { method: "PUT", headers: { "Authorization": "Bearer " + token, "Content-Type": file.type || "application/octet-stream" }, body: file });
                if (resp.ok) { const d = await resp.json(); previewBust = Date.now(); renderPreview(); setSave(`✓ Uploaded ${d.path} (${(d.bytes / 1024).toFixed(1)} KB)`, "save-ok"); }
                else setSave(`✗ ${resp.status}: ${(await resp.text()).trim()}`, "save-err");
            } catch (e) { setSave("✗ " + e.message, "save-err"); }
        }

        document.getElementById("sp-new").onclick = () => selectEntity(null);
        document.getElementById("sp-delete").onclick = () => { if (currentId && catalog[currentId]) { delete catalog[currentId]; selectEntity(Object.keys(catalog)[0] || null); } };

        document.getElementById("sp-save").onclick = async () => {
            const id = document.getElementById("sp-id").value.trim();
            if (!id) { setSave("Entity needs an id.", "save-err"); return; }
            const layers = layerRows().map((l) => (l.when ? { url: l.url, when: l.when } : l.url));
            const defaults = {}; Object.keys(axisValues).forEach((k) => { if (axisValues[k]) defaults[k] = axisValues[k]; });
            const axes = {}; Object.keys(entityAxes).forEach((k) => { if ((entityAxes[k] || []).length) axes[k] = entityAxes[k]; });
            const name = document.getElementById("sp-name").value.trim();
            const color = document.getElementById("sp-color").value.trim();
            if (currentId && currentId !== id) delete catalog[currentId];
            const e = { layers };
            if (name) e.name = name;
            if (color) e.color = color;
            if (Object.keys(axes).length) e.axes = axes;
            if (Object.keys(defaults).length) e.defaults = defaults;
            catalog[id] = e; currentId = id; renderList();
            const token = (document.getElementById("admin-token").value || "").trim();
            try {
                const m = await (await fetch("/v1/content/manifest", { cache: "no-store" })).json();
                m.sprites = catalog;
                const resp = await fetch("/v1/admin/assets/manifest.json", { method: "PUT", headers: { "Authorization": "Bearer " + token, "Content-Type": "application/json" }, body: JSON.stringify(m, null, 2) });
                if (resp.ok) setSave(`✓ Saved catalog (${Object.keys(catalog).length} entities) — live in ~2s`, "save-ok");
                else setSave(`✗ ${resp.status}: ${(await resp.text()).trim()}`, "save-err");
            } catch (e) { setSave("✗ " + e.message, "save-err"); }
        };
    }

    await initWasm();
    compile();
});
