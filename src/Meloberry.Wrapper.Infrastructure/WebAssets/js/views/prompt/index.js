import { api } from "../../api.js";
import { state, on, withButtonBusy } from "../../store.js";
import { registerDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { openInputPromptModal } from "../../ui/modal.js";
import { addNotification, toast } from "../../ui/chrome.js";
import { getSpecModeStructure, getActiveModeCounts } from "./specModes.js";
import { renderSpecSelectorPane, wireSpecSelector } from "./specSelector.js";
import { renderSpecOrganizerPane, wireSpecOrganizer } from "./specOrganizer.js";
import { wireReadyWorkspaceControls } from "./readyWorkspace.js";

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
    orderedItems: c.orderedItems ? c.orderedItems.map(o => ({ ...o })) : null
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

  if (!state.prompt.components.some(c => c.id === state.prompt.selectedComponentId)) {
    state.prompt.selectedComponentId = state.prompt.components[0]?.id || null;
  }
}

export function getActiveComponent() {
  ensureComponentsForContainer();
  return state.prompt.components.find(c => c.id === state.prompt.selectedComponentId) || state.prompt.components[0];
}

export function getComponentBadges() {
  let a = 0;
  let q = 0;
  const map = {};
  state.prompt.components.forEach(c => {
    map[c.id] = c.kind === "article" ? `A${++a}` : `Q${++q}`;
  });
  return map;
}

export function renderOrganizationCard() {
  ensureComponentsForContainer();
  const isSession = state.prompt.container === "session";
  document.getElementById("orgAddButtons").classList.toggle("hidden", !isSession);

  const track = document.getElementById("orgTrack");
  track.innerHTML = "";
  const badges = getComponentBadges();

  state.prompt.components.forEach((comp, idx) => {
    const box = document.createElement("div");
    const isSelected = comp.id === state.prompt.selectedComponentId;
    const isJustAdded = state.lastAddedOrgId && comp.id === state.lastAddedOrgId;
    box.className = `drag-box ${isSelected ? "active" : ""}${isJustAdded ? " animate-add" : ""}`.trim();
    box.title = comp.title || (comp.kind === "article" ? "Article" : "Quiz");
    box.draggable = state.prompt.components.length > 1;

    const top = document.createElement("div");
    top.className = "drag-box-top";
    const badge = document.createElement("span");
    badge.className = "drag-badge";
    badge.textContent = badges[comp.id];
    top.appendChild(badge);

    if (isSession && state.prompt.components.length > 1) {
      const rm = document.createElement("button");
      rm.className = "drag-remove-btn";
      rm.textContent = "\u00d7";
      rm.title = "Remove component";
      rm.onclick = e => {
        e.stopPropagation();
        markPresetModified();
        state.prompt.components.splice(idx, 1);
        ensureComponentsForContainer();
        renderOrganizationCard();
        renderPromptStudioSpecArea();
        refreshPromptPreview();
      };
      top.appendChild(rm);
    }

    const titleSpan = document.createElement("div");
    titleSpan.className = "drag-box-title";
    titleSpan.textContent = comp.title || (comp.kind === "article" ? "Study Article" : "Practice Quiz");

    const countSpan = document.createElement("div");
    countSpan.className = "muted";
    const enabledCount = comp.selections.filter(s => s.enabled).length;
    countSpan.textContent = `${enabledCount} specs`;

    box.append(top, titleSpan, countSpan);
    box.onclick = () => {
      state.prompt.selectedComponentId = comp.id;
      state.prompt.organizerChosenTopic = null;
      state.prompt.organizerChosenSubtype = "";
      renderOrganizationCard();
      renderPromptStudioSpecArea();
    };

    box.addEventListener("dragstart", () => {
      state.dragOrgIndex = idx;
      box.classList.add("dragging");
    });
    box.addEventListener("dragend", () => {
      state.dragOrgIndex = null;
      document.querySelectorAll("#orgTrack .drag-box").forEach(el => el.classList.remove("dragging", "drag-over"));
    });
    box.addEventListener("dragover", e => { e.preventDefault(); box.classList.add("drag-over"); });
    box.addEventListener("dragleave", () => box.classList.remove("drag-over"));
    box.addEventListener("drop", e => {
      e.preventDefault();
      box.classList.remove("drag-over");
      if (state.dragOrgIndex === null || state.dragOrgIndex === idx) return;
      markPresetModified();
      const [moved] = state.prompt.components.splice(state.dragOrgIndex, 1);
      state.prompt.components.splice(idx, 0, moved);
      renderOrganizationCard();
      refreshPromptPreview();
    });

    track.appendChild(box);
    if (isJustAdded) {
      requestAnimationFrame(() => box.scrollIntoView({ behavior: "smooth", inline: "nearest", block: "nearest" }));
    }
  });
  state.lastAddedOrgId = null;

  const active = getActiveComponent();
  document.getElementById("orgSelectedBar").classList.remove("hidden");
  document.getElementById("orgSelectedBadge").textContent = badges[active.id];
  document.getElementById("orgSelectedTitle").value = active.title;
}

