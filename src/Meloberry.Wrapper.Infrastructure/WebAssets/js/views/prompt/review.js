import { api } from "../../api.js";
import { state, emit, on, escapeHtml, withButtonBusy } from "../../store.js";
import { registerDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { addNotification, toast } from "../../ui/chrome.js";
import { buildPromptDraftPayload, flushLiveDraftSave, refreshPromptOptionsAvailability, refreshPromptPreview } from "./promptDraft.js";
import { buildOrganizationDisplayItems, materializeOrderedItems } from "./specModes.js";
import { getSpecContentBadge } from "./organize.js";
import { wireReadyWorkspaceControls } from "./readyWorkspace.js";

function renderDirectReadinessPanel() {
  const panel = document.getElementById("directReadinessPanel");
  if (!panel) return;
  panel.innerHTML = "";
  let total = 0;
  let filled = 0;

  const activeComps = state.prompt.components.filter(comp => {
    if (state.prompt.container === "standaloneArticle") return comp.kind === "article";
    if (state.prompt.container === "standaloneQuiz") return comp.kind === "quiz";
    return true;
  });

  if (!activeComps.length) {
    panel.innerHTML = '<div class="readiness-empty-state">No components configured yet. Add an Article or Quiz in Organize.</div>';
  }

  activeComps.forEach(comp => {
    const items = buildOrganizationDisplayItems(comp);
    const sec = document.createElement("div");
    sec.className = "readiness-comp-group";
    sec.innerHTML = `<div class="readiness-comp-head"><strong>${escapeHtml(comp.title || comp.kind)}</strong><span class="pill">${escapeHtml(comp.kind)}</span></div>`;

    if (!items.length) {
      const emptyRow = document.createElement("div");
      emptyRow.className = "readiness-spec-row is-empty";
      emptyRow.innerHTML = '<span class="muted">No specs added yet in this component.</span>';
      sec.appendChild(emptyRow);
    }

    items.forEach(inst => {
      total++;
      const badge = getSpecContentBadge(inst.content);
      if (badge) filled++;

      const row = document.createElement("div");
      row.className = `readiness-spec-row ${badge ? "is-filled" : "is-empty"}`;
      const left = document.createElement("span");
      left.innerHTML = `<code>${escapeHtml(inst.topic)}</code>${inst.subtype ? ` <span class="muted">(${escapeHtml(inst.subtype)})</span>` : ""}`;

      const right = document.createElement("div");
      right.className = "row gap-8";
      const pill = document.createElement("span");
      pill.className = `pill ${badge ? "pill-ok" : ""}`;
      pill.textContent = badge ? badge.label : "Empty";
      if (badge?.title) pill.title = badge.title;

      const editBtn = document.createElement("button");
      editBtn.type = "button";
      editBtn.className = "btn btn-xs btn-ghost";
      editBtn.textContent = badge ? "Edit" : "Fill";
      editBtn.onclick = () => {
        materializeOrderedItems(comp);
        const live = (comp.orderedItems || []).find(o => o.id === inst.id)
          || (comp.orderedItems || []).find(o => o.topic === inst.topic && o.instanceNumber === inst.instanceNumber)
          || comp.orderedItems?.[0];
        if (live) {
          emit("spec:open-content-modal", { compId: comp.id, instId: live.id, focusMelomg: false });
        }
      };

      right.append(pill, editBtn);
      row.append(left, right);
      sec.appendChild(row);
    });

    panel.appendChild(sec);
  });

  const metrics = document.getElementById("promptMetrics");
  if (metrics) {
    metrics.textContent = `${filled}/${total} specs filled`;
  }
}

function syncReviewModeUi() {
  const mode = state.prompt.workflowMode || "direct";
  const isDirect = mode === "direct";
  const isLive = mode === "liveAgent";
  const isClassic = mode === "classicPrompt";

  const heading = document.getElementById("reviewPreviewHeading");
  const finalizeGroup = document.getElementById("finalizeCtaGroup");
  const allowPlaceholdersWrap = document.getElementById("chkAllowPlaceholdersWrap");
  const copyLiveBtn = document.getElementById("btnCopyLiveHandoff");
  const readyWsWrap = document.getElementById("readyWsMenuWrap");
  const readyWsBtn = document.getElementById("btnReadyWorkspace");
  const actionsWrap = document.getElementById("actionsMenuWrap");
  const promptOptsWrap = document.getElementById("promptOptionsWrap");
  const melomgWrap = document.getElementById("chkUseMelomgWrap");
  const liveBanner = document.getElementById("liveAgentHandoffBanner");
  const readinessPanel = document.getElementById("directReadinessPanel");
  const previewBox = document.getElementById("promptPreviewBox");
  const chkPlaceholders = document.getElementById("chkAllowPlaceholders");
  const finalizeBtn = document.getElementById("btnFinalizeWebApp");

  document.querySelectorAll("#reviewWorkflowModeTabs .seg-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.wfMode === mode);
  });

  if (heading) {
    heading.textContent = isDirect
      ? "Build Readiness"
      : (isLive ? "Live Agent Handoff Prompt" : "Prompt Preview");
  }
  if (finalizeGroup) finalizeGroup.classList.toggle("hidden", isClassic);
  if (allowPlaceholdersWrap) allowPlaceholdersWrap.classList.toggle("hidden", !isDirect);
  if (copyLiveBtn) copyLiveBtn.classList.toggle("hidden", !isLive);
  if (readyWsWrap) readyWsWrap.classList.toggle("hidden", isDirect);
  if (readyWsBtn) readyWsBtn.textContent = isLive ? "Ready Workspace (Live AGENTS.md)" : "Ready Workspace";
  if (actionsWrap) actionsWrap.classList.toggle("hidden", !isClassic);
  if (promptOptsWrap) promptOptsWrap.classList.toggle("hidden", !isClassic);
  if (melomgWrap) melomgWrap.classList.toggle("hidden", isDirect);
  if (liveBanner) liveBanner.classList.toggle("hidden", !isLive);
  if (readinessPanel) readinessPanel.classList.toggle("hidden", !isDirect);
  if (previewBox) previewBox.classList.toggle("hidden", isDirect);

  if (chkPlaceholders) {
    chkPlaceholders.checked = !!state.prompt.allowPlaceholdersOnFinalize;
  }

  if (finalizeBtn) {
    const wsPath = state.settings.activeWorkspace?.workspacePath;
    const hasMby = Boolean(state.resolvedTools?.mby || state.apps.some(a => a.kind === "mby" && a.isInstalled));
    finalizeBtn.disabled = !wsPath || !hasMby;
    finalizeBtn.title = !wsPath
      ? "Select an Active Workspace in Setup first."
      : (!hasMby ? "Install Meloberry CLI (mby) in Setup to compile WebApps." : "Assemble modular Markdown and compile via mby batch -");
  }

  if (isDirect) {
    renderDirectReadinessPanel();
  }
}

