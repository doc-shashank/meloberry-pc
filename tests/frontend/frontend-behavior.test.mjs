import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const jsRoot = path.resolve(__dirname, "../../src/Meloberry.Wrapper.Infrastructure/WebAssets/js");
const toModuleUrl = rel => pathToFileURL(path.join(jsRoot, rel)).href;

// Minimal DOM harness for executing real browser ES modules in Node
class FakeClassList {
  constructor(el) { this.el = el; this.set = new Set(); }
  _syncFromString(str) {
    this.set = new Set(String(str || "").split(/\s+/).filter(Boolean));
  }
  _toString() { return Array.from(this.set).join(" "); }
  add(...names) { names.forEach(n => n && this.set.add(n)); }
  remove(...names) { names.forEach(n => this.set.delete(n)); }
  contains(n) { return this.set.has(n); }
  toggle(n, force) {
    if (force === undefined) {
      if (this.set.has(n)) { this.set.delete(n); return false; }
      this.set.add(n); return true;
    }
    if (force) this.set.add(n); else this.set.delete(n);
    return !!force;
  }
}

class FakeElement {
  constructor(tagName = "div", id = "") {
    this.tagName = tagName.toUpperCase();
    this.id = id;
    this.children = [];
    this.parentElement = null;
    this.dataset = {};
    this.attributes = new Map();
    this.style = { setProperty() {} };
    this.classList = new FakeClassList(this);
    this.listeners = new Map();
    this._innerHTML = "";
    this.textContent = "";
    this.value = "";
    this.checked = false;
    this.disabled = false;
    this.open = false;
    this.title = "";
    this.src = "";
    this.type = "";
    this.offsetWidth = 100;
    this.options = [];
    this.onclick = null;
    this.onchange = null;
    this.oninput = null;
    this.onpointerdown = null;
  }
  get isConnected() {
    let cur = this;
    while (cur) {
      if (cur === rootDocEl) return true;
      cur = cur.parentElement;
    }
    return false;
  }
  get className() { return this.classList._toString(); }
  set className(v) { this.classList._syncFromString(v); }
  get innerHTML() { return this._innerHTML; }
  set innerHTML(v) {
    this._innerHTML = String(v || "");
    if (this._innerHTML === "") {
      this.children = [];
      this.options = [];
    }
  }
  appendChild(child) {
    if (!child) return child;
    child.parentElement = this;
    this.children.push(child);
    if (this.tagName === "SELECT" && child.tagName === "OPTION") this.options.push(child);
    return child;
  }
  append(...nodes) { nodes.forEach(n => this.appendChild(n)); }
  remove() {
    if (!this.parentElement) return;
    const i = this.parentElement.children.indexOf(this);
    if (i >= 0) this.parentElement.children.splice(i, 1);
    this.parentElement = null;
  }
  setAttribute(k, v) { this.attributes.set(k, String(v)); }
  getAttribute(k) { return this.attributes.has(k) ? this.attributes.get(k) : null; }
  removeAttribute(k) { this.attributes.delete(k); }
  addEventListener(type, fn) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(fn);
  }
  dispatchEvent(type, ev = {}) {
    ev.target = ev.target || this;
    ev.currentTarget = ev.currentTarget || this;
    ev.stopPropagation = ev.stopPropagation || (() => {});
    ev.preventDefault = ev.preventDefault || (() => {});
    if (type === "click" && typeof this.onclick === "function") this.onclick(ev);
    if (type === "change" && typeof this.onchange === "function") this.onchange(ev);
    if (type === "input" && typeof this.oninput === "function") this.oninput(ev);
    for (const fn of (this.listeners.get(type) || [])) fn(ev);
  }
  click() { this.dispatchEvent("click"); }
  focus() { globalThis.document.activeElement = this; }
  getBoundingClientRect() { return { left: 10, top: 10, right: 110, bottom: 40, width: 100, height: 30 }; }
  scrollIntoView() {}
  matches(sel) {
    const parts = sel.split(",").map(s => s.trim());
    return parts.some(part => {
      if (part.includes(" ")) {
        const tokens = part.split(/\s+/);
        const desc = tokens[tokens.length - 1];
        const anc = tokens.slice(0, -1).join(" ");
        if (!this.matches(desc)) return false;
        let p = this.parentElement;
        while (p) { if (p.matches(anc)) return true; p = p.parentElement; }
        return false;
      }
      const attrRegex = /\[([^=\]]+)="([^"]*)"\]/g;
      let m;
      while ((m = attrRegex.exec(part)) !== null) {
        const attrName = m[1];
        const attrVal = m[2];
        if (attrName.startsWith("data-")) {
          const camel = attrName.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase());
          if (this.dataset[camel] !== attrVal) return false;
        } else if (this.getAttribute(attrName) !== attrVal) {
          return false;
        }
      }
      const base = part.replace(/\[[^\]]*\]/g, "");
      if (!base) return true;
      if (base.startsWith("#")) return this.id === base.slice(1);
      if (base.startsWith(".")) return base.slice(1).split(".").every(c => this.classList.contains(c));
      return this.tagName.toLowerCase() === base.toLowerCase();
    });
  }
  closest(sel) {
    let cur = this;
    while (cur) {
      if (cur.matches(sel)) return cur;
      cur = cur.parentElement;
    }
    return null;
  }
  querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
  querySelectorAll(sel) {
    const out = [];
    const walk = node => {
      for (const ch of node.children) {
        if (ch.matches && ch.matches(sel)) out.push(ch);
        if (ch.children) walk(ch);
      }
    };
    walk(this);
    return out;
  }
}

const byId = new Map();
const rootDocEl = new FakeElement("html");
const bodyEl = new FakeElement("body");
rootDocEl.appendChild(bodyEl);

function mk(tag, id, cls = "", attrs = {}, parent = bodyEl) {
  const el = new FakeElement(tag, id);
  if (cls) el.className = cls;
  for (const [k, v] of Object.entries(attrs)) {
    if (k.startsWith("data-")) {
      const camel = k.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase());
      el.dataset[camel] = v;
    } else {
      el.setAttribute(k, v);
    }
  }
  if (id) byId.set(id, el);
  if (parent) parent.appendChild(el);
  return el;
}

const docListeners = new Map();
globalThis.document = {
  documentElement: rootDocEl,
  body: bodyEl,
  activeElement: null,
  getElementById: id => byId.get(id) || null,
  createElement: tag => new FakeElement(tag),
  createTextNode: text => ({ textContent: text, children: [] }),
  querySelector: sel => {
    if (sel.startsWith("meta[name=\"mby-token\"]")) return { getAttribute: () => "test-token", remove() {} };
    return rootDocEl.querySelector(sel);
  },
  querySelectorAll: sel => rootDocEl.querySelectorAll(sel),
  addEventListener(type, fn) {
    if (!docListeners.has(type)) docListeners.set(type, []);
    docListeners.get(type).push(fn);
  }
};
const winListeners = new Map();
globalThis.window = {
  innerWidth: 1280,
  innerHeight: 800,
  addEventListener(type, fn) {
    if (!winListeners.has(type)) winListeners.set(type, []);
    winListeners.get(type).push(fn);
  },
  removeEventListener(type, fn) {
    const arr = winListeners.get(type);
    if (!arr) return;
    const i = arr.indexOf(fn);
    if (i >= 0) arr.splice(i, 1);
  },
  dispatchEvent(type, ev = {}) {
    for (const fn of [...(winListeners.get(type) || [])]) fn(ev);
  },
  chrome: { webview: { postMessage() {} } }
};
let copiedClipboard = "";
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: {
    clipboard: {
      writeText: async txt => { copiedClipboard = txt; }
    }
  }
});
globalThis.requestAnimationFrame = fn => fn();
globalThis.CSS = { escape: s => s };
globalThis.URL.createObjectURL = () => "blob:mock-preview";
globalThis.URL.revokeObjectURL = () => {};
const _origSetTimeout = globalThis.setTimeout;
globalThis.setTimeout = (fn, ms, ...args) => {
  const t = _origSetTimeout(fn, ms, ...args);
  if (t && typeof t.unref === "function") t.unref();
  return t;
};

