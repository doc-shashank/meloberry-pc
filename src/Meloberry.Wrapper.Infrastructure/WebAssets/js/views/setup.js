import { api } from "../api.js";
import { loadWorkspaceDraft } from "./prompt/promptDraft.js";
import { state, emit, escapeHtml, extractFolderName, withButtonBusy } from "../store.js";
import { getIcon } from "../icons.js";
import { registerDropdown } from "../ui/dropdowns.js";
import { openConfirmModal, closeConfirmModal, showPathTooltip, hidePathTooltip } from "../ui/modal.js";
import {
  addNotification,
  toast,
  pruneSelectedAppNames,
  syncWorkspaceChrome,
  renderIncompatibleMbyBanner,
  renderOutdatedBanner,
  updateChromeOffset
} from "../ui/chrome.js";

export function selectUpgradedApp(appEntry) {
  if (!appEntry || !appEntry.fileName) return;
  if (appEntry.kind === "customExecutable") {
    state.selectedAppNames.add(appEntry.fileName);
  }
}

function syncStagedCopyBarVisibility() {
  const bar = document.getElementById("stagedCopyBar");
  const box = document.getElementById("planConflictsBox");
  const hasPending = state.stagedItems.length > 0 || state.selectedAppNames.size > 0;
  if (bar) bar.classList.toggle("hidden", !hasPending);
  if (!hasPending && box) box.classList.add("hidden");
}

export function loadWorkspaceDraftState() {
  const aws = state.settings.activeWorkspace;
  const customPending = new Set(
    state.apps
      .filter(a => a.isInstalled && a.kind === "customExecutable" && !a.isPresentInWorkspace)
      .map(a => a.fileName)
  );

  if (aws && aws.workspacePath) {
    document.getElementById("setupWsPath").value = aws.workspacePath;
    state.selectedAppNames = new Set(
      (aws.selectedAppFileNames || []).filter(n => customPending.has(n))
    );
    state.stagedItems = [...(aws.stagedItems || [])];
  } else {
    state.selectedAppNames.clear();
    state.stagedItems = [];
  }

  syncWorkspaceChrome();
  renderAppsCart();
  renderStagedItems();
}

export function renderRecentWorkspaces() {
  const section = document.getElementById("recentWsSection");
  const row = document.getElementById("recentWsRow");
  row.innerHTML = "";
  const recents = (state.settings.recentWorkspaces || []).slice(0, 5);
  if (!recents.length) {
    section.classList.add("hidden");
    return;
  }

  section.classList.remove("hidden");
  recents.forEach(p => {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "chip";
    chip.textContent = extractFolderName(p);
    chip.addEventListener("mouseenter", () => {
      clearTimeout(state.hoverTimer);
      state.hoverTimer = setTimeout(() => showPathTooltip(chip, p), 350);
    });
    chip.addEventListener("mouseleave", hidePathTooltip);
    chip.onclick = async () => {
      hidePathTooltip();
      document.getElementById("setupWsPath").value = p;
      await selectWorkspace(p);
    };
    row.appendChild(chip);
  });
}

