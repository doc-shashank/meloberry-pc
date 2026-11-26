import { api, openEventStream } from "./api.js";
import { state, on, extractFolderName } from "./store.js";
import { loadIconRegistry } from "./icons.js";
import { wireGlobalOverlayDismissal } from "./ui/dropdowns.js";
import { closeConfirmModal, closeInputPromptModal, wireModalControls } from "./ui/modal.js";
import {
  addNotification,
  applyTheme,
  renderOutdatedBanner,
  switchView,
  syncWorkspaceChrome,
  toast,
  updateChromeOffset,
  wireChromeControls
} from "./ui/chrome.js";
import {
  loadWorkspaceDraftState,
  previewPlan,
  refreshApps,
  renderAppsCart,
  renderRecentWorkspaces,
  renderStagedItems,
  selectWorkspace,
  wireSetupView
} from "./views/setup.js";
import {
  loadPresets,
  loadSpecs,
  refreshPromptOptionsAvailability,
  refreshPromptPreview,
  renderPromptAttachments,
  wirePromptStudioView
} from "./views/prompt/index.js";
import {
  applyViewerThemeOverride,
  openWebAppPath,
  refreshViewerDiscovery,
  syncOverrideThemeUi,
  wireViewerView
} from "./views/viewer.js";
import {
  renderEcosystemRepos,
  syncSettingsInputs,
  wireSettingsView
} from "./views/settings.js";