// Seed all DOM elements required by format.js, review.js, organize.js, specPopovers.js, specContentModal.js, setup.js, nativeDrop.js
mk("div", "toastContainer");
mk("span", "notifBadge", "hidden");
mk("div", "notifList");
const livePill = mk("button", "liveAgentActivityPill", "live-agent-pill hidden");
mk("span", "liveAgentActivityText", "", {}, livePill);
mk("div", "agentActivityList");
mk("div", "workspaceStrip");
mk("div", "outdatedToolsBar", "hidden");
const incompatBar = mk("div", "incompatibleMbyBar", "outdated-bar incompatible-bar hidden");
mk("span", "incompatibleMbyText", "", {}, incompatBar);
mk("button", "btnUpgradeMbyNow", "btn btn-xs btn-primary", {}, incompatBar);
mk("div", "topChromeOverlay");
mk("div", "viewsViewport", "views-viewport");
mk("div", "viewSetup", "view-panel");
mk("div", "viewFormat", "view-panel");
mk("div", "viewOrganize", "view-panel");
mk("div", "viewReview", "view-panel");
mk("div", "viewViewer", "view-panel");

// Step 2: Details
const containerTabs = mk("div", "containerTabs");
mk("button", "", "seg-btn active", { "data-container": "session" }, containerTabs);
mk("button", "", "seg-btn", { "data-container": "standaloneArticle" }, containerTabs);
mk("button", "", "seg-btn", { "data-container": "standaloneQuiz" }, containerTabs);
mk("input", "promptTitle");
mk("input", "promptDescription");
mk("div", "promptAttachmentsList", "hidden");
const studyNotesDisclosure = mk("details", "studyNotesDisclosure");
mk("textarea", "promptSourceText", "", {}, studyNotesDisclosure);
mk("button", "btnAttachPromptFiles");

// Step 3: Organize
mk("div", "orgCanvasTrack");
mk("div", "addSpecPopover", "hidden");
mk("div", "specInspectorPopover", "hidden");
mk("div", "presetDropdownMenu", "hidden");
mk("span", "presetBtnLabel");

// Step 4: Finalize (Two-Tab Hub)
const finalizeTabs = mk("div", "finalizeDestinationTabs");
const tabDirect = mk("button", "", "seg-btn active", { "data-finalize-tab": "direct" }, finalizeTabs);
const tabAi = mk("button", "", "seg-btn", { "data-finalize-tab": "ai" }, finalizeTabs);
const directActionsGroup = mk("div", "directActionsGroup");
const btnFinalize = mk("button", "btnFinalizeWebApp", "btn btn-primary", {}, directActionsGroup);
mk("input", "chkAllowPlaceholders", "", {}, directActionsGroup);
const aiActionsGroup = mk("div", "aiActionsGroup", "hidden");
const btnCopyHandoff = mk("button", "btnCopyLiveHandoff", "btn btn-primary", {}, aiActionsGroup);
mk("span", "btnCopyHandoffLabel", "", {}, btnCopyHandoff);
const readyWsWrap = mk("div", "readyWsMenuWrap", "", {}, aiActionsGroup);
mk("button", "btnReadyWorkspace", "", {}, readyWsWrap);
mk("div", "readyWsMenu", "hidden", {}, readyWsWrap);
mk("button", "optReadyWsRoot", "", {}, readyWsWrap);
mk("button", "optReadyWsChoose", "", {}, readyWsWrap);
const promptOptsWrap = mk("div", "promptOptionsWrap", "", {}, aiActionsGroup);
mk("button", "btnPromptOptions", "", {}, promptOptsWrap);
mk("div", "promptOptionsMenu", "hidden", {}, promptOptsWrap);
mk("button", "optCopyPlainText", "", {}, promptOptsWrap);
mk("button", "optCopyMarkdown", "", {}, promptOptsWrap);
mk("button", "optSaveMarkdown", "", {}, promptOptsWrap);
const btnBuildNowSecondary = mk("button", "btnBuildNowSecondary", "btn btn-ghost", {}, aiActionsGroup);
const actionsWrap = mk("div", "actionsMenuWrap");
mk("button", "btnWorkspaceActions", "", {}, actionsWrap);
mk("div", "actionsMenu", "hidden", {}, actionsWrap);
mk("button", "optExportRawJson", "", {}, actionsWrap);
mk("button", "optGenerateTemplate", "", {}, actionsWrap);
mk("div", "promptMetrics");
mk("h3", "reviewPreviewHeading");
const aiStyleTabs = mk("div", "aiHandoffStyleTabs", "hidden");
const tabLiveStyle = mk("button", "", "seg-btn active", { "data-ai-style": "liveAgent" }, aiStyleTabs);
const tabClassicStyle = mk("button", "", "seg-btn", { "data-ai-style": "classicPrompt" }, aiStyleTabs);
mk("select", "promptFormat", "hidden");
const chkUseMelomgWrap = mk("label", "chkUseMelomgWrap", "hidden");
const chkUseMelomg = mk("input", "chkUseMelomg", "", {}, chkUseMelomgWrap);
chkUseMelomg.checked = true;
mk("div", "liveAgentHandoffBanner", "hidden");
mk("div", "directReadinessPanel");
mk("pre", "promptPreviewBox", "hidden");

// Spec Content Modal (Phase 5 streamlined 2-tab + inline image strip + stepper + subtypes)
const specModal = mk("div", "specContentModal", "hidden");
const specModalStepper = mk("div", "specModalStepper", "row gap-4", {}, specModal);
mk("button", "btnSpecModalPrev", "btn btn-xs btn-ghost", {}, specModalStepper);
mk("span", "specModalCounter", "muted", {}, specModalStepper);
mk("button", "btnSpecModalNext", "btn btn-xs btn-ghost", {}, specModalStepper);
mk("h3", "specContentModalTitle", "", {}, specModal);
mk("span", "specContentTopicBadge", "", {}, specModal);
mk("button", "btnInsertSpecStarter", "btn btn-xs btn-ghost", {}, specModal);
mk("button", "btnToggleSpecSyntax", "hidden", { "aria-expanded": "false" }, specModal);
mk("button", "btnCloseSpecContentModal", "", {}, specModal);
mk("div", "specModalSubtypesRow", "row gap-6 wrap hidden", {}, specModal);
const modeBar = mk("div", "specContentModeBar", "row-between wrap gap-8", {}, specModal);
const modeTabs = mk("div", "specContentModeTabs", "", {}, modeBar);
mk("button", "", "seg-btn active", { "data-spec-content-mode": "rawText" }, modeTabs);
mk("button", "", "seg-btn", { "data-spec-content-mode": "markdownFile" }, modeTabs);
mk("span", "specContentModeStatus", "", {}, modeBar);
mk("pre", "specContentSyntaxHint", "hidden", {}, specModal);
const imgPane = mk("div", "specContentImagePane", "spec-image-attach-box hidden", {}, specModal);
mk("input", "specContentImagePath", "", {}, imgPane);
mk("button", "btnPickSpecImageFile", "", {}, imgPane);
mk("button", "btnClearSpecImageFile", "hidden", {}, imgPane);
mk("div", "specContentImageMetaFields", "hidden", {}, imgPane);
mk("input", "specContentImageCaption", "", {}, imgPane);
mk("input", "specContentImageAlt", "", {}, imgPane);
mk("div", "specImageDirectPreviewWrap", "hidden", {}, imgPane);
mk("img", "specImageDirectPreview", "", {}, imgPane);
mk("div", "specImageDirectPreviewPath", "", {}, imgPane);
const rawPane = mk("div", "specContentRawPane", "hidden", {}, specModal);
mk("textarea", "specContentRawInput", "", {}, rawPane);
const filePane = mk("div", "specContentFilePane", "hidden", {}, specModal);
mk("input", "specContentFilePath", "", {}, filePane);
mk("button", "btnPickSpecMdFile", "", {}, filePane);
const melomgDetails = mk("details", "specMelomgDetails", "hidden", {}, specModal);
mk("span", "specMelomgStatus", "", {}, melomgDetails);
mk("div", "specMelomgMissing", "hidden", {}, melomgDetails);
mk("div", "specMelomgControls", "", {}, melomgDetails);
mk("select", "specMelomgPdfSelect", "", {}, melomgDetails);
mk("input", "specMelomgQuery", "", {}, melomgDetails);
const qualitySel = mk("select", "specMelomgQuality", "", {}, melomgDetails);
qualitySel.value = "high";
mk("input", "specMelomgMaskAll", "", {}, melomgDetails);
mk("button", "btnSpecMelomgExtract", "", {}, melomgDetails);
mk("div", "specMelomgPreviewWrap", "hidden", {}, melomgDetails);
mk("img", "specMelomgPreviewImg", "", {}, melomgDetails);
mk("div", "specMelomgPreviewPath", "", {}, melomgDetails);
mk("div", "specMelomgPreviewMeta", "", {}, melomgDetails);
mk("button", "btnClearSpecContent", "", {}, specModal);
mk("button", "btnSaveSpecContent", "", {}, specModal);

