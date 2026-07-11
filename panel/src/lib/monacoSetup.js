import { loader } from "@monaco-editor/react";
import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import jsonWorker from "monaco-editor/esm/vs/language/json/json.worker?worker";

// One Monaco bootstrap for the whole app (npm bundle, local workers — offline).
// Imported by BOTH the lvns script editor and the admin JSON editors, so
// whichever loads first wires the full worker set (a per-file setup would let
// the later import silently drop the other's worker).
self.MonacoEnvironment = {
  getWorker: (_id, label) => (label === "json" ? new jsonWorker() : new editorWorker()),
};
loader.config({ monaco });

export { monaco };