function startRealtimeEvents() {
  if (state.eventSource) {
    state.eventSource.close();
  }

  const es = openEventStream();
  es.addEventListener("fschange", async ev => {
    let info;
    try { info = JSON.parse(ev.data); } catch { return; }
    if (!info) return;

    const isExe = (info.fileName || "").toLowerCase().endsWith(".exe");

    if (info.scope === "apps") {
      await refreshApps(false);
      renderAppsCart();
      refreshPromptOptionsAvailability();
      await loadSpecs(true);
      if (info.changeKind === "deleted" && isExe) {
        addNotification("Manually Removed", `${info.fileName} was removed from apps\\`, "warn");
      } else {
        const verb = info.changeKind === "deleted" ? "removed from apps\\" : "updated in apps\\";
        addNotification("Apps Folder Changed", `${info.fileName} was ${verb}`, info.changeKind === "deleted" ? "warn" : "info");
      }
    } else if (info.scope === "workspace") {
      const aws = state.settings.activeWorkspace;
      if (aws && aws.workspacePath) {
        const outRes = await api("GET", `/api/workspace/outdated?workspacePath=${encodeURIComponent(aws.workspacePath)}`);
        state.outdatedTools = outRes.ok ? (outRes.data.outdatedTools || []) : [];
        renderOutdatedBanner();

        const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(aws.workspacePath)}`);
        state.agentsMdStatus = st.ok ? st.data : null;
        await refreshApps(false);
        syncWorkspaceChrome();
        renderAppsCart();
        await refreshPromptPreview();

        if (info.changeKind === "deleted" && isExe) {
          addNotification("Manually Removed", `${info.fileName} was removed from workspace`, "warn");
        } else {
          const verb = info.changeKind === "deleted" ? "manually removed from" : "changed in";
          addNotification("Workspace Updated", `${info.fileName} ${verb} workspace`, info.changeKind === "deleted" ? "warn" : "info");
        }
      }
    }
  });
  state.eventSource = es;
}

function wireDragAndDrop() {
  function clearDropHighlights() {
    document.querySelectorAll(".drop-target-active").forEach(el => el.classList.remove("drop-target-active"));
  }

  function resolveExternalDropTarget(eventTarget) {
    if (state.currentView === "setup") {
      if (eventTarget && eventTarget.closest("#setupWsCard")) return { view: "setup", target: "setup-workspace", el: document.getElementById("setupWsCard") };
      return { view: "setup", target: "setup-staged", el: document.getElementById("setupStagedCard") };
    }
    if (state.currentView === "prompt") {
      return { view: "prompt", target: "prompt-notes", el: document.querySelector("#viewPrompt .notes-card") };
    }
    if (state.currentView === "viewer") {
      return { view: "viewer", target: "viewer", el: document.querySelector("#viewViewer .viewer-full") };
    }
    return null;
  }

  function isExternalFileDrag(e) {
    return !!(e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files"));
  }

  document.addEventListener("dragover", e => {
    if (!isExternalFileDrag(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
    const info = resolveExternalDropTarget(e.target);
    clearDropHighlights();
    if (info && info.el) {
      info.el.classList.add("drop-target-active");
      if (info.view === "prompt" && !state.promptDragScrolled) {
        state.promptDragScrolled = true;
        info.el.scrollIntoView({ behavior: "smooth", block: "center" });
      }
    }
  });

  document.addEventListener("dragleave", e => {
    if (e.relatedTarget === null) {
      clearDropHighlights();
      state.promptDragScrolled = false;
    }
  });

  document.addEventListener("drop", e => {
    if (!isExternalFileDrag(e)) return;
    e.preventDefault();
    const info = resolveExternalDropTarget(e.target);
    clearDropHighlights();
    state.promptDragScrolled = false;

    const files = Array.from(e.dataTransfer.files || []);
    if (files.length > 0 && window.chrome?.webview?.postMessageWithAdditionalObjects && info) {
      window.chrome.webview.postMessageWithAdditionalObjects(
        JSON.stringify({ type: "native-drop", view: info.view, target: info.target }),
        files
      );
    }
  });

  window.__onNativeDrop = async payload => {
    if (!payload) return;
    const items = Array.isArray(payload.items) ? payload.items : [];
    const view = payload.view || state.currentView;
    const target = payload.target || "";
    if (!items.length) return;

    if (view === "setup") {
      if (target === "setup-workspace" && items.length === 1 && items[0].kind === "folder") {
        document.getElementById("setupWsPath").value = items[0].path;
        await selectWorkspace(items[0].path);
        return;
      }

      let added = 0;
      items.forEach(it => {
        const name = extractFolderName(it.path) || it.path.split(/[\\/]/).pop();
        if (!state.stagedItems.some(s => s.sourcePath.toLowerCase() === it.path.toLowerCase())) {
          state.stagedItems.push({
            sourcePath: it.path,
            kind: it.kind === "folder" ? "folder" : "file",
            displayName: name,
            targetRelativePath: name
          });
          added++;
        }
      });
      renderStagedItems();
      await previewPlan();
      if (added > 0) toast(`Staged ${added} item(s).`, "ok");
      return;
    }

    if (view === "prompt") {
      const filesOnly = items.filter(i => i.kind === "file");
      const foldersCount = items.length - filesOnly.length;
      if (foldersCount > 0) {
        toast("Folders cannot be attached directly to Study Notes. Drop folders onto Setup or WebApp Viewer instead.", "warn");
      }
      let attached = 0;
      filesOnly.forEach(f => {
        if (!state.prompt.extraAttachments.some(a => a.filePath.toLowerCase() === f.path.toLowerCase())) {
          state.prompt.extraAttachments.push({
            filePath: f.path,
            kind: f.path.toLowerCase().endsWith(".pdf") ? "pdf" : "file"
          });
          attached++;
        }
      });
      if (attached > 0) {
        renderPromptAttachments();
        await refreshPromptPreview();
        toast(`Attached ${attached} file(s) to Study Notes.`, "ok");
      }
      return;
    }

    if (view === "viewer") {
      const folderItem = items.find(i => i.kind === "folder");
      if (folderItem) {
        await openWebAppPath(folderItem.path);
        return;
      }
      const indexFile = items.find(i => i.kind === "file" && /[\\/]index\.html$/i.test(i.path));
      if (indexFile) {
        const lastSep = Math.max(indexFile.path.lastIndexOf("\\"), indexFile.path.lastIndexOf("/"));
        await openWebAppPath(lastSep > 0 ? indexFile.path.slice(0, lastSep) : indexFile.path);
        return;
      }
      toast("Drop a compiled WebApp folder (or its index.html) to open it in WebApp Viewer.", "warn");
    }
  };

  window.__onWin32Drop = path => {
    if (!path) return;
    window.__onNativeDrop({ view: state.currentView, target: "", items: [{ path, kind: "folder" }] });
  };
}

function wireCrossViewEvents() {
  on("view:changed", ({ viewName }) => {
    if (viewName === "viewer") {
      refreshViewerDiscovery();
    } else if (viewName === "settings") {
      syncSettingsInputs();
    }
  });
  on("apps:refresh", render => refreshApps(!!render));
  on("viewer:theme-changed", applyViewerThemeOverride);
}

async function bootstrap() {
  const [res] = await Promise.all([api("GET", "/api/bootstrap"), loadIconRegistry()]);
  if (!res.ok) {
    toast("Failed to initialize backend.", "err");
    return;
  }

  const d = res.data;
  state.metadata = d.metadata || state.metadata;
  state.paperThemeFallbackCss = d.paperThemeFallbackCss || "";
  state.appsDirectory = d.appsDirectory || "";
  state.viewerOrigin = d.viewerOrigin || "";
  state.settings = d.settings || {};
  if (state.settings.overrideWebAppTheme === undefined) {
    state.settings.overrideWebAppTheme = true;
  }
  state.apps = d.apps || [];
  state.resolvedTools = d.resolvedTools || { mby: null, melomg: null };
  state.outdatedTools = d.outdatedTools || [];
  state.ecosystemRepos = d.ecosystemRepos || [];
  state.agentsMdStatus = d.agentsMdStatus || null;

  document.getElementById("versionBadge").textContent = `v${state.metadata.version}`;
  document.getElementById("settingsMetaLine").textContent = `${state.metadata.productName} v${state.metadata.version}`;

  applyTheme(state.settings.themeMode || "berry");
  syncOverrideThemeUi();
  loadWorkspaceDraftState();
  renderOutdatedBanner();
  await loadPresets();
  await loadSpecs(false);
  renderAppsCart();
  renderRecentWorkspaces();
  renderPromptAttachments();
  renderEcosystemRepos();
  syncSettingsInputs();
  startRealtimeEvents();

  const hasValidActiveWs = state.settings.activeWorkspace && state.settings.activeWorkspace.isInitialized;
  switchView(hasValidActiveWs ? "prompt" : "setup");
  updateChromeOffset();
  if (hasValidActiveWs) {
    refreshPromptPreview();
  }
}

wireGlobalOverlayDismissal(() => {
  if (!document.getElementById("confirmAgentsModal").classList.contains("hidden")) {
    closeConfirmModal();
  }
  if (!document.getElementById("inputPromptModal").classList.contains("hidden")) {
    closeInputPromptModal(null);
  }
});
wireModalControls();
wireChromeControls();
wireSetupView();
wirePromptStudioView();
wireViewerView();
wireSettingsView();
wireCrossViewEvents();
wireDragAndDrop();

window.addEventListener("resize", updateChromeOffset);
const chromeEl = document.getElementById("topChromeOverlay");
if (chromeEl && typeof ResizeObserver !== "undefined") {
  new ResizeObserver(updateChromeOffset).observe(chromeEl);
}

bootstrap();