// Setup view
mk("div", "appsCartList");
mk("div", "stagedCopyBar", "hidden");
mk("div", "planConflictsBox", "hidden");
mk("div", "stagedItemsList");
mk("input", "setupWsPath");

// Mock fetch for API routes used during tests
const apiCalls = [];
let composeCounter = 0;
globalThis.fetch = async (url, opts = {}) => {
  const method = opts.method || "GET";
  const body = opts.body ? JSON.parse(opts.body) : null;
  apiCalls.push({ method, url, body });

  if (url === "/api/prompt/compose") {
    composeCounter++;
    const md = `# Composed v${composeCounter} (${body.workflowMode})`;
    return {
      ok: true,
      status: 200,
      headers: { get: () => "application/json" },
      json: async () => ({ markdown: md, plainText: md, characterCount: md.length, approximateTokens: 10 })
    };
  }
  if (url === "/api/live/draft" && method === "PUT") {
    return { ok: true, status: 200, headers: { get: () => "application/json" }, json: async () => ({ ok: true }) };
  }
  if (url === "/api/workspace/import-asset") {
    return {
      ok: true,
      status: 200,
      headers: { get: () => "application/json" },
      json: async () => ({ relativePath: "assets/Figure_1.png", copied: true })
    };
  }
  if (url === "/api/melomg/extract-figure") {
    return {
      ok: true,
      status: 200,
      headers: { get: () => "application/json" },
      json: async () => ({
        selector: "1.1",
        relativeImagePath: "assets/fig_1.1.png",
        caption: "Cardiac Cycle",
        pixelWidth: 800,
        pixelHeight: 600
      })
    };
  }
  if (url.startsWith("/api/workspace/asset?")) {
    return {
      ok: true,
      status: 200,
      blob: async () => new Blob(["png"])
    };
  }
  return { ok: true, status: 200, headers: { get: () => "application/json" }, json: async () => ({}) };
};

// Import real app modules
const { state, on } = await import(toModuleUrl("store.js"));
const {
  addNotification,
  renderNotifications,
  toast,
  recordAgentActivity,
  renderAgentActivityPillAndFeed
} = await import(toModuleUrl("ui/chrome.js"));
const { syncFormatInputs } = await import(toModuleUrl("views/prompt/format.js"));
const { wireReviewView, syncReviewModeUi } = await import(toModuleUrl("views/prompt/review.js"));
const { getSpecContentBadge, renderOrganizeCanvas } = await import(toModuleUrl("views/prompt/organize.js"));
const organizeFlip = await import(toModuleUrl("views/prompt/organizeFlip.js"));
const promptDraft = await import(toModuleUrl("views/prompt/promptDraft.js"));
const { openAddSpecPopover, openSpecInspectorPopover } = await import(toModuleUrl("views/prompt/specPopovers.js"));
const { buildOrganizationDisplayItems } = await import(toModuleUrl("views/prompt/specModes.js"));
const { initSpecContentModal, openSpecContentModal, closeSpecContentModal, isSpecContentModalOpen } = await import(toModuleUrl("views/prompt/specContentModal.js"));
const { attachSpecDrag } = await import(toModuleUrl("views/prompt/organizeDrag.js"));
const { renderAppsCart } = await import(toModuleUrl("views/setup.js"));
const { resolveExternalDropTarget, handleOrganizeNativeDrop } = await import(toModuleUrl("ui/nativeDrop.js"));

state.settings.activeWorkspace = { workspacePath: "C:\\ws\\study" };
state.resolvedTools = { mby: "C:\\apps\\mby.exe", melomg: "C:\\apps\\melomg.exe" };
state.specs = [
  {
    topic: "mcq",
    category: "question",
    displayName: "Multiple Choice",
    supportsImage: true,
    markdownHint: "MCQ hint: mark correct option with `- [x]` inside `:::mcq`.",
    markdownStarter: ":::mcq\nQuestion stem?\n- [x] Correct option\n- [ ] Distractor\n:::",
    compactSignature: ":::mcq{multi=true}\nStem\n- [x] A\n:::",
    modeGroups: [{ fieldName: "variant", label: "Selection", multiSelect: false, options: [{ key: "single", label: "Single" }, { key: "multi", label: "Multi" }] }]
  },
  {
    topic: "image",
    category: "block",
    displayName: "Image Block",
    supportsImage: true,
    markdownHint: "Markdown image syntax `![alt](assets/file.png \"caption\")`.",
    markdownStarter: "![Diagram caption](assets/diagram.png \"Diagram caption\")",
    compactSignature: "![Alt](assets/fig.png)",
    modeGroups: []
  },
  {
    topic: "callout",
    category: "block",
    displayName: "Callout Box",
    supportsImage: false,
    markdownHint: "Callout box directive `:::callout{tone=\"pearl\"}`.",
    markdownStarter: ":::callout{tone=\"pearl\"}\nKey takeaway.\n:::",
    summary: "Highlight clinical pearls",
    modeGroups: [{ fieldName: "tone", label: "Tone", multiSelect: false, options: [{ key: "pearl", label: "Pearl" }, { key: "clinical", label: "Clinical" }, { key: "warning", label: "Warning" }] }]
  },
  {
    topic: "fill-blank",
    category: "question",
    displayName: "Fill in the Blank",
    supportsImage: false,
    markdownHint: "Cloze blank syntax using `{{answer}}` inside `:::fill-blank`.",
    markdownStarter: ":::fill-blank\nThe mitral valve closes at {{S1}}.\n:::",
    summary: "Cloze recall prompt",
    modeGroups: [{ fieldName: "matching", label: "Matching", multiSelect: false, options: [{ key: "fuzzy", label: "Fuzzy" }] }]
  }
];

wireReviewView();
initSpecContentModal();

