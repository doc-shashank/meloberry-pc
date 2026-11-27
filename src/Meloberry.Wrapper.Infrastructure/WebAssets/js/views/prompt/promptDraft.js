import { api } from "../../api.js";
import { state, emit, on } from "../../store.js";
import { getSpecModeStructure, getActiveModeCounts } from "./specModes.js";

export function markPresetModified() {
  if (!state.activePresetName) return;
  state.activePresetName = "";
  const lbl = document.getElementById("presetBtnLabel");
  if (lbl) lbl.textContent = "Preset";
  document.querySelectorAll("#presetDropdownMenu .dropdown-item").forEach(b => b.classList.remove("active"));
}

export function cloneComponent(c) {
  return {
    id: c.id || `c_${Math.random().toString(36).slice(2, 8)}`,
    kind: c.kind || "article",
    title: c.title || (c.kind === "quiz" ? "Practice Quiz" : "Study Article"),
    selections: (c.selections || []).map(s => ({
      topic: s.topic,
      enabled: !!s.enabled,
      desiredCount: s.desiredCount || null,
      notes: s.notes || "",
      modes: [...(s.modes || [])],
      modeCounts: s.modeCounts ? { ...s.modeCounts } : null
    })),
    orderedItems: c.orderedItems
      ? c.orderedItems.map(o => ({
          ...o,
          content: o.content ? { ...o.content } : null
        }))
      : null
  };
}

export function ensureComponentsForContainer() {
  const cKind = state.prompt.container;
  if (cKind === "standaloneArticle") {
    const firstArt = state.prompt.components.find(c => c.kind === "article")
      || cloneComponent({ id: "a1", kind: "article", title: state.prompt.title || "Article", selections: [] });
    state.prompt.components = [firstArt];
  } else if (cKind === "standaloneQuiz") {
    const firstQuiz = state.prompt.components.find(c => c.kind === "quiz")
      || cloneComponent({ id: "q1", kind: "quiz", title: state.prompt.title || "Quiz", selections: [] });
    state.prompt.components = [firstQuiz];
  } else if (!state.prompt.components.length) {
    state.prompt.components = [
      cloneComponent({ id: "a1", kind: "article", title: "Core Concepts", selections: [] }),
      cloneComponent({ id: "q1", kind: "quiz", title: "Mastery Check", selections: [] })
    ];
  }
}

export function applyPresetObj(preset) {
  state.prompt.containerCache = { session: null, standaloneArticle: null, standaloneQuiz: null };
  if (!preset) {
    ensureComponentsForContainer();
    emit("organize:render");
    return;
  }

  state.prompt.container = preset.container;
  state.prompt.authoringFormat = preset.authoringFormat;
  state.activePresetName = preset.name;

  if (preset.components && preset.components.length) {
    state.prompt.components = preset.components.map(cloneComponent);
  } else {
    state.prompt.components = [];
    ensureComponentsForContainer();
    const comp = state.prompt.components[0];
    if (comp) {
      comp.selections = (preset.selections || []).map(s => ({
        topic: s.topic,
        enabled: !!s.enabled,
        desiredCount: s.desiredCount || null,
        notes: s.notes || "",
        modes: [...(s.modes || [])],
        modeCounts: s.modeCounts ? { ...s.modeCounts } : null
      }));
    }
  }

  ensureComponentsForContainer();
  emit("format:sync");
  emit("organize:render");
  refreshPromptPreview();
}

export async function loadPresets() {
  const res = await api("GET", "/api/presets");
  if (res.ok) {
    state.presets = res.data.presets || [];
    emit("organize:presets-loaded");
  }
}

export function animateSyncStatusWipe(statusEl) {
  if (!statusEl) return;
  statusEl.classList.remove("sync-wipe-in");
  void statusEl.offsetWidth;
  statusEl.classList.add("sync-wipe-in");
}

export async function loadSpecs(forceRefresh) {
  const res = await api("GET", `/api/specs?refresh=${forceRefresh ? "true" : "false"}`);
  if (res.ok) {
    state.specs = res.data.specs || [];
    state.cliSpecIcons = res.data.specIcons || {};
    state.isLiveSpecs = !!res.data.isLive;
    state.activeMbyPath = res.data.activeMbyPath || "";

    const statusEl = document.getElementById("specSyncStatus");
    const reloadBtn = document.getElementById("btnRefreshSpecs");
    if (reloadBtn) reloadBtn.title = "Re-query live mby spec schemas";
    if (statusEl) {
      if (state.isLiveSpecs) {
        statusEl.textContent = "Live mby specs";
        statusEl.className = "sync-status-text live";
        statusEl.title = state.activeMbyPath ? `Source: ${state.activeMbyPath}` : "Synced live from installed mby CLI";
      } else {
        statusEl.textContent = "Fallback specs (mby not found)";
        statusEl.className = "sync-status-text";
        statusEl.title = "Using built-in schema catalog. Install mby in Setup or configure override in Settings.";
      }
      if (forceRefresh) {
        animateSyncStatusWipe(statusEl);
      }
    }

    if (!state.prompt.components.length) {
      applyPresetObj(state.presets[0] || null);
    } else {
      emit("organize:render");
    }
  }
}