export function wireReviewView() {
  wireReadyWorkspaceControls({ buildPromptDraftPayload, refreshPromptPreview });

  registerDropdown("btnWorkspaceActions", "actionsMenu", {
    wrapSelector: "#actionsMenuWrap",
    beforeOpen: refreshPromptOptionsAvailability
  });
  registerDropdown("btnPromptOptions", "promptOptionsMenu", {
    wrapSelector: "#promptOptionsWrap",
    beforeOpen: refreshPromptOptionsAvailability
  });

  on("view:changed", ({ viewName }) => {
    if (viewName === "review") {
      syncReviewModeUi();
      refreshPromptPreview();
    }
  });
  on("format:sync", syncReviewModeUi);
  on("prompt:availability-changed", syncReviewModeUi);
  on("organize:render", () => {
    if ((state.prompt.workflowMode || "direct") === "direct") {
      renderDirectReadinessPanel();
    }
  });

  const chkPlaceholders = document.getElementById("chkAllowPlaceholders");
  chkPlaceholders?.addEventListener("change", () => {
    state.prompt.allowPlaceholdersOnFinalize = !!chkPlaceholders.checked;
    refreshPromptPreview();
  });

  const finalizeBtn = document.getElementById("btnFinalizeWebApp");
  if (finalizeBtn) {
    finalizeBtn.onclick = withButtonBusy(finalizeBtn, "Building WebApp...", async () => {
      const wsPath = state.settings.activeWorkspace?.workspacePath;
      if (!wsPath) {
        toast("Select an Active Workspace in Setup first.", "err");
        return;
      }

      await flushLiveDraftSave("Pre-finalize save");
      const res = await api("POST", "/api/live/finalize", {
        workspacePath: wsPath,
        allowPlaceholders: !!state.prompt.allowPlaceholdersOnFinalize,
        verifyStaticOnly: true
      });

      if (!res.ok) {
        const msg = res.data?.error?.description || "Finalize failed.";
        toast(msg, "err");
        addNotification("Finalize Blocked", msg, "err");
        return;
      }

      addNotification("WebApp Built", `Session ${res.data.workspaceId} compiled cleanly.`, "info");
      toast(`Built ${res.data.workspaceId} -> Viewer ready!`, "ok");
    });
  }

  const chkMelomg = document.getElementById("chkUseMelomg");
  chkMelomg.addEventListener("input", () => refreshPromptPreview());
  chkMelomg.addEventListener("change", () => refreshPromptPreview());

  const copyLiveBtn = document.getElementById("btnCopyLiveHandoff");
  if (copyLiveBtn) {
    copyLiveBtn.onclick = async () => {
      await flushLiveDraftSave("Synced before prompt export");
      try {
        await navigator.clipboard.writeText(state.composedPrompt.markdown || "");
        toast("Copied Live Agent handoff prompt.", "ok");
      } catch {
        toast("Failed to copy to clipboard.", "err");
      }
    };
  }

  document.getElementById("optCopyPlainText").onclick = async () => {
    closeAllDropdowns();
    await flushLiveDraftSave("Synced before prompt export");
    try {
      await navigator.clipboard.writeText(state.composedPrompt.plainText || state.composedPrompt.markdown || "");
      toast("Copied prompt as plain text.", "ok");
    } catch {
      toast("Failed to copy to clipboard.", "err");
    }
  };

  document.getElementById("optCopyMarkdown").onclick = async () => {
    closeAllDropdowns();
    await flushLiveDraftSave("Synced before prompt export");
    try {
      await navigator.clipboard.writeText(state.composedPrompt.markdown || "");
      toast("Copied Markdown prompt.", "ok");
    } catch {
      toast("Failed to copy to clipboard.", "err");
    }
  };

  document.getElementById("optSaveMarkdown").onclick = async () => {
    closeAllDropdowns();
    await flushLiveDraftSave("Synced before prompt export");
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
}