export async function selectWorkspace(path) {
  if (!path) return;
  const res = await api("POST", "/api/workspace/select", { path });
  if (res.ok) {
    const wsState = res.data.state || res.data;
    state.settings.activeWorkspace = wsState;
    if (Array.isArray(res.data.recentWorkspaces)) {
      state.settings.recentWorkspaces = res.data.recentWorkspaces;
    }
    state.outdatedTools = res.data.outdatedTools || [];
    if (state.outdatedTools.length > 0) {
      addNotification("Legacy Workspace Tools", `${state.outdatedTools.length} outdated workspace executable(s) can be migrated to mby-pc.bat.`, "warn");
    }

    const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(wsState.workspacePath)}`);
    state.agentsMdStatus = st.ok ? st.data : null;
    await refreshApps(false);
    loadWorkspaceDraftState();
    await loadWorkspaceDraft(wsState.workspacePath);
    renderRecentWorkspaces();
    renderOutdatedBanner();
    await previewPlan();
    emit("prompt:refresh");
  } else {
    toast(res.data?.error?.description || "Invalid workspace path.", "err");
  }
}

export async function refreshApps(render = true) {
  const res = await api("GET", "/api/apps");
  if (res.ok) {
    state.apps = res.data.apps || [];
    if (res.data.resolvedTools) {
      state.resolvedTools = res.data.resolvedTools;
    }
    if ("mbyCompatibility" in res.data) {
      state.mbyCompatibility = res.data.mbyCompatibility ?? null;
    }
    pruneSelectedAppNames();
    renderIncompatibleMbyBanner();
    if (render) {
      renderAppsCart();
      emit("prompt:availability-changed");
    }
  }
}

export function renderAppsCart() {
  const container = document.getElementById("appsCartList");
  container.innerHTML = "";

  state.apps.forEach(app => {
    const isManagedCli = app.kind === "mby" || app.kind === "melomg";
    const card = document.createElement("div");
    const checked = !isManagedCli && state.selectedAppNames.has(app.fileName);
    card.className = `app-card${isManagedCli ? " app-card-central" : ""}${checked ? " selected" : ""}`;

    const top = document.createElement("div");
    top.className = "app-card-top";

    const lbl = document.createElement(isManagedCli ? "div" : "label");
    lbl.className = "check-label";
    if (!isManagedCli) {
      const cb = document.createElement("input");
      cb.type = "checkbox";
      cb.checked = checked;
      cb.disabled = !app.isInstalled;
      cb.onchange = async () => {
        if (cb.checked) state.selectedAppNames.add(app.fileName);
        else state.selectedAppNames.delete(app.fileName);
        card.classList.toggle("selected", cb.checked);
        syncStagedCopyBarVisibility();
        await previewPlan();
      };
      lbl.appendChild(cb);
    }

    const titleSpan = document.createElement("span");
    if (app.kind === "mby") titleSpan.innerHTML = "Meloberry <code>cli</code>";
    else if (app.kind === "melomg") titleSpan.innerHTML = "Meloberry <code>melomg</code>";
    else titleSpan.textContent = app.displayName;
    lbl.appendChild(titleSpan);

    const statusEl = document.createElement("span");
    if (app.isInstalled) {
      statusEl.className = "pill pill-ok";
      statusEl.textContent = app.version ? `v${app.version}` : "Installed";
    } else {
      statusEl.className = "orange-text";
      statusEl.textContent = "Not Installed";
    }

    top.append(lbl, statusEl);
    card.appendChild(top);

    const desc = document.createElement("div");
    desc.className = "muted";
    desc.textContent = app.isInstalled
      ? (isManagedCli ? `Central tool (${app.fileName}) \u00b7 invoked via mby-pc.bat` : (app.description || app.fileName))
      : "Not installed. Download from GitHub.";
    card.appendChild(desc);

    const actions = document.createElement("div");
    actions.className = "row gap-6 wrap";
    const statusLine = document.createElement("div");
    statusLine.className = "muted hidden";
    const progWrap = document.createElement("div");
    progWrap.className = "progress-wrap hidden";
    const progBar = document.createElement("div");
    progBar.className = "progress-bar";
    progWrap.appendChild(progBar);

    if (app.repoKey) {
      if (!app.isInstalled) {
        const dlBtn = document.createElement("button");
        dlBtn.className = "btn btn-xs btn-primary";
        dlBtn.innerHTML = `${getIcon("ui/download")}<span>via GitHub</span>`;
        dlBtn.onclick = () => downloadToolFromGitHub(app.repoKey, dlBtn, progWrap, progBar, statusLine);
        actions.appendChild(dlBtn);
      } else {
        const checkInfo = state.appUpdateChecks[app.repoKey];
        const updBtn = document.createElement("button");
        updBtn.className = "btn btn-xs btn-primary";
        if (checkInfo && checkInfo.updateAvailable) {
          updBtn.innerHTML = `${getIcon("ui/download")}<span>v${escapeHtml(checkInfo.latestVersion || "latest")}</span>`;
          updBtn.onclick = () => downloadToolFromGitHub(app.repoKey, updBtn, progWrap, progBar, statusLine);
        } else {
          updBtn.textContent = "Check for updates";
          updBtn.onclick = async () => {
            updBtn.disabled = true;
            updBtn.textContent = "Checking...";
            const chk = await api("GET", `/api/apps/check-update?tool=${encodeURIComponent(app.repoKey)}`);
            updBtn.disabled = false;
            if (chk.ok) {
              if (chk.data.updateAvailable) {
                state.appUpdateChecks[app.repoKey] = chk.data;
                addNotification("CLI Update Available", `${app.displayName} v${chk.data.latestVersion} is ready to download.`, "info");
              } else {
                delete state.appUpdateChecks[app.repoKey];
              }
              renderAppsCart();
              toast(chk.data.statusMessage, chk.data.updateAvailable ? "ok" : "info");
            } else {
              updBtn.textContent = "Check for updates";
              toast(chk.data?.error?.description || "Update check failed.", "err");
            }
          };
        }
        actions.appendChild(updBtn);
      }
    }

    if (app.isInstalled) {
      const unBtn = document.createElement("button");
      unBtn.className = "btn btn-xs btn-danger-ghost";
      unBtn.textContent = "Uninstall";
      unBtn.onclick = () => confirmUninstallApp(app);
      actions.appendChild(unBtn);
    }

    card.append(actions, statusLine, progWrap);
    container.appendChild(card);
  });
  syncStagedCopyBarVisibility();
}

export function confirmUninstallApp(app) {
  openConfirmModal({
    title: "Uninstall App",
    body: `Remove "${app.fileName}" from Meloberry apps\\ store? Any copies inside workspaces remain untouched.`,
    proceedText: "Uninstall",
    showProceed: true,
    onProceed: async () => {
      closeConfirmModal();
      const res = await api("POST", "/api/apps/uninstall", { idOrFileName: app.id });
      if (!res.ok) {
        toast(res.data?.error?.description || "Uninstall failed.", "err");
        return;
      }

      state.selectedAppNames.delete(app.fileName);
      if (app.repoKey) delete state.appUpdateChecks[app.repoKey];
      await refreshApps();
      emit("specs:reload", true);
      toast(`Uninstalled ${res.data}`, "ok");
    }
  });
}

export async function downloadToolFromGitHub(toolKey, btn, progWrap, progBar, statusLine) {
  btn.disabled = true;
  progWrap.classList.remove("hidden");
  statusLine.classList.remove("hidden");
  statusLine.textContent = "Querying GitHub...";

  const poller = setInterval(async () => {
    const s = await api("GET", `/api/apps/download-status?tool=${encodeURIComponent(toolKey)}`);
    if (s.ok && s.data.status) {
      const snap = s.data.status;
      statusLine.textContent = snap.message;
      if (snap.percent !== null && snap.percent !== undefined) {
        progBar.style.width = `${snap.percent}%`;
      }
    }
  }, 350);

  try {
    const res = await api("POST", "/api/apps/download", { tool: toolKey });
    if (res.ok) {
      progBar.style.width = "100%";
      delete state.appUpdateChecks[toolKey];
      selectUpgradedApp(res.data);
      await refreshApps();
      if (toolKey === "mby") emit("specs:reload", true);
      toast(`Installed ${res.data.fileName}`, "ok");
    } else {
      statusLine.textContent = res.data?.error?.description || "Download failed.";
      toast(statusLine.textContent, "err");
    }
  } finally {
    clearInterval(poller);
    btn.disabled = false;
  }
}

export function renderStagedItems() {
  const list = document.getElementById("stagedItemsList");
  list.innerHTML = "";
  syncStagedCopyBarVisibility();
  if (!state.stagedItems.length) {
    list.innerHTML = '<div class="muted">No files or folder added...</div>';
    return;
  }

  state.stagedItems.forEach((item, idx) => {
    const row = document.createElement("div");
    row.className = "staged-item";

    const left = document.createElement("div");
    const typeTag = document.createElement("code");
    typeTag.textContent = item.kind === "folder" ? "Folder" : "File";
    const colon = document.createTextNode(": ");
    const nameSpan = document.createElement("span");
    nameSpan.className = "staged-name-hover";
    nameSpan.textContent = item.displayName;

    nameSpan.addEventListener("mouseenter", () => {
      clearTimeout(state.hoverTimer);
      state.hoverTimer = setTimeout(() => showPathTooltip(nameSpan, item.sourcePath), 350);
    });
    nameSpan.addEventListener("mouseleave", hidePathTooltip);

    left.append(typeTag, colon, nameSpan);

    const rm = document.createElement("button");
    rm.className = "btn btn-xs btn-ghost";
    rm.textContent = "Remove";
    rm.onclick = async () => {
      hidePathTooltip();
      state.stagedItems.splice(idx, 1);
      renderStagedItems();
      await previewPlan();
    };

    row.append(left, rm);
    list.appendChild(row);
  });
}

export async function previewPlan() {
  const wsPath = document.getElementById("setupWsPath").value.trim();
  const box = document.getElementById("planConflictsBox");
  const summary = document.getElementById("planSummaryText");
  syncStagedCopyBarVisibility();
  if (!wsPath || (state.stagedItems.length === 0 && state.selectedAppNames.size === 0)) {
    if (summary) summary.textContent = "";
    if (box) box.classList.add("hidden");
    return null;
  }

  const res = await api("POST", "/api/workspace/plan", {
    workspacePath: wsPath,
    selectedAppFileNames: Array.from(state.selectedAppNames),
    stagedItems: state.stagedItems
  });
  if (!res.ok) {
    summary.textContent = res.data?.error?.description || "Invalid path.";
    box.classList.add("hidden");
    return null;
  }

  const p = res.data;
  summary.textContent = `${p.plannedCopiesCount} to copy, ${p.skippedIdenticalCount} identical skipped`;
  if (p.conflicts.length > 0) {
    box.classList.remove("hidden");
    box.innerHTML = `<strong>${p.conflicts.length} conflict(s):</strong> ${p.conflicts.map(c => `<code>${escapeHtml(c.relativeTargetPath)}</code>`).join(", ")}`;
  } else {
    box.classList.add("hidden");
  }
  return p;
}

export async function copyStagedItems() {
  const wsPath = document.getElementById("setupWsPath").value.trim();
  if (!wsPath) {
    toast("Select an Active Workspace folder first.", "err");
    return;
  }

  const res = await api("POST", "/api/workspace/initialize", {
    workspacePath: wsPath,
    selectedAppFileNames: Array.from(state.selectedAppNames),
    stagedItems: state.stagedItems,
    conflictPolicy: state.conflictPolicy
  });

  if (!res.ok) {
    toast(res.data?.error?.description || "Failed to copy items to workspace.", "err");
    return;
  }

  state.settings.activeWorkspace = res.data.activeWorkspace;
  if (Array.isArray(res.data.recentWorkspaces)) {
    state.settings.recentWorkspaces = res.data.recentWorkspaces;
  }
  state.stagedItems = [];
  state.selectedAppNames.clear();

  const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(wsPath)}`);
  state.agentsMdStatus = st.ok ? st.data : null;

  const outRes = await api("GET", `/api/workspace/outdated?workspacePath=${encodeURIComponent(wsPath)}`);
  state.outdatedTools = outRes.ok ? (outRes.data.outdatedTools || []) : [];

  await refreshApps(false);
  emit("specs:reload", true);
  syncWorkspaceChrome();
  renderAppsCart();
  renderStagedItems();
  await previewPlan();
  renderOutdatedBanner();
  renderRecentWorkspaces();
  emit("prompt:attachments-changed");
  emit("prompt:refresh");

  const ir = res.data.initResult;
  toast(`Copied ${ir.copiedApps.length} custom app(s), ${ir.copiedFiles.length} file(s), ${ir.copiedFolders.length} folder(s) to workspace.`, "ok");
}

