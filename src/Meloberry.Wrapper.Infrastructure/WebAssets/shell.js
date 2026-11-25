(() => {
  const token = window.__MBY_TOKEN__ || "";
  try { delete window.__MBY_TOKEN__; } catch { window.__MBY_TOKEN__ = ""; }

  const DOWNLOAD_SVG = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
  const PENCIL_SVG = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 1 1 3 3L7 19l-4 1 1-4Z"/></svg>';

  const SPEC_ICONS = {
    text: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 6h16M4 12h12M4 18h14"/></svg>',
    image: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="m21 15-5-5L5 21"/></svg>',
    callout: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M9 18h6M10 22h4M12 2a7 7 0 0 0-4 12.7c.7.6 1 1.3 1 2.3h6c0-1 .3-1.7 1-2.3A7 7 0 0 0 12 2Z"/></svg>',
    table: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M3 15h18M9 3v18"/></svg>',
    link: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>',
    mcq: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><circle cx="6" cy="7" r="2"/><path d="M11 7h9"/><circle cx="6" cy="12" r="2" fill="currentColor"/><path d="M11 12h9"/><circle cx="6" cy="17" r="2"/><path d="M11 17h9"/></svg>',
    flashcard: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="5" width="14" height="14" rx="2"/><path d="M7 3h12a2 2 0 0 1 2 2v12"/></svg>',
    "fill-blank": '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 17h6M14 17h6"/><rect x="8" y="6" width="8" height="6" rx="1"/></svg>',
    "assertion-reason": '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 7h10l-3-3M20 17H10l3 3"/></svg>',
    "spot-error": '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 8v5M12 16h.01"/></svg>',
    diff: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="8" height="16" rx="1"/><rect x="13" y="4" width="8" height="16" rx="1"/></svg>',
    mechanism: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><circle cx="5" cy="12" r="2"/><path d="M7 12h4"/><circle cx="13" cy="7" r="2"/><circle cx="13" cy="17" r="2"/><path d="M15 7l4 5-4 5"/></svg>',
    relabel: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0Z"/><circle cx="12" cy="10" r="3"/></svg>'
  };

  function getSpecIcon(topic) {
    return SPEC_ICONS[(topic || "").toLowerCase()] || SPEC_ICONS.text;
  }

  const PAPER_THEME_FALLBACK_CSS = `
    html[data-theme="paper"] {
      --bg: #f7f3e9;
      --surface: #fffdf8;
      --text: #2c241b;
      --muted: #6f6254;
      --accent: #9a3412;
      --accent-soft: #ffedd5;
      --select: #1d4ed8;
      --select-soft: #e0e7ff;
      --table-hideable-bg: #eef2e6;
      --table-hideable-color: #3f6212;
      --border: #e7dcc8;
      --callout-pearl-bg: #fef7e0;
      --callout-clinical-bg: #eef7fb;
      --callout-warning-bg: #fdf0ed;
    }
  `;

  const VIEW_ORDER = { setup: 0, prompt: 1, viewer: 2 };

  const state = {
    metadata: { version: "1.0.0", selfUpdateRepoOwner: "doc-shashank", selfUpdateRepoName: "meloberry-pc" },
    appsDirectory: "",
    viewerOrigin: "",
    settings: { themeMode: "berry", overrideWebAppTheme: true, recentWebApps: [] },
    apps: [],
    appUpdateChecks: {},
    outdatedTools: [],
    resetScope: "executablesOnly",
    conflictPolicy: "skipExisting",
    currentView: "setup",
    selectedAppNames: new Set(),
    stagedItems: [],
    specs: [],
    isLiveSpecs: false,
    presets: [],
    activePresetName: "Full Chapter Study Session",
    ecosystemRepos: [],
    agentsMdStatus: null,
    discoveredWebApps: [],
    discoveredMbyProjects: [],
    notifications: [],
    eventSource: null,
    previewSeq: 0,
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
      organizerChosenTopic: null
    },
    composedPrompt: { markdown: "", plainText: "", characterCount: 0, approximateTokens: 0 },
    activeConfirmAction: null,
    activeNoteTarget: null,
    hoverTimer: null,
    dragOrgIndex: null,
    dragSpecOrgIndex: null,
    lastAddedOrgId: null,
    lastAddedSpecOrgId: null,
    lastAddedSelectorTopic: null
  };

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

  function toast(msg, kind = "info") {
    const box = document.getElementById("toastContainer");
    const el = document.createElement("div");
    el.className = `toast ${kind}`;
    el.textContent = msg;
    box.appendChild(el);
    setTimeout(() => el.remove(), 4200);
    if (kind === "err") {
      addNotification("Error", msg, "err");
    }
  }

  function escapeHtml(str) {
    return String(str || "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));
  }

  function switchView(viewName) {
    const prev = state.currentView;
    state.currentView = viewName;
    const dirForward = (VIEW_ORDER[viewName] ?? 0) >= (VIEW_ORDER[prev] ?? 0);

    const map = { setup: "viewSetup", prompt: "viewPrompt", viewer: "viewViewer" };
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

    // Pull-up Top Chrome Overlay on WebApp Viewer without reflowing .views-viewport
    document.getElementById("topChromeOverlay").classList.toggle("pulled-up", viewName === "viewer");
    updateChromeOffset();

    if (viewName === "viewer") {
      refreshViewerDiscovery();
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
        fallbackCss: PAPER_THEME_FALLBACK_CSS
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
          restoreActiveWorkspaceUi();
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
    const res = await api("GET", "/api/bootstrap");
    if (!res.ok) {
      toast("Failed to initialize backend.", "err");
      return;
    }

    const d = res.data;
    state.metadata = d.metadata || state.metadata;
    state.appsDirectory = d.appsDirectory || "";
    state.viewerOrigin = d.viewerOrigin || "";
    state.settings = d.settings || {};
    if (state.settings.overrideWebAppTheme === undefined) {
      state.settings.overrideWebAppTheme = true;
    }
    state.apps = d.apps || [];
    state.outdatedTools = d.outdatedTools || [];
    state.ecosystemRepos = d.ecosystemRepos || [];
    state.agentsMdStatus = d.agentsMdStatus || null;

    document.getElementById("versionBadge").textContent = `v${state.metadata.version}`;
    document.getElementById("settingsMetaLine").textContent = `${state.metadata.productName} v${state.metadata.version}`;

    applyTheme(state.settings.themeMode || "berry");
    syncOverrideThemeUi();
    restoreActiveWorkspaceUi();
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

  function restoreActiveWorkspaceUi() {
    const aws = state.settings.activeWorkspace;
    const dot = document.getElementById("wsStatusDot");
    const label = document.getElementById("activeWsLabel");
    const agentsPill = document.getElementById("agentsReadyPill");

    if (aws && aws.workspacePath) {
      document.getElementById("setupWsPath").value = aws.workspacePath;
      label.textContent = formatTruncatedWorkspace(aws.workspacePath);
      label.title = aws.workspacePath;
      dot.classList.toggle("ready", !!aws.isInitialized);
      state.selectedAppNames = new Set(aws.selectedAppFileNames || []);
      state.stagedItems = [...(aws.stagedItems || [])];

      const agentsExists = !!(state.agentsMdStatus && state.agentsMdStatus.exists);
      state.prompt.agentsMdExported = agentsExists;
      agentsPill.classList.toggle("hidden", !agentsExists);
      document.getElementById("agentsExportBanner").classList.toggle("hidden", !agentsExists);
    } else {
      label.textContent = "No Active Workspace";
      label.title = "";
      dot.classList.remove("ready");
      agentsPill.classList.add("hidden");
      document.getElementById("agentsExportBanner").classList.add("hidden");
      state.apps.filter(a => a.isInstalled && (a.kind === "mby" || a.kind === "melomg"))
        .forEach(a => state.selectedAppNames.add(a.fileName));
    }

    pruneSelectedAppNames();
    renderAppsCart();
    renderStagedItems();
    refreshPromptOptionsAvailability();
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
      chip.title = p;
      chip.addEventListener("mouseenter", () => {
        clearTimeout(state.hoverTimer);
        state.hoverTimer = setTimeout(() => showPathTooltip(chip, p), 1500);
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
      restoreActiveWorkspaceUi();
      renderRecentWorkspaces();
      renderOutdatedBanner();
      await previewPlan();
    } else {
      toast(res.data?.error?.description || "Invalid workspace path.", "err");
    }
  }

  async function refreshApps(render = true) {
    const res = await api("GET", "/api/apps");
    if (res.ok) {
      state.apps = res.data.apps || [];
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
          dlBtn.innerHTML = `${DOWNLOAD_SVG}<span>via GitHub</span>`;
          dlBtn.onclick = () => downloadToolFromGitHub(app.repoKey, dlBtn, progWrap, progBar, statusLine);
          actions.appendChild(dlBtn);
        } else {
          const checkInfo = state.appUpdateChecks[app.repoKey];
          const updBtn = document.createElement("button");
          updBtn.className = "btn btn-xs btn-primary";
          if (checkInfo && checkInfo.updateAvailable) {
            updBtn.innerHTML = `${DOWNLOAD_SVG}<span>v${escapeHtml(checkInfo.latestVersion || "latest")}</span>`;
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

    const res = await api("POST", "/api/apps/download", { tool: toolKey });
    clearInterval(poller);
    btn.disabled = false;

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
        state.hoverTimer = setTimeout(() => showPathTooltip(nameSpan, item.sourcePath), 1500);
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
    const top = Math.max(8, rect.top - tipRect.height - 8);
    tip.style.left = `${left}px`;
    tip.style.top = `${top}px`;
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
    restoreActiveWorkspaceUi();
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
      toast("No Meloberry executables" + (state.resetScope === "executablesAndAgentsMd" ? " or AGENTS.md" : "") + " found.", "info");
      return;
    }

    const items = [...plan.executablesToRemove];
    if (plan.agentsMdToRemove) items.push(plan.agentsMdToRemove);

    document.getElementById("confirmModalTitle").textContent = "Confirm Workspace Reset";
    document.getElementById("confirmModalBody").textContent =
      `Remove ${items.length} file(s) from "${plan.workspacePath}" (recursively across subfolders)?`;
    const pre = document.getElementById("agentsPreviewModalCode");
    pre.textContent = items.join("\n");
    pre.classList.remove("hidden");

    const proceed = document.getElementById("btnConfirmModalProceed");
    proceed.textContent = "Delete Listed Files";
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
      if (execRes.data.resetResult.removedAgentsMd) {
        state.agentsMdStatus = null;
        state.prompt.agentsMdExported = false;
      }

      const outRes = await api("GET", `/api/workspace/outdated?workspacePath=${encodeURIComponent(plan.workspacePath)}`);
      state.outdatedTools = outRes.ok ? (outRes.data.outdatedTools || []) : [];

      await refreshApps(false);
      restoreActiveWorkspaceUi();
      renderOutdatedBanner();
      await refreshPromptPreview();
      toast(`Reset removed ${execRes.data.resetResult.removedRelativePaths.length} executable(s)${execRes.data.resetResult.removedAgentsMd ? " + AGENTS.md" : ""}.`, "ok");
    };

    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  }

  // ---- Prompt Studio: Presets, Organization, SVG Spec Dropdowns ----

  async function loadSpecs(forceRefresh) {
    const res = await api("GET", `/api/specs?refresh=${forceRefresh ? "true" : "false"}`);
    if (res.ok) {
      state.specs = res.data.specs || [];
      state.isLiveSpecs = !!res.data.isLive;
      const statusEl = document.getElementById("specSyncStatus");
      if (state.isLiveSpecs) {
        statusEl.textContent = "Synced";
        statusEl.className = "sync-status-text live";
      } else {
        statusEl.textContent = "Meloberry CLI not installed, failed to sync";
        statusEl.className = "sync-status-text";
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
        modes: [...(s.modes || [])]
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
        modes: [...(s.modes || [])]
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
          state.prompt.components.splice(idx, 1);
          ensureComponentsForContainer();
          renderOrganizationCard();
          renderPromptStudioSpecArea();
          refreshPromptPreview();
        };
        top.appendChild(rm);
      }

      const countSpan = document.createElement("div");
      countSpan.className = "muted";
      const enabledCount = comp.selections.filter(s => s.enabled).length;
      countSpan.textContent = `${enabledCount} specs`;

      box.append(top, countSpan);
      box.onclick = () => {
        state.prompt.selectedComponentId = comp.id;
        state.prompt.organizerChosenTopic = null;
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

  function findOrInitSelection(comp, topic) {
    let sel = comp.selections.find(s => s.topic.toLowerCase() === topic.toLowerCase());
    if (!sel) {
      sel = { topic, enabled: false, desiredCount: null, notes: "", modes: [] };
      comp.selections.push(sel);
    }
    return sel;
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
        const target = findOrInitSelection(comp, spec.topic);
        const wasEnabled = !!target.enabled;
        target.enabled = true;
        if (!target.desiredCount || target.desiredCount <= 0) target.desiredCount = 1;
        if (!wasEnabled) state.lastAddedSelectorTopic = spec.topic;
        syncOrderedItemsFromSelections(comp);
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

      const lbl = document.createElement("div");
      lbl.className = "row gap-8";
      lbl.title = spec.summary || "";
      lbl.innerHTML = `<span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><code>${escapeHtml(spec.topic)}</code>`;

      const cnt = document.createElement("input");
      cnt.type = "number";
      cnt.min = "1";
      cnt.max = "99";
      cnt.className = "input shop-count";
      cnt.placeholder = "Qty";
      cnt.value = sel.desiredCount || "";
      cnt.oninput = () => {
        const raw = cnt.value.trim();
        if (raw === "") {
          sel.desiredCount = null;
          refreshPromptPreview();
          return;
        }
        const parsed = parseInt(raw, 10);
        if (!Number.isFinite(parsed) || parsed <= 0) {
          sel.desiredCount = null;
          refreshPromptPreview();
          return;
        }
        const val = Math.min(99, parsed);
        if (val !== parsed) cnt.value = String(val);
        sel.desiredCount = val;
        syncOrderedItemsFromSelections(comp);
        refreshPromptPreview();
      };

      const modeCell = document.createElement("div");
      modeCell.className = "mode-cell";
      const groups = spec.modeGroups || [];
      if (!groups.length) {
        const none = document.createElement("span");
        none.className = "muted";
        none.textContent = spec.summary || "";
        modeCell.appendChild(none);
      } else {
        groups.forEach(g => {
          const gLabel = document.createElement("span");
          gLabel.className = "mode-group-label";
          gLabel.textContent = `${g.label}:`;
          modeCell.appendChild(gLabel);

          g.options.forEach(opt => {
            const btn = document.createElement("button");
            btn.type = "button";
            const active = (sel.modes || []).includes(opt.key);
            btn.className = `mode-chip ${active ? "active" : ""}`;
            btn.textContent = opt.label;
            btn.onclick = () => {
              sel.modes = sel.modes || [];
              if (g.multiSelect) {
                const i = sel.modes.indexOf(opt.key);
                if (i >= 0) sel.modes.splice(i, 1);
                else {
                  sel.modes.push(opt.key);
                  if (spec.topic.toLowerCase() === "assertion-reason" && opt.key === "both-false") {
                    sel.modes = sel.modes.filter(m => m !== "4opt");
                    if (!sel.modes.includes("5opt")) sel.modes.push("5opt");
                  }
                }
              } else {
                const siblingKeys = g.options.map(o => o.key);
                const wasActive = sel.modes.includes(opt.key);
                sel.modes = sel.modes.filter(m => !siblingKeys.includes(m));
                if (!wasActive) sel.modes.push(opt.key);
                if (spec.topic.toLowerCase() === "assertion-reason" && opt.key === "4opt" && !wasActive) {
                  sel.modes = sel.modes.filter(m => m !== "both-false");
                }
              }
              renderSpecSelectorPane(comp);
              refreshPromptPreview();
            };
            modeCell.appendChild(btn);
          });
        });
      }

      const pencil = document.createElement("button");
      pencil.type = "button";
      pencil.className = `pencil-btn ${sel.notes ? "has-note" : ""}`;
      pencil.title = sel.notes ? `Note: ${sel.notes}` : "Add custom instructions";
      pencil.innerHTML = PENCIL_SVG;
      pencil.onclick = e => {
        e.stopPropagation();
        openNotePopover(pencil, comp, sel);
      };

      const rm = document.createElement("button");
      rm.type = "button";
      rm.className = "drag-remove-btn";
      rm.textContent = "\u00d7";
      rm.title = `Remove ${spec.topic}`;
      rm.onclick = () => {
        sel.enabled = false;
        sel.desiredCount = null;
        syncOrderedItemsFromSelections(comp);
        renderSpecSelectorPane(comp);
        renderOrganizationCard();
        refreshPromptPreview();
      };

      row.append(lbl, cnt, modeCell, pencil, rm);
      root.appendChild(row);
    });
    state.lastAddedSelectorTopic = null;
  }

  function syncOrderedItemsFromSelections(comp) {
    const desiredCounts = {};
    comp.selections.filter(s => s.enabled).forEach(s => {
      desiredCounts[s.topic] = Math.max(1, s.desiredCount || 1);
    });

    const existing = comp.orderedItems ? [...comp.orderedItems] : [];
    const currentCounts = {};
    const kept = [];

    existing.forEach(inst => {
      const targetMax = desiredCounts[inst.topic] || 0;
      const seen = currentCounts[inst.topic] || 0;
      if (seen < targetMax) {
        currentCounts[inst.topic] = seen + 1;
        kept.push({ ...inst, instanceNumber: seen + 1 });
      }
    });

    Object.entries(desiredCounts).forEach(([topic, count]) => {
      let seen = currentCounts[topic] || 0;
      while (seen < count) {
        seen++;
        kept.push({ id: `${topic}_${seen}_${Math.random().toString(36).slice(2, 6)}`, topic, instanceNumber: seen });
      }
    });

    comp.orderedItems = kept;
  }

  function syncSelectionsFromOrderedItems(comp) {
    const counts = {};
    (comp.orderedItems || []).forEach(inst => {
      counts[inst.topic] = (counts[inst.topic] || 0) + 1;
      inst.instanceNumber = counts[inst.topic];
    });

    comp.selections.forEach(sel => {
      const c = counts[sel.topic] || 0;
      if (c > 0) {
        sel.enabled = true;
        sel.desiredCount = c;
      } else {
        sel.enabled = false;
        sel.desiredCount = null;
      }
    });
  }

  function renderSpecOrganizerPane(comp) {
    syncOrderedItemsFromSelections(comp);
    const targetCategory = comp.kind === "article" ? "block" : "question";
    const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

    // Preserve chosen spec type across additions!
    if (!state.prompt.organizerChosenTopic || !allowedSpecs.some(s => s.topic === state.prompt.organizerChosenTopic)) {
      state.prompt.organizerChosenTopic = allowedSpecs[0]?.topic || "";
    }

    const chosen = state.prompt.organizerChosenTopic;
    document.getElementById("orgSpecChosenDisplay").innerHTML =
      `<span class="spec-icon-wrap">${getSpecIcon(chosen)}</span><span>${escapeHtml(chosen)}</span>`;

    const menu = document.getElementById("orgSpecMenu");
    menu.innerHTML = "";
    allowedSpecs.forEach(spec => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = `dropdown-item dropdown-item-row ${spec.topic === chosen ? "active" : ""}`;
      item.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span></span>`;
      item.onclick = () => {
        state.prompt.organizerChosenTopic = spec.topic;
        menu.classList.add("hidden");
        menu.querySelectorAll(".dropdown-item").forEach(b => b.classList.toggle("active", b === item));
        document.getElementById("orgSpecChosenDisplay").innerHTML =
          `<span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span>`;
      };
      menu.appendChild(item);
    });

    const track = document.getElementById("specOrgTrack");
    track.innerHTML = "";
    const items = comp.orderedItems || [];
    if (!items.length) {
      state.lastAddedSpecOrgId = null;
      track.innerHTML = '<div class="muted">No items selected yet. Use + Add or switch to Selector.</div>';
      return;
    }

    items.forEach((inst, idx) => {
      const box = document.createElement("div");
      const isJustAdded = state.lastAddedSpecOrgId && inst.id === state.lastAddedSpecOrgId;
      box.className = `drag-box spec-org-box${isJustAdded ? " animate-add" : ""}`;
      box.title = `${inst.topic} #${inst.instanceNumber}`;
      box.draggable = items.length > 1;

      const top = document.createElement("div");
      top.className = "drag-box-top";
      const badge = document.createElement("span");
      badge.className = "drag-badge";
      badge.textContent = `#${inst.instanceNumber}`;

      const rm = document.createElement("button");
      rm.className = "drag-remove-btn";
      rm.textContent = "\u00d7";
      rm.onclick = e => {
        e.stopPropagation();
        comp.orderedItems.splice(idx, 1);
        syncSelectionsFromOrderedItems(comp);
        renderSpecOrganizerPane(comp);
        renderOrganizationCard();
        refreshPromptPreview();
      };
      top.append(badge, rm);

      const sub = document.createElement("div");
      sub.className = "drag-box-sub";
      sub.innerHTML = `<span class="spec-icon-wrap spec-icon-org">${getSpecIcon(inst.topic)}</span>`;

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
        const [moved] = comp.orderedItems.splice(state.dragSpecOrgIndex, 1);
        comp.orderedItems.splice(idx, 0, moved);
        syncSelectionsFromOrderedItems(comp);
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
    const pop = document.getElementById("notePopover");
    const ta = document.getElementById("notePopoverText");
    document.getElementById("notePopoverTitle").innerHTML = `Notes for <code>${escapeHtml(sel.topic)}</code>`;
    ta.value = sel.notes || "";
    state.activeNoteTarget = { comp, sel };

    pop.classList.remove("hidden");
    const rect = anchorBtn.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - 332, rect.right - 320));
    const top = Math.min(window.innerHeight - 165, rect.bottom + 6);
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
      selections: c.selections.filter(s => s.enabled).map(s => ({
        topic: s.topic,
        enabled: true,
        desiredCount: s.desiredCount || null,
        notes: s.notes || null,
        modes: s.modes && s.modes.length ? s.modes : null
      })),
      orderedItems: c.orderedItems && c.orderedItems.length ? c.orderedItems : null
    }));

    const flatSelections = activeComps.flatMap(c => c.selections);
    const wsMby = state.apps.find(a => a.kind === "mby");
    const wsMel = state.apps.find(a => a.kind === "melomg");
    const mbyOverride = state.settings.mbyExecutableOverride && state.settings.mbyExecutableOverride.trim();
    const melOverride = state.settings.melomgExecutableOverride && state.settings.melomgExecutableOverride.trim();

    const resolvedMbyPath = mbyOverride
      || (wsPath && wsMby && wsMby.isPresentInWorkspace ? `${wsPath}\\${wsMby.workspaceFileName || wsMby.fileName}` : (wsMby && wsMby.isInstalled ? wsMby.fullPath : null));
    const resolvedMelomgPath = melOverride
      || (wsPath && wsMel && wsMel.isPresentInWorkspace ? `${wsPath}\\${wsMel.workspaceFileName || wsMel.fileName}` : (wsMel && wsMel.isInstalled ? wsMel.fullPath : null));

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
      mbyExecutablePath: resolvedMbyPath,
      melomgExecutablePath: resolvedMelomgPath,
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
    const hasMbyInstalled = state.apps.some(a => a.kind === "mby" && a.isInstalled)
      || !!(state.settings.mbyExecutableOverride && state.settings.mbyExecutableOverride.trim());
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

  async function refreshPromptPreview() {
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

  async function readyWorkspace(overwriteConfirmed = false) {
    const draft = buildPromptDraftPayload();
    if (!draft.workingDirectory) {
      toast("Select & Initialize an Active Workspace first.", "err");
      switchView("setup");
      return;
    }

    const wasUninitialized = !(state.settings.activeWorkspace && state.settings.activeWorkspace.isInitialized);

    if (!overwriteConfirmed) {
      const statusRes = await api("GET", `/api/workspace/agents-md/status?workspacePath=${encodeURIComponent(draft.workingDirectory)}`);
      if (statusRes.ok && statusRes.data.exists) {
        openOverwriteConfirmModal(statusRes.data.agentsMdPath);
        return;
      }
    }

    const res = await api("POST", "/api/workspace/ready", { draft, overwriteConfirmed });
    if (res.status === 409) {
      openOverwriteConfirmModal(draft.workingDirectory + "\\AGENTS.md");
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
      preview: res.data.markdownPreview
    };
    document.getElementById("agentsReadyPill").classList.remove("hidden");
    document.getElementById("agentsExportBanner").classList.remove("hidden");
    document.getElementById("agentsBannerDetail").textContent =
      `AGENTS.md Ready (${res.data.bytesWritten} B \u00b7 ~${res.data.approximateTokens} tok)`;

    await refreshPromptPreview();
    if (wasUninitialized) {
      toast("Workspace might not have been initialized", "warn");
    }
    if (Array.isArray(res.data.warnings) && res.data.warnings.length > 0) {
      res.data.warnings.forEach(w => toast(w, "warn"));
    }
    toast(`Exported AGENTS.md (~${res.data.approximateTokens} tokens).`, "ok");
  }

  function openOverwriteConfirmModal(agentsPath) {
    document.getElementById("confirmModalTitle").textContent = "Overwrite Existing AGENTS.md?";
    document.getElementById("confirmModalBody").textContent =
      `AGENTS.md already exists at "${agentsPath}". Overwrite it with the updated workspace playbook and targeted schemas?`;
    document.getElementById("agentsPreviewModalCode").classList.add("hidden");
    const proceed = document.getElementById("btnConfirmModalProceed");
    proceed.textContent = "Overwrite AGENTS.md";
    proceed.classList.remove("hidden");
    state.activeConfirmAction = async () => {
      closeConfirmModal();
      await readyWorkspace(true);
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

  function renderViewerMenus() {
    const recentMenu = document.getElementById("recentSubmenu");
    recentMenu.innerHTML = "";
    const topFive = (state.settings.recentWebApps || []).slice(0, 5);
    if (!topFive.length) {
      recentMenu.innerHTML = '<div class="muted" style="padding:6px 8px">No recent WebApps</div>';
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
        recentMenu.appendChild(row);
      });
    }

    const wsWrap = document.getElementById("activeWsSubmenuWrap");
    const wsMenu = document.getElementById("activeWsSubmenu");
    const hasWsItems = state.discoveredWebApps.length > 0 || state.discoveredMbyProjects.length > 0;
    wsWrap.classList.toggle("hidden", !hasWsItems);
    wsMenu.innerHTML = "";

    if (state.discoveredWebApps.length > 0) {
      const lbl = document.createElement("div");
      lbl.className = "submenu-label";
      lbl.textContent = "Compiled WebApps";
      wsMenu.appendChild(lbl);
      state.discoveredWebApps.forEach(w => {
        const row = document.createElement("div");
        row.className = "submenu-item-row";
        const link = document.createElement("button");
        link.className = "submenu-link";
        link.textContent = w.name;
        link.onclick = () => { closeViewerMenus(); openWebAppPath(w.path); };
        row.appendChild(link);
        wsMenu.appendChild(row);
      });
    }

    if (state.discoveredMbyProjects.length > 0) {
      const lbl = document.createElement("div");
      lbl.className = "submenu-label";
      lbl.textContent = "mby Projects";
      wsMenu.appendChild(lbl);
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
        wsMenu.appendChild(row);
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
  document.getElementById("btnNotifications").onclick = e => {
    e.stopPropagation();
    document.getElementById("notifPopover").classList.toggle("hidden");
  };
  document.getElementById("btnNotifReadAll").onclick = () => {
    state.notifications.forEach(n => { n.unread = false; });
    renderNotifications();
  };
  document.getElementById("btnNotifClear").onclick = () => {
    state.notifications = [];
    renderNotifications();
  };

  // Workspace Action Strip controls
  document.getElementById("btnResetWorkspace").onclick = e => {
    e.stopPropagation();
    document.getElementById("resetMenu").classList.toggle("hidden");
  };
  document.querySelectorAll("#resetMenu .dropdown-item").forEach(btn => {
    btn.onclick = () => {
      state.resetScope = btn.dataset.scope;
      document.querySelectorAll("#resetMenu .dropdown-item").forEach(b => b.classList.toggle("active", b === btn));
      document.getElementById("resetMenu").classList.add("hidden");
      triggerResetPrompt();
    };
  });

  document.getElementById("btnReadyWorkspace").onclick = () => readyWorkspace(false);

  document.getElementById("btnPromptOptions").onclick = e => {
    e.stopPropagation();
    refreshPromptOptionsAvailability();
    document.getElementById("promptOptionsMenu").classList.toggle("hidden");
  };

  document.getElementById("optCopyPlainText").onclick = async () => {
    document.getElementById("promptOptionsMenu").classList.add("hidden");
    await navigator.clipboard.writeText(state.composedPrompt.plainText || state.composedPrompt.markdown || "");
    toast("Copied prompt as plain text.", "ok");
  };
  document.getElementById("optCopyMarkdown").onclick = async () => {
    document.getElementById("promptOptionsMenu").classList.add("hidden");
    await navigator.clipboard.writeText(state.composedPrompt.markdown || "");
    toast("Copied Markdown prompt.", "ok");
  };
  document.getElementById("optSaveMarkdown").onclick = async () => {
    document.getElementById("promptOptionsMenu").classList.add("hidden");
    const r = await api("POST", "/api/prompt/save", { markdown: state.composedPrompt.markdown });
    if (r.ok && !r.data.cancelled) toast(`Saved ${r.data}`, "ok");
    else if (!r.ok) toast(r.data?.error?.description || "Save failed.", "err");
  };
  document.getElementById("optExportRawJson").onclick = async () => {
    document.getElementById("promptOptionsMenu").classList.add("hidden");
    const rawRes = await api("POST", "/api/prompt/raw-json", buildPromptDraftPayload());
    if (!rawRes.ok) {
      toast(rawRes.data?.error?.description || "Failed to generate raw JSON.", "err");
      return;
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
  };
  document.getElementById("optGenerateTemplate").onclick = async () => {
    document.getElementById("promptOptionsMenu").classList.add("hidden");
    const genRes = await api("POST", "/api/prompt/generate-template", buildPromptDraftPayload());
    if (!genRes.ok) {
      toast(genRes.data?.error?.description || "Failed to generate template.", "err");
      return;
    }

    addNotification("Draft Template Created", `Session ${genRes.data.workspaceId} created from ${genRes.data.jsonPath}`, "info");
    toast(`Created draft workspace ${genRes.data.workspaceId}!`, "ok");
  };

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
    restoreActiveWorkspaceUi();
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

  document.getElementById("btnConflictDropdown").onclick = e => {
    e.stopPropagation();
    document.getElementById("conflictMenu").classList.toggle("hidden");
  };
  document.querySelectorAll("#conflictMenu .dropdown-item").forEach(item => {
    item.onclick = () => {
      state.conflictPolicy = item.dataset.policy;
      document.querySelectorAll("#conflictMenu .dropdown-item").forEach(b => b.classList.toggle("active", b === item));
      document.getElementById("conflictBtnLabel").textContent =
        state.conflictPolicy === "overwriteExisting" ? "Conflicts: Overwrite" : "Conflicts: Skip existing";
      document.getElementById("conflictMenu").classList.add("hidden");
    };
  });

  document.getElementById("btnInitializeWorkspace").onclick = () => initializeWorkspace();

  // Prompt Studio controls
  document.querySelectorAll("#containerTabs .seg-btn").forEach(btn => {
    btn.onclick = () => {
      const prev = state.prompt.container;
      const next = btn.dataset.container;
      if (prev === next) return;

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

  document.getElementById("btnPresetDropdown").onclick = e => {
    e.stopPropagation();
    document.getElementById("presetDropdownMenu").classList.toggle("hidden");
  };

  document.getElementById("btnSavePreset").onclick = async () => {
    const name = window.prompt("Name for custom preset:", "Custom Preset");
    if (!name) return;
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
    }
  };

  document.getElementById("btnAddOrgArticle").onclick = () => {
    const created = cloneComponent({ kind: "article", title: "New Article", selections: [] });
    state.prompt.components.push(created);
    state.prompt.selectedComponentId = created.id;
    state.lastAddedOrgId = created.id;
    renderOrganizationCard();
    renderPromptStudioSpecArea();
    refreshPromptPreview();
  };

  document.getElementById("btnAddOrgQuiz").onclick = () => {
    const created = cloneComponent({ kind: "quiz", title: "New Quiz", selections: [] });
    state.prompt.components.push(created);
    state.prompt.selectedComponentId = created.id;
    state.lastAddedOrgId = created.id;
    renderOrganizationCard();
    renderPromptStudioSpecArea();
    refreshPromptPreview();
  };

  document.getElementById("orgSelectedTitle").oninput = e => {
    const comp = getActiveComponent();
    comp.title = e.target.value;
    const activeBox = document.querySelector("#orgTrack .drag-box.active");
    if (activeBox) activeBox.title = comp.title || (comp.kind === "article" ? "Article" : "Quiz");
    refreshPromptPreview();
  };

  document.querySelectorAll("#specCardModeTabs .seg-btn").forEach(btn => {
    btn.onclick = () => {
      state.prompt.specCardMode = btn.dataset.specmode;
      document.querySelectorAll("#specCardModeTabs .seg-btn").forEach(b => b.classList.toggle("active", b === btn));
      renderPromptStudioSpecArea();
    };
  });

  document.getElementById("btnSelectorAddSpecDrop").onclick = e => {
    e.stopPropagation();
    document.getElementById("selectorSpecMenu").classList.toggle("hidden");
  };

  document.getElementById("btnOrgSpecDrop").onclick = e => {
    e.stopPropagation();
    document.getElementById("orgSpecMenu").classList.toggle("hidden");
  };

  document.getElementById("btnSpecOrgAdd").onclick = () => {
    const topic = state.prompt.organizerChosenTopic;
    if (!topic) return;
    const comp = getActiveComponent();
    const sel = findOrInitSelection(comp, topic);
    const currentCount = (comp.orderedItems || []).filter(i => i.topic.toLowerCase() === topic.toLowerCase()).length;
    const nextCount = currentCount + 1;
    sel.enabled = true;
    sel.desiredCount = nextCount;
    const newId = `${topic}_${Date.now()}_${Math.random().toString(36).slice(2, 5)}`;
    comp.orderedItems = comp.orderedItems || [];
    comp.orderedItems.push({ id: newId, topic, instanceNumber: nextCount });
    state.lastAddedSpecOrgId = newId;
    // Note: intentionally keeps state.prompt.organizerChosenTopic untouched across additions!
    renderSpecOrganizerPane(comp);
    renderOrganizationCard();
    refreshPromptPreview();
  };

  ["promptTitle", "promptDescription", "promptFormat", "promptSourceText", "chkUseMelomg"].forEach(id => {
    const el = document.getElementById(id);
    el.addEventListener("input", () => refreshPromptPreview());
    el.addEventListener("change", () => refreshPromptPreview());
  });

  document.getElementById("btnRefreshSpecs").onclick = () => loadSpecs(true);

  document.getElementById("btnAttachPromptFiles").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-files", { title: "Attach Files", allowMultiple: true });
    if (r.ok && !r.data.cancelled) {
      (r.data.paths || []).forEach(p => {
        state.prompt.extraAttachments.push({ filePath: p, kind: p.toLowerCase().endsWith(".pdf") ? "pdf" : "file" });
      });
      renderPromptAttachments();
      refreshPromptPreview();
    }
  };

  document.getElementById("notePopoverText").oninput = e => {
    if (!state.activeNoteTarget) return;
    state.activeNoteTarget.sel.notes = e.target.value;
    refreshPromptPreview();
  };
  document.getElementById("btnCloseNotePopover").onclick = () => {
    if (state.activeNoteTarget) renderSpecSelectorPane(state.activeNoteTarget.comp);
    closeNotePopover();
  };

  document.getElementById("btnBannerOpenWs").onclick = () => api("POST", "/api/workspace/open-folder", {});
  document.getElementById("btnPreviewAgentsMd").onclick = () => {
    document.getElementById("confirmModalTitle").textContent = "AGENTS.md";
    document.getElementById("confirmModalBody").textContent = state.agentsMdStatus?.agentsMdPath || "AGENTS.md";
    const pre = document.getElementById("agentsPreviewModalCode");
    pre.textContent = state.agentsMdStatus?.preview || "(Click Ready Workspace to inspect freshly rendered content.)";
    pre.classList.remove("hidden");
    document.getElementById("btnConfirmModalProceed").classList.add("hidden");
    document.getElementById("confirmAgentsModal").classList.remove("hidden");
  };

  document.getElementById("btnConfirmModalCancel").onclick = closeConfirmModal;
  document.getElementById("btnConfirmModalProceed").onclick = () => {
    if (state.activeConfirmAction) state.activeConfirmAction();
  };

  // Minimal WebApp Viewer
  function closeViewerSubmenus() {
    document.getElementById("recentSubmenu").classList.add("hidden");
    document.getElementById("activeWsSubmenu").classList.add("hidden");
  }

  document.getElementById("webAppFrame").addEventListener("load", () => {
    applyViewerThemeOverride();
  });

  document.getElementById("btnEmptyOpenWebApp").onclick = () => openWebAppPath(null);
  document.getElementById("btnViewerHelp").onclick = e => {
    e.stopPropagation();
    closeViewerMenus();
    document.getElementById("viewerHelpPopover").classList.toggle("hidden");
  };
  document.getElementById("btnViewerUpTrigger").onclick = e => {
    e.stopPropagation();
    document.getElementById("viewerHelpPopover").classList.add("hidden");
    const menu = document.getElementById("viewerUpMenu");
    const wasHidden = menu.classList.contains("hidden");
    closeViewerMenus();
    if (wasHidden) {
      refreshViewerDiscovery();
      menu.classList.remove("hidden");
    }
  };
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

  // Global outside-click menu dismissal
  document.addEventListener("click", e => {
    if (!e.target.closest("#resetMenuWrap")) document.getElementById("resetMenu").classList.add("hidden");
    if (!e.target.closest("#promptOptionsWrap")) document.getElementById("promptOptionsMenu").classList.add("hidden");
    if (!e.target.closest("#conflictMenuWrap")) document.getElementById("conflictMenu").classList.add("hidden");
    if (!e.target.closest("#presetMenuWrap")) document.getElementById("presetDropdownMenu").classList.add("hidden");
    if (!e.target.closest("#selectorSpecDropWrap")) document.getElementById("selectorSpecMenu").classList.add("hidden");
    if (!e.target.closest("#orgSpecDropWrap")) document.getElementById("orgSpecMenu").classList.add("hidden");
    if (!e.target.closest(".notif-wrap")) document.getElementById("notifPopover").classList.add("hidden");
    if (!e.target.closest("#viewerFabWrap")) {
      closeViewerMenus();
      document.getElementById("viewerHelpPopover").classList.add("hidden");
    }
    if (!e.target.closest("#notePopover") && !e.target.closest(".pencil-btn") && !document.getElementById("notePopover").classList.contains("hidden")) {
      const targetComp = state.activeNoteTarget?.comp;
      closeNotePopover();
      if (targetComp) renderSpecSelectorPane(targetComp);
    }
  });

  // Settings Modal
  document.getElementById("btnOpenSettings").onclick = () => document.getElementById("settingsModal").classList.remove("hidden");
  document.getElementById("btnCloseSettings").onclick = () => document.getElementById("settingsModal").classList.add("hidden");

  document.getElementById("btnSaveSettings").onclick = async () => {
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
      await loadSpecs(true);
      toast("Settings saved.", "ok");
    }
  };

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
    const wrap = document.getElementById("selfUpdateProgressWrap");
    const bar = document.getElementById("selfUpdateProgressBar");
    const statusText = document.getElementById("selfUpdateStatusText");
    wrap.classList.remove("hidden");
    const poller = setInterval(async () => {
      const st = await api("GET", "/api/self-update/status");
      if (st.ok && st.data.status) {
        statusText.textContent = st.data.status.message;
        if (st.data.status.percent != null) bar.style.width = `${st.data.status.percent}%`;
      }
    }, 300);

    const res = await api("POST", "/api/self-update/install", {});
    clearInterval(poller);
    if (!res.ok) toast(res.data?.error?.description || "Self-update failed.", "err");
  };

  window.__onWin32Drop = path => {
    if (path) openWebAppPath(path);
  };

  bootstrap();
})();
