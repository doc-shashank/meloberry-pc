import { api } from "../../api.js";
import { state } from "../../store.js";
import { registerDropdown, toggleDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { openConfirmModal, closeConfirmModal } from "../../ui/modal.js";
import { renderAgentsPill, switchView, toast } from "../../ui/chrome.js";

export function openOverwriteConfirmModal(agentsPath, targetDirectory, alreadyWarnedMissingMby, onConfirmed) {
  openConfirmModal({
    title: "Overwrite Existing AGENTS.md?",
    body: `AGENTS.md already exists at "${agentsPath}". Overwrite it with the updated workspace playbook and targeted schemas?`,
    proceedText: "Overwrite AGENTS.md",
    showProceed: true,
    onProceed: async () => {
      closeConfirmModal();
      await onConfirmed(true, targetDirectory, alreadyWarnedMissingMby);
    }
  });
}

export async function readyWorkspace(
  { buildPromptDraftPayload, refreshPromptPreview },
  overwriteConfirmed = false,
  targetDirectory = null,
  alreadyWarnedMissingMby = false
) {
  const draft = buildPromptDraftPayload();
  if (!draft.workingDirectory) {
    toast("Select an Active Workspace first.", "err");
    switchView("setup");
    return;
  }

  const hasMbyInstalled = Boolean(state.resolvedTools?.mby || state.apps.some(a => a.kind === "mby" && a.isInstalled));
  if (!hasMbyInstalled && !alreadyWarnedMissingMby) {
    toast("Meloberry CLI (mby) is not installed yet — install it in Setup to run workspace commands via mby-pc.bat.", "warn", { persistent: true, key: "missing-ws-mby" });
    alreadyWarnedMissingMby = true;
  }

  if (!overwriteConfirmed) {
    const q = targetDirectory
      ? `&targetDirectory=${encodeURIComponent(targetDirectory)}`
      : "";
    const statusRes = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(draft.workingDirectory)}${q}`);
    if (statusRes.ok && statusRes.data.exists) {
      openOverwriteConfirmModal(statusRes.data.agentsMdPath, targetDirectory, alreadyWarnedMissingMby, (ow, dir, warned) =>
        readyWorkspace({ buildPromptDraftPayload, refreshPromptPreview }, ow, dir, warned));
      return;
    }
  }

  const res = await api("POST", "/api/workspace/ready", { draft, overwriteConfirmed, targetDirectory });
  if (res.status === 409) {
    const targetPath = (targetDirectory || draft.workingDirectory) + "\\AGENTS.md";
    openOverwriteConfirmModal(targetPath, targetDirectory, alreadyWarnedMissingMby, (ow, dir, warned) =>
      readyWorkspace({ buildPromptDraftPayload, refreshPromptPreview }, ow, dir, warned));
    return;
  }

  if (!res.ok) {
    toast(res.data?.error?.description || "Failed to export AGENTS.md.", "err");
    return;
  }

  state.prompt.agentsMdExported = true;
  state.agentsMdStatus = {
    exists: true,
    agentsMdPath: res.data.outputPath,
    sizeBytes: res.data.bytesWritten,
    characterCount: res.data.characterCount ?? (res.data.markdownPreview ? res.data.markdownPreview.length : 0),
    approximateTokens: res.data.approximateTokens,
    preview: res.data.markdownPreview
  };
  renderAgentsPill(state.agentsMdStatus, true);

  await refreshPromptPreview();
  if (Array.isArray(res.data.warnings) && res.data.warnings.length > 0) {
    res.data.warnings.forEach(w => toast(w, "warn"));
  }
  toast(`AGENTS.md Ready (${res.data.bytesWritten} B \u00b7 ~${res.data.approximateTokens} tokens).`, "ok");
}

export function wireReadyWorkspaceControls({ buildPromptDraftPayload, refreshPromptPreview }) {
  const deps = { buildPromptDraftPayload, refreshPromptPreview };
  registerDropdown("btnReadyWorkspace", "readyWsMenu", { wrapSelector: "#readyWsMenuWrap", autoWireClick: false });

  document.getElementById("btnReadyWorkspace").onclick = async e => {
    e.stopPropagation();
    const aws = state.settings.activeWorkspace;
    const wsPath = aws?.workspacePath;
    if (!wsPath) {
      closeAllDropdowns();
      toast("Select an Active Workspace first.", "err");
      switchView("setup");
      return;
    }

    const subRes = await api("GET", `/api/workspace/subfolders?workspacePath=${encodeURIComponent(wsPath)}`);
    if (subRes.ok && subRes.data.hasSubfolders) {
      toggleDropdown("readyWsMenu");
      return;
    }

    closeAllDropdowns();
    await readyWorkspace(deps, false, null);
  };

  document.getElementById("optReadyWsRoot").onclick = async () => {
    closeAllDropdowns();
    await readyWorkspace(deps, false, null);
  };

  document.getElementById("optReadyWsChoose").onclick = async () => {
    closeAllDropdowns();
    const wsPath = state.settings.activeWorkspace?.workspacePath;
    if (!wsPath) return;
    const pickRes = await api("POST", "/api/dialogs/pick-folder", {
      title: "Select Subfolder in Active Workspace for AGENTS.md",
      initialDir: wsPath
    });
    if (!pickRes.ok || pickRes.data.cancelled || !pickRes.data.path) return;
    const picked = pickRes.data.path;
    const normWs = wsPath.replace(/[\\/]+$/, "").toLowerCase();
    const normPick = picked.replace(/[\\/]+$/, "").toLowerCase();
    if (normPick !== normWs && !normPick.startsWith(normWs + "\\") && !normPick.startsWith(normWs + "/")) {
      toast("Selected folder must be inside the Active Workspace.", "err");
      return;
    }
    await readyWorkspace(deps, false, picked);
  };
}