export function wireSetupView() {
  document.getElementById("btnDismissOutdated").onclick = () => {
    document.getElementById("outdatedToolsBar").classList.add("hidden");
    updateChromeOffset();
  };

  document.getElementById("btnUpdateOutdatedTools").onclick = async () => {
    const aws = state.settings.activeWorkspace;
    if (!aws || !aws.workspacePath) return;
    const res = await api("POST", "/api/workspace/update-tools", { path: aws.workspacePath });
    if (!res.ok) {
      toast(res.data?.error?.description || "Tool update failed.", "err");
      return;
    }

    if (res.data.activeWorkspace) state.settings.activeWorkspace = res.data.activeWorkspace;
    state.outdatedTools = [];
    renderOutdatedBanner();
    await refreshApps(false);
    syncWorkspaceChrome();
    renderAppsCart();
    emit("specs:reload", true);
    emit("prompt:refresh");
    const removedCount = res.data.updateResult?.removedOldRelativePaths?.length || 0;
    const migrated = (res.data.updateResult?.copiedNewFileNames || []).join(", ") || "mby-pc.bat";
    toast(`Migrated ${removedCount} legacy workspace CLI(s) to ${migrated}`, "ok");
  };

  document.getElementById("btnPickWsFolder").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-folder", { title: "Select Active Workspace Folder", initialDir: document.getElementById("setupWsPath").value });
    if (r.ok && !r.data.cancelled) {
      document.getElementById("setupWsPath").value = r.data.path;
      await selectWorkspace(r.data.path);
    }
  };
  document.getElementById("setupWsPath").onchange = e => selectWorkspace(e.target.value);

  document.getElementById("btnRefreshApps").onclick = () => refreshApps();
  document.getElementById("btnImportCustomExe").onclick = async () => {
    const r = await api("POST", "/api/apps/import", {});
    if (r.ok && !r.data.cancelled) {
      selectUpgradedApp(r.data);
      await refreshApps();
      emit("specs:reload", true);
      toast(`Imported ${r.data.fileName}`, "ok");
    } else if (!r.ok) {
      toast(r.data?.error?.description || "Import failed.", "err");
    }
  };

  document.getElementById("btnStageFiles").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-files", { title: "Select Files", allowMultiple: true });
    if (r.ok && !r.data.cancelled) {
      (r.data.paths || []).forEach(p => {
        const name = p.split(/[\\/]/).pop();
        if (!state.stagedItems.some(i => i.sourcePath.toLowerCase() === p.toLowerCase())) {
          state.stagedItems.push({ sourcePath: p, kind: "file", displayName: name, targetRelativePath: name });
        }
      });
      renderStagedItems();
      await previewPlan();
    }
  };

  document.getElementById("btnStageFolder").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-folder", { title: "Select Folder" });
    if (r.ok && !r.data.cancelled && r.data.path) {
      const folderName = r.data.path.replace(/[\\/]+$/, "").split(/[\\/]/).pop();
      if (!state.stagedItems.some(i => i.sourcePath.toLowerCase() === r.data.path.toLowerCase())) {
        state.stagedItems.push({ sourcePath: r.data.path, kind: "folder", displayName: folderName, targetRelativePath: folderName });
      }
      renderStagedItems();
      await previewPlan();
    }
  };

  registerDropdown("btnConflictDropdown", "conflictMenu", { wrapSelector: "#conflictMenuWrap" });
  document.querySelectorAll("#conflictMenu .dropdown-item").forEach(item => {
    item.onclick = () => {
      state.conflictPolicy = item.dataset.policy;
      document.querySelectorAll("#conflictMenu .dropdown-item").forEach(b => b.classList.toggle("active", b === item));
      document.getElementById("conflictBtnLabel").textContent =
        state.conflictPolicy === "overwriteExisting" ? "Conflicts: Overwrite" : "Conflicts: Skip existing";
      document.getElementById("conflictMenu").classList.add("hidden");
    };
  });

  const btnCopyStaged = document.getElementById("btnCopyStagedItems");
  if (btnCopyStaged) {
    btnCopyStaged.onclick = withButtonBusy(btnCopyStaged, "Copying...", copyStagedItems);
  }
}