export function renderPromptStudioSpecArea() {
  const comp = getActiveComponent();
  const targetName = comp.kind === "article" ? "article" : "quiz";
  document.getElementById("specCardHeading").innerHTML = `What do you want in a <code>${targetName}</code>`;

  const isSelector = state.prompt.specCardMode === "selector";
  document.getElementById("specSelectorPane").classList.toggle("hidden", !isSelector);
  document.getElementById("specOrganizerPane").classList.toggle("hidden", isSelector);

  if (isSelector) {
    renderSpecSelectorPane(comp);
  } else {
    renderSpecOrganizerPane(comp);
  }
}

export function applyPresetObj(preset) {
  state.prompt.containerCache = { session: null, standaloneArticle: null, standaloneQuiz: null };
  if (!preset) {
    ensureComponentsForContainer();
    renderOrganizationCard();
    renderPromptStudioSpecArea();
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
    const comp = getActiveComponent();
    comp.selections = (preset.selections || []).map(s => ({
      topic: s.topic,
      enabled: !!s.enabled,
      desiredCount: s.desiredCount || null,
      notes: s.notes || "",
      modes: [...(s.modes || [])],
      modeCounts: s.modeCounts ? { ...s.modeCounts } : null
    }));
  }

  ensureComponentsForContainer();
  state.prompt.selectedComponentId = state.prompt.components[0]?.id || null;

  document.querySelectorAll("#containerTabs .seg-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.container === state.prompt.container);
  });
  document.getElementById("promptFormat").value = state.prompt.authoringFormat;

  renderOrganizationCard();
  renderPromptStudioSpecArea();
  refreshPromptPreview();
}

function buildPresetMenuRow(preset, canDelete) {
  const item = document.createElement("button");
  item.type = "button";
  item.className = `dropdown-item dropdown-item-row ${state.activePresetName === preset.name ? "active" : ""}`;
  const span = document.createElement("span");
  span.textContent = preset.name;
  item.appendChild(span);

  if (canDelete) {
    const del = document.createElement("button");
    del.type = "button";
    del.className = "preset-del-btn";
    del.textContent = "\u00d7";
    del.onclick = async e => {
      e.stopPropagation();
      await api("DELETE", `/api/presets?id=${encodeURIComponent(preset.id)}`);
      if (state.activePresetName === preset.name) {
        state.activePresetName = "";
      }
      await loadPresets();
    };
    item.appendChild(del);
  }

  item.onclick = () => {
    document.getElementById("presetDropdownMenu").classList.add("hidden");
    state.activePresetName = preset.name;
    applyPresetObj(preset);
    renderPresetMenu();
  };
  return item;
}

