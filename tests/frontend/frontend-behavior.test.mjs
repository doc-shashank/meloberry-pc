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
    this.options = [];
    this.onclick = null;
    this.onchange = null;
    this.oninput = null;
    this.onpointerdown = null;
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
        const [anc, desc] = part.split(/\s+/);
        if (!this.matches(desc)) return false;
        let p = this.parentElement;
        while (p) { if (p.matches(anc)) return true; p = p.parentElement; }
        return false;
      }
      if (part.startsWith("#")) return this.id === part.slice(1);
      if (part.startsWith(".")) return this.classList.contains(part.slice(1));
      return this.tagName.toLowerCase() === part.toLowerCase();
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
globalThis.window = {
  innerWidth: 1280,
  innerHeight: 800,
  addEventListener() {},
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
mk("div", "workspaceStrip");
mk("div", "outdatedToolsBar", "hidden");
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

// Spec Content Modal
const specModal = mk("div", "specContentModal", "hidden");
mk("h3", "specContentModalTitle", "", {}, specModal);
mk("span", "specContentTopicBadge", "", {}, specModal);
mk("button", "btnToggleSpecSyntax", "hidden", { "aria-expanded": "false" }, specModal);
mk("button", "btnCloseSpecContentModal", "", {}, specModal);
const modeTabs = mk("div", "specContentModeTabs", "", {}, specModal);
mk("button", "", "seg-btn", { "data-spec-content-mode": "none" }, modeTabs);
mk("button", "tabSpecContentImage", "seg-btn hidden", { "data-spec-content-mode": "imageFile" }, modeTabs);
mk("button", "", "seg-btn", { "data-spec-content-mode": "rawText" }, modeTabs);
mk("button", "", "seg-btn", { "data-spec-content-mode": "markdownFile" }, modeTabs);
mk("span", "specContentModeStatus", "", {}, specModal);
mk("pre", "specContentSyntaxHint", "hidden", {}, specModal);
mk("div", "specContentImagePane", "hidden", {}, specModal);
mk("input", "specContentImagePath", "", {}, specModal);
mk("input", "specContentImageCaption", "", {}, specModal);
mk("input", "specContentImageAlt", "", {}, specModal);
mk("div", "specContentImageMetaFields", "", {}, specModal);
mk("div", "specImageDirectPreviewWrap", "hidden", {}, specModal);
mk("img", "specImageDirectPreview", "", {}, specModal);
mk("div", "specImageDirectPreviewPath", "", {}, specModal);
mk("div", "specImageBodyWrap", "hidden", {}, specModal);
mk("textarea", "specContentImageBody", "", {}, specModal);
mk("div", "specContentRawPane", "hidden", {}, specModal);
mk("textarea", "specContentRawInput", "", {}, specModal);
mk("div", "specContentFilePane", "hidden", {}, specModal);
mk("input", "specContentFilePath", "", {}, specModal);
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
const { syncFormatInputs } = await import(toModuleUrl("views/prompt/format.js"));
const { wireReviewView, syncReviewModeUi } = await import(toModuleUrl("views/prompt/review.js"));
const { getSpecContentBadge, renderOrganizeCanvas } = await import(toModuleUrl("views/prompt/organize.js"));
const { openSpecInspectorPopover } = await import(toModuleUrl("views/prompt/specPopovers.js"));
const { initSpecContentModal, openSpecContentModal } = await import(toModuleUrl("views/prompt/specContentModal.js"));
const { renderAppsCart } = await import(toModuleUrl("views/setup.js"));
const { resolveExternalDropTarget, handleOrganizeNativeDrop } = await import(toModuleUrl("ui/nativeDrop.js"));

state.settings.activeWorkspace = { workspacePath: "C:\\ws\\study" };
state.resolvedTools = { mby: "C:\\apps\\mby.exe", melomg: "C:\\apps\\melomg.exe" };
state.specs = [
  {
    topic: "mcq",
    category: "question",
    displayName: "Multiple Choice",
    compactSignature: ":::mcq{multi=true}\nStem\n- [x] A\n:::",
    modeGroups: [{ fieldName: "variant", label: "Selection", multiSelect: false, options: [{ key: "single", label: "Single" }, { key: "multi", label: "Multi" }] }]
  },
  {
    topic: "image",
    category: "block",
    displayName: "Image Block",
    compactSignature: "![Alt](assets/fig.png)",
    modeGroups: []
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

  // Open on unmaterialized preset item with focusMelomg: true
  openSpecContentModal("q2", "q2_mcq_1", { focusMelomg: true });
  assert.ok(Array.isArray(comp.orderedItems) && comp.orderedItems.length === 1);
  assert.equal(byId.get("specContentImagePane").classList.contains("hidden"), false, "Expected imageFile pane active for focusMelomg on mcq");
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