export function buildPromptDraftPayload() {
  const aws = state.settings.activeWorkspace;
  const wsPath = aws ? aws.workspacePath : null;
  const copiedFolders = state.stagedItems.filter(i => i.kind === "folder").map(i => i.targetRelativePath);
  const copiedFiles = state.stagedItems
    .filter(i => i.kind === "file")
    .map(i => ({ filePath: wsPath ? `${wsPath}\\${i.targetRelativePath}` : i.sourcePath, kind: i.sourcePath.toLowerCase().endsWith(".pdf") ? "pdf" : "file" }));

  const allAttachments = copiedFiles.concat(state.prompt.extraAttachments || []);
  const activeComps = state.prompt.components.map(c => ({
    id: c.id,
    kind: c.kind,
    title: c.title,
    selections: (c.selections || []).filter(s => s.enabled).map(s => {
      const { primaryGroup } = getSpecModeStructure(s.topic);
      const counts = getActiveModeCounts(s, primaryGroup);
      return {
        topic: s.topic,
        enabled: true,
        desiredCount: s.desiredCount || null,
        notes: s.notes || null,
        modes: s.modes && s.modes.length ? s.modes : null,
        modeCounts: Object.keys(counts).length ? counts : null
      };
    }),
    orderedItems: c.orderedItems && c.orderedItems.length
      ? c.orderedItems.map(o => ({
          id: o.id,
          topic: o.topic,
          instanceNumber: o.instanceNumber || 1,
          subtype: o.subtype || null,
          content: o.content && o.content.mode && o.content.mode !== "none"
            ? { mode: o.content.mode, rawText: o.content.rawText || null, markdownFilePath: o.content.markdownFilePath || null }
            : null
        }))
      : null
  }));

  return {
    container: state.prompt.container,
    title: document.getElementById("promptTitle")?.value.trim() || "Untitled Study Module",
    description: document.getElementById("promptDescription")?.value.trim() || "",
    authoringFormat: document.getElementById("promptFormat")?.value || state.prompt.authoringFormat,
    sourceText: document.getElementById("promptSourceText")?.value || "",
    selections: activeComps.flatMap(c => c.selections),
    attachments: allAttachments,
    useMelomgForPdfs: document.getElementById("chkUseMelomg")?.checked ?? true,
    embedInlineSchemas: false,
    lazyCliDiscovery: !state.prompt.agentsMdExported,
    agentsMdExported: state.prompt.agentsMdExported,
    workingDirectory: wsPath,
    mbyExecutablePath: state.resolvedTools?.mby || null,
    melomgExecutablePath: state.resolvedTools?.melomg || null,
    copiedFolders,
    components: activeComps,
    workflowMode: state.prompt.workflowMode || "direct",
    allowPlaceholdersOnFinalize: !!state.prompt.allowPlaceholdersOnFinalize
  };
}

export function applyRemoteDraft(draft) {
  if (!draft) return;
  state.live.suppressDraftPush = true;
  clearTimeout(state.live.draftSaveTimer);
  try {
    state.prompt.container = draft.container || "session";
    state.prompt.title = draft.title || "";
    state.prompt.description = draft.description || "";
    state.prompt.authoringFormat = draft.authoringFormat || "markdownDirectory";
    state.prompt.sourceText = draft.sourceText || "";
    state.prompt.useMelomgForPdfs = draft.useMelomgForPdfs ?? true;
    state.prompt.workflowMode = draft.workflowMode || "direct";
    state.prompt.allowPlaceholdersOnFinalize = !!draft.allowPlaceholdersOnFinalize;
    state.prompt.components = Array.isArray(draft.components) ? draft.components.map(cloneComponent) : [];

    const titleEl = document.getElementById("promptTitle");
    const descEl = document.getElementById("promptDescription");
    const srcEl = document.getElementById("promptSourceText");
    const chkMelomg = document.getElementById("chkUseMelomg");
    const chkPlaceholders = document.getElementById("chkAllowPlaceholders");
    if (titleEl && document.activeElement !== titleEl) titleEl.value = state.prompt.title;
    if (descEl && document.activeElement !== descEl) descEl.value = state.prompt.description;
    if (srcEl && document.activeElement !== srcEl) srcEl.value = state.prompt.sourceText;
    if (chkMelomg) chkMelomg.checked = state.prompt.useMelomgForPdfs;
    if (chkPlaceholders) chkPlaceholders.checked = state.prompt.allowPlaceholdersOnFinalize;

    ensureComponentsForContainer();
    emit("format:sync");
    emit("organize:render");
  } finally {
    state.live.suppressDraftPush = false;
  }
}

