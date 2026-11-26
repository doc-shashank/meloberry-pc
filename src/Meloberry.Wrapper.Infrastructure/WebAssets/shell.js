(() => {
  const token = window.__MBY_TOKEN__ || "";
  try { delete window.__MBY_TOKEN__; } catch { window.__MBY_TOKEN__ = ""; }

  const VIEW_ORDER = { setup: 0, prompt: 1, viewer: 2, settings: 3 };

  const state = {
    metadata: { version: "1.0.0", productName: "Meloberry PC", selfUpdateRepoOwner: "doc-shashank", selfUpdateRepoName: "meloberry-pc" },
    icons: {},
    paperThemeFallbackCss: "",
    appsDirectory: "",
    viewerOrigin: "",
    settings: { themeMode: "berry", overrideWebAppTheme: true, recentWebApps: [] },
    apps: [],
    resolvedTools: { mby: null, melomg: null },
    appUpdateChecks: {},
    outdatedTools: [],
    resetScope: "executablesOnly",
    conflictPolicy: "skipExisting",
    currentView: "setup",
    lastNonSettingsView: "setup",
    selectedAppNames: new Set(),
    stagedItems: [],
    specs: [],
    isLiveSpecs: false,
    activeMbyPath: "",
    presets: [],
    activePresetName: "Full Chapter Study Session",
    ecosystemRepos: [],
    agentsMdStatus: null,
    discoveredWebApps: [],
    discoveredMbyProjects: [],
    notifications: [],
    eventSource: null,
    previewSeq: 0,
    previewDebounceTimer: null,
    prompt: {
      container: "session",
      title: "",
      description: "",
      authoringFormat: "markdownDirectory",
      sourceText: "",
      extraAttachments: [],
      useMelomgForPdfs: true,
      agentsMdExported: false,
      components: [],
      selectedComponentId: null,
      containerCache: { session: null, standaloneArticle: null, standaloneQuiz: null },
      specCardMode: "selector",
      organizerChosenTopic: null,
      organizerChosenSubtype: ""
    },
    composedPrompt: { markdown: "", plainText: "", characterCount: 0, approximateTokens: 0 },
    activeConfirmAction: null,
    activeInputResolve: null,
    activeNoteTarget: null,
    activeDistTarget: null,
    hoverTimer: null,
    dragOrgIndex: null,
    dragSpecOrgIndex: null,
    lastAddedOrgId: null,
    lastAddedSpecOrgId: null,
    lastAddedSelectorTopic: null,
    promptDragScrolled: false,
    viewerLoadToken: 0,
    viewerLoadFadeTimer: null,
    viewerHideDelayTimer: null
  };

  function getIcon(key) {
    if (!key) return "";
    const norm = String(key).trim().replace(/^\/+|\/+$/g, "");
    if (state.icons[norm]) return state.icons[norm];
    const lower = norm.toLowerCase();
    const matchKey = Object.keys(state.icons).find(k => k.toLowerCase() === lower);
    return matchKey ? state.icons[matchKey] : "";
  }

  function getSpecIcon(topic, subtype) {
    const t = String(topic || "text").trim().toLowerCase();
    if (subtype) {
      const sub = String(subtype).trim();
      const hit = getIcon(`specs/${t}-${sub}`);
      if (hit) return hit;
    }
    return getIcon(`specs/${t}`) || getIcon("specs/text") || "";
  }

  function hydrateStaticIcons() {
    document.querySelectorAll("[data-icon]").forEach(el => {
      const svg = getIcon(el.getAttribute("data-icon"));
      if (svg) el.innerHTML = svg;
    });
  }

  async function loadIconRegistry() {
    try {
      const resp = await fetch("/shell/icons.json");
      if (resp.ok) {
        const data = await resp.json();
        if (data && typeof data === "object") {
          state.icons = data;
          hydrateStaticIcons();
        }
      }
    } catch {
      // Fallback gracefully if icons endpoint is unreachable.
    }
  }

  function postWindowCommand(type) {
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

  async function api(method, url, body) {
    const opts = {
      method,
      headers: {
        "X-Mby-Token": token,
        "Content-Type": "application/json"
      }
    };
    if (body !== undefined) {
      opts.body = JSON.stringify(body);
    }
    const resp = await fetch(url, opts);
    const data = await resp.json().catch(() => ({}));
    return { ok: resp.ok, status: resp.status, data };
  }

  function addNotification(title, detail, kind = "info") {
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

  function renderNotifications() {
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

  const overlayRegistry = [];

  function registerPopover(menuId, opts = {}) {
    const entry = {
      menuId,
      triggerId: opts.triggerId || null,
      wrapSelector: opts.wrapSelector || `#${menuId}`,
      beforeOpen: opts.beforeOpen || null,
      onClose: opts.onClose || null
    };
    overlayRegistry.push(entry);
    return entry;
  }

  function closeAllDropdowns(exceptIds = []) {
    const keep = new Set(exceptIds);
    const toNotify = [];
    overlayRegistry.forEach(entry => {
      if (keep.has(entry.menuId)) return;
      const el = document.getElementById(entry.menuId);
      if (el && !el.classList.contains("hidden")) {
        el.classList.add("hidden");
        if (entry.onClose) toNotify.push(entry.onClose);
      }
    });
    toNotify.forEach(fn => fn());
  }

  function openDropdown(menuId) {
    const entry = overlayRegistry.find(e => e.menuId === menuId);
    const el = document.getElementById(menuId);
    if (!el) return;
    closeAllDropdowns([menuId]);
    if (entry?.beforeOpen) entry.beforeOpen();
    el.classList.remove("hidden");
  }

  function toggleDropdown(menuId) {
    const el = document.getElementById(menuId);
    if (!el) return;
    if (el.classList.contains("hidden")) {
      openDropdown(menuId);
    } else {
      closeAllDropdowns();
    }
  }

  function registerDropdown(triggerId, menuId, opts = {}) {
    const entry = registerPopover(menuId, { ...opts, triggerId });
    if (opts.autoWireClick !== false) {
      const trigger = document.getElementById(triggerId);
      if (trigger) {
        trigger.onclick = e => {
          e.stopPropagation();
          toggleDropdown(menuId);
        };
      }
    }
    return entry;
  }

  function toast(msg, kind = "info", opts = {}) {
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

  function withButtonBusy(btn, busyLabel, action) {
    return async (...args) => {
      if (btn && btn.disabled) return;
      const origHtml = btn ? btn.innerHTML : "";
      if (btn) {
        btn.disabled = true;
        if (busyLabel) btn.textContent = busyLabel;
      }
      try {
        return await action(...args);
      } finally {
        if (btn) {
          btn.disabled = false;
          if (busyLabel) btn.innerHTML = origHtml;
        }
      }
    };
  }

  function escapeHtml(str) {
    return String(str || "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));
  }

  function switchView(viewName) {
    closeAllDropdowns();
    const prev = state.currentView;
    if (prev !== "settings") {
      state.lastNonSettingsView = prev;
    }
    state.currentView = viewName;
    if (viewName !== "settings") {
      state.lastNonSettingsView = viewName;
    }
    const dirForward = (VIEW_ORDER[viewName] ?? 0) >= (VIEW_ORDER[prev] ?? 0);

    const map = { setup: "viewSetup", prompt: "viewPrompt", viewer: "viewViewer", settings: "viewSettings" };
    const tabMap = { setup: "tabSetup", prompt: "tabPrompt", viewer: "tabViewer" };

    document.querySelectorAll(".view-panel").forEach(v => {
      v.classList.remove("active", "slide-in-right", "slide-in-left");
    });
    document.querySelectorAll(".nav-tab").forEach(b => b.classList.remove("active"));

    const nextEl = document.getElementById(map[viewName] || "viewSetup");
    nextEl.classList.add("active");
    if (prev !== viewName) {
      nextEl.classList.add(dirForward ? "slide-in-right" : "slide-in-left");
    }

    const tab = document.getElementById(tabMap[viewName]);
    if (tab) tab.classList.add("active");

    document.getElementById("btnOpenSettings")?.classList.toggle("active", viewName === "settings");

    // Pull-up Top Chrome Overlay on WebApp Viewer and Settings without reflowing .views-viewport
    document.getElementById("topChromeOverlay").classList.toggle("pulled-up", viewName === "viewer" || viewName === "settings");
    updateChromeOffset();

    if (viewName === "viewer") {
      refreshViewerDiscovery();
    } else if (viewName === "settings") {
      syncSettingsInputs();
    }
  }

  function updateChromeOffset() {
    const viewport = document.querySelector(".views-viewport");
    const strip = document.getElementById("workspaceStrip");
    const outdatedBar = document.getElementById("outdatedToolsBar");
    if (!viewport || !strip) return;
    const stripH = strip.offsetHeight || 42;
    const outdatedH = outdatedBar && !outdatedBar.classList.contains("hidden") ? outdatedBar.offsetHeight : 0;
    viewport.style.setProperty("--chrome-offset", `${stripH + outdatedH}px`);
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme || "berry");
  }

  function syncOverrideThemeUi() {
    const isOn = state.settings.overrideWebAppTheme !== false;
    const sw = document.getElementById("overrideThemeSwitch");
    const btn = document.getElementById("menuToggleOverrideTheme");
    if (sw) sw.classList.toggle("on", isOn);
    if (btn) btn.setAttribute("aria-checked", isOn ? "true" : "false");
  }

  function applyViewerThemeOverride() {
    const frame = document.getElementById("webAppFrame");
    if (!frame || frame.classList.contains("hidden") || !frame.contentWindow || !state.viewerOrigin) return;
    const isOverrideOn = state.settings.overrideWebAppTheme !== false;
    const targetTheme = state.settings.themeMode || "berry";
    frame.contentWindow.postMessage(
      {
        type: "mby-theme-override",
        override: isOverrideOn,
        theme: targetTheme,
        fallbackCss: state.paperThemeFallbackCss
      },
      state.viewerOrigin
    );
  }

  function startRealtimeEvents() {
    if (state.eventSource) {
      state.eventSource.close();
    }

    const es = new EventSource(`/api/events?token=${encodeURIComponent(token)}`);
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

  function formatTruncatedWorkspace(fullPath) {
    if (!fullPath) return "No Active Workspace";
    const parts = fullPath.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
    if (parts.length <= 2) return `Active Workspace : ${fullPath}`;
    return `Active Workspace : ...\\${parts[parts.length - 2]}\\${parts[parts.length - 1]}`;
  }

  function extractFolderName(fullPath) {
    if (!fullPath) return "";
    const trimmed = fullPath.replace(/[\\/]+$/, "");
    const parts = trimmed.split(/[\\/]/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : trimmed;
  }

  function pruneSelectedAppNames() {
    const installedSet = new Set(state.apps.filter(a => a.isInstalled).map(a => a.fileName));
    for (const name of Array.from(state.selectedAppNames)) {
      if (!installedSet.has(name)) {
        state.selectedAppNames.delete(name);
      }
    }
  }

  function selectUpgradedApp(appEntry) {
    if (!appEntry || !appEntry.fileName) return;
    if (appEntry.kind === "mby" || appEntry.kind === "melomg") {
      const isMby = appEntry.kind === "mby";
      for (const name of Array.from(state.selectedAppNames)) {
        const lower = name.toLowerCase();
        const matchesKind = isMby
          ? (lower === "mby.exe" || lower.startsWith("mby-cli"))
          : (lower === "melomg.exe" || lower.startsWith("meloberry-melomg"));
        if (matchesKind) {
          state.selectedAppNames.delete(name);
        }
      }
    }
    state.selectedAppNames.add(appEntry.fileName);
  }

  function renderAgentsPill(status, animateOnReveal = false) {
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

  function syncWorkspaceChrome() {
    const aws = state.settings.activeWorkspace;
    const dot = document.getElementById("wsStatusDot");
    const label = document.getElementById("activeWsLabel");

    if (aws && aws.workspacePath) {
      label.textContent = formatTruncatedWorkspace(aws.workspacePath);
      label.title = aws.workspacePath;
      dot.classList.toggle("ready", !!aws.isInitialized);

      const agentsExists = !!(state.agentsMdStatus && state.agentsMdStatus.exists);
      state.prompt.agentsMdExported = agentsExists;
      renderAgentsPill(state.agentsMdStatus, false);
    } else {
      label.textContent = "No Active Workspace";
      label.title = "";
      dot.classList.remove("ready");
      state.prompt.agentsMdExported = false;
      renderAgentsPill(null, false);
    }

    pruneSelectedAppNames();
    refreshPromptOptionsAvailability();
  }

  function loadWorkspaceDraftState() {
    const aws = state.settings.activeWorkspace;
    if (aws && aws.workspacePath) {
      document.getElementById("setupWsPath").value = aws.workspacePath;
      state.selectedAppNames = new Set(aws.selectedAppFileNames || []);
      state.stagedItems = [...(aws.stagedItems || [])];
    } else {
      state.selectedAppNames.clear();
      state.apps.filter(a => a.isInstalled && (a.kind === "mby" || a.kind === "melomg"))
        .forEach(a => state.selectedAppNames.add(a.fileName));
    }

    syncWorkspaceChrome();
    renderAppsCart();
    renderStagedItems();
  }

  function renderOutdatedBanner() {
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

  function renderRecentWorkspaces() {
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

  async function selectWorkspace(path) {
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
        addNotification("Outdated Workspace Tools", `${state.outdatedTools.length} executable(s) can be updated from apps\\.`, "warn");
      }

      const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(wsState.workspacePath)}`);
      state.agentsMdStatus = st.ok ? st.data : null;
      await refreshApps(false);
      loadWorkspaceDraftState();
      renderRecentWorkspaces();
      renderOutdatedBanner();
      await previewPlan();
      await refreshPromptPreview();
    } else {
      toast(res.data?.error?.description || "Invalid workspace path.", "err");
    }
  }

  async function refreshApps(render = true) {
    const res = await api("GET", "/api/apps");
    if (res.ok) {
      state.apps = res.data.apps || [];
      if (res.data.resolvedTools) {
        state.resolvedTools = res.data.resolvedTools;
      }
      pruneSelectedAppNames();
      if (render) {
        renderAppsCart();
        refreshPromptOptionsAvailability();
      }
    }
  }

  function renderAppsCart() {
    const container = document.getElementById("appsCartList");
    container.innerHTML = "";

    state.apps.forEach(app => {
      const card = document.createElement("div");
      const checked = state.selectedAppNames.has(app.fileName);
      card.className = `app-card ${checked ? "selected" : ""}`;

      const top = document.createElement("div");
      top.className = "app-card-top";

      const lbl = document.createElement("label");
      lbl.className = "check-label";
      const cb = document.createElement("input");
      cb.type = "checkbox";
      cb.checked = checked;
      cb.disabled = !app.isInstalled;
      cb.onchange = async () => {
        if (cb.checked) state.selectedAppNames.add(app.fileName);
        else state.selectedAppNames.delete(app.fileName);
        card.classList.toggle("selected", cb.checked);
        await previewPlan();
      };

      const titleSpan = document.createElement("span");
      if (app.kind === "mby") titleSpan.innerHTML = "Meloberry <code>cli</code>";
      else if (app.kind === "melomg") titleSpan.innerHTML = "Meloberry <code>melomg</code>";
      else titleSpan.textContent = app.displayName;
      lbl.append(cb, titleSpan);

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
      desc.textContent = app.isInstalled ? (app.description || app.fileName) : "Not installed. Download from GitHub.";
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
  }

  function confirmUninstallApp(app) {
    document.getElementById("confirmModalTitle").textContent = "Uninstall App";
    document.getElementById("confirmModalBody").textContent =
      `Remove "${app.fileName}" from Meloberry apps\\ store? Any copies inside workspaces remain untouched.`;
    document.getElementById("agentsPreviewModalCode").classList.add("hidden");
    const proceed = document.getElementById("btnConfirmModalProceed");
    proceed.textContent = "Uninstall";
    proceed.classList.remove("hidden");

    state.activeConfirmAction = async () => {
      closeConfirmModal();
      const res = await api("POST", "/api/apps/uninstall", { idOrFileName: app.id });
      if (!res.ok) {
        toast(res.data?.error?.description || "Uninstall failed.", "err");
        return;
      }

      state.selectedAppNames.delete(app.fileName);
      if (app.repoKey) delete state.appUpdateChecks[app.repoKey];
      await refreshApps();
      await loadSpecs(true);
      toast(`Uninstalled ${res.data}`, "ok");
    };

    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  }

  async function downloadToolFromGitHub(toolKey, btn, progWrap, progBar, statusLine) {
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
        if (toolKey === "mby") await loadSpecs(true);
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

  function renderStagedItems() {
    const list = document.getElementById("stagedItemsList");
    list.innerHTML = "";
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

  function showPathTooltip(targetEl, fullPath) {
    const tip = document.getElementById("hoverPathTooltip");
    tip.textContent = fullPath;
    tip.classList.remove("hidden");
    const rect = targetEl.getBoundingClientRect();
    const tipRect = tip.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - tipRect.width - 8, rect.left));
    const preferredTop = rect.top - tipRect.height - 8;
    const top = preferredTop >= 8
      ? preferredTop
      : Math.min(window.innerHeight - tipRect.height - 8, rect.bottom + 8);
    tip.style.left = `${left}px`;
    tip.style.top = `${Math.max(8, top)}px`;
  }

  function hidePathTooltip() {
    clearTimeout(state.hoverTimer);
    document.getElementById("hoverPathTooltip").classList.add("hidden");
  }

  async function previewPlan() {
    const wsPath = document.getElementById("setupWsPath").value.trim();
    const box = document.getElementById("planConflictsBox");
    const summary = document.getElementById("planSummaryText");
    if (!wsPath) {
      summary.textContent = "";
      box.classList.add("hidden");
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

  async function initializeWorkspace() {
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
      toast(res.data?.error?.description || "Initialization failed.", "err");
      return;
    }

    state.settings.activeWorkspace = res.data.activeWorkspace;
    if (Array.isArray(res.data.recentWorkspaces)) {
      state.settings.recentWorkspaces = res.data.recentWorkspaces;
    }
    const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(wsPath)}`);
    state.agentsMdStatus = st.ok ? st.data : null;

    const outRes = await api("GET", `/api/workspace/outdated?workspacePath=${encodeURIComponent(wsPath)}`);
    state.outdatedTools = outRes.ok ? (outRes.data.outdatedTools || []) : [];

    await refreshApps(false);
    await loadSpecs(true);
    loadWorkspaceDraftState();
    renderOutdatedBanner();
    renderRecentWorkspaces();
    renderPromptAttachments();
    await refreshPromptPreview();
    switchView("prompt");

    const ir = res.data.initResult;
    toast(`Initialized (${ir.copiedApps.length} tools, ${ir.copiedFiles.length} files, ${ir.copiedFolders.length} folders).`, "ok");
  }

  async function triggerResetPrompt() {
    const aws = state.settings.activeWorkspace;
    if (!aws || !aws.workspacePath) {
      toast("No Active Workspace selected.", "err");
      return;
    }

    const res = await api("POST", "/api/workspace/reset/plan", {
      workspacePath: aws.workspacePath,
      scope: state.resetScope
    });
    if (!res.ok) {
      toast(res.data?.error?.description || "Failed to scan workspace for Reset.", "err");
      return;
    }

    const plan = res.data;
    if (plan.totalCount === 0) {
      if (state.resetScope === "undoReadyWorkspace") {
        toast("No Ready Workspace artifacts (AGENTS.md) found to undo.", "info");
      } else {
        toast("No Meloberry executables" + (state.resetScope === "executablesAndAgentsMd" ? " or AGENTS.md" : "") + " found.", "info");
      }
      return;
    }

    const seenItems = new Set();
    const items = [];
    [...(plan.executablesToRemove || []), ...(plan.readyArtifactsToRemove || []), ...(plan.agentsMdToRemove ? [plan.agentsMdToRemove] : [])]
      .forEach(rel => {
        if (rel && !seenItems.has(rel.toLowerCase())) {
          seenItems.add(rel.toLowerCase());
          items.push(rel);
        }
      });

    const isUndo = state.resetScope === "undoReadyWorkspace";
    document.getElementById("confirmModalTitle").textContent = isUndo ? "Undo Ready Workspace?" : "Confirm Workspace Reset";
    document.getElementById("confirmModalBody").textContent = isUndo
      ? `Remove ${items.length} Ready Workspace file(s) from "${plan.workspacePath}"? Executables will remain untouched.`
      : `Remove ${items.length} file(s) from "${plan.workspacePath}" (recursively across subfolders)?`;
    const pre = document.getElementById("agentsPreviewModalCode");
    pre.textContent = items.join("\n");
    pre.classList.remove("hidden");

    const proceed = document.getElementById("btnConfirmModalProceed");
    proceed.textContent = isUndo ? "Remove Ready Artifacts" : "Delete Listed Files";
    proceed.classList.remove("hidden");

    state.activeConfirmAction = async () => {
      closeConfirmModal();
      const execRes = await api("POST", "/api/workspace/reset/execute", {
        workspacePath: plan.workspacePath,
        scope: state.resetScope
      });
      if (!execRes.ok) {
        toast(execRes.data?.error?.description || "Reset failed.", "err");
        return;
      }

      if (execRes.data.activeWorkspace) state.settings.activeWorkspace = execRes.data.activeWorkspace;
      if (execRes.data.resetResult.removedAgentsMd || state.resetScope === "undoReadyWorkspace") {
        const st = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(plan.workspacePath)}`);
        state.agentsMdStatus = st.ok ? st.data : null;
        state.prompt.agentsMdExported = !!(state.agentsMdStatus && state.agentsMdStatus.exists);
      }

      const outRes = await api("GET", `/api/workspace/outdated?workspacePath=${encodeURIComponent(plan.workspacePath)}`);
      state.outdatedTools = outRes.ok ? (outRes.data.outdatedTools || []) : [];

      await refreshApps(false);
      syncWorkspaceChrome();
      renderAppsCart();
      renderOutdatedBanner();
      await refreshPromptPreview();

      if (isUndo) {
        const count = (execRes.data.resetResult.removedReadyArtifacts || execRes.data.resetResult.removedRelativePaths || []).length;
        toast(`Reverted Ready Workspace (removed ${count} artifact(s)).`, "ok");
      } else {
        toast(`Reset removed ${execRes.data.resetResult.removedRelativePaths.length} executable(s)${execRes.data.resetResult.removedAgentsMd ? " + AGENTS.md" : ""}.`, "ok");
      }
    };

    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  }

  // ---- Prompt Studio: Presets, Organization, SVG Spec Dropdowns ----

  async function loadSpecs(forceRefresh) {
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

  async function loadPresets() {
    const res = await api("GET", "/api/presets");
    if (res.ok) {
      state.presets = res.data.presets || [];
      renderPresetMenu();
    }
  }

  function renderPresetMenu() {
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

  function markPresetModified() {
    if (!state.activePresetName) return;
    state.activePresetName = "";
    const lbl = document.getElementById("presetBtnLabel");
    if (lbl) lbl.textContent = "Preset";
    document.querySelectorAll("#presetDropdownMenu .dropdown-item").forEach(b => b.classList.remove("active"));
  }

  function cloneComponent(c) {
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

  function ensureComponentsForContainer() {
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

  function getActiveComponent() {
    ensureComponentsForContainer();
    return state.prompt.components.find(c => c.id === state.prompt.selectedComponentId) || state.prompt.components[0];
  }

  function applyPresetObj(preset) {
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

  function getComponentBadges() {
    let a = 0;
    let q = 0;
    const map = {};
    state.prompt.components.forEach(c => {
      map[c.id] = c.kind === "article" ? `A${++a}` : `Q${++q}`;
    });
    return map;
  }

  function renderOrganizationCard() {
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

  function renderPromptStudioSpecArea() {
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

  function findSpecMeta(topic) {
    const t = String(topic || "").toLowerCase();
    return state.specs.find(s => (s.topic || "").toLowerCase() === t) || null;
  }

  function getSpecModeStructure(specOrTopic) {
    const spec = typeof specOrTopic === "string" ? findSpecMeta(specOrTopic) : specOrTopic;
    const groups = spec?.modeGroups || [];
    return {
      spec,
      primaryGroup: groups[0] || null,
      secondaryGroups: groups.slice(1)
    };
  }

  function findOrInitSelection(comp, topic) {
    let sel = comp.selections.find(s => s.topic.toLowerCase() === topic.toLowerCase());
    if (!sel) {
      sel = { topic, enabled: false, desiredCount: null, notes: "", modes: [], modeCounts: null };
      comp.selections.push(sel);
    }
    if (!Array.isArray(sel.modes)) sel.modes = [];
    return sel;
  }

  function getActiveModeCounts(sel, primaryGroup) {
    if (!primaryGroup || !sel.modeCounts || typeof sel.modeCounts !== "object") return {};
    const valid = new Set(primaryGroup.options.map(o => o.key));
    const clean = {};
    for (const opt of primaryGroup.options) {
      const v = parseInt(sel.modeCounts[opt.key], 10);
      if (valid.has(opt.key) && Number.isFinite(v) && v > 0) clean[opt.key] = v;
    }
    return clean;
  }

  function getPrimarySubtypeKeys(sel, primaryGroup) {
    if (!primaryGroup) return [];
    const counts = getActiveModeCounts(sel, primaryGroup);
    const countKeys = Object.keys(counts);
    if (countKeys.length > 0) return countKeys;
    const valid = new Set(primaryGroup.options.map(o => o.key));
    return (sel.modes || []).filter(m => valid.has(m));
  }

  function syncModesFromDistribution(sel, primaryGroup, secondaryGroups) {
    const cleanCounts = getActiveModeCounts(sel, primaryGroup);
    const secondaryValid = new Set((secondaryGroups || []).flatMap(g => g.options.map(o => o.key)));
    const activeSecondary = (sel.modes || []).filter(m => secondaryValid.has(m));

    if (Object.keys(cleanCounts).length > 0) {
      sel.modeCounts = cleanCounts;
      const total = Object.values(cleanCounts).reduce((a, b) => a + b, 0);
      sel.desiredCount = total > 0 ? total : null;
      sel.modes = [...Object.keys(cleanCounts), ...activeSecondary];
    } else {
      sel.modeCounts = null;
      const primaryValid = new Set((primaryGroup?.options || []).map(o => o.key));
      const keepPrimary = (sel.modes || []).filter(m => primaryValid.has(m));
      sel.modes = [...keepPrimary, ...activeSecondary];
    }
  }

  function applyTotalQtyChange(sel, spec, newTotalRaw) {
    const { primaryGroup, secondaryGroups } = getSpecModeStructure(spec);
    if (newTotalRaw === null || newTotalRaw <= 0) {
      sel.desiredCount = null;
      sel.modeCounts = null;
      return;
    }

    const newTotal = Math.min(99, Math.max(1, newTotalRaw));
    if (!primaryGroup || !primaryGroup.options.length) {
      sel.desiredCount = newTotal;
      sel.modeCounts = null;
      return;
    }

    const counts = getActiveModeCounts(sel, primaryGroup);
    const activePrimaryKeys = getPrimarySubtypeKeys(sel, primaryGroup);
    if (!activePrimaryKeys.length) {
      // Generic spec stays generic until a subtype is actively selected.
      sel.desiredCount = newTotal;
      sel.modeCounts = null;
      return;
    }

    // Seed counts if user toggled a subtype at Qty = null/Auto before entering Total Qty.
    if (!Object.keys(counts).length) {
      counts[activePrimaryKeys[0]] = newTotal;
      sel.modeCounts = counts;
      syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
      return;
    }

    // Leftmost subtype in popup receives any Total Qty delta.
    const leftmostKey = primaryGroup.options[0].key;
    const otherKeys = primaryGroup.options.slice(1).map(o => o.key);
    let othersSum = otherKeys.reduce((sum, k) => sum + (counts[k] || 0), 0);

    if (newTotal >= othersSum) {
      const firstVal = newTotal - othersSum;
      if (firstVal > 0) counts[leftmostKey] = firstVal;
      else delete counts[leftmostKey];
    } else {
      delete counts[leftmostKey];
      let excess = othersSum - newTotal;
      for (let i = otherKeys.length - 1; i >= 0 && excess > 0; i--) {
        const k = otherKeys[i];
        const cur = counts[k] || 0;
        if (cur > 0) {
          const take = Math.min(cur, excess);
          const rem = cur - take;
          if (rem > 0) counts[k] = rem;
          else delete counts[k];
          excess -= take;
        }
      }
    }

    sel.modeCounts = counts;
    syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
  }

  function handlePrimaryChipClick(comp, spec, sel, optKey) {
    const { primaryGroup, secondaryGroups } = getSpecModeStructure(spec);
    if (!primaryGroup) return;
    const totalQty = sel.desiredCount && sel.desiredCount > 0 ? sel.desiredCount : 0;
    const counts = getActiveModeCounts(sel, primaryGroup);
    const activeKeys = getPrimarySubtypeKeys(sel, primaryGroup);
    const isActive = activeKeys.includes(optKey);

    if (totalQty === 0) {
      // Auto mode: store preference in sel.modes without forcing numeric Qty.
      if (isActive) {
        sel.modes = (sel.modes || []).filter(m => m !== optKey);
      } else {
        const siblingKeys = primaryGroup.options.map(o => o.key);
        sel.modes = [...(sel.modes || []).filter(m => !siblingKeys.includes(m)), optKey];
      }
      sel.modeCounts = null;
      return;
    }

    if (totalQty === 1) {
      // At Qty = 1, auto-deselect any previous primary subtype.
      sel.modeCounts = isActive ? null : { [optKey]: 1 };
      if (isActive) {
        sel.modes = (sel.modes || []).filter(m => !primaryGroup.options.some(o => o.key === m));
      }
      syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
      return;
    }

    // totalQty > 1
    if (isActive) {
      delete counts[optKey];
      sel.modeCounts = Object.keys(counts).length ? counts : null;
      if (!sel.modeCounts) {
        sel.modes = (sel.modes || []).filter(m => !primaryGroup.options.some(o => o.key === m));
        sel.desiredCount = totalQty;
      } else {
        syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
      }
      return;
    }

    if (!Object.keys(counts).length) {
      counts[optKey] = totalQty;
    } else {
      const donorKey = Object.keys(counts).find(k => counts[k] > 1);
      if (donorKey) {
        counts[donorKey] -= 1;
        counts[optKey] = 1;
      } else {
        counts[optKey] = 1;
      }
    }
    sel.modeCounts = counts;
    syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
  }

  function handleSecondaryChipToggle(spec, sel, optKey, group) {
    sel.modes = sel.modes || [];
    if (group.multiSelect) {
      const idx = sel.modes.indexOf(optKey);
      if (idx >= 0) sel.modes.splice(idx, 1);
      else {
        sel.modes.push(optKey);
        if (spec.topic.toLowerCase() === "assertion-reason" && optKey === "both-false") {
          sel.modes = sel.modes.filter(m => m !== "4opt");
          if (!sel.modes.includes("5opt")) sel.modes.push("5opt");
          if (sel.modeCounts && sel.modeCounts["4opt"]) {
            sel.modeCounts["5opt"] = (sel.modeCounts["5opt"] || 0) + sel.modeCounts["4opt"];
            delete sel.modeCounts["4opt"];
          }
        }
      }
    } else {
      const siblingKeys = group.options.map(o => o.key);
      const wasActive = sel.modes.includes(optKey);
      sel.modes = sel.modes.filter(m => !siblingKeys.includes(m));
      if (!wasActive) sel.modes.push(optKey);
    }
  }

  function renderSpecSelectorPane(comp) {
    const targetCategory = comp.kind === "article" ? "block" : "question";
    const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

    document.getElementById("selectorGroupTitle").textContent = comp.kind === "article" ? "Article Blocks" : "Questions";
    document.getElementById("selectorAddSpecLabel").textContent = comp.kind === "article" ? "+ Add Block" : "+ Add Question";

    // Populate Selector SVG-icon dropdown menu
    const menu = document.getElementById("selectorSpecMenu");
    menu.innerHTML = "";
    allowedSpecs.forEach(spec => {
      const sel = comp.selections.find(s => s.topic.toLowerCase() === spec.topic.toLowerCase() && s.enabled);
      const item = document.createElement("button");
      item.type = "button";
      item.className = `dropdown-item dropdown-item-row ${sel ? "active" : ""}`;
      item.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><code>${escapeHtml(spec.topic)}</code></span><small>${sel ? "Active" : "Add"}</small>`;
      item.onclick = () => {
        menu.classList.add("hidden");
        markPresetModified();
        const target = findOrInitSelection(comp, spec.topic);
        const wasEnabled = !!target.enabled;
        target.enabled = true;
        if (!target.desiredCount || target.desiredCount <= 0) target.desiredCount = 1;
        if (!wasEnabled) state.lastAddedSelectorTopic = spec.topic;
        if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
        renderSpecSelectorPane(comp);
        renderOrganizationCard();
        refreshPromptPreview();
      };
      menu.appendChild(item);
    });

    // Render active rows cleanly
    const root = document.getElementById("selectorRowsList");
    root.innerHTML = "";
    const activeRows = allowedSpecs
      .map(spec => ({ spec, sel: comp.selections.find(s => s.topic.toLowerCase() === spec.topic.toLowerCase() && s.enabled) }))
      .filter(pair => !!pair.sel);

    if (!activeRows.length) {
      state.lastAddedSelectorTopic = null;
      const msg = comp.kind === "quiz"
        ? "No question types added yet (Prompt falls back to 5 MCQs). Use + Add Question above."
        : "No blocks added yet. Use + Add Block above.";
      root.innerHTML = `<div class="muted">${msg}</div>`;
      return;
    }

    activeRows.forEach(({ spec, sel }) => {
      const row = document.createElement("div");
      const isJustAdded = state.lastAddedSelectorTopic && spec.topic.toLowerCase() === state.lastAddedSelectorTopic.toLowerCase();
      row.className = `shop-row${isJustAdded ? " animate-add" : ""}`;

      const { primaryGroup, secondaryGroups } = getSpecModeStructure(spec);
      const counts = getActiveModeCounts(sel, primaryGroup);
      const activePrimaryKeys = getPrimarySubtypeKeys(sel, primaryGroup);
      const rowIconSubtype = activePrimaryKeys.length === 1 ? activePrimaryKeys[0] : null;

      const lbl = document.createElement("div");
      lbl.className = "shop-topic-cell";
      lbl.title = spec.summary || "";
      lbl.innerHTML = `<span class="spec-icon-wrap">${getSpecIcon(spec.topic, rowIconSubtype)}</span><code>${escapeHtml(spec.topic)}</code>`;

      const qtyCell = document.createElement("div");
      qtyCell.className = "shop-qty-cell";
      const cnt = document.createElement("input");
      cnt.type = "number";
      cnt.min = "1";
      cnt.max = "99";
      cnt.className = "input shop-count";
      cnt.placeholder = "Auto";
      cnt.value = sel.desiredCount || "";
      cnt.oninput = () => {
        markPresetModified();
        const raw = cnt.value.trim();
        const parsed = raw === "" ? NaN : parseInt(raw, 10);
        if (raw === "" || !Number.isFinite(parsed) || parsed <= 0) {
          applyTotalQtyChange(sel, spec, null);
          if (comp.orderedItems) {
            comp.orderedItems = comp.orderedItems.filter(o => o.topic.toLowerCase() !== spec.topic.toLowerCase());
            if (!comp.orderedItems.length) comp.orderedItems = null;
          }
          renderSpecSelectorRowBadges(row, spec, sel);
          if (state.activeDistTarget?.sel === sel) openDistPopover(state.activeDistTarget.anchorBtn, comp, spec, sel);
          refreshPromptPreview();
          return;
        }
        const val = Math.min(99, parsed);
        if (val !== parsed) cnt.value = String(val);
        applyTotalQtyChange(sel, spec, val);
        if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
        renderSpecSelectorRowBadges(row, spec, sel);
        if (state.activeDistTarget?.sel === sel) openDistPopover(state.activeDistTarget.anchorBtn, comp, spec, sel);
        refreshPromptPreview();
      };
      qtyCell.appendChild(cnt);

      const distCell = document.createElement("div");
      distCell.className = "shop-dist-cell";
      if (primaryGroup && primaryGroup.options.length > 0) {
        const distBtn = document.createElement("button");
        distBtn.type = "button";
        distBtn.className = `dist-trigger-btn${Object.keys(counts).length > 0 ? " active" : ""}`;
        distBtn.title = "Distribute subtypes & options";
        distBtn.innerHTML = getIcon("ui/distribute");
        distBtn.onclick = e => {
          e.stopPropagation();
          if (state.activeDistTarget?.sel === sel && !document.getElementById("distPopover").classList.contains("hidden")) {
            closeDistPopover();
          } else {
            openDistPopover(distBtn, comp, spec, sel, cnt);
          }
        };
        distCell.appendChild(distBtn);
      }

      const modeCell = document.createElement("div");
      modeCell.className = "mode-cell shop-modes-cell";
      const groups = spec.modeGroups || [];
      if (!groups.length) {
        const none = document.createElement("span");
        none.className = "muted";
        none.textContent = spec.summary || "";
        modeCell.appendChild(none);
      } else {
        groups.forEach((g, gIdx) => {
          const gLabel = document.createElement("span");
          gLabel.className = "mode-group-label";
          gLabel.textContent = `${g.label}:`;
          modeCell.appendChild(gLabel);

          g.options.forEach(opt => {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.dataset.modeKey = opt.key;
            const cVal = gIdx === 0 ? (counts[opt.key] || 0) : 0;
            const active = activePrimaryKeys.includes(opt.key) || (gIdx > 0 && (sel.modes || []).includes(opt.key));
            btn.className = `mode-chip ${active ? "active" : ""}`;
            btn.innerHTML = cVal > 0 && Object.keys(counts).length > 0
              ? `<span>${escapeHtml(opt.label)}</span><span class="mode-chip-badge">\u00d7${cVal}</span>`
              : `<span>${escapeHtml(opt.label)}</span>`;
            btn.onclick = () => {
              markPresetModified();
              if (gIdx === 0) {
                handlePrimaryChipClick(comp, spec, sel, opt.key);
              } else {
                handleSecondaryChipToggle(spec, sel, opt.key, g);
                if (spec.topic.toLowerCase() === "assertion-reason" && opt.key === "4opt") {
                  sel.modes = sel.modes.filter(m => m !== "both-false");
                }
              }
              if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
              renderSpecSelectorPane(comp);
              refreshPromptPreview();
            };
            modeCell.appendChild(btn);
          });
        });
      }

      const pencilCell = document.createElement("div");
      pencilCell.className = "shop-pencil-cell";
      const pencil = document.createElement("button");
      pencil.type = "button";
      pencil.className = `pencil-btn ${sel.notes ? "has-note" : ""}`;
      pencil.title = sel.notes ? `Note: ${sel.notes}` : "Add custom instructions";
      pencil.innerHTML = getIcon("ui/pencil");
      pencil.onclick = e => {
        e.stopPropagation();
        openNotePopover(pencil, comp, sel);
      };
      pencilCell.appendChild(pencil);

      const rmCell = document.createElement("div");
      rmCell.className = "shop-remove-cell";
      const rm = document.createElement("button");
      rm.type = "button";
      rm.className = "drag-remove-btn";
      rm.textContent = "\u00d7";
      rm.title = `Remove ${spec.topic}`;
      rm.onclick = () => {
        markPresetModified();
        sel.enabled = false;
        sel.desiredCount = null;
        sel.modeCounts = null;
        if (comp.orderedItems) syncOrderedItemsFromSelections(comp, spec.topic);
        renderSpecSelectorPane(comp);
        renderOrganizationCard();
        refreshPromptPreview();
      };
      rmCell.appendChild(rm);

      row.append(lbl, qtyCell, distCell, modeCell, pencilCell, rmCell);
      root.appendChild(row);
    });
    state.lastAddedSelectorTopic = null;
  }

  function renderSpecSelectorRowBadges(rowEl, spec, sel) {
    const { primaryGroup } = getSpecModeStructure(spec);
    const counts = getActiveModeCounts(sel, primaryGroup);
    const activePrimaryKeys = getPrimarySubtypeKeys(sel, primaryGroup);
    const distBtn = rowEl.querySelector(".dist-trigger-btn");
    if (distBtn) distBtn.classList.toggle("active", Object.keys(counts).length > 0);
    const iconEl = rowEl.querySelector(".shop-topic-cell .spec-icon-wrap");
    if (iconEl) {
      const sub = activePrimaryKeys.length === 1 ? activePrimaryKeys[0] : null;
      iconEl.innerHTML = getSpecIcon(spec.topic, sub);
    }
    rowEl.querySelectorAll(".mode-chip").forEach(btn => {
      const key = btn.dataset.modeKey;
      const isPrimary = primaryGroup?.options.some(o => o.key === key);
      const opt = (spec.modeGroups || []).flatMap(g => g.options).find(o => o.key === key);
      if (!opt) return;
      if (isPrimary) {
        const cVal = counts[key] || 0;
        const active = activePrimaryKeys.includes(key);
        btn.classList.toggle("active", active);
        btn.innerHTML = cVal > 0
          ? `<span>${escapeHtml(opt.label)}</span><span class="mode-chip-badge">\u00d7${cVal}</span>`
          : `<span>${escapeHtml(opt.label)}</span>`;
      } else {
        btn.classList.toggle("active", (sel.modes || []).includes(key));
      }
    });
  }

  function positionAnchoredPopover(pop, anchorEl) {
    pop.classList.remove("hidden");
    const rect = anchorEl.getBoundingClientRect();
    const popRect = pop.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - popRect.width - 8, rect.left));
    const fitsBelow = rect.bottom + 6 + popRect.height <= window.innerHeight - 8;
    const top = fitsBelow
      ? rect.bottom + 6
      : Math.max(8, rect.top - popRect.height - 6);
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  }

  function openDistPopover(anchorBtn, comp, spec, sel, qtyInputEl = null) {
    closeAllDropdowns(["distPopover"]);
    const pop = document.getElementById("distPopover");
    const { primaryGroup, secondaryGroups } = getSpecModeStructure(spec);
    if (!primaryGroup) return;

    state.activeDistTarget = { anchorBtn, comp, spec, sel, qtyInputEl };
    pop.innerHTML = "";

    const head = document.createElement("div");
    head.className = "dist-popover-head";
    head.innerHTML = `<strong><code>${escapeHtml(spec.topic)}</code> Distribution</strong>`;
    const doneBtn = document.createElement("button");
    doneBtn.type = "button";
    doneBtn.className = "btn btn-xs btn-ghost";
    doneBtn.textContent = "Done";
    doneBtn.onclick = () => closeDistPopover();
    head.appendChild(doneBtn);
    pop.appendChild(head);

    const row = document.createElement("div");
    row.className = "dist-types-row";
    const counts = getActiveModeCounts(sel, primaryGroup);

    primaryGroup.options.forEach(opt => {
      const col = document.createElement("div");
      const val = counts[opt.key] || 0;
      col.className = `dist-type-col${val > 0 ? " active" : ""}`;

      const tile = document.createElement("button");
      tile.type = "button";
      tile.className = "dist-type-tile";
      tile.title = opt.label;
      tile.innerHTML = getSpecIcon(spec.topic, opt.key);

      const lbl = document.createElement("span");
      lbl.className = "dist-type-label";
      lbl.textContent = opt.label;

      const num = document.createElement("input");
      num.type = "number";
      num.min = "0";
      num.max = "99";
      num.className = "dist-type-count";
      num.value = String(val);

      const updateCount = nextVal => {
        markPresetModified();
        const clamped = Math.max(0, Math.min(99, parseInt(nextVal, 10) || 0));
        const curCounts = getActiveModeCounts(sel, primaryGroup);
        if (clamped > 0) curCounts[opt.key] = clamped;
        else delete curCounts[opt.key];

        sel.modeCounts = Object.keys(curCounts).length ? curCounts : null;
        if (!sel.modeCounts) {
          sel.modes = (sel.modes || []).filter(m => !primaryGroup.options.some(o => o.key === m));
        }
        syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
        if (comp.orderedItems) syncOrderedItemsFromSelections(comp);

        col.classList.toggle("active", clamped > 0);
        if (state.activeDistTarget?.qtyInputEl) {
          state.activeDistTarget.qtyInputEl.value = sel.desiredCount || "";
        } else {
          renderSpecSelectorPane(comp);
        }
        renderSpecSelectorPane(comp);
        refreshPromptPreview();
      };

      tile.onclick = () => {
        const cur = parseInt(num.value, 10) || 0;
        const next = cur > 0 ? 0 : 1;
        num.value = String(next);
        updateCount(next);
      };
      num.oninput = () => updateCount(num.value);

      col.append(tile, lbl, num);
      row.appendChild(col);
    });
    pop.appendChild(row);

    if (secondaryGroups.length > 0) {
      const modRow = document.createElement("div");
      modRow.className = "dist-modifiers-row";
      secondaryGroups.forEach(g => {
        const gl = document.createElement("span");
        gl.className = "mode-group-label";
        gl.textContent = `${g.label}:`;
        modRow.appendChild(gl);
        g.options.forEach(opt => {
          const btn = document.createElement("button");
          btn.type = "button";
          const active = (sel.modes || []).includes(opt.key);
          btn.className = `mode-chip${active ? " active" : ""}`;
          btn.textContent = opt.label;
          btn.onclick = () => {
            markPresetModified();
            handleSecondaryChipToggle(spec, sel, opt.key, g);
            openDistPopover(anchorBtn, comp, spec, sel, qtyInputEl);
            renderSpecSelectorPane(comp);
            refreshPromptPreview();
          };
          modRow.appendChild(btn);
        });
      });
      pop.appendChild(modRow);
    }

    positionAnchoredPopover(pop, anchorBtn);
  }

  function closeDistPopover() {
    document.getElementById("distPopover")?.classList.add("hidden");
    state.activeDistTarget = null;
  }

  function buildOrganizationDisplayItems(comp) {
    if (comp.orderedItems && comp.orderedItems.length > 0) {
      const counters = {};
      return comp.orderedItems.map(inst => {
        const key = (inst.topic || "").toLowerCase();
        counters[key] = (counters[key] || 0) + 1;
        inst.instanceNumber = counters[key];
        return { ...inst, subtype: inst.subtype || null, isAuto: false };
      });
    }

    const preview = [];
    comp.selections.filter(s => s.enabled).forEach(sel => {
      const { primaryGroup } = getSpecModeStructure(sel.topic);
      const isAuto = !sel.desiredCount || sel.desiredCount <= 0;
      if (isAuto) {
        const activePrimary = getPrimarySubtypeKeys(sel, primaryGroup);
        const sub = activePrimary.length === 1 ? activePrimary[0] : null;
        preview.push({
          id: `${sel.topic}_1_auto`,
          topic: sel.topic,
          instanceNumber: 1,
          subtype: sub,
          isAuto: true
        });
        return;
      }

      const counts = getActiveModeCounts(sel, primaryGroup);
      const countKeys = Object.keys(counts);
      let num = 0;
      if (countKeys.length > 0) {
        countKeys.forEach(sub => {
          for (let i = 0; i < counts[sub] && num < sel.desiredCount; i++) {
            num++;
            preview.push({ id: `${sel.topic}_${num}`, topic: sel.topic, instanceNumber: num, subtype: sub, isAuto: false });
          }
        });
      }
      const fallbackSub = !countKeys.length && getPrimarySubtypeKeys(sel, primaryGroup).length === 1
        ? getPrimarySubtypeKeys(sel, primaryGroup)[0]
        : null;
      while (num < sel.desiredCount) {
        num++;
        preview.push({ id: `${sel.topic}_${num}`, topic: sel.topic, instanceNumber: num, subtype: fallbackSub, isAuto: false });
      }
    });
    return preview;
  }

  function materializeOrderedItems(comp) {
    if (comp.orderedItems && comp.orderedItems.length > 0) return;
    comp.orderedItems = buildOrganizationDisplayItems(comp).map(it => ({
      id: `${it.topic}_${it.instanceNumber}_${Math.random().toString(36).slice(2, 6)}`,
      topic: it.topic,
      instanceNumber: it.instanceNumber,
      subtype: it.subtype || null
    }));
  }

  function syncOrderedItemsFromSelections(comp, touchedTopic = null) {
    if (!comp.orderedItems) return;

    const desiredCounts = {};
    const desiredModeQueues = {};
    comp.selections.filter(s => s.enabled).forEach(s => {
      const k = s.topic.toLowerCase();
      desiredCounts[k] = Math.max(1, s.desiredCount || 1);
      const { primaryGroup } = getSpecModeStructure(s.topic);
      const counts = getActiveModeCounts(s, primaryGroup);
      const q = [];
      Object.entries(counts).forEach(([sub, n]) => {
        for (let i = 0; i < n; i++) q.push(sub);
      });
      desiredModeQueues[k] = q;
    });

    const existing = [...comp.orderedItems];
    const currentCounts = {};
    const kept = [];

    existing.forEach(inst => {
      const k = (inst.topic || "").toLowerCase();
      const targetMax = desiredCounts[k] || 0;
      const seen = currentCounts[k] || 0;
      if (seen < targetMax) {
        currentCounts[k] = seen + 1;
        kept.push({ ...inst, instanceNumber: seen + 1 });
      }
    });

    comp.selections.filter(s => s.enabled).forEach(s => {
      const k = s.topic.toLowerCase();
      const count = desiredCounts[k] || 0;
      let seen = currentCounts[k] || 0;
      const q = desiredModeQueues[k] || [];
      while (seen < count) {
        const sub = q[seen] || null;
        seen++;
        kept.push({ id: `${s.topic}_${seen}_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: seen, subtype: sub });
      }
    });

    comp.orderedItems = kept.length ? kept : null;
  }

  function syncSelectionsFromOrderedItems(comp, touchedTopic = null) {
    const counts = {};
    const subtypeCountsByTopic = {};
    (comp.orderedItems || []).forEach(inst => {
      const k = (inst.topic || "").toLowerCase();
      counts[k] = (counts[k] || 0) + 1;
      inst.instanceNumber = counts[k];
      if (inst.subtype) {
        subtypeCountsByTopic[k] = subtypeCountsByTopic[k] || {};
        subtypeCountsByTopic[k][inst.subtype] = (subtypeCountsByTopic[k][inst.subtype] || 0) + 1;
      }
    });

    const touchedKey = touchedTopic ? touchedTopic.toLowerCase() : null;
    comp.selections.forEach(sel => {
      const k = sel.topic.toLowerCase();
      const c = counts[k] || 0;
      if (c > 0) {
        sel.enabled = true;
        // Preserve untouched Auto (desiredCount: null) specs when another item was manipulated.
        if (!(sel.desiredCount === null && c === 1 && touchedKey && k !== touchedKey)) {
          sel.desiredCount = c;
        }
        const subCounts = subtypeCountsByTopic[k];
        if (subCounts && Object.keys(subCounts).length > 0) {
          sel.modeCounts = subCounts;
          const { primaryGroup, secondaryGroups } = getSpecModeStructure(sel.topic);
          syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
        }
      } else {
        sel.enabled = false;
        sel.desiredCount = null;
        sel.modeCounts = null;
      }
    });
  }

  function renderOrgSubtypePicker(topic) {
    const wrap = document.getElementById("orgSubtypeDropWrap");
    const chosenDisplay = document.getElementById("orgSubtypeChosenDisplay");
    const menu = document.getElementById("orgSubtypeMenu");
    const { primaryGroup } = getSpecModeStructure(topic);

    if (!primaryGroup || !primaryGroup.options.length) {
      state.prompt.organizerChosenSubtype = "";
      wrap.classList.add("hidden");
      menu.classList.add("hidden");
      return;
    }

    wrap.classList.remove("hidden");
    const validKeys = primaryGroup.options.map(o => o.key);
    if (state.prompt.organizerChosenSubtype && !validKeys.includes(state.prompt.organizerChosenSubtype)) {
      state.prompt.organizerChosenSubtype = "";
    }

    const curSub = state.prompt.organizerChosenSubtype;
    const curOpt = primaryGroup.options.find(o => o.key === curSub);
    chosenDisplay.innerHTML = curOpt
      ? `<span class="spec-icon-wrap">${getSpecIcon(topic, curOpt.key)}</span><span>${escapeHtml(curOpt.label)}</span>`
      : `<span class="spec-icon-wrap">${getSpecIcon(topic)}</span><span>Generic</span>`;

    menu.innerHTML = "";
    const genBtn = document.createElement("button");
    genBtn.type = "button";
    genBtn.className = `dropdown-item dropdown-item-row ${!curSub ? "active" : ""}`;
    genBtn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(topic)}</span><span>Generic</span></span>`;
    genBtn.onclick = () => {
      state.prompt.organizerChosenSubtype = "";
      menu.classList.add("hidden");
      renderOrgSubtypePicker(topic);
    };
    menu.appendChild(genBtn);

    primaryGroup.options.forEach(opt => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = `dropdown-item dropdown-item-row ${curSub === opt.key ? "active" : ""}`;
      btn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(topic, opt.key)}</span><span>${escapeHtml(opt.label)}</span></span>`;
      btn.onclick = () => {
        state.prompt.organizerChosenSubtype = opt.key;
        menu.classList.add("hidden");
        renderOrgSubtypePicker(topic);
      };
      menu.appendChild(btn);
    });
  }

  function renderSpecOrganizerPane(comp) {
    const targetCategory = comp.kind === "article" ? "block" : "question";
    const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

    // Preserve chosen spec type across additions!
    if (!state.prompt.organizerChosenTopic || !allowedSpecs.some(s => s.topic.toLowerCase() === state.prompt.organizerChosenTopic.toLowerCase())) {
      state.prompt.organizerChosenTopic = allowedSpecs[0]?.topic || "";
      state.prompt.organizerChosenSubtype = "";
    }

    const chosen = state.prompt.organizerChosenTopic;
    document.getElementById("orgSpecChosenDisplay").innerHTML =
      `<span class="spec-icon-wrap">${getSpecIcon(chosen)}</span><span>${escapeHtml(chosen)}</span>`;
    renderOrgSubtypePicker(chosen);

    const hasCustomOrder = !!(comp.orderedItems && comp.orderedItems.length > 0);
    document.getElementById("btnSpecOrgResetOrder").classList.toggle("hidden", !hasCustomOrder);
    document.getElementById("specOrgHint").textContent = hasCustomOrder
      ? "Custom sequence active — drag boxes to reorder"
      : "Preview order — drag or + Add to customize exact sequence";

    const menu = document.getElementById("orgSpecMenu");
    menu.innerHTML = "";
    allowedSpecs.forEach(spec => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = `dropdown-item dropdown-item-row ${spec.topic.toLowerCase() === chosen.toLowerCase() ? "active" : ""}`;
      item.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span></span>`;
      item.onclick = () => {
        state.prompt.organizerChosenTopic = spec.topic;
        state.prompt.organizerChosenSubtype = "";
        menu.classList.add("hidden");
        menu.querySelectorAll(".dropdown-item").forEach(b => b.classList.toggle("active", b === item));
        document.getElementById("orgSpecChosenDisplay").innerHTML =
          `<span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span>`;
        renderOrgSubtypePicker(spec.topic);
      };
      menu.appendChild(item);
    });

    const track = document.getElementById("specOrgTrack");
    track.innerHTML = "";
    const items = buildOrganizationDisplayItems(comp);
    if (!items.length) {
      state.lastAddedSpecOrgId = null;
      track.innerHTML = '<div class="muted">No items selected yet. Use + Add or switch to Selector.</div>';
      return;
    }

    items.forEach((inst, idx) => {
      const box = document.createElement("div");
      const isJustAdded = state.lastAddedSpecOrgId && inst.id === state.lastAddedSpecOrgId;
      box.className = `drag-box spec-org-box${isJustAdded ? " animate-add" : ""}`;
      box.title = inst.subtype ? `${inst.topic}:${inst.subtype} #${inst.instanceNumber}` : `${inst.topic} #${inst.instanceNumber}`;
      box.draggable = items.length > 1;

      const top = document.createElement("div");
      top.className = "drag-box-top";
      const badge = document.createElement("span");
      badge.className = inst.isAuto ? "drag-badge-auto" : "drag-badge";
      badge.textContent = inst.isAuto ? "Auto" : `#${inst.instanceNumber}`;

      const rm = document.createElement("button");
      rm.className = "drag-remove-btn";
      rm.textContent = "\u00d7";
      rm.onclick = e => {
        e.stopPropagation();
        markPresetModified();
        materializeOrderedItems(comp);
        comp.orderedItems.splice(idx, 1);
        if (!comp.orderedItems.length) comp.orderedItems = null;
        syncSelectionsFromOrderedItems(comp, inst.topic);
        renderSpecOrganizerPane(comp);
        renderOrganizationCard();
        refreshPromptPreview();
      };
      top.append(badge, rm);

      const sub = document.createElement("div");
      sub.className = "drag-box-sub";
      const labelText = inst.subtype || inst.topic;
      sub.innerHTML = `<span class="spec-icon-wrap spec-icon-org">${getSpecIcon(inst.topic, inst.subtype)}</span><span class="spec-org-label">${escapeHtml(labelText)}</span>`;

      box.append(top, sub);

      box.addEventListener("dragstart", () => { state.dragSpecOrgIndex = idx; box.classList.add("dragging"); });
      box.addEventListener("dragend", () => {
        state.dragSpecOrgIndex = null;
        document.querySelectorAll("#specOrgTrack .drag-box").forEach(el => el.classList.remove("dragging", "drag-over"));
      });
      box.addEventListener("dragover", e => { e.preventDefault(); box.classList.add("drag-over"); });
      box.addEventListener("dragleave", () => box.classList.remove("drag-over"));
      box.addEventListener("drop", e => {
        e.preventDefault();
        box.classList.remove("drag-over");
        if (state.dragSpecOrgIndex === null || state.dragSpecOrgIndex === idx) return;
        markPresetModified();
        materializeOrderedItems(comp);
        const [moved] = comp.orderedItems.splice(state.dragSpecOrgIndex, 1);
        comp.orderedItems.splice(idx, 0, moved);
        syncSelectionsFromOrderedItems(comp, moved?.topic || inst.topic);
        renderSpecOrganizerPane(comp);
        refreshPromptPreview();
      });

      track.appendChild(box);
      if (isJustAdded) {
        requestAnimationFrame(() => box.scrollIntoView({ behavior: "smooth", inline: "nearest", block: "nearest" }));
      }
    });
    state.lastAddedSpecOrgId = null;
  }

  function openNotePopover(anchorBtn, comp, sel) {
    closeAllDropdowns(["notePopover"]);
    const pop = document.getElementById("notePopover");
    const ta = document.getElementById("notePopoverText");
    document.getElementById("notePopoverTitle").innerHTML = `Notes for <code>${escapeHtml(sel.topic)}</code>`;
    ta.value = sel.notes || "";
    state.activeNoteTarget = { comp, sel };

    pop.classList.remove("hidden");
    const rect = anchorBtn.getBoundingClientRect();
    const popRect = pop.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - popRect.width - 8, rect.right - popRect.width));
    const fitsBelow = rect.bottom + 6 + popRect.height <= window.innerHeight - 8;
    const top = fitsBelow
      ? rect.bottom + 6
      : Math.max(8, rect.top - popRect.height - 6);
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
    ta.focus();
  }

  function closeNotePopover() {
    document.getElementById("notePopover").classList.add("hidden");
    state.activeNoteTarget = null;
  }

  function buildPromptDraftPayload() {
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

  function getPromptOptionsEligibility() {
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

  function refreshPromptOptionsAvailability() {
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

  function schedulePromptPreview(delayMs = 120) {
    refreshPromptOptionsAvailability();
    clearTimeout(state.previewDebounceTimer);
    state.previewDebounceTimer = setTimeout(() => {
      refreshPromptPreview();
    }, delayMs);
  }

  async function refreshPromptPreview() {
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

  function renderPromptAttachments() {
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

  async function readyWorkspace(overwriteConfirmed = false, targetDirectory = null, alreadyWarnedMissingMby = false) {
    const draft = buildPromptDraftPayload();
    if (!draft.workingDirectory) {
      toast("Select & Initialize an Active Workspace first.", "err");
      switchView("setup");
      return;
    }

    const hasWorkspaceMby = state.apps.some(a => a.kind === "mby" && a.isPresentInWorkspace);
    if (!hasWorkspaceMby && !alreadyWarnedMissingMby) {
      toast("Active Workspace does not have Meloberry CLI (mby) yet.", "warn", { persistent: true, key: "missing-ws-mby" });
      alreadyWarnedMissingMby = true;
    }

    const wasUninitialized = !(state.settings.activeWorkspace && state.settings.activeWorkspace.isInitialized);

    if (!overwriteConfirmed) {
      const q = targetDirectory
        ? `&targetDirectory=${encodeURIComponent(targetDirectory)}`
        : "";
      const statusRes = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(draft.workingDirectory)}${q}`);
      if (statusRes.ok && statusRes.data.exists) {
        openOverwriteConfirmModal(statusRes.data.agentsMdPath, targetDirectory, alreadyWarnedMissingMby);
        return;
      }
    }

    const res = await api("POST", "/api/workspace/ready", { draft, overwriteConfirmed, targetDirectory });
    if (res.status === 409) {
      const targetPath = (targetDirectory || draft.workingDirectory) + "\\AGENTS.md";
      openOverwriteConfirmModal(targetPath, targetDirectory, alreadyWarnedMissingMby);
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
    if (wasUninitialized && !alreadyWarnedMissingMby) {
      toast("Workspace might not have been initialized", "warn");
    }
    if (Array.isArray(res.data.warnings) && res.data.warnings.length > 0) {
      res.data.warnings.forEach(w => toast(w, "warn"));
    }
    toast(`AGENTS.md Ready (${res.data.bytesWritten} B \u00b7 ~${res.data.approximateTokens} tokens).`, "ok");
  }

  function openOverwriteConfirmModal(agentsPath, targetDirectory = null, alreadyWarnedMissingMby = false) {
    document.getElementById("confirmModalTitle").textContent = "Overwrite Existing AGENTS.md?";
    document.getElementById("confirmModalBody").textContent =
      `AGENTS.md already exists at "${agentsPath}". Overwrite it with the updated workspace playbook and targeted schemas?`;
    document.getElementById("agentsPreviewModalCode").classList.add("hidden");
    const proceed = document.getElementById("btnConfirmModalProceed");
    proceed.textContent = "Overwrite AGENTS.md";
    proceed.classList.remove("hidden");
    state.activeConfirmAction = async () => {
      closeConfirmModal();
      await readyWorkspace(true, targetDirectory, alreadyWarnedMissingMby);
    };
    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  }

  function closeConfirmModal() {
    document.getElementById("confirmAgentsModal").classList.add("hidden");
    state.activeConfirmAction = null;
  }

  // ---- Minimal WebApp Viewer ----

  async function refreshViewerDiscovery() {
    const res = await api("GET", "/api/workspace/discover");
    if (res.ok) {
      state.discoveredWebApps = res.data.webApps || [];
      state.discoveredMbyProjects = res.data.mbyWorkspaces || [];
    } else {
      state.discoveredWebApps = [];
      state.discoveredMbyProjects = [];
    }
    renderViewerMenus();
  }

  function showViewerLoadingOverlay(label = "Applying theme & loading WebApp...") {
    clearTimeout(state.viewerLoadFadeTimer);
    clearTimeout(state.viewerHideDelayTimer);
    const ov = document.getElementById("viewerLoadingOverlay");
    const txt = document.getElementById("viewerLoadingText");
    if (txt) txt.textContent = label;
    if (ov) {
      ov.classList.remove("hidden", "fading");
    }
    return ++state.viewerLoadToken;
  }

  function hideViewerLoadingOverlay(expectedToken) {
    if (expectedToken !== undefined && expectedToken !== state.viewerLoadToken) return;
    clearTimeout(state.viewerLoadFadeTimer);
    clearTimeout(state.viewerHideDelayTimer);
    const ov = document.getElementById("viewerLoadingOverlay");
    if (!ov || ov.classList.contains("hidden")) return;
    ov.classList.add("fading");
    state.viewerHideDelayTimer = setTimeout(() => {
      ov.classList.add("hidden");
      ov.classList.remove("fading");
    }, 220);
  }

  function renderViewerMenus() {
    const recentList = document.getElementById("recentSubmenuList");
    recentList.innerHTML = "";
    const topFive = (state.settings.recentWebApps || []).slice(0, 5);
    if (!topFive.length) {
      recentList.innerHTML = '<div class="muted" style="padding:6px 8px">No recent WebApps</div>';
    } else {
      topFive.forEach(r => {
        const row = document.createElement("div");
        row.className = "submenu-item-row";
        const link = document.createElement("button");
        link.className = "submenu-link";
        link.title = r.path;
        link.textContent = r.name;
        link.onclick = () => { closeViewerMenus(); openWebAppPath(r.path); };
        const pill = document.createElement("span");
        pill.className = "pill";
        pill.textContent = r.reportCount;
        row.append(link, pill);
        recentList.appendChild(row);
      });
    }

    const wsWrap = document.getElementById("activeWsSubmenuWrap");
    const wsList = document.getElementById("activeWsSubmenuList");
    const hasWsItems = state.discoveredWebApps.length > 0 || state.discoveredMbyProjects.length > 0;
    wsWrap.classList.toggle("hidden", !hasWsItems);
    wsList.innerHTML = "";

    if (state.discoveredWebApps.length > 0) {
      const lbl = document.createElement("div");
      lbl.className = "submenu-label";
      lbl.textContent = "Compiled WebApps";
      wsList.appendChild(lbl);
      state.discoveredWebApps.forEach(w => {
        const row = document.createElement("div");
        row.className = "submenu-item-row";
        const link = document.createElement("button");
        link.className = "submenu-link";
        link.textContent = w.name;
        link.onclick = () => { closeViewerMenus(); openWebAppPath(w.path); };
        row.appendChild(link);
        wsList.appendChild(row);
      });
    }

    if (state.discoveredMbyProjects.length > 0) {
      const lbl = document.createElement("div");
      lbl.className = "submenu-label";
      lbl.textContent = "mby Projects";
      wsList.appendChild(lbl);
      state.discoveredMbyProjects.forEach(p => {
        const row = document.createElement("div");
        row.className = "submenu-item-row";
        const nameBtn = document.createElement("button");
        nameBtn.className = "submenu-link";
        nameBtn.textContent = p.id;
        nameBtn.onclick = () => { closeViewerMenus(); buildAndViewWorkspace(p.id); };
        const expBtn = document.createElement("button");
        expBtn.className = "btn btn-xs btn-ghost";
        expBtn.textContent = ".mbypkg";
        expBtn.onclick = e => { e.stopPropagation(); closeViewerMenus(); exportWorkspacePkg(p.id); };
        row.append(nameBtn, expBtn);
        wsList.appendChild(row);
      });
    }
  }

  function closeViewerMenus() {
    document.getElementById("viewerUpMenu").classList.add("hidden");
    document.getElementById("recentSubmenu").classList.add("hidden");
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  }

  async function openWebAppPath(path) {
    const res = await api("POST", "/api/viewer/open", path ? { path } : {});
    if (res.ok && !res.data.cancelled) {
      const frame = document.getElementById("webAppFrame");
      document.getElementById("viewerPlaceholder").classList.add("hidden");
      showViewerLoadingOverlay("Applying theme & loading WebApp...");
      frame.classList.remove("hidden");
      frame.src = `${res.data.url}?v=${Date.now()}`;
      switchView("viewer");
      if (res.data.entry) {
        const list = (state.settings.recentWebApps || []).filter(x => x.path.toLowerCase() !== res.data.entry.path.toLowerCase());
        state.settings.recentWebApps = [res.data.entry, ...list].slice(0, 20);
        renderViewerMenus();
      }
    } else if (!res.ok) {
      toast(res.data?.error?.description || "Unable to open WebApp.", "err");
    }
  }

  async function buildAndViewWorkspace(workspaceId) {
    toast(`Building ${workspaceId}...`, "info");
    const res = await api("POST", "/api/workspaces/build", { workspaceId });
    if (res.ok) {
      const frame = document.getElementById("webAppFrame");
      document.getElementById("viewerPlaceholder").classList.add("hidden");
      showViewerLoadingOverlay(`Opening ${workspaceId}...`);
      frame.classList.remove("hidden");
      frame.src = `${res.data.url}?v=${Date.now()}`;
      refreshViewerDiscovery();
      toast(`Built & opened ${workspaceId}`, "ok");
    } else {
      toast(res.data?.error?.description || "Build failed.", "err");
    }
  }

  async function exportWorkspacePkg(workspaceId) {
    const res = await api("POST", "/api/workspaces/export", { workspaceId, target: "android" });
    if (res.ok && !res.data.cancelled) toast(`Exported .mbypkg to ${res.data}`, "ok");
    else if (!res.ok) toast(res.data?.error?.description || "Export failed.", "err");
  }

  function renderEcosystemRepos() {
    const cards = document.getElementById("ecosystemRepoCards");
    cards.innerHTML = "";
    state.ecosystemRepos.forEach(r => {
      const card = document.createElement("div");
      card.className = "repo-card";
      card.innerHTML = `<strong>${escapeHtml(r.displayName)}</strong><div class="muted">${escapeHtml(r.description)}</div>`;
      const btn = document.createElement("button");
      btn.className = "btn btn-xs btn-ghost";
      btn.textContent = r.slug;
      btn.onclick = () => api("POST", "/api/shell/open-url", { url: r.repoUrl });
      card.appendChild(btn);
      cards.appendChild(card);
    });
  }

  function syncSettingsInputs() {
    document.getElementById("setThemeMode").value = state.settings.themeMode || "berry";
    document.getElementById("setDefaultBuildDir").value = state.settings.defaultBuildOutputDir || "";
    document.getElementById("setMbyOverride").value = state.settings.mbyExecutableOverride || "";
    document.getElementById("setMelomgOverride").value = state.settings.melomgExecutableOverride || "";
    document.getElementById("setUpdateOwner").value = state.settings.selfUpdateRepoOwner || state.metadata.selfUpdateRepoOwner || "doc-shashank";
    document.getElementById("setUpdateRepo").value = state.settings.selfUpdateRepoName || state.metadata.selfUpdateRepoName || "meloberry-pc";
  }

  // Titlebar drag, dblclick maximize, caption buttons
  const titlebar = document.getElementById("titlebar");
  titlebar.addEventListener("mousedown", e => {
    if (e.button !== 0 || e.target.closest("button, input, select, a, .nav-tabs, .notif-wrap")) return;
    postWindowCommand("window.drag");
  });
  titlebar.addEventListener("dblclick", e => {
    if (e.target.closest("button, input, select, a, .nav-tabs, .notif-wrap")) return;
    postWindowCommand("window.toggleMaximize");
  });
  document.getElementById("btnWinMinimize").onclick = () => postWindowCommand("window.minimize");
  document.getElementById("btnWinMaximize").onclick = () => postWindowCommand("window.toggleMaximize");
  document.getElementById("btnWinClose").onclick = () => postWindowCommand("window.close");

  // Notification Center
  registerDropdown("btnNotifications", "notifPopover", { wrapSelector: ".notif-wrap" });
  document.getElementById("btnNotifReadAll").onclick = () => {
    state.notifications.forEach(n => { n.unread = false; });
    renderNotifications();
  };
  document.getElementById("btnNotifClear").onclick = () => {
    state.notifications = [];
    renderNotifications();
  };

  // AGENTS.md Ready Pill Menu
  registerDropdown("agentsReadyPill", "agentsPillMenu", { wrapSelector: "#agentsPillWrap" });
  document.getElementById("btnMenuInspectAgents").onclick = () => {
    closeAllDropdowns();
    document.getElementById("confirmModalTitle").textContent = "AGENTS.md";
    document.getElementById("confirmModalBody").textContent = state.agentsMdStatus?.agentsMdPath || "AGENTS.md";
    const pre = document.getElementById("agentsPreviewModalCode");
    pre.textContent = state.agentsMdStatus?.preview || "(Click Ready Workspace to inspect freshly rendered content.)";
    pre.classList.remove("hidden");
    document.getElementById("btnConfirmModalProceed").classList.add("hidden");
    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  };
  document.getElementById("btnMenuOpenAgentsFolder").onclick = () => {
    closeAllDropdowns();
    const fullAgentsPath = state.agentsMdStatus?.agentsMdPath || "";
    const lastSep = Math.max(fullAgentsPath.lastIndexOf("\\"), fullAgentsPath.lastIndexOf("/"));
    const folder = lastSep > 0 ? fullAgentsPath.slice(0, lastSep) : (state.settings.activeWorkspace?.workspacePath || "");
    api("POST", "/api/workspace/open-folder", { path: folder });
  };

  // Workspace Action Strip controls
  registerDropdown("btnResetWorkspace", "resetMenu", { wrapSelector: "#resetMenuWrap" });
  document.querySelectorAll("#resetMenu .dropdown-item").forEach(btn => {
    btn.onclick = () => {
      state.resetScope = btn.dataset.scope;
      closeAllDropdowns();
      triggerResetPrompt();
    };
  });

  registerDropdown("btnReadyWorkspace", "readyWsMenu", { wrapSelector: "#readyWsMenuWrap", autoWireClick: false });
  document.getElementById("btnReadyWorkspace").onclick = async e => {
    e.stopPropagation();
    const aws = state.settings.activeWorkspace;
    const wsPath = aws?.workspacePath;
    if (!wsPath) {
      closeAllDropdowns();
      toast("Select & Initialize an Active Workspace first.", "err");
      switchView("setup");
      return;
    }

    const subRes = await api("GET", `/api/workspace/subfolders?workspacePath=${encodeURIComponent(wsPath)}`);
    if (subRes.ok && subRes.data.hasSubfolders) {
      toggleDropdown("readyWsMenu");
      return;
    }

    closeAllDropdowns();
    await readyWorkspace(false, null);
  };

  document.getElementById("optReadyWsRoot").onclick = async () => {
    closeAllDropdowns();
    await readyWorkspace(false, null);
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
    await readyWorkspace(false, picked);
  };

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

  // Outdated banner
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
    await loadSpecs(true);
    await refreshPromptPreview();
    toast(`Updated workspace CLI(s): ${res.data.updateResult.copiedNewFileNames.join(", ")}`, "ok");
  };

  // Navigation & Setup controls
  document.querySelectorAll(".nav-tab").forEach(btn => {
    btn.onclick = () => switchView(btn.dataset.view);
  });

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
      await loadSpecs(true);
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

  const btnInitWs = document.getElementById("btnInitializeWorkspace");
  btnInitWs.onclick = withButtonBusy(btnInitWs, "Initializing...", initializeWorkspace);

  function openInputPromptModal(title, message, defaultValue = "") {
    return new Promise(resolve => {
      state.activeInputResolve = resolve;
      document.getElementById("inputModalTitle").textContent = title;
      document.getElementById("inputModalMessage").textContent = message;
      const field = document.getElementById("inputModalField");
      field.value = defaultValue;
      document.getElementById("inputPromptModal").classList.remove("hidden");
      field.focus();
      field.select();
    });
  }

  function closeInputPromptModal(result = null) {
    document.getElementById("inputPromptModal").classList.add("hidden");
    const resolve = state.activeInputResolve;
    state.activeInputResolve = null;
    if (resolve) resolve(result);
  }

  document.getElementById("btnInputModalCancel").onclick = () => closeInputPromptModal(null);
  document.getElementById("btnInputModalOk").onclick = () => closeInputPromptModal(document.getElementById("inputModalField").value);
  document.getElementById("inputModalField").addEventListener("keydown", e => {
    if (e.key === "Enter") {
      e.preventDefault();
      closeInputPromptModal(document.getElementById("inputModalField").value);
    }
  });
  document.getElementById("inputPromptModal").addEventListener("click", e => {
    if (e.target === e.currentTarget) closeInputPromptModal(null);
  });

  // Prompt Studio controls
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

  registerDropdown("btnSelectorAddSpecDrop", "selectorSpecMenu", { wrapSelector: "#selectorSpecDropWrap" });
  registerDropdown("btnOrgSpecDrop", "orgSpecMenu", { wrapSelector: "#orgSpecDropWrap" });
  registerDropdown("btnOrgSubtypeDrop", "orgSubtypeMenu", { wrapSelector: "#orgSubtypeDropWrap" });

  document.getElementById("btnSpecOrgResetOrder").onclick = () => {
    markPresetModified();
    const comp = getActiveComponent();
    comp.orderedItems = null;
    renderSpecOrganizerPane(comp);
    renderOrganizationCard();
    refreshPromptPreview();
  };

  document.getElementById("btnSpecOrgAdd").onclick = () => {
    const topic = state.prompt.organizerChosenTopic;
    if (!topic) return;
    markPresetModified();
    const comp = getActiveComponent();
    const subtype = state.prompt.organizerChosenSubtype || null;
    if (!comp.orderedItems) {
      comp.orderedItems = [];
      comp.selections.filter(s => s.enabled).forEach(s => {
        if (s.desiredCount && s.desiredCount > 0) {
          const { primaryGroup } = getSpecModeStructure(s.topic);
          const counts = getActiveModeCounts(s, primaryGroup);
          let n = 0;
          Object.entries(counts).forEach(([sub, cnt]) => {
            for (let i = 0; i < cnt && n < s.desiredCount; i++) {
              n++;
              comp.orderedItems.push({ id: `${s.topic}_${n}_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: n, subtype: sub });
            }
          });
          while (n < s.desiredCount) {
            n++;
            comp.orderedItems.push({ id: `${s.topic}_${n}_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: n, subtype: null });
          }
        } else {
          comp.orderedItems.push({ id: `${s.topic}_1_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: 1, subtype: null });
        }
      });
    }

    const sel = findOrInitSelection(comp, topic);
    const currentCount = comp.orderedItems.filter(i => i.topic.toLowerCase() === topic.toLowerCase()).length;
    const nextCount = currentCount + 1;
    sel.enabled = true;
    sel.desiredCount = nextCount;
    const newId = `${topic}_${Date.now()}_${Math.random().toString(36).slice(2, 5)}`;
    comp.orderedItems.push({ id: newId, topic, instanceNumber: nextCount, subtype });
    syncSelectionsFromOrderedItems(comp, topic);
    state.lastAddedSpecOrgId = newId;
    renderSpecOrganizerPane(comp);
    renderOrganizationCard();
    refreshPromptPreview();
  };

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

  document.getElementById("notePopoverText").oninput = e => {
    if (!state.activeNoteTarget) return;
    markPresetModified();
    state.activeNoteTarget.sel.notes = e.target.value;
    schedulePromptPreview(120);
  };
  document.getElementById("btnCloseNotePopover").onclick = () => {
    if (state.activeNoteTarget) renderSpecSelectorPane(state.activeNoteTarget.comp);
    closeNotePopover();
  };

  document.getElementById("btnConfirmModalCancel").onclick = closeConfirmModal;
  document.getElementById("btnConfirmModalProceed").onclick = () => {
    if (state.activeConfirmAction) state.activeConfirmAction();
  };
  document.getElementById("confirmAgentsModal").addEventListener("click", e => {
    if (e.target === e.currentTarget) closeConfirmModal();
  });

  // Minimal WebApp Viewer
  function closeViewerSubmenus() {
    document.getElementById("recentSubmenu").classList.add("hidden");
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  }

  document.getElementById("webAppFrame").addEventListener("load", () => {
    const loadTok = state.viewerLoadToken;
    applyViewerThemeOverride();
    clearTimeout(state.viewerLoadFadeTimer);
    state.viewerLoadFadeTimer = setTimeout(() => hideViewerLoadingOverlay(loadTok), 300);
  });

  window.addEventListener("message", ev => {
    if (!ev.origin || !state.viewerOrigin || ev.origin !== state.viewerOrigin) return;
    if (ev.data && ev.data.type === "mby-theme-applied") {
      hideViewerLoadingOverlay(state.viewerLoadToken);
    }
  });

  registerPopover("notePopover", {
    wrapSelector: "#notePopover, .pencil-btn",
    onClose: () => {
      const comp = state.activeNoteTarget?.comp;
      state.activeNoteTarget = null;
      if (comp) renderSpecSelectorPane(comp);
    }
  });
  registerPopover("distPopover", {
    wrapSelector: "#distPopover, .dist-trigger-btn",
    onClose: () => { state.activeDistTarget = null; }
  });
  registerPopover("recentSubmenu", { wrapSelector: "#viewerFabWrap" });
  registerPopover("activeWsSubmenu", { wrapSelector: "#viewerFabWrap" });

  document.getElementById("btnEmptyOpenWebApp").onclick = () => openWebAppPath(null);
  registerDropdown("btnViewerHelp", "viewerHelpPopover", { wrapSelector: "#viewerFabWrap" });
  registerDropdown("btnViewerUpTrigger", "viewerUpMenu", {
    wrapSelector: "#viewerFabWrap",
    beforeOpen: refreshViewerDiscovery
  });
  document.getElementById("menuOpenWebAppFolder").addEventListener("mouseenter", closeViewerSubmenus);
  document.getElementById("menuOpenWebAppFolder").onclick = () => {
    closeViewerMenus();
    openWebAppPath(null);
  };
  document.getElementById("menuToggleOverrideTheme").addEventListener("mouseenter", closeViewerSubmenus);
  document.getElementById("menuToggleOverrideTheme").onclick = async e => {
    e.stopPropagation();
    const nextVal = !(state.settings.overrideWebAppTheme !== false);
    state.settings.overrideWebAppTheme = nextVal;
    syncOverrideThemeUi();
    applyViewerThemeOverride();
    const res = await api("PUT", "/api/settings/override-theme", { overrideWebAppTheme: nextVal });
    if (res.ok && res.data) {
      state.settings = res.data;
      syncOverrideThemeUi();
      applyViewerThemeOverride();
    } else if (!res.ok) {
      toast(res.data?.error?.description || "Failed to update theme override setting.", "err");
    }
  };
  document.getElementById("btnSubRecent").addEventListener("mouseenter", () => {
    document.getElementById("recentSubmenu").classList.remove("hidden");
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  });
  document.getElementById("btnSubRecent").onclick = e => {
    e.stopPropagation();
    document.getElementById("recentSubmenu").classList.toggle("hidden");
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  };
  document.getElementById("recentSubmenuWrap").addEventListener("mouseleave", () => {
    document.getElementById("recentSubmenu").classList.add("hidden");
  });
  document.getElementById("btnSubActiveWs").addEventListener("mouseenter", () => {
    document.getElementById("activeWsSubmenu").classList.remove("hidden");
    document.getElementById("recentSubmenu").classList.add("hidden");
  });
  document.getElementById("btnSubActiveWs").onclick = e => {
    e.stopPropagation();
    document.getElementById("activeWsSubmenu").classList.toggle("hidden");
    document.getElementById("recentSubmenu").classList.add("hidden");
  };
  document.getElementById("activeWsSubmenuWrap").addEventListener("mouseleave", () => {
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  });
  document.getElementById("viewerUpMenu").addEventListener("mouseleave", closeViewerSubmenus);

  // Global outside-click & Escape key overlay dismissal
  document.addEventListener("click", e => {
    const insideMenu = overlayRegistry.some(entry => entry.wrapSelector && e.target.closest(entry.wrapSelector));
    if (!insideMenu) {
      closeAllDropdowns();
    }
  });

  document.addEventListener("keydown", e => {
    if (e.key === "Escape") {
      closeAllDropdowns();
      if (!document.getElementById("confirmAgentsModal").classList.contains("hidden")) {
        closeConfirmModal();
      }
      if (!document.getElementById("inputPromptModal").classList.contains("hidden")) {
        closeInputPromptModal(null);
      }
    }
  });

  // Full-Page Settings Navigation & Actions
  document.getElementById("btnOpenSettings").onclick = () => {
    if (state.currentView === "settings") {
      switchView(state.lastNonSettingsView || "setup");
    } else {
      switchView("settings");
    }
  };
  document.getElementById("btnSettingsBack").onclick = () => {
    switchView(state.lastNonSettingsView || "setup");
  };

  const btnSaveSettings = document.getElementById("btnSaveSettings");
  btnSaveSettings.onclick = withButtonBusy(btnSaveSettings, "Saving...", async () => {
    const payload = {
      themeMode: document.getElementById("setThemeMode").value,
      defaultBuildOutputDir: document.getElementById("setDefaultBuildDir").value,
      mbyExecutableOverride: document.getElementById("setMbyOverride").value,
      melomgExecutableOverride: document.getElementById("setMelomgOverride").value,
      selfUpdateRepoOwner: document.getElementById("setUpdateOwner").value,
      selfUpdateRepoName: document.getElementById("setUpdateRepo").value
    };
    const res = await api("PUT", "/api/settings", payload);
    if (res.ok) {
      state.settings = res.data;
      applyTheme(state.settings.themeMode);
      applyViewerThemeOverride();
      await refreshApps(false);
      await loadSpecs(true);
      await refreshPromptPreview();
      toast("Settings saved.", "ok");
    } else {
      toast(res.data?.error?.description || "Failed to save settings.", "err");
    }
  });

  document.getElementById("btnOpenAppsDir").onclick = () => api("POST", "/api/workspace/open-folder", { path: state.appsDirectory });

  document.getElementById("btnCheckSelfUpdate").onclick = async () => {
    const statusText = document.getElementById("selfUpdateStatusText");
    const installBtn = document.getElementById("btnInstallSelfUpdate");
    statusText.textContent = "Checking GitHub Releases...";
    installBtn.classList.add("hidden");
    const res = await api("POST", "/api/self-update/check", {});
    if (res.ok) {
      statusText.textContent = res.data.statusMessage;
      installBtn.classList.toggle("hidden", !res.data.updateAvailable);
      if (res.data.updateAvailable) {
        addNotification("App Update Available", res.data.statusMessage, "info");
      }
    } else {
      statusText.textContent = res.data?.error?.description || "Update check failed.";
    }
  };

  document.getElementById("btnInstallSelfUpdate").onclick = async () => {
    const installBtn = document.getElementById("btnInstallSelfUpdate");
    const wrap = document.getElementById("selfUpdateProgressWrap");
    const bar = document.getElementById("selfUpdateProgressBar");
    const statusText = document.getElementById("selfUpdateStatusText");
    installBtn.disabled = true;
    wrap.classList.remove("hidden");
    const poller = setInterval(async () => {
      const st = await api("GET", "/api/self-update/status");
      if (st.ok && st.data.status) {
        statusText.textContent = st.data.status.message;
        if (st.data.status.percent != null) bar.style.width = `${st.data.status.percent}%`;
      }
    }, 300);

    try {
      const res = await api("POST", "/api/self-update/install", {});
      if (!res.ok) toast(res.data?.error?.description || "Self-update failed.", "err");
    } finally {
      clearInterval(poller);
      installBtn.disabled = false;
    }
  };

  // Context-Aware External File/Folder Drag-and-Drop + Prompt Studio Auto-Scroll
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

  // Dynamic Chrome Offset Observer
  window.addEventListener("resize", updateChromeOffset);
  const chromeEl = document.getElementById("topChromeOverlay");
  if (chromeEl && typeof ResizeObserver !== "undefined") {
    new ResizeObserver(updateChromeOffset).observe(chromeEl);
  }

  bootstrap();
})();