export function renderPresetMenu() {
  const menu = document.getElementById("presetDropdownMenu");
  menu.innerHTML = "";

  const builtIn = state.presets.filter(p => p.isBuiltIn);
  const custom = state.presets.filter(p => !p.isBuiltIn);

  const preHdr = document.createElement("div");
  preHdr.className = "dropdown-heading";
  preHdr.textContent = "PRE-DEFINED";
  menu.appendChild(preHdr);

  builtIn.forEach(p => menu.appendChild(buildPresetMenuRow(p, false)));

  if (custom.length > 0) {
    const div = document.createElement("div");
    div.className = "menu-divider";
    menu.appendChild(div);

    const custHdr = document.createElement("div");
    custHdr.className = "dropdown-heading";
    custHdr.textContent = "CUSTOM";
    menu.appendChild(custHdr);

    custom.forEach(p => menu.appendChild(buildPresetMenuRow(p, true)));
  }

  document.getElementById("presetBtnLabel").textContent = state.activePresetName || "Preset";
}

export async function loadPresets() {
  const res = await api("GET", "/api/presets");
  if (res.ok) {
    state.presets = res.data.presets || [];
    renderPresetMenu();
  }
}

export async function loadSpecs(forceRefresh) {
  const res = await api("GET", `/api/specs?refresh=${forceRefresh ? "true" : "false"}`);
  if (res.ok) {
    state.specs = res.data.specs || [];
    state.isLiveSpecs = !!res.data.isLive;
    state.activeMbyPath = res.data.activeMbyPath || "";
    const statusEl = document.getElementById("specSyncStatus");
    const reloadBtn = document.getElementById("btnRefreshSpecs");
    if (reloadBtn) reloadBtn.title = "Re-query live mby spec schemas";
    if (state.isLiveSpecs) {
      statusEl.textContent = "Live mby specs";
      statusEl.className = "sync-status-text live";
      statusEl.title = state.activeMbyPath ? `Source: ${state.activeMbyPath}` : "Synced live from installed mby CLI";
    } else {
      statusEl.textContent = "Fallback specs (mby not found)";
      statusEl.className = "sync-status-text";
      statusEl.title = "Using built-in schema catalog. Install mby in Setup or configure override in Settings.";
    }

    if (!state.prompt.components.length) {
      applyPresetObj(state.presets[0] || null);
    } else {
      renderPromptStudioSpecArea();
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
    selections: c.selections.filter(s => s.enabled).map(s => {
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
    orderedItems: c.orderedItems && c.orderedItems.length ? c.orderedItems : null
  }));

  const flatSelections = activeComps.flatMap(c => c.selections);

  return {
    container: state.prompt.container,
    title: document.getElementById("promptTitle").value.trim() || "Untitled Study Module",
    description: document.getElementById("promptDescription").value.trim(),
    authoringFormat: document.getElementById("promptFormat").value,
    sourceText: document.getElementById("promptSourceText").value,
    selections: flatSelections,
    attachments: allAttachments,
    useMelomgForPdfs: document.getElementById("chkUseMelomg").checked,
    embedInlineSchemas: false,
    lazyCliDiscovery: !state.prompt.agentsMdExported,
    agentsMdExported: state.prompt.agentsMdExported,
    workingDirectory: wsPath,
    mbyExecutablePath: state.resolvedTools?.mby || null,
    melomgExecutablePath: state.resolvedTools?.melomg || null,
    copiedFolders,
    components: activeComps
  };
}

export function getPromptOptionsEligibility() {
  const activeComps = state.prompt.components.filter(comp => {
    if (state.prompt.container === "standaloneArticle") return comp.kind === "article";
    if (state.prompt.container === "standaloneQuiz") return comp.kind === "quiz";
    return true;
  });
  const enabledSpecs = activeComps.flatMap(c => c.selections.filter(s => s.enabled));
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

  const rawDisabled = !e.hasAnyActiveSpec || e.hasMissingQty;
  rawBtn.disabled = rawDisabled;
  rawBtn.title = !e.hasAnyActiveSpec
    ? "Add at least one active spec first."
    : (e.hasMissingQty ? "Fill Qty on every active spec first." : "");

  const genReasons = [];
  if (!e.hasAnyActiveSpec) genReasons.push("add at least one active spec");
  else if (e.hasMissingQty) genReasons.push("fill Qty on every active spec");
  if (!e.hasActiveWorkspace) genReasons.push("select an Active Workspace");
  if (!e.hasMbyInstalled) genReasons.push("install Meloberry CLI (mby)");

  genBtn.disabled = genReasons.length > 0;
  genBtn.title = genReasons.length > 0
    ? `Requires: ${genReasons.join(", ")}.`
    : "";
}

export function schedulePromptPreview(delayMs = 120) {
  refreshPromptOptionsAvailability();
  clearTimeout(state.previewDebounceTimer);
  state.previewDebounceTimer = setTimeout(() => {
    refreshPromptPreview();
  }, delayMs);
}

export async function refreshPromptPreview() {
  clearTimeout(state.previewDebounceTimer);
  refreshPromptOptionsAvailability();
  const seq = ++state.previewSeq;
  const res = await api("POST", "/api/prompt/compose", buildPromptDraftPayload());
  if (seq !== state.previewSeq) return;
  if (res.ok) {
    state.composedPrompt = res.data;
    document.getElementById("promptPreviewBox").textContent = res.data.markdown;
    document.getElementById("promptMetrics").textContent = `${res.data.characterCount} chars \u00b7 ~${res.data.approximateTokens} tokens`;
  }
}

export function renderPromptAttachments() {
  const box = document.getElementById("promptAttachmentsList");
  box.innerHTML = "";
  const items = state.prompt.extraAttachments || [];
  box.classList.toggle("hidden", !items.length);
  items.forEach((a, idx) => {
    const chip = document.createElement("span");
    chip.className = "chip";
    chip.textContent = a.filePath.split(/[\\/]/).pop();
    const rm = document.createElement("button");
    rm.textContent = "x";
    rm.onclick = () => {
      state.prompt.extraAttachments.splice(idx, 1);
      renderPromptAttachments();
      refreshPromptPreview();
    };
    chip.appendChild(rm);
    box.appendChild(chip);
  });
}

export function wirePromptStudioView() {
  wireSpecSelector();
  wireSpecOrganizer({ getActiveComponent });
  wireReadyWorkspaceControls({ buildPromptDraftPayload, refreshPromptPreview });

  on("prompt:preset-modified", markPresetModified);
  on("prompt:org-render", renderOrganizationCard);
  on("prompt:refresh", refreshPromptPreview);
  on("prompt:schedule-preview", delay => schedulePromptPreview(delay ?? 120));
  on("prompt:availability-changed", refreshPromptOptionsAvailability);
  on("prompt:attachments-changed", renderPromptAttachments);
  on("specs:reload", force => loadSpecs(!!force));

  registerDropdown("btnWorkspaceActions", "actionsMenu", {
    wrapSelector: "#actionsMenuWrap",
    beforeOpen: refreshPromptOptionsAvailability
  });
  registerDropdown("btnPromptOptions", "promptOptionsMenu", {
    wrapSelector: "#promptOptionsWrap",
    beforeOpen: refreshPromptOptionsAvailability
  });

  document.getElementById("optCopyPlainText").onclick = async () => {
    closeAllDropdowns();
    try {
      await navigator.clipboard.writeText(state.composedPrompt.plainText || state.composedPrompt.markdown || "");
      toast("Copied prompt as plain text.", "ok");
    } catch {
      toast("Failed to copy to clipboard.", "err");
    }
  };
  document.getElementById("optCopyMarkdown").onclick = async () => {
    closeAllDropdowns();
    try {
      await navigator.clipboard.writeText(state.composedPrompt.markdown || "");
      toast("Copied Markdown prompt.", "ok");
    } catch {
      toast("Failed to copy to clipboard.", "err");
    }
  };
  document.getElementById("optSaveMarkdown").onclick = async () => {
    closeAllDropdowns();
    const r = await api("POST", "/api/prompt/save", { markdown: state.composedPrompt.markdown });
    if (r.ok && !r.data.cancelled) toast(`Saved ${r.data}`, "ok");
    else if (!r.ok) toast(r.data?.error?.description || "Save failed.", "err");
  };

  const optExportRawBtn = document.getElementById("optExportRawJson");
  optExportRawBtn.onclick = withButtonBusy(optExportRawBtn, null, async () => {
    closeAllDropdowns();
    const rawRes = await api("POST", "/api/prompt/raw-json", buildPromptDraftPayload());
    if (!rawRes.ok) {
      toast(rawRes.data?.error?.description || "Failed to generate raw JSON.", "err");
      return;
    }

    if (rawRes.data?.warnings?.length > 0) {
      const warnMsg = rawRes.data.warnings.join(" ");
      addNotification("Organizer Sequence Reconciled", warnMsg, "warn");
      toast(warnMsg, "warn");
    }

    const saveRes = await api("POST", "/api/prompt/save-json", {
      rawJson: rawRes.data.rawJson,
      defaultName: rawRes.data.suggestedFileName
    });
    if (saveRes.ok && !saveRes.data.cancelled) {
      toast(`Exported raw session JSON to ${saveRes.data}`, "ok");
    } else if (!saveRes.ok) {
      toast(saveRes.data?.error?.description || "Export failed.", "err");
    }
  });

  const optGenTplBtn = document.getElementById("optGenerateTemplate");
  optGenTplBtn.onclick = withButtonBusy(optGenTplBtn, null, async () => {
    closeAllDropdowns();
    const genRes = await api("POST", "/api/prompt/generate-template", buildPromptDraftPayload());
    if (!genRes.ok) {
      toast(genRes.data?.error?.description || "Failed to generate template.", "err");
      return;
    }

    if (genRes.data?.warnings?.length > 0) {
      const warnMsg = genRes.data.warnings.join(" ");
      addNotification("Organizer Sequence Reconciled", warnMsg, "warn");
      toast(warnMsg, "warn");
    }

    addNotification("Draft Template Created", `Session ${genRes.data.workspaceId} created from ${genRes.data.jsonPath}`, "info");
    toast(`Created draft workspace ${genRes.data.workspaceId}!`, "ok");
  });

  document.querySelectorAll("#containerTabs .seg-btn").forEach(btn => {
    btn.onclick = () => {
      const prev = state.prompt.container;
      const next = btn.dataset.container;
      if (prev === next) return;

      markPresetModified();
      state.prompt.containerCache[prev] = {
        components: state.prompt.components.map(cloneComponent),
        selectedComponentId: state.prompt.selectedComponentId
      };

      state.prompt.container = next;
      document.querySelectorAll("#containerTabs .seg-btn").forEach(b => b.classList.toggle("active", b === btn));

      const cached = state.prompt.containerCache[next];
      if (cached && Array.isArray(cached.components) && cached.components.length > 0) {
        state.prompt.components = cached.components.map(cloneComponent);
        state.prompt.selectedComponentId = cached.selectedComponentId;
      } else {
        state.prompt.components = state.prompt.components.map(cloneComponent);
        ensureComponentsForContainer();
      }

      renderOrganizationCard();
      renderPromptStudioSpecArea();
      refreshPromptPreview();
    };
  });

  registerDropdown("btnPresetDropdown", "presetDropdownMenu", { wrapSelector: "#presetMenuWrap" });

  const btnSavePreset = document.getElementById("btnSavePreset");
  btnSavePreset.onclick = withButtonBusy(btnSavePreset, "Saving...", async () => {
    const rawName = await openInputPromptModal("Save Custom Preset", "Enter a name for this preset:", state.activePresetName || "Custom Preset");
    if (!rawName || !rawName.trim()) return;
    const name = rawName.trim();
    const draft = buildPromptDraftPayload();
    const res = await api("POST", "/api/presets", {
      id: "",
      name,
      description: "Saved from Prompt Studio",
      container: draft.container,
      authoringFormat: draft.authoringFormat,
      selections: draft.selections,
      components: draft.components
    });
    if (res.ok) {
      state.activePresetName = res.data.name;
      await loadPresets();
      toast(`Saved preset "${res.data.name}"`, "ok");
    } else {
      toast(res.data?.error?.description || "Failed to save preset.", "err");
    }
  });

  document.getElementById("btnAddOrgArticle").onclick = () => {
    markPresetModified();
    const created = cloneComponent({ kind: "article", title: "New Article", selections: [] });
    state.prompt.components.push(created);
    state.prompt.selectedComponentId = created.id;
    state.lastAddedOrgId = created.id;
    renderOrganizationCard();
    renderPromptStudioSpecArea();
    refreshPromptPreview();
  };

  document.getElementById("btnAddOrgQuiz").onclick = () => {
    markPresetModified();
    const created = cloneComponent({ kind: "quiz", title: "New Quiz", selections: [] });
    state.prompt.components.push(created);
    state.prompt.selectedComponentId = created.id;
    state.lastAddedOrgId = created.id;
    renderOrganizationCard();
    renderPromptStudioSpecArea();
    refreshPromptPreview();
  };

  document.getElementById("orgSelectedTitle").oninput = e => {
    markPresetModified();
    const comp = getActiveComponent();
    comp.title = e.target.value;
    const activeBox = document.querySelector("#orgTrack .drag-box.active");
    if (activeBox) {
      const displayTitle = comp.title || (comp.kind === "article" ? "Study Article" : "Practice Quiz");
      activeBox.title = displayTitle;
      const tEl = activeBox.querySelector(".drag-box-title");
      if (tEl) tEl.textContent = displayTitle;
    }
    schedulePromptPreview(120);
  };

  document.querySelectorAll("#specCardModeTabs .seg-btn").forEach(btn => {
    btn.onclick = () => {
      state.prompt.specCardMode = btn.dataset.specmode;
      document.querySelectorAll("#specCardModeTabs .seg-btn").forEach(b => b.classList.toggle("active", b === btn));
      renderPromptStudioSpecArea();
    };
  });

  ["promptTitle", "promptDescription", "promptSourceText"].forEach(id => {
    const el = document.getElementById(id);
    el.addEventListener("input", () => schedulePromptPreview(120));
    el.addEventListener("change", () => refreshPromptPreview());
  });

  ["promptFormat", "chkUseMelomg"].forEach(id => {
    const el = document.getElementById(id);
    el.addEventListener("input", () => {
      if (id === "promptFormat") markPresetModified();
      refreshPromptPreview();
    });
    el.addEventListener("change", () => {
      if (id === "promptFormat") markPresetModified();
      refreshPromptPreview();
    });
  });

  document.getElementById("btnRefreshSpecs").onclick = () => loadSpecs(true);

  document.getElementById("btnAttachPromptFiles").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-files", { title: "Attach Files", allowMultiple: true });
    if (r.ok && !r.data.cancelled) {
      (r.data.paths || []).forEach(p => {
        if (!state.prompt.extraAttachments.some(a => a.filePath.toLowerCase() === p.toLowerCase())) {
          state.prompt.extraAttachments.push({ filePath: p, kind: p.toLowerCase().endsWith(".pdf") ? "pdf" : "file" });
        }
      });
      renderPromptAttachments();
      refreshPromptPreview();
    }
  };
}
