import { api, openEventStream } from "./api.js";
import { state, on, VIEW_ORDER } from "./store.js";
import { loadIconRegistry } from "./icons.js";
import { wireGlobalOverlayDismissal } from "./ui/dropdowns.js";
import { closeConfirmModal, closeInputPromptModal, wireModalControls } from "./ui/modal.js";
import {
  addNotification,
  applyTheme,
  recordAgentActivity,
  renderOutdatedBanner,
  switchView,
  syncWorkspaceChrome,
  toast,
  updateChromeOffset,
  wireChromeControls
} from "./ui/chrome.js";
import { wireWorkspaceTree } from "./ui/workspaceTree.js";
import { wireDragAndDrop } from "./ui/nativeDrop.js";
import {
  loadWorkspaceDraftState,
  refreshApps,
  renderAppsCart,
  renderRecentWorkspaces,
  selectWorkspace,
  wireSetupView
} from "./views/setup.js";
import {
  applyAnimatedAgentDraft,
  loadPresets,
  loadSpecs,
  loadWorkspaceDraft,
  refreshPromptOptionsAvailability,
  refreshPromptPreview,
  wirePromptDraftEvents
} from "./views/prompt/promptDraft.js";
import { renderPromptAttachments, wireFormatView } from "./views/prompt/format.js";
import { highlightAgentTargets, wireOrganizeView } from "./views/prompt/organize.js";
import {
  closeSpecContentModal,
  initSpecContentModal,
  isSpecContentModalOpen,
  openSpecContentModal
} from "./views/prompt/specContentModal.js";
import { wireReviewView } from "./views/prompt/review.js";
import {
  applyViewerThemeOverride,
  openMountedWebAppFromSignal,
  refreshViewerDiscovery,
  syncOverrideThemeUi,
  wireViewerView
} from "./views/viewer.js";
import {
  closeSettingsModal,
  isSettingsModalOpen,
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
  es.addEventListener("webapp-built", ev => {
    let payload;
    try { payload = JSON.parse(ev.data); } catch { return; }
    if (!payload || !payload.url) return;
    openMountedWebAppFromSignal(payload);
    if (payload.source !== "ui") {
      const title = payload.title || payload.entry?.name || payload.workspaceId || "Study WebApp";
      addNotification("WebApp Auto-Loaded", `${title} was built and opened in Viewer.`, "info");
    }
  });
  es.addEventListener("draft-updated", ev => {
    let payload;
    try { payload = JSON.parse(ev.data); } catch { return; }
    if (!payload || payload.source !== "agent" || !payload.draft) return;

    const activeWs = (state.settings.activeWorkspace?.workspacePath || "").replace(/[\\/]+$/, "").toLowerCase();
    const evtWs = (payload.workspacePath || "").replace(/[\\/]+$/, "").toLowerCase();
    if (activeWs && evtWs && activeWs !== evtWs) return;

    const nextDraft = payload.workflowModeChanged
      ? payload.draft
      : { ...payload.draft, workflowMode: state.prompt.workflowMode || "direct" };

    applyAnimatedAgentDraft(nextDraft, payload);
    const draftActId = `draft_${++state.live.activitySeq}`;
    recordAgentActivity({
      id: draftActId,
      state: "done",
      message: payload.summary || "Updated blueprint"
    });

    if (payload.op === "patch-meta" && !payload.targetCompId) {
      if (state.currentView !== "format") switchView("format");
    } else if (state.currentView !== "organize") {
      switchView("organize");
    }

    requestAnimationFrame(() => highlightAgentTargets(payload.targetCompId, payload.targetSpecId));
    refreshPromptPreview({ skipSave: true });
  });
  es.addEventListener("view-requested", ev => {
    let payload;
    try { payload = JSON.parse(ev.data); } catch { return; }
    const aliasMap = { details: "format", project: "format", finalize: "review" };
    const rawView = (payload?.view || "").toLowerCase();
    const resolvedView = aliasMap[rawView] || rawView;
    if (resolvedView && VIEW_ORDER[resolvedView] !== undefined) {
      switchView(resolvedView);
    }
  });
  es.addEventListener("agent-activity", ev => {
    let payload;
    try { payload = JSON.parse(ev.data); } catch { return; }
    if (payload?.message) {
      recordAgentActivity({
        id: payload.id,
        state: payload.state || (payload.level === "err" ? "error" : "done"),
        message: payload.message,
        detail: payload.detail || "",
        exitCode: payload.exitCode ?? null,
        durationMs: payload.durationMs ?? null
      });
    }
  });
  state.eventSource = es;
}

function wireCrossViewEvents() {
  on("view:changed", ({ viewName }) => {
    if (viewName === "viewer") {
      refreshViewerDiscovery();
    }
  });
  on("apps:refresh", render => refreshApps(!!render));
  on("viewer:theme-changed", applyViewerThemeOverride);
  on("spec:open-content-modal", opts => {
    if (opts?.compId && opts?.instId) {
      openSpecContentModal(opts.compId, opts.instId, { focusMelomg: !!opts.focusMelomg });
    }
  });
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
  if (state.settings.activeWorkspace?.workspacePath) {
    await loadWorkspaceDraft(state.settings.activeWorkspace.workspacePath);
  }
  renderAppsCart();
  renderRecentWorkspaces();
  renderPromptAttachments();
  renderEcosystemRepos();
  syncSettingsInputs();
  startRealtimeEvents();

  const hasValidActiveWs = Boolean(state.settings.activeWorkspace?.workspacePath);
  switchView(hasValidActiveWs ? "format" : "setup");
  updateChromeOffset();
  if (hasValidActiveWs) {
    await refreshPromptPreview({ skipSave: true });
  }
}

wireGlobalOverlayDismissal(() => {
  if (!document.getElementById("confirmAgentsModal").classList.contains("hidden")) {
    closeConfirmModal();
  }
  if (!document.getElementById("inputPromptModal").classList.contains("hidden")) {
    closeInputPromptModal(null);
  }
  if (isSpecContentModalOpen()) {
    closeSpecContentModal();
  }
  if (isSettingsModalOpen()) {
    closeSettingsModal();
  }
});
wireModalControls();
wireChromeControls();
wireWorkspaceTree({ selectWorkspace });
wireSetupView();
wirePromptDraftEvents();
wireFormatView();
wireOrganizeView();
initSpecContentModal();
wireReviewView();
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
