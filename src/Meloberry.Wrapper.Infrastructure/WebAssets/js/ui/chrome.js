import { api } from "../api.js";
import { VIEW_ORDER, WORKFLOW_STEPS, state, emit, escapeHtml, formatTruncatedWorkspace } from "../store.js";
import { registerDropdown, toggleDropdown, closeAllDropdowns } from "./dropdowns.js";
import { openInspectModal } from "./modal.js";

export function postWindowCommand(type) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ type }));
  }
}

window.__onWindowState = info => {
  const btn = document.getElementById("btnWinMaximize");
  if (btn && info) {
    btn.innerHTML = info.maximized ? "&#10064;" : "&#9723;";
    btn.title = info.maximized ? "Restore" : "Maximize";
  }
};

export function addNotification(title, detail, kind = "info") {
  state.notifications.unshift({
    id: `${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
    title,
    detail,
    kind,
    unread: true,
    time: new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  });
  if (state.notifications.length > 40) state.notifications.pop();
  renderNotifications();
}

export function renderNotifications() {
  const unreadCount = state.notifications.filter(n => n.unread).length;
  const badge = document.getElementById("notifBadge");
  badge.textContent = unreadCount > 99 ? "99+" : String(unreadCount);
  badge.classList.toggle("hidden", unreadCount === 0);

  const list = document.getElementById("notifList");
  list.innerHTML = "";
  if (!state.notifications.length) {
    list.innerHTML = '<div class="muted" style="padding:10px">No notifications yet.</div>';
    return;
  }

  state.notifications.forEach(n => {
    const item = document.createElement("div");
    item.className = `notif-item ${n.unread ? "unread" : ""}`;
    item.innerHTML = `<div class="notif-item-top"><span>${escapeHtml(n.title)}</span><span class="muted">${escapeHtml(n.time)}</span></div><div class="muted">${escapeHtml(n.detail)}</div>`;
    item.onclick = () => {
      n.unread = false;
      renderNotifications();
    };
    list.appendChild(item);
  });
}

export function toast(msg, kind = "info", opts = {}) {
  const box = document.getElementById("toastContainer");
  const persistent = !!(opts && opts.persistent);
  const dedupKey = opts && opts.key ? String(opts.key) : null;

  if (persistent) {
    const existing = Array.from(box.querySelectorAll(".toast.persistent"))
      .find(t => (dedupKey ? t.dataset.toastKey === dedupKey : t.dataset.toastMsg === msg));
    if (existing) return;
  }

  const el = document.createElement("div");
  el.className = `toast ${kind}${persistent ? " persistent" : ""}`;
  if (dedupKey) el.dataset.toastKey = dedupKey;
  el.dataset.toastMsg = msg;

  const text = document.createElement("span");
  text.textContent = msg;
  el.appendChild(text);

  if (persistent) {
    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "toast-close-btn";
    closeBtn.title = "Dismiss";
    closeBtn.setAttribute("aria-label", "Dismiss notification");
    closeBtn.textContent = "\u00d7";
    closeBtn.onclick = () => el.remove();
    el.appendChild(closeBtn);
  } else {
    setTimeout(() => el.remove(), 4200);
  }

  box.appendChild(el);
  if (kind === "err") {
    addNotification("Error", msg, "err");
  }
}

export function updateChromeOffset() {
  const viewport = document.querySelector(".views-viewport");
  const strip = document.getElementById("workspaceStrip");
  const outdatedBar = document.getElementById("outdatedToolsBar");
  if (!viewport || !strip) return;
  const stripH = strip.offsetHeight || 42;
  const outdatedH = outdatedBar && !outdatedBar.classList.contains("hidden") ? outdatedBar.offsetHeight : 0;
  viewport.style.setProperty("--chrome-offset", `${stripH + outdatedH}px`);
}

export function switchView(viewName) {
  closeAllDropdowns();
  const prev = state.currentView;
  state.currentView = viewName;
  const dirForward = (VIEW_ORDER[viewName] ?? 0) >= (VIEW_ORDER[prev] ?? 0);

  const map = {
    setup: "viewSetup",
    format: "viewFormat",
    organize: "viewOrganize",
    review: "viewReview",
    viewer: "viewViewer"
  };

  document.querySelectorAll(".view-panel").forEach(v => {
    v.classList.remove("active", "slide-in-right", "slide-in-left");
  });

  const nextEl = document.getElementById(map[viewName] || "viewSetup");
  nextEl.classList.add("active");
  if (prev !== viewName) {
    nextEl.classList.add(dirForward ? "slide-in-right" : "slide-in-left");
  }

  const activeStepIdx = WORKFLOW_STEPS.indexOf(viewName);
  document.querySelectorAll(".workflow-stepper .step-node").forEach(btn => {
    const idx = WORKFLOW_STEPS.indexOf(btn.dataset.view);
    const isActive = idx >= 0 && idx === activeStepIdx;
    const isCompleted = activeStepIdx >= 0 && idx >= 0 && idx < activeStepIdx;
    btn.classList.toggle("active", isActive);
    btn.classList.toggle("completed", isCompleted);
    if (isActive) btn.setAttribute("aria-current", "step");
    else btn.removeAttribute("aria-current");
  });
  document.querySelectorAll(".workflow-stepper .step-connector").forEach(conn => {
    const cIdx = Number(conn.dataset.stepConnector);
    conn.classList.toggle("reached", activeStepIdx > cIdx);
  });

  document.getElementById("topChromeOverlay").classList.toggle("pulled-up", viewName === "viewer");
  updateChromeOffset();

  emit("view:changed", { viewName });
}

export function applyTheme(theme) {
  document.documentElement.setAttribute("data-theme", theme || "berry");
}

export function renderAgentsPill(status, animateOnReveal = false) {
  const wrap = document.getElementById("agentsPillWrap");
  const pill = document.getElementById("agentsReadyPill");
  const metrics = document.getElementById("agentsMenuMetrics");
  const exists = !!(status && status.exists);

  const wasHidden = wrap.classList.contains("hidden");
  wrap.classList.toggle("hidden", !exists);
  if (!exists) {
    document.getElementById("agentsPillMenu")?.classList.add("hidden");
    return;
  }

  if (animateOnReveal || wasHidden) {
    pill.classList.remove("pill-animate");
    void pill.offsetWidth;
    pill.classList.add("pill-animate");
  }

  const bytes = status.sizeBytes ?? 0;
  const chars = status.characterCount ?? (status.preview ? status.preview.length : 0);
  const toks = status.approximateTokens ?? Math.max(1, Math.round(chars / 4));
  metrics.textContent = `${bytes} B \u00b7 ${chars} chars \u00b7 ~${toks} tok`;
}

export function pruneSelectedAppNames() {
  const installedSet = new Set(state.apps.filter(a => a.isInstalled).map(a => a.fileName));
  for (const name of Array.from(state.selectedAppNames)) {
    if (!installedSet.has(name)) {
      state.selectedAppNames.delete(name);
    }
  }
}

export function syncWorkspaceChrome() {
  const aws = state.settings.activeWorkspace;
  const dot = document.getElementById("wsStatusDot");
  const label = document.getElementById("activeWsLabel");
  const btn = document.getElementById("activeWsBtn");

  if (aws && aws.workspacePath) {
    label.textContent = formatTruncatedWorkspace(aws.workspacePath);
    if (btn) btn.title = `${aws.workspacePath} (click to browse directory tree)`;
    dot.classList.toggle("ready", !!aws.isInitialized);

    const agentsExists = !!(state.agentsMdStatus && state.agentsMdStatus.exists);
    state.prompt.agentsMdExported = agentsExists;
    renderAgentsPill(state.agentsMdStatus, false);
  } else {
    label.textContent = "No Active Workspace";
    if (btn) btn.title = "Select or browse an Active Workspace";
    dot.classList.remove("ready");
    state.prompt.agentsMdExported = false;
    renderAgentsPill(null, false);
  }

  pruneSelectedAppNames();
  emit("prompt:availability-changed");
}

export function renderOutdatedBanner() {
  const bar = document.getElementById("outdatedToolsBar");
  const text = document.getElementById("outdatedToolsText");
  if (!state.outdatedTools || state.outdatedTools.length === 0) {
    bar.classList.add("hidden");
    updateChromeOffset();
    return;
  }

  const summary = state.outdatedTools
    .map(t => `${t.workspaceRelativePath} (v${t.workspaceVersion} -> apps\\ v${t.latestAppVersion})`)
    .join(" | ");
  text.innerHTML = `<strong>Older workspace CLI detected:</strong> ${escapeHtml(summary)}`;
  bar.classList.remove("hidden");
  updateChromeOffset();
}

export function showLiveAgentActivity(message, level = "info") {
  if (!message) return;
  const pill = document.getElementById("liveAgentActivityPill");
  const textEl = document.getElementById("liveAgentActivityText");
  if (pill && textEl) {
    textEl.textContent = message;
    pill.classList.remove("hidden", "idle");
    clearTimeout(state.live.activityTimer);
    state.live.activityTimer = setTimeout(() => {
      pill.classList.add("idle");
    }, 10000);
  }

  addNotification("Live Agent", message, level);
}

export function wireChromeControls() {
  const titlebar = document.getElementById("titlebar");
  titlebar.addEventListener("mousedown", e => {
    if (e.button !== 0 || e.target.closest("button, input, select, a, .workflow-stepper, .notif-wrap")) return;
    postWindowCommand("window.drag");
  });
  titlebar.addEventListener("dblclick", e => {
    if (e.target.closest("button, input, select, a, .workflow-stepper, .notif-wrap")) return;
    postWindowCommand("window.toggleMaximize");
  });
  document.getElementById("btnWinMinimize").onclick = () => postWindowCommand("window.minimize");
  document.getElementById("btnWinMaximize").onclick = () => postWindowCommand("window.toggleMaximize");
  document.getElementById("btnWinClose").onclick = () => postWindowCommand("window.close");

  document.querySelectorAll(".workflow-stepper .step-node").forEach(btn => {
    btn.onclick = () => switchView(btn.dataset.view);
  });

  registerDropdown("btnNotifications", "notifPopover", { wrapSelector: ".notif-wrap" });
  document.getElementById("liveAgentActivityPill")?.addEventListener("click", e => {
    e.stopPropagation();
    toggleDropdown("notifPopover");
  });
  document.getElementById("btnNotifReadAll").onclick = () => {
    state.notifications.forEach(n => { n.unread = false; });
    renderNotifications();
  };
  document.getElementById("btnNotifClear").onclick = () => {
    state.notifications = [];
    renderNotifications();
  };

  registerDropdown("agentsReadyPill", "agentsPillMenu", { wrapSelector: "#agentsPillWrap" });
  document.getElementById("btnMenuInspectAgents").onclick = () => {
    closeAllDropdowns();
    openInspectModal({
      title: "AGENTS.md",
      body: state.agentsMdStatus?.agentsMdPath || "AGENTS.md",
      preText: state.agentsMdStatus?.preview || "(Click Ready Workspace to inspect freshly rendered content.)"
    });
  };
  document.getElementById("btnMenuOpenAgentsFolder").onclick = () => {
    closeAllDropdowns();
    const fullAgentsPath = state.agentsMdStatus?.agentsMdPath || "";
    const lastSep = Math.max(fullAgentsPath.lastIndexOf("\\"), fullAgentsPath.lastIndexOf("/"));
    const folder = lastSep > 0 ? fullAgentsPath.slice(0, lastSep) : (state.settings.activeWorkspace?.workspacePath || "");
    api("POST", "/api/workspace/open-folder", { path: folder });
  };
}