export async function loadWorkspaceDraft(workspacePath) {
  if (!workspacePath) return;
  const res = await api("GET", `/api/live/draft?workspacePath=${encodeURIComponent(workspacePath)}`);
  if (res.ok && res.data?.draft) {
    applyRemoteDraft(res.data.draft);
    await refreshPromptPreview({ skipSave: true });
  }
}

export async function flushLiveDraftSave(summary = "Updated draft in UI") {
  clearTimeout(state.live.draftSaveTimer);
  const wsPath = state.settings.activeWorkspace?.workspacePath;
  if (!wsPath || state.live.suppressDraftPush) return;
  await api("PUT", "/api/live/draft", {
    workspacePath: wsPath,
    draft: buildPromptDraftPayload(),
    source: "ui",
    summary
  });
}

export function scheduleLiveDraftSave(summary = "Updated draft in UI", delayMs = 350) {
  if (state.live.suppressDraftPush) return;
  const wsPath = state.settings.activeWorkspace?.workspacePath;
  if (!wsPath) return;
  clearTimeout(state.live.draftSaveTimer);
  state.live.draftSaveTimer = setTimeout(() => {
    flushLiveDraftSave(summary);
  }, delayMs);
}

export function getPromptOptionsEligibility() {
  const activeComps = state.prompt.components.filter(comp => {
    if (state.prompt.container === "standaloneArticle") return comp.kind === "article";
    if (state.prompt.container === "standaloneQuiz") return comp.kind === "quiz";
    return true;
  });
  const enabledSpecs = activeComps.flatMap(c => (c.selections || []).filter(s => s.enabled));
  const hasAnyActiveSpec = enabledSpecs.length > 0;
  const hasMissingQty = enabledSpecs.some(s => !s.desiredCount || s.desiredCount <= 0);
  const hasActiveWorkspace = !!(state.settings.activeWorkspace && state.settings.activeWorkspace.workspacePath);
  const hasMbyInstalled = !!state.resolvedTools?.mby;
  return { hasAnyActiveSpec, hasMissingQty, hasActiveWorkspace, hasMbyInstalled };
}

export function refreshPromptOptionsAvailability() {
  const e = getPromptOptionsEligibility();
  const rawBtn = document.getElementById("optExportRawJson");
  const genBtn = document.getElementById("optGenerateTemplate");
  if (!rawBtn || !genBtn) return;

  rawBtn.disabled = !e.hasAnyActiveSpec || e.hasMissingQty;
  rawBtn.title = !e.hasAnyActiveSpec
    ? "Add at least one active spec first."
    : (e.hasMissingQty ? "Fill Qty on every active spec first." : "");

  const genReasons = [];
  if (!e.hasAnyActiveSpec) genReasons.push("add at least one active spec");
  else if (e.hasMissingQty) genReasons.push("fill Qty on every active spec");
  if (!e.hasActiveWorkspace) genReasons.push("select an Active Workspace");
  if (!e.hasMbyInstalled) genReasons.push("install Meloberry CLI (mby)");

  genBtn.disabled = genReasons.length > 0;
  genBtn.title = genReasons.length > 0 ? `Requires: ${genReasons.join(", ")}.` : "";
}

export function schedulePromptPreview(delayMs = 120) {
  refreshPromptOptionsAvailability();
  clearTimeout(state.previewDebounceTimer);
  state.previewDebounceTimer = setTimeout(() => {
    refreshPromptPreview();
  }, delayMs);
}

export async function refreshPromptPreview(opts = {}) {
  clearTimeout(state.previewDebounceTimer);
  refreshPromptOptionsAvailability();
  if (!opts?.skipSave) {
    scheduleLiveDraftSave();
  }
  const seq = ++state.previewSeq;
  const res = await api("POST", "/api/prompt/compose", buildPromptDraftPayload());
  if (seq !== state.previewSeq) return;
  if (res.ok) {
    state.composedPrompt = res.data;
    const box = document.getElementById("promptPreviewBox");
    const metrics = document.getElementById("promptMetrics");
    if (box) box.textContent = res.data.markdown;
    if (metrics) metrics.textContent = `${res.data.characterCount} chars \u00b7 ~${res.data.approximateTokens} tokens`;
  }
}

export function wirePromptDraftEvents() {
  on("prompt:preset-modified", markPresetModified);
  on("prompt:refresh", refreshPromptPreview);
  on("prompt:schedule-preview", delay => schedulePromptPreview(delay ?? 120));
  on("prompt:availability-changed", refreshPromptOptionsAvailability);
  on("specs:reload", force => loadSpecs(!!force));
}
