import { api } from "../api.js";
import { state } from "../store.js";
import { registerDropdown, registerPopover } from "../ui/dropdowns.js";
import { switchView, toast } from "../ui/chrome.js";

export function syncOverrideThemeUi() {
  const isOn = state.settings.overrideWebAppTheme !== false;
  const sw = document.getElementById("overrideThemeSwitch");
  const btn = document.getElementById("menuToggleOverrideTheme");
  if (sw) sw.classList.toggle("on", isOn);
  if (btn) btn.setAttribute("aria-checked", isOn ? "true" : "false");
}

export function applyViewerThemeOverride() {
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

export function showViewerLoadingOverlay(label = "Applying theme & loading WebApp...") {
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

export function hideViewerLoadingOverlay(expectedToken) {
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

export function closeViewerSubmenus() {
  document.getElementById("recentSubmenu").classList.add("hidden");
  document.getElementById("activeWsSubmenu").classList.add("hidden");
}

export function closeViewerMenus() {
  document.getElementById("viewerUpMenu").classList.add("hidden");
  closeViewerSubmenus();
}

export async function refreshViewerDiscovery() {
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

export function renderViewerMenus() {
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

export async function openWebAppPath(path) {
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

export async function buildAndViewWorkspace(workspaceId) {
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

export async function exportWorkspacePkg(workspaceId) {
  const res = await api("POST", "/api/workspaces/export", { workspaceId, target: "android" });
  if (res.ok && !res.data.cancelled) toast(`Exported .mbypkg to ${res.data}`, "ok");
  else if (!res.ok) toast(res.data?.error?.description || "Export failed.", "err");
}

export function wireViewerView() {
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
}
