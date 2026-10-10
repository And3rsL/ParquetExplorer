// ParquetExplorer client helpers: CodeMirror SQL editor + small UI utilities used through JS interop.
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, dropCursor, rectangularSelection, crosshairCursor, placeholder } from "@codemirror/view";
import { EditorState, Compartment, Prec } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { syntaxHighlighting, defaultHighlightStyle, bracketMatching, indentOnInput, foldGutter } from "@codemirror/language";
import { highlightSelectionMatches, searchKeymap } from "@codemirror/search";
import { sql, SQLDialect, PostgreSQL } from "@codemirror/lang-sql";
import { oneDark } from "@codemirror/theme-one-dark";

const duckdbKeywords =
  PostgreSQL.spec.keywords +
  " summarize describe pivot unpivot qualify exclude replace columns read_parquet read_csv read_json glob parquet_metadata parquet_schema" +
  " asof positional anti semi struct_pack list_value unnest range generate_series sample using sample reservoir bernoulli system";

const DuckDB = SQLDialect.define({ ...PostgreSQL.spec, keywords: duckdbKeywords });

const editors = new Map();
let nextId = 1;
let currentSchema = {};
let darkMode = document.documentElement.classList.contains("dark");

const languageConf = new Compartment();
const themeConf = new Compartment();

function language() {
  return sql({ dialect: DuckDB, schema: currentSchema, upperCaseKeywords: true });
}

const baseTheme = EditorView.theme({
  "&": { height: "100%", fontSize: "13px" },
  ".cm-scroller": { fontFamily: "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace", lineHeight: "1.6" },
  ".cm-content": { padding: "8px 0" },
  ".cm-gutters": { border: "none" },
  "&.cm-focused": { outline: "none" },
});

const lightTheme = EditorView.theme({
  "&": { backgroundColor: "transparent", color: "#0f172a" },
  ".cm-gutters": { backgroundColor: "transparent", color: "#94a3b8" },
  ".cm-activeLine": { backgroundColor: "rgba(99,102,241,0.06)" },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "#4f46e5" },
}, { dark: false });

const darkTheme = [oneDark, EditorView.theme({
  "&": { backgroundColor: "transparent !important" },
  ".cm-gutters": { backgroundColor: "transparent !important", color: "#475569" },
  ".cm-activeLine": { backgroundColor: "rgba(129,140,248,0.08)" },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "#a5b4fc" },
}, { dark: true })];

function themeExt() {
  return darkMode ? darkTheme : [lightTheme, syntaxHighlighting(defaultHighlightStyle)];
}

function create(element, dotnetRef, doc) {
  const id = nextId++;
  const run = () => { dotnetRef.invokeMethodAsync("ExecuteFromEditor"); return true; };
  const view = new EditorView({
    parent: element,
    state: EditorState.create({
      doc: doc || "",
      extensions: [
        lineNumbers(), highlightActiveLineGutter(), foldGutter(), history(), drawSelection(), dropCursor(),
        EditorState.allowMultipleSelections.of(true), indentOnInput(), bracketMatching(), closeBrackets(),
        autocompletion({ activateOnTyping: true }), rectangularSelection(), crosshairCursor(), highlightActiveLine(),
        highlightSelectionMatches(), placeholder("-- Write a query and press Ctrl+Enter (or F5)…"),
        Prec.highest(keymap.of([
          { key: "Mod-Enter", run },
          { key: "F5", run, preventDefault: true },
          { key: "Shift-Enter", run },
        ])),
        keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...completionKeymap, indentWithTab]),
        languageConf.of(language()),
        themeConf.of(themeExt()),
        baseTheme,
        EditorView.updateListener.of(u => { if (u.docChanged) scheduleSave(); }),
      ],
    }),
  });
  editors.set(id, { view, dotnetRef });
  return id;
}

/** Selected text if any (SSMS behaviour), otherwise the whole document. */
function getRunnableText(id) {
  const e = editors.get(id);
  if (!e) return "";
  const { state } = e.view;
  const sel = state.selection.ranges.filter(r => !r.empty).map(r => state.sliceDoc(r.from, r.to));
  return sel.length ? sel.join("\n") : state.doc.toString();
}

function getDoc(id) {
  const e = editors.get(id);
  return e ? e.view.state.doc.toString() : "";
}

function setDoc(id, text) {
  const e = editors.get(id);
  if (!e) return;
  e.view.dispatch({ changes: { from: 0, to: e.view.state.doc.length, insert: text } });
}

function insertText(id, text) {
  const e = editors.get(id);
  if (!e) return;
  const { from, to } = e.view.state.selection.main;
  e.view.dispatch({ changes: { from, to, insert: text }, selection: { anchor: from + text.length } });
  e.view.focus();
}

