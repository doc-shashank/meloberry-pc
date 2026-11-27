import { api } from "../../api.js";
import { state, on, withButtonBusy } from "../../store.js";
import { registerDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { addNotification, toast } from "../../ui/chrome.js";
import { buildPromptDraftPayload, flushLiveDraftSave, refreshPromptOptionsAvailability, refreshPromptPreview } from "./promptDraft.js";
import { wireReadyWorkspaceControls } from "./readyWorkspace.js";

function syncReviewModeUi() {
  const mode = state.prompt.workflowMode || "direct";
  const heading = document.getElementById("reviewPreviewHeading");
  const promptOptsWrap = document.getElementById("promptOptionsWrap");
  const chkPlaceholders = document.getElementById("chkAllowPlaceholders");
  const finalizeBtn = document.getElementById("btnFinalizeWebApp");

  document.querySelectorAll("#reviewWorkflowModeTabs .seg-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.wfMode === mode);
  });

  if (heading) {
    heading.textContent = mode === "direct"
      ? "Build Readiness"
      : (mode === "liveAgent" ? "Live Agent Handoff Prompt" : "Prompt Preview");
  }
  if (promptOptsWrap) {
    promptOptsWrap.classList.toggle("hidden", mode === "direct");
  }
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
}