test("Phase 3: Option B two-tab Finalize Hub remembers lastAiStyle and refreshes prompt before copy (Bug #10)", async () => {
  state.prompt.workflowMode = "direct";
  syncReviewModeUi();
  assert.equal(directActionsGroup.classList.contains("hidden"), false);
  assert.equal(aiActionsGroup.classList.contains("hidden"), true);
  assert.equal(aiStyleTabs.classList.contains("hidden"), true);
  assert.equal(byId.get("promptFormat").classList.contains("hidden"), true);

  // Switch to Hand Off to AI -> defaults to liveAgent, shows single primary CTA
  tabAi.click();
  assert.equal(state.prompt.workflowMode, "liveAgent");
  assert.equal(directActionsGroup.classList.contains("hidden"), true);
  assert.equal(aiActionsGroup.classList.contains("hidden"), false);
  assert.equal(aiStyleTabs.classList.contains("hidden"), false);
  assert.equal(byId.get("promptFormat").classList.contains("hidden"), true);

  // Switch AI sub-tab to One-Shot Prompt -> reveals #promptFormat
  tabClassicStyle.click();
  assert.equal(state.prompt.workflowMode, "classicPrompt");
  assert.equal(byId.get("promptFormat").classList.contains("hidden"), false);

  // Switch back to Build Directly, then back to Hand Off to AI -> remembers classicPrompt
  tabDirect.click();
  assert.equal(state.prompt.workflowMode, "direct");
  tabAi.click();
  assert.equal(state.prompt.workflowMode, "classicPrompt");

  // Bug #10: Copy must await fresh /api/prompt/compose instead of copying stale markdown
  state.composedPrompt.markdown = "STALE_PROMPT_TEXT";
  copiedClipboard = "";
  await btnCopyHandoff.onclick();
  assert.notEqual(copiedClipboard, "STALE_PROMPT_TEXT");
  assert.match(copiedClipboard, /^# Composed v\d+ \(classicPrompt\)$/);

  // Step 2: #studyNotesDisclosure auto-opens when study notes exist
  studyNotesDisclosure.open = false;
  byId.get("promptSourceText").value = "Action potential phases";
  syncFormatInputs();
  assert.equal(studyNotesDisclosure.open, true);
});

test("Phase 4: Native drop materializes preset orderedItems and clears stale canvas hover (Bug #1)", async () => {
  state.currentView = "organize";
  const comp = {
    id: "q1",
    kind: "quiz",
    title: "Quiz",
    selections: [{ topic: "mcq", enabled: true, desiredCount: 1, modes: ["single"], modeCounts: { single: 1 } }],
    orderedItems: null
  };
  state.prompt.components = [comp];

  const tile = new FakeElement("div");
  tile.className = "org-spec-tile";
  tile.dataset.compId = "q1";
  tile.dataset.specId = "q1_mcq_1";

  // Hovering tile sets hoveredSpecTarget
  const onTile = resolveExternalDropTarget(tile);
  assert.equal(onTile.target, "spec-tile");
  assert.deepEqual(state.live.hoveredSpecTarget, { compId: "q1", specId: "q1_mcq_1" });

  // Leaving tile onto empty canvas clears hoveredSpecTarget
  const canvas = byId.get("orgCanvasTrack");
  const onCanvas = resolveExternalDropTarget(canvas);
  assert.equal(onCanvas.target, "organize-canvas");
  assert.equal(state.live.hoveredSpecTarget, null);

  // Dropping image directly onto unmaterialized preset tile materializes orderedItems and attaches asset
  resolveExternalDropTarget(tile);
  await handleOrganizeNativeDrop("spec-tile", [{ kind: "file", path: "C:\\ext\\Figure 1.png" }]);
  assert.ok(Array.isArray(comp.orderedItems) && comp.orderedItems.length === 1);
  assert.equal(comp.orderedItems[0].content.mode, "imageFile");
  assert.equal(comp.orderedItems[0].content.imagePath, "assets/Figure_1.png");
});

test("Phase 4: Spec modal defaults to imageFile on focusMelomg (Bug #3), suppresses duplicate thumbnail (Bug #8), and toggles ? Syntax", async () => {
  const comp = {
    id: "q2",
    kind: "quiz",
    title: "Visual Quiz",
    selections: [{ topic: "mcq", enabled: true, desiredCount: 1, modes: [], modeCounts: null }],
    orderedItems: null
  };
  state.prompt.components = [comp];

  // Open on unmaterialized preset item with focusMelomg: true -> both inline image strip and raw textarea visible
  openSpecContentModal("q2", "q2_mcq_1", { focusMelomg: true });
  assert.ok(Array.isArray(comp.orderedItems) && comp.orderedItems.length === 1);
  assert.equal(byId.get("specContentImagePane").classList.contains("hidden"), false, "Expected inline image strip visible for image-capable mcq");
  assert.equal(byId.get("specContentRawPane").classList.contains("hidden"), false, "Expected unified raw editor visible alongside image strip");
  assert.equal(melomgDetails.open, true, "Expected melomg details expanded when focusMelomg=true");

  // Syntax hint collapsed by default behind ? Syntax toggle
  const syntaxBtn = byId.get("btnToggleSpecSyntax");
  const syntaxHint = byId.get("specContentSyntaxHint");
  assert.equal(syntaxBtn.classList.contains("hidden"), false);
  assert.equal(syntaxHint.classList.contains("hidden"), true);
  syntaxBtn.click();
  assert.equal(syntaxHint.classList.contains("hidden"), false);
  assert.equal(syntaxBtn.getAttribute("aria-expanded"), "true");

  // Run melomg extract -> populates direct image preview & keeps melomg duplicate preview HIDDEN (Bug #8)
  const pdfSel = byId.get("specMelomgPdfSelect");
  const opt = new FakeElement("option");
  opt.value = "C:\\ws\\study\\atlas.pdf";
  pdfSel.appendChild(opt);
  pdfSel.value = opt.value;
  byId.get("specMelomgQuery").value = "Figure 1.1";

  await byId.get("btnSpecMelomgExtract").onclick();
  assert.equal(byId.get("specContentImagePath").value, "assets/fig_1.1.png");
  assert.equal(byId.get("specImageDirectPreviewWrap").classList.contains("hidden"), false);
  assert.equal(byId.get("specMelomgPreviewWrap").classList.contains("hidden"), true, "Duplicate melomg thumbnail must stay hidden in imageFile mode");
});

test("Phase 4: Inspector places Spec Content first & collapses AI instructions; Organize tile supports 1-click pencil & dblclick", () => {
  const comp = {
    id: "q3",
    kind: "quiz",
    title: "Drill",
    selections: [{ topic: "mcq", enabled: true, desiredCount: 1, notes: "", modes: [], modeCounts: null }],
    orderedItems: [{ id: "s_drill_1", topic: "mcq", instanceNumber: 1, subtype: "single", content: null }]
  };
  state.prompt.components = [comp];

  const anchor = new FakeElement("button");
  openSpecInspectorPopover(anchor, comp, comp.orderedItems[0], () => {});
  const pop = byId.get("specInspectorPopover");
  // children[0] = head, children[1] = Spec Content section
  assert.ok(pop.children[1].querySelector(".inspector-content-bar"), "Expected Spec Content section immediately below header");
  const aiNotes = pop.querySelector(".inspector-ai-notes");
  assert.ok(aiNotes && aiNotes.tagName === "DETAILS", "Expected AI instructions wrapped in <details class='inspector-ai-notes'>");
  assert.equal(aiNotes.open, false, "Expected AI instructions collapsed when notes are empty");

  // Organize tile exposes inline pencil edit button & dblclick handler opening content modal
  const opened = [];
  const off = on("spec:open-content-modal", payload => opened.push(payload));
  renderOrganizeCanvas();
  const track = byId.get("orgCanvasTrack");
  const tile = track.querySelector(".org-spec-tile");
  const editBtn = tile.querySelector(".org-spec-edit");
  assert.ok(editBtn, "Expected inline .org-spec-edit pencil button on spec tile");
  editBtn.click();
  tile.dispatchEvent("dblclick", { target: tile });
  off();
  assert.equal(opened.length, 2);
  assert.equal(opened[0].instId, "s_drill_1");
  assert.equal(opened[1].instId, "s_drill_1");

  // Badge marks bodyless non-image ImageFile as incomplete
  const partialBadge = getSpecContentBadge({ mode: "imageFile", imagePath: "assets/ecg.png", rawText: "" }, "mcq");
  assert.equal(partialBadge.complete, false);
});

test("Phase 5: Central tools use app-card-central without selected highlight, and store.live.activitySeq is initialized", () => {
  assert.equal(state.live.activitySeq, 0);
  state.apps = [
    { id: "mby", kind: "mby", fileName: "mby-cli-2.1.2.exe", displayName: "Meloberry cli", isInstalled: true, version: "2.1.2" },
    { id: "melomg", kind: "melomg", fileName: "meloberry-melomg-2.2.1.exe", displayName: "Meloberry melomg", isInstalled: true, version: "2.2.1" },
    { id: "custom", kind: "customExecutable", fileName: "helper.exe", displayName: "Helper", isInstalled: true }
  ];
  state.selectedAppNames = new Set(["helper.exe"]);
  renderAppsCart();

  const cards = byId.get("appsCartList").children;
  assert.equal(cards.length, 3);
  assert.equal(cards[0].classList.contains("app-card-central"), true);
  assert.equal(cards[0].classList.contains("selected"), false);
  assert.equal(cards[1].classList.contains("app-card-central"), true);
  assert.equal(cards[1].classList.contains("selected"), false);
  assert.equal(cards[2].classList.contains("app-card-central"), false);
  assert.equal(cards[2].classList.contains("selected"), true);
});

test("Beta-4 Phase 2: Multi-subtype preset expansion, empty readiness count, clean Add Spec popover, and incomplete Img* badge class", () => {
  // 2.3: specModes.js round-robins multi-value primary modes when modeCounts is null
  const presetComp = {
    id: "a_preset",
    kind: "article",
    title: "Core Concepts",
    selections: [{ topic: "callout", enabled: true, desiredCount: 2, modes: ["pearl", "clinical"], modeCounts: null }],
    orderedItems: null
  };
  const expanded = buildOrganizationDisplayItems(presetComp);
  assert.deepEqual(expanded.map(i => i.subtype), ["pearl", "clinical"]);

  // 2.2: review.js counts empty Article/Quiz component as 1 unfilled item and offers Fix in Organize button
  state.prompt.container = "session";
  state.prompt.workflowMode = "direct";
  state.prompt.components = [
    { id: "a_empty", kind: "article", title: "Empty Card", selections: [], orderedItems: null }
  ];
  state.currentView = "review";
  syncReviewModeUi();
  assert.equal(byId.get("promptMetrics").textContent, "0/1 specs filled");
  const panel = byId.get("directReadinessPanel");
  const emptyRow = panel.querySelector(".readiness-spec-row");
  assert.ok(emptyRow && emptyRow.classList.contains("is-empty"));
  const warnPill = emptyRow.querySelector(".pill-warn");
  assert.ok(warnPill, "Expected .pill-warn badge on empty card row");
  const fixBtn = emptyRow.querySelector("button");
  assert.ok(fixBtn && fixBtn.textContent === "Fix in Organize", "Expected Fix in Organize button on empty card row");
  fixBtn.click();
  assert.equal(state.currentView, "organize");

  // 2.5: specPopovers.js Add Spec popover omits inline displayName text while keeping displayName in title tooltip
  const anchor = new FakeElement("button");
  openAddSpecPopover(anchor, { id: "q_pop", kind: "quiz", title: "Quiz", selections: [], orderedItems: [] }, () => {});
  const addPop = byId.get("addSpecPopover");
  const qItems = addPop.querySelectorAll(".add-spec-item");
  assert.ok(qItems.length >= 2);
  const fillItem = qItems.find(el => el.innerHTML.includes("fill-blank"));
  assert.ok(fillItem, "Expected fill-blank row in Add Spec popover");
  assert.equal(fillItem.innerHTML.includes("Fill in the Blank"), false, "Add Spec popover must not render inline displayName badge");
  assert.ok(fillItem.title.includes("Fill in the Blank"), "Add Spec popover must retain displayName in tooltip title");

  openAddSpecPopover(anchor, { id: "a_pop", kind: "article", title: "Article", selections: [], orderedItems: [] }, () => {});
  const bItems = addPop.querySelectorAll(".add-spec-item");
  const calloutItem = bItems.find(el => el.innerHTML.includes("callout"));
  assert.ok(calloutItem, "Expected callout row in Add Spec popover");
  assert.equal(calloutItem.innerHTML.includes("Callout Box"), false, "Add Spec popover must not render inline displayName for blocks");
  assert.ok(calloutItem.title.includes("Callout Box"));

  // 2.4: organize.js applies .is-incomplete to .spec-content-badge when badge.complete === false
  state.prompt.components = [
    {
      id: "q_inc",
      kind: "quiz",
      title: "Incomplete Img Quiz",
      selections: [{ topic: "mcq", enabled: true, desiredCount: 1, modes: [], modeCounts: null }],
      orderedItems: [
        { id: "s_inc_1", topic: "mcq", instanceNumber: 1, subtype: null, content: { mode: "imageFile", imagePath: "assets/ecg.png", rawText: "" } }
      ]
    }
  ];
  renderOrganizeCanvas();
  const badgeBtn = byId.get("orgCanvasTrack").querySelector(".spec-content-badge");
  assert.ok(badgeBtn, "Expected .spec-content-badge on organize tile");
  assert.equal(badgeBtn.textContent, "Img*");
  assert.equal(badgeBtn.classList.contains("is-incomplete"), true, "Expected .is-incomplete class on incomplete Img* badge");
});

test("Beta-4 Phase 3: Notification deduplication, severity & structured formatting, and Live Agent Pill lifecycle", () => {
  state.notifications = [];
  byId.get("toastContainer").innerHTML = "";

  // 3.1a: toast(..., { notify: false }) + addNotification produces exactly 1 notification
  const finalizeMsg = "Cannot finalize: 6 spec(s) have no content (Card (A):mcq, Card B:text, Card C:callout, Card D:table, Card E:image (+1 more)). Fill them in Organize or enable 'Allow Placeholders'.";
  toast(finalizeMsg, "err", { notify: false });
  addNotification("Finalize Blocked", finalizeMsg, "err", { code: "Prompt.InvalidDraft" });
  assert.equal(state.notifications.length, 1, "Expected 1 notification when toast opts out via notify: false");

  // 3.1b: Rapid duplicate addNotification calls within 2.5s are deduplicated and upgrade generic 'Error' title
  addNotification("Error", "Same failure detail", "err");
  addNotification("Finalize Blocked", "Same failure detail", "err");
  assert.equal(state.notifications.length, 2, "Duplicate detail within 2.5s must be deduplicated");
  assert.equal(state.notifications[0].title, "Finalize Blocked", "Specific title should replace generic 'Error' on dedup");

  // 3.1c: Severity classes, [Error.Code] pill extraction/stripping, and structured missing-spec sublist
  addNotification("Build Failed", "Compilation failed [Markdown.InvalidFlashcard] invalid image attribute", "err", { code: "Cli.Failed" });
  addNotification("Heads Up", "Check workspace tools", "warn");
  addNotification("Build Ready", "Study WebApp compiled", "ok");
  addNotification("Synced", "Draft updated", "info");
  renderNotifications();

  const notifItems = byId.get("notifList").querySelectorAll(".notif-item");
  assert.equal(notifItems.length, 6);
  assert.ok(notifItems[0].classList.contains("kind-info"));
  assert.ok(notifItems[1].classList.contains("kind-ok"));
  assert.ok(notifItems[2].classList.contains("kind-warn"));
  assert.ok(notifItems[3].classList.contains("kind-err"));

  // Inline [Markdown.InvalidFlashcard] extracted into pill and stripped from body text
  assert.ok(notifItems[3].innerHTML.includes("notif-code-pill"), "Expected .notif-code-pill badge");
  assert.ok(notifItems[3].innerHTML.includes("Markdown.InvalidFlashcard"), "Expected extracted error code in pill");
  assert.equal(notifItems[3].innerHTML.includes("[Markdown.InvalidFlashcard]"), false, "Raw bracketed error code must be stripped from detail body");

  // Structured missing-spec list handles nested parens and (+1 more) without truncating
  const finalizeItem = notifItems[5];
  assert.ok(finalizeItem.classList.contains("kind-err"));
  assert.ok(finalizeItem.innerHTML.includes("Prompt.InvalidDraft"));
  assert.ok(finalizeItem.innerHTML.includes("notif-sublist"), "Expected .notif-sublist for missing specs");
  assert.ok(finalizeItem.innerHTML.includes("<li>Card (A):mcq</li>"), "Must preserve card title with parentheses");
  assert.ok(finalizeItem.innerHTML.includes("<li>Card E:image</li>"), "Must keep last item before (+1 more)");
  assert.ok(finalizeItem.innerHTML.includes("<li>+1 more</li>"), "Must render (+N more) cleanly as a list item");

  // 3.2: Live Agent Pill stale-running expiry (>45s), full hover title, and idle action-count summary
  state.live.activities = [];
  const scheduled = [];
  const prevSetTimeout = globalThis.setTimeout;
  globalThis.setTimeout = (fn, ms) => {
    const token = { fn, ms, cleared: false };
    scheduled.push(token);
    return token;
  };
  const prevClearTimeout = globalThis.clearTimeout;
  globalThis.clearTimeout = token => {
    if (token && typeof token === "object") token.cleared = true;
  };

  try {
    // Seed a stale running entry (>45s old) alongside a fresh completed entry
    state.live.activities = [
      { id: "act_done", state: "done", message: "Filled mcq text in \"Quiz 1\"", detail: "spec_1", ts: Date.now(), time: "12:00:02" },
      { id: "act_stale", state: "running", message: "Stale orphaned build", detail: "", ts: Date.now() - 60000, time: "11:59:00" }
    ];
    renderAgentActivityPillAndFeed();

    const pill = byId.get("liveAgentActivityPill");
    const pillText = byId.get("liveAgentActivityText");
    assert.equal(pill.classList.contains("is-running"), false, "Stale running activity >45s old must not pin pill in running state");
    assert.equal(pillText.textContent, "Filled mcq text in \"Quiz 1\"");
    assert.equal(pill.title, "Filled mcq text in \"Quiz 1\" — spec_1", "Expected full hover tooltip with detail");

    // Fire idle timeout -> pill enters idle state and displays action count summary
    const idleTimer = scheduled.find(t => !t.cleared && t.ms === 10000);
    assert.ok(idleTimer, "Expected 10s idle timer on completed agent activity");
    idleTimer.fn();
    assert.equal(pill.classList.contains("idle"), true);
    assert.equal(pillText.textContent, "Agent \u00b7 2 actions");

    // Record a fresh running activity -> stores ts and pins running state
    recordAgentActivity({ id: "act_live", state: "running", message: "Building WebApp" });
    assert.equal(typeof state.live.activities[0].ts, "number");
    assert.equal(pill.classList.contains("is-running"), true);
    assert.equal(pillText.textContent, "Building WebApp");
    assert.equal(pill.title, "Building WebApp");
  } finally {
    globalThis.setTimeout = prevSetTimeout;
    globalThis.clearTimeout = prevClearTimeout;
  }
});

test("Beta-4 Phase 4: Keyed FLIP across re-rendered DOM, label wipe with rapid-rename guard, and agent draft-updated animations", () => {
  assert.equal(typeof organizeFlip.capturePositionsByKey, "function", "Expected capturePositionsByKey export");
  assert.equal(typeof organizeFlip.animateFromKeyedPositions, "function", "Expected animateFromKeyedPositions export");
  assert.equal(typeof organizeFlip.animateLabelWipe, "function", "Expected animateLabelWipe export");
  assert.equal(typeof promptDraft.applyAnimatedAgentDraft, "function", "Expected applyAnimatedAgentDraft export");

  // 4.1a: capturePositionsByKey + animateFromKeyedPositions bridges newly created DOM nodes by compId/specId
  const container = new FakeElement("div");
  bodyEl.appendChild(container);
  const oldCardA = new FakeElement("section");
  oldCardA.className = "org-comp-card";
  oldCardA.dataset.compId = "a1";
  oldCardA.getBoundingClientRect = () => ({ left: 20, top: 10, right: 320, bottom: 410, width: 300, height: 400 });
  const oldSpec1 = new FakeElement("div");
  oldSpec1.className = "org-spec-tile";
  oldSpec1.dataset.specId = "s1";
  oldSpec1.getBoundingClientRect = () => ({ left: 30, top: 80, right: 300, bottom: 120, width: 270, height: 40 });
  oldCardA.appendChild(oldSpec1);
  container.appendChild(oldCardA);

  const prevCardRects = organizeFlip.capturePositionsByKey(container, ".org-comp-card", "compId");
  const prevSpecRects = organizeFlip.capturePositionsByKey(container, ".org-spec-tile", "specId");
  assert.equal(prevCardRects.get("a1").left, 20);
  assert.equal(prevSpecRects.get("s1").top, 80);

  // Re-create DOM nodes at shifted positions (simulating track.innerHTML = "")
  container.innerHTML = "";
  const newCardA = new FakeElement("section");
  newCardA.className = "org-comp-card";
  newCardA.dataset.compId = "a1";
  newCardA.getBoundingClientRect = () => ({ left: 360, top: 10, right: 660, bottom: 410, width: 300, height: 400 });
  const newSpec1 = new FakeElement("div");
  newSpec1.className = "org-spec-tile";
  newSpec1.dataset.specId = "s1";
  newSpec1.getBoundingClientRect = () => ({ left: 370, top: 160, right: 640, bottom: 200, width: 270, height: 40 });
  newCardA.appendChild(newSpec1);
  container.appendChild(newCardA);

  const rafQueue = [];
  const prevRaf = globalThis.requestAnimationFrame;
  globalThis.requestAnimationFrame = fn => rafQueue.push(fn);
  try {
    organizeFlip.animateFromKeyedPositions(container, ".org-comp-card", "compId", prevCardRects, 260);
    organizeFlip.animateFromKeyedPositions(container, ".org-spec-tile", "specId", prevSpecRects, 220);

    // Inverted FLIP transform before rAF
    assert.equal(newCardA.style.transform, "translate3d(-340px, 0px, 0)");
    assert.equal(newSpec1.style.transform, "translate3d(-340px, -80px, 0)");

    // Flush rAF -> plays transition back to origin
    rafQueue.forEach(fn => fn());
    assert.equal(newCardA.style.transform, "translate3d(0, 0, 0)");
    assert.ok(newCardA.style.transition.includes("260ms"));
    assert.equal(newSpec1.style.transform, "translate3d(0, 0, 0)");
    assert.ok(newSpec1.style.transition.includes("220ms"));
  } finally {
    globalThis.requestAnimationFrame = prevRaf;
    container.remove();
  }

  // 4.1b: animateLabelWipe applies label-wipe-out -> updates value -> label-wipe-in & cancels prior timer on rapid renames
  const timers = [];
  const prevSetTimeout = globalThis.setTimeout;
  const prevClearTimeout = globalThis.clearTimeout;
  globalThis.setTimeout = (fn, ms) => {
    const t = { fn, ms, cleared: false };
    timers.push(t);
    return t;
  };
  globalThis.clearTimeout = t => {
    if (t && typeof t === "object") t.cleared = true;
  };

  try {
    const titleInput = byId.get("promptTitle");
    titleInput.value = "Old Title";
    globalThis.document.activeElement = null;

    organizeFlip.animateLabelWipe(titleInput, "Old Title", "Mid Title");
    assert.equal(titleInput.value, "Old Title");
    assert.equal(titleInput.classList.contains("label-wipe-out"), true);
    const firstOutTimer = timers[timers.length - 1];

    // Rapid second rename before first 140ms timer fires -> cancels first timer
    organizeFlip.animateLabelWipe(titleInput, "Mid Title", "Final Title");
    assert.equal(firstOutTimer.cleared, true, "Rapid second rename must cancel previous wipe-out timer");
    assert.equal(titleInput.value, "Mid Title");
    assert.equal(titleInput.classList.contains("label-wipe-out"), true);

    const secondOutTimer = timers[timers.length - 1];
    assert.equal(secondOutTimer.ms, 140);
    secondOutTimer.fn();
    assert.equal(titleInput.value, "Final Title");
    assert.equal(titleInput.classList.contains("label-wipe-out"), false);
    assert.equal(titleInput.classList.contains("label-wipe-in"), true);

    const inCleanupTimer = timers[timers.length - 1];
    assert.equal(inCleanupTimer.ms, 260);
    inCleanupTimer.fn();
    assert.equal(titleInput.classList.contains("label-wipe-in"), false);

    // 4.2: applyAnimatedAgentDraft sets pop-in classes for add-comp / add-spec and triggers wipe on title/desc/card renames
    const unsubOrg = on("organize:render", () => renderOrganizeCanvas());
    try {
      const baseDraft = {
        container: "session",
        title: "Cardiology",
        description: "Initial scope",
        workflowMode: "liveAgent",
        components: [
          { id: "a1", kind: "article", title: "Basics", selections: [], orderedItems: [] }
        ]
      };
      promptDraft.applyRemoteDraft(baseDraft);
      renderOrganizeCanvas();

      // add-comp -> new card receives .animate-add
      promptDraft.applyAnimatedAgentDraft(
        {
          ...baseDraft,
          components: [
            ...baseDraft.components,
            { id: "q1", kind: "quiz", title: "Drill", selections: [], orderedItems: [] }
          ]
        },
        { op: "add-comp", targetCompId: "q1" }
      );
      const track = byId.get("orgCanvasTrack");
      const addedCard = track.querySelector('.org-comp-card[data-comp-id="q1"]');
      assert.ok(addedCard && addedCard.classList.contains("animate-add"), "Expected .animate-add on newly added card");

      // add-spec -> new spec tile receives .animate-add
      promptDraft.applyAnimatedAgentDraft(
        {
          ...baseDraft,
          components: [
            baseDraft.components[0],
            {
              id: "q1",
              kind: "quiz",
              title: "Drill",
              selections: [{ topic: "mcq", enabled: true, desiredCount: 1 }],
              orderedItems: [{ id: "q1_mcq_1", topic: "mcq", instanceNumber: 1, subtype: null, content: null }]
            }
          ]
        },
        { op: "add-spec", targetCompId: "q1", targetSpecId: "q1_mcq_1" }
      );
      const addedTile = track.querySelector('.org-spec-tile[data-spec-id="q1_mcq_1"]');
      assert.ok(addedTile && addedTile.classList.contains("animate-add"), "Expected .animate-add on newly added spec tile");

      // patch-meta rename on title, description, and component card title -> all trigger label-wipe-out -> label-wipe-in
      timers.length = 0;
      promptDraft.applyAnimatedAgentDraft(
        {
          ...baseDraft,
          title: "Cardiology II",
          description: "Valves & ECG",
          components: [
            { id: "a1", kind: "article", title: "Hemodynamics", selections: [], orderedItems: [] },
            {
              id: "q1",
              kind: "quiz",
              title: "Drill",
              selections: [{ topic: "mcq", enabled: true, desiredCount: 1 }],
              orderedItems: [{ id: "q1_mcq_1", topic: "mcq", instanceNumber: 1, subtype: null, content: null }]
            }
          ]
        },
        { op: "patch-meta", targetCompId: "a1" }
      );
      const descInput = byId.get("promptDescription");
      const cardTitleInput = track.querySelector('.org-comp-card[data-comp-id="a1"] .org-comp-title-input');
      assert.equal(titleInput.classList.contains("label-wipe-out"), true, "Expected project title wipe-out");
      assert.equal(descInput.classList.contains("label-wipe-out"), true, "Expected project description wipe-out");
      assert.ok(cardTitleInput && cardTitleInput.classList.contains("label-wipe-out"), "Expected card title wipe-out");

      // Fire all 140ms wipe-out timers
      timers.filter(t => !t.cleared && t.ms === 140).forEach(t => t.fn());
      assert.equal(titleInput.value, "Cardiology II");
      assert.equal(descInput.value, "Valves & ECG");
      assert.equal(cardTitleInput.value, "Hemodynamics");
      assert.equal(titleInput.classList.contains("label-wipe-in"), true);
      assert.equal(descInput.classList.contains("label-wipe-in"), true);
      assert.equal(cardTitleInput.classList.contains("label-wipe-in"), true);
    } finally {
      unsubOrg();
    }
  } finally {
    globalThis.setTimeout = prevSetTimeout;
    globalThis.clearTimeout = prevClearTimeout;
  }
});

test("Beta-4 Phase 3/4 Bugfixes: Clear activity cancels pending idle timer; card FLIP skips child spec-tile double-shift", () => {
  // Bugfix 1: clearing activities cancels pending 10s idle timer so it cannot overwrite hidden pill text later
  const scheduled = [];
  const prevSetTimeout = globalThis.setTimeout;
  const prevClearTimeout = globalThis.clearTimeout;
  globalThis.setTimeout = (fn, ms) => {
    const t = { fn, ms, cleared: false };
    scheduled.push(t);
    return t;
  };
  globalThis.clearTimeout = t => {
    if (t && typeof t === "object") t.cleared = true;
  };
  try {
    state.live.activities = [
      { id: "a1", state: "done", message: "First", detail: "", ts: Date.now(), time: "12:00:00" },
      { id: "a2", state: "done", message: "Second", detail: "", ts: Date.now(), time: "12:00:01" }
    ];
    renderAgentActivityPillAndFeed();
    const idleTimer = scheduled.find(t => !t.cleared && t.ms === 10000);
    assert.ok(idleTimer, "Expected idle timer scheduled");

    state.live.activities = [];
    renderAgentActivityPillAndFeed();
    assert.equal(idleTimer.cleared, true, "Clearing activities must cancel pending idle timer");
  } finally {
    globalThis.setTimeout = prevSetTimeout;
    globalThis.clearTimeout = prevClearTimeout;
  }

  // Bugfix 2: animateFromKeyedPositions returns moved keys & supports skipEl so child spec tiles inside a moved card do not double-FLIP
  const container = new FakeElement("div");
  bodyEl.appendChild(container);
  const oldCard = new FakeElement("section");
  oldCard.className = "org-comp-card";
  oldCard.dataset.compId = "c_move";
  oldCard.getBoundingClientRect = () => ({ left: 20, top: 10, right: 320, bottom: 410, width: 300, height: 400 });
  const oldTile = new FakeElement("div");
  oldTile.className = "org-spec-tile";
  oldTile.dataset.compId = "c_move";
  oldTile.dataset.specId = "s_child";
  oldTile.getBoundingClientRect = () => ({ left: 30, top: 80, right: 300, bottom: 120, width: 270, height: 40 });
  oldCard.appendChild(oldTile);
  container.appendChild(oldCard);

  const prevCards = organizeFlip.capturePositionsByKey(container, ".org-comp-card", "compId");
  const prevSpecs = organizeFlip.capturePositionsByKey(container, ".org-spec-tile", "specId");

  container.innerHTML = "";
  const newCard = new FakeElement("section");
  newCard.className = "org-comp-card";
  newCard.dataset.compId = "c_move";
  newCard.getBoundingClientRect = () => ({ left: 360, top: 10, right: 660, bottom: 410, width: 300, height: 400 });
  const newTile = new FakeElement("div");
  newTile.className = "org-spec-tile";
  newTile.dataset.compId = "c_move";
  newTile.dataset.specId = "s_child";
  newTile.getBoundingClientRect = () => ({ left: 370, top: 80, right: 640, bottom: 120, width: 270, height: 40 });
  newCard.appendChild(newTile);
  container.appendChild(newCard);

  const rafQueue = [];
  const prevRaf = globalThis.requestAnimationFrame;
  globalThis.requestAnimationFrame = fn => rafQueue.push(fn);
  try {
    const movedCardIds = organizeFlip.animateFromKeyedPositions(container, ".org-comp-card", "compId", prevCards, 260);
    assert.ok(movedCardIds instanceof Set && movedCardIds.has("c_move"), "Expected movedCardIds Set containing moved card key");
    organizeFlip.animateFromKeyedPositions(
      container,
      ".org-spec-tile",
      "specId",
      prevSpecs,
      220,
      el => movedCardIds.has(el.dataset?.compId)
    );
    assert.equal(newCard.style.transform, "translate3d(-340px, 0px, 0)", "Card should FLIP by -340px");
    assert.equal(newTile.style.transform || "", "", "Child spec tile inside a moved card must not receive a second horizontal FLIP transform");
  } finally {
    globalThis.requestAnimationFrame = prevRaf;
    container.remove();
  }
});

test("Beta-4 Phase 5: Streamlined Spec Editor (templates, unified write+image, subtypes, stepper, shortcuts, tile snippet & dblclick debounce)", async () => {
  const { extractSpecPreviewSnippet } = await import(toModuleUrl("views/prompt/organize.js"));
  const { getSpecTemplateInfo, isImageCapableTopic } = await import(toModuleUrl("views/prompt/specModes.js"));

  // 5.1: Spec hints, starter templates, and image capability come from state.specs (CLI-driven)
  assert.equal(isImageCapableTopic("mcq"), true);
  assert.equal(isImageCapableTopic("image"), true);
  assert.equal(isImageCapableTopic("callout"), false);
  for (const t of ["mcq", "image", "callout", "fill-blank"]) {
    const tpl = getSpecTemplateInfo(t);
    assert.ok(tpl.hint && tpl.hint.length > 5, `Expected Markdown hint for ${t}`);
    assert.equal(tpl.hint.includes("{ kind:"), false, `Hint for ${t} must not contain raw JSON signature`);
    assert.ok(tpl.starter && tpl.starter.length > 5, `Expected non-empty starter template for ${t}`);
  }
  assert.equal(extractSpecPreviewSnippet({ mode: "rawText", rawText: ":::mcq\n**Which valve** closes at S1?\n- [x] Mitral\n:::" }), "Which valve closes at S1?");
  assert.equal(extractSpecPreviewSnippet({ mode: "markdownFile", markdownFilePath: "notes/cardio.md" }), "cardio.md");
  assert.equal(extractSpecPreviewSnippet({ mode: "imageFile", imagePath: "assets/ecg.png", caption: "Normal ECG" }), "Normal ECG");

  // 5.2: Unified Write + Image strip preserves body text when attaching & clearing an image, plus inline subtypes & stepper
  const comp = {
    id: "q_p5",
    kind: "quiz",
    title: "Cardio Quiz",
    selections: [{ topic: "mcq", enabled: true, desiredCount: 2, modes: [], modeCounts: null }],
    orderedItems: [
      { id: "s_p5_1", topic: "mcq", instanceNumber: 1, subtype: "single", content: { mode: "rawText", rawText: "Original stem\n- [x] A\n- [ ] B" } },
      { id: "s_p5_2", topic: "mcq", instanceNumber: 2, subtype: "single", content: null }
    ]
  };
  state.prompt.components = [comp];

  openSpecContentModal("q_p5", "s_p5_1");
  assert.equal(byId.get("specModalCounter").textContent, "1/2");
  assert.equal(byId.get("btnSpecModalPrev").disabled, true);
  assert.equal(byId.get("btnSpecModalNext").disabled, false);

  // ? Syntax uses Markdown reference from specTemplates.js (not JSON)
  const syntaxText = byId.get("specContentSyntaxHint").textContent;
  assert.ok(syntaxText.includes("- [x]"), "Expected Markdown syntax hint for mcq");

  // Inline subtype chips update inst.subtype live
  const subRow = byId.get("specModalSubtypesRow");
  assert.equal(subRow.classList.contains("hidden"), false, "Expected inline subtype chips visible for mcq");
  const multiChip = subRow.querySelectorAll(".mode-chip").find(b => b.textContent.includes("Multi"));
  assert.ok(multiChip, "Expected Multi subtype chip");
  multiChip.click();
  assert.equal(comp.orderedItems[0].subtype, "multi");
  assert.equal(byId.get("specContentTopicBadge").textContent, "mcq:multi");

  // Attaching an image via melomg keeps existing rawText intact (zero lost text)
  const pdfSel = byId.get("specMelomgPdfSelect");
  const opt = new FakeElement("option");
  opt.value = "C:\\ws\\study\\atlas.pdf";
  pdfSel.appendChild(opt);
  pdfSel.value = opt.value;
  byId.get("specMelomgQuery").value = "Figure 1.1";
  await byId.get("btnSpecMelomgExtract").onclick();
  assert.equal(byId.get("specContentImagePath").value, "assets/fig_1.1.png");
  assert.equal(byId.get("specContentRawInput").value, "Original stem\n- [x] A\n- [ ] B", "Attaching image must NEVER discard typed body text");
  assert.equal(byId.get("btnClearSpecImageFile").classList.contains("hidden"), false);

  // Next button commits spec 1 (imageFile + preserved rawText) and advances to spec 2 without closing modal
  byId.get("btnSpecModalNext").click();
  assert.equal(isSpecContentModalOpen(), true, "Stepping to next spec must keep modal open");
  assert.equal(comp.orderedItems[0].content.mode, "imageFile");
  assert.equal(comp.orderedItems[0].content.imagePath, "assets/fig_1.1.png");
  assert.equal(comp.orderedItems[0].content.rawText, "Original stem\n- [x] A\n- [ ] B");
  assert.equal(byId.get("specModalCounter").textContent, "2/2");
  assert.equal(byId.get("btnSpecModalPrev").disabled, false);
  assert.equal(byId.get("btnSpecModalNext").disabled, true);

  // + Starter inserts Markdown starter template into empty spec 2
  byId.get("btnInsertSpecStarter").click();
  assert.ok(byId.get("specContentRawInput").value.includes("- [x] Correct option"), "Expected + Starter to populate mcq starter");

  // Ctrl+S saves without closing
  specModal.dispatchEvent("keydown", { ctrlKey: true, key: "s" });
  assert.equal(isSpecContentModalOpen(), true, "Ctrl+S must save without closing modal");
  assert.equal(comp.orderedItems[1].content.mode, "rawText");

  // Ctrl+Enter on last spec saves & closes modal
  specModal.dispatchEvent("keydown", { ctrlKey: true, key: "Enter" });
  assert.equal(isSpecContentModalOpen(), false, "Ctrl+Enter on last spec must save & close modal");

  // Clear attached image preserves body text on spec 1; backdrop click closes modal
  openSpecContentModal("q_p5", "s_p5_1");
  byId.get("btnClearSpecImageFile").click();
  assert.equal(byId.get("specContentImagePath").value, "");
  assert.equal(byId.get("specContentRawInput").value, "Original stem\n- [x] A\n- [ ] B", "Clearing image must preserve raw body text");
  byId.get("btnSaveSpecContent").click();
  assert.equal(comp.orderedItems[0].content.mode, "rawText");
  assert.equal(comp.orderedItems[0].content.imagePath, null);
  assert.equal(comp.orderedItems[0].content.rawText, "Original stem\n- [x] A\n- [ ] B");

  // Backdrop click closes modal
  openSpecContentModal("q_p5", "s_p5_1");
  assert.equal(isSpecContentModalOpen(), true);
  specModal.dispatchEvent("click", { target: specModal, currentTarget: specModal });
  assert.equal(isSpecContentModalOpen(), false, "Clicking modal backdrop must close modal");

  // 5.3a: Organize tile renders .org-spec-tile-head-row wrapper + 1-line preview snippet
  renderOrganizeCanvas();
  const track = byId.get("orgCanvasTrack");
  const tile1 = track.querySelector('.org-spec-tile[data-spec-id="s_p5_1"]');
  const headRow = tile1?.querySelector(".org-spec-tile-head-row");
  assert.ok(headRow && headRow.querySelector(".org-spec-tile-label") && headRow.querySelector(".org-spec-tile-sub"), "Expected topic label & subtype wrapped inside .org-spec-tile-head-row");
  const snipEl = tile1?.querySelector(".org-spec-tile-snippet");
  assert.ok(snipEl && snipEl.textContent === "Original stem", "Expected 1-line preview snippet on filled Organize tile");

  // 5.3b: Double-clicking a spec tile debounces single-click so inspector popover does NOT flash open
  const dragTimers = [];
  const prevST = globalThis.setTimeout;
  const prevCT = globalThis.clearTimeout;
  globalThis.setTimeout = (fn, ms) => {
    const t = { fn, ms, cleared: false };
    dragTimers.push(t);
    return t;
  };
  globalThis.clearTimeout = t => {
    if (t && typeof t === "object") t.cleared = true;
  };
  try {
    let inspectorClicks = 0;
    const dummyTile = new FakeElement("div");
    dummyTile.className = "org-spec-tile";
    track.appendChild(dummyTile);
    attachSpecDrag(dummyTile, comp, {
      onClick: () => { inspectorClicks++; },
      onCommit: () => {}
    });

    // Simulate click 1 -> click 2 -> dblclick before 180ms elapses
    dummyTile.dispatchEvent("pointerdown", { button: 0, clientX: 10, clientY: 10, target: dummyTile });
    globalThis.window.dispatchEvent("pointerup", {});
    dummyTile.dispatchEvent("pointerdown", { button: 0, clientX: 10, clientY: 10, target: dummyTile });
    globalThis.window.dispatchEvent("pointerup", {});
    dummyTile.dispatchEvent("dblclick", { target: dummyTile });

    dragTimers.filter(t => !t.cleared).forEach(t => t.fn());
    assert.equal(inspectorClicks, 0, "Double-click must cancel single-click timer so Inspector popover never flashes");
    dummyTile.remove();
  } finally {
    globalThis.setTimeout = prevST;
    globalThis.clearTimeout = prevCT;
  }
});

test("Part 2 Compatibility Gate: incompatible mby-cli shows persistent banner and disables dependent CTAs", async () => {
  const { renderIncompatibleMbyBanner } = await import(toModuleUrl("ui/chrome.js"));
  const { refreshPromptOptionsAvailability } = await import(toModuleUrl("views/prompt/promptDraft.js"));

  state.prompt.components = [
    { id: "c_comp", kind: "quiz", title: "Quiz", selections: [{ topic: "mcq", enabled: true, desiredCount: 1 }], orderedItems: [] }
  ];
  state.mbyCompatibility = {
    isCompatible: false,
    installedVersion: "2.1.6",
    minimumVersion: "2.2.0",
    bannerMessage: "Update mby-cli to v2.2.0+ (installed v2.1.6)."
  };

  renderIncompatibleMbyBanner();
  syncReviewModeUi();
  refreshPromptOptionsAvailability();

  assert.equal(incompatBar.classList.contains("hidden"), false);
  assert.ok(byId.get("incompatibleMbyText").textContent.includes("2.1.6"));
  assert.equal(byId.get("btnFinalizeWebApp").disabled, true);
  assert.equal(byId.get("btnBuildNowSecondary").disabled, true);
  assert.equal(byId.get("btnReadyWorkspace").disabled, true);
  assert.equal(byId.get("optExportRawJson").disabled, true);
  assert.equal(byId.get("optGenerateTemplate").disabled, true);

  state.mbyCompatibility = { isCompatible: true, installedVersion: "2.2.0-beta-1", minimumVersion: "2.2.0", bannerMessage: "" };
  renderIncompatibleMbyBanner();
  syncReviewModeUi();
  refreshPromptOptionsAvailability();

  assert.equal(incompatBar.classList.contains("hidden"), true);
  assert.equal(byId.get("btnFinalizeWebApp").disabled, false);
  assert.equal(byId.get("btnReadyWorkspace").disabled, false);
  assert.equal(byId.get("optExportRawJson").disabled, false);
  assert.equal(byId.get("optGenerateTemplate").disabled, false);
});