function focus(id) {
  const e = editors.get(id);
  if (e) requestAnimationFrame(() => e.view.focus());
}

function destroy(id) {
  const e = editors.get(id);
  if (!e) return;
  e.view.destroy();
  editors.delete(id);
}

function setSchema(schema) {
  currentSchema = schema || {};
  for (const { view } of editors.values()) view.dispatch({ effects: languageConf.reconfigure(language()) });
}

// ---- Theme ---------------------------------------------------------------------------------
function setDark(dark) {
  darkMode = !!dark;
  document.documentElement.classList.toggle("dark", darkMode);
  try { localStorage.setItem("px-theme", darkMode ? "dark" : "light"); } catch { }
  for (const { view } of editors.values()) view.dispatch({ effects: themeConf.reconfigure(themeExt()) });
}
function isDark() { return darkMode; }

// ---- Tab persistence (per browser) -----------------------------------------------------------
let saveTimer = null;
let tabProvider = null;
function registerTabProvider(dotnetRef) { tabProvider = dotnetRef; }
function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => { if (tabProvider) tabProvider.invokeMethodAsync("PersistTabs").catch(() => { }); }, 800);
}
function saveTabs(key, json) { try { localStorage.setItem(key, json); } catch { } }
function loadTabs(key) { try { return localStorage.getItem(key); } catch { return null; } }

// ---- Export / clipboard ----------------------------------------------------------------------
async function exportQuery(sqlText, format) {
  const res = await fetch("api/export", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "same-origin",
    body: JSON.stringify({ sql: sqlText, format }),
  });
  if (!res.ok) {
    const txt = await res.text();
    try { return JSON.parse(txt); } catch { return txt || res.statusText; }
  }
  const blob = await res.blob();
  const cd = res.headers.get("Content-Disposition") || "";
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = m ? decodeURIComponent(m[1]) : "export." + format;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
  return null;
}

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; }
  catch {
    const ta = document.createElement("textarea");
    ta.value = text; document.body.appendChild(ta); ta.select();
    const ok = document.execCommand("copy"); ta.remove(); return ok;
  }
}

// ---- Resizable panes -------------------------------------------------------------------------
/** Dragging `handle` resizes `target` along `axis` ("x" = width, "y" = height). Size saved in localStorage. */
function initSplitter(handle, target, axis, storageKey, min, max) {
  if (!handle || !target || handle.dataset.pxInit) return;
  handle.dataset.pxInit = "1";
  const prop = axis === "x" ? "width" : "height";
  try { const saved = localStorage.getItem(storageKey); if (saved) target.style[prop] = saved; } catch { }
  handle.addEventListener("pointerdown", ev => {
    ev.preventDefault();
    handle.setPointerCapture(ev.pointerId);
    const start = axis === "x" ? ev.clientX : ev.clientY;
    const startSize = target.getBoundingClientRect()[prop];
    document.body.classList.add(axis === "x" ? "cursor-col-resize" : "cursor-row-resize", "select-none");
    const move = e => {
      const delta = (axis === "x" ? e.clientX : e.clientY) - start;
      const size = Math.min(Math.max(startSize + delta, min), max || 10000);
      target.style[prop] = size + "px";
    };
    const up = () => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", up);
      document.body.classList.remove("cursor-col-resize", "cursor-row-resize", "select-none");
      try { localStorage.setItem(storageKey, target.style[prop]); } catch { }
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", up);
  });
}

// ---- Global shortcuts / context menu helpers ----------------------------------------------------
function registerShortcuts(dotnetRef) {
  window.addEventListener("keydown", e => {
    if (e.key === "F5" || ((e.ctrlKey || e.metaKey) && e.key === "Enter")) {
      if (document.activeElement && document.activeElement.closest(".cm-editor")) return; // handled by the editor
      e.preventDefault();
      dotnetRef.invokeMethodAsync("ExecuteFromEditor");
    } else if ((e.ctrlKey || e.metaKey) && e.altKey && e.key.toLowerCase() === "n") {
      e.preventDefault();
      dotnetRef.invokeMethodAsync("NewTabFromShortcut");
    } else if (e.key === "Escape") {
      dotnetRef.invokeMethodAsync("CloseMenus");
    }
  });
}

function scrollTop(el) { if (el) el.scrollTop = 0; }

window.pxEditor = {
  create, getRunnableText, getDoc, setDoc, insertText, focus, destroy, setSchema,
  setDark, isDark, registerTabProvider, saveTabs, loadTabs, exportQuery, copyText,
  initSplitter, registerShortcuts, scrollTop,
};
