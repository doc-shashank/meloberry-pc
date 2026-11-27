import { api } from "../api.js";
import { state } from "../store.js";
import { getIcon } from "../icons.js";
import { registerPopover, closeAllDropdowns } from "./dropdowns.js";
import { openConfirmModal, closeConfirmModal } from "./modal.js";
import { toast } from "./chrome.js";

const dirCache = new Map();
let currentTreeRoot = "";
let selectWorkspaceFn = null;
let clickNavTimer = null;

function isWithinActiveWorkspace(candidatePath) {
  const activeWs = (state.settings.activeWorkspace?.workspacePath || "").replace(/[\\/]+$/, "");
  if (!activeWs || !candidatePath) return false;
  const cand = candidatePath.replace(/[\\/]+$/, "");
  return cand.toLowerCase() === activeWs.toLowerCase()
    || cand.toLowerCase().startsWith(activeWs.toLowerCase() + "\\")
    || cand.toLowerCase().startsWith(activeWs.toLowerCase() + "/");
}

async function fetchDirectoryListing(dirPath) {
  const res = await api("GET", `/api/workspace/tree?path=${encodeURIComponent(dirPath)}`);
  if (!res.ok) {
    toast(res.data?.error?.description || "Unable to inspect directory.", "err");
    return null;
  }
  dirCache.set(res.data.currentPath.toLowerCase(), res.data);
  return res.data;
}

function appendDirectoryContents(listEl, listing) {
  const activeWsPath = (state.settings.activeWorkspace?.workspacePath || "").replace(/[\\/]+$/, "").toLowerCase();

  (listing.folders || []).forEach(folder => {
    const key = folder.fullPath.toLowerCase();
    const isActiveWs = activeWsPath && key === activeWsPath;

    const row = document.createElement("button");
    row.type = "button";
    row.className = `ws-tree-row${isActiveWs ? " is-active-ws" : ""}`;
    row.title = `${folder.fullPath}\nClick to open \u2022 Double-click to set as Active Workspace`;

    const icon = document.createElement("span");
    icon.className = "ws-tree-icon";
    icon.innerHTML = getIcon("ui/folder");

    const name = document.createElement("span");
    name.className = "ws-tree-name";
    name.textContent = folder.name;

    const chev = document.createElement("span");
    chev.className = "ws-tree-chevron";
    chev.textContent = folder.hasSubfolders ? "\u25b8" : "";

    row.append(icon, name, chev);

    row.onclick = e => {
      e.stopPropagation();
      clearTimeout(clickNavTimer);
      clickNavTimer = setTimeout(async () => {
        if (!isWithinActiveWorkspace(folder.fullPath)) return;
        currentTreeRoot = folder.fullPath;
        await fetchDirectoryListing(currentTreeRoot);
        renderWorkspaceTree();
      }, 190);
    };

    row.ondblclick = e => {
      e.stopPropagation();
      clearTimeout(clickNavTimer);
      closeAllDropdowns();
      openConfirmModal({
        title: "Switch Active Workspace?",
        body: `Set "${folder.fullPath}" as the Active Workspace?`,
        proceedText: "Set Active Workspace",
        showProceed: true,
        onProceed: async () => {
          closeConfirmModal();
          if (selectWorkspaceFn) {
            await selectWorkspaceFn(folder.fullPath);
          }
        }
      });
    };

    listEl.appendChild(row);
  });

  (listing.files || []).forEach(file => {
    const fRow = document.createElement("div");
    fRow.className = "ws-tree-row is-file";

    const icon = document.createElement("span");
    icon.className = "ws-tree-icon";
    icon.innerHTML = getIcon("ui/file");

    const name = document.createElement("span");
    name.className = "ws-tree-name";
    name.textContent = file.name;

    fRow.append(icon, name);
    listEl.appendChild(fRow);
  });

  if (!(listing.folders || []).length && !(listing.files || []).length) {
    const empty = document.createElement("div");
    empty.className = "muted";
    empty.style.padding = "10px 12px";
    empty.textContent = "This folder is empty.";
    listEl.appendChild(empty);
  }
}

async function renderWorkspaceTree() {
  const pop = document.getElementById("wsTreePopover");
  if (!pop) return;
  pop.innerHTML = "";

  if (!currentTreeRoot) {
    const emptyHead = document.createElement("div");
    emptyHead.className = "ws-tree-head";
    emptyHead.innerHTML = "<strong>No Active Workspace</strong>";
    const browseBtn = document.createElement("button");
    browseBtn.type = "button";
    browseBtn.className = "btn btn-xs btn-primary";
    browseBtn.textContent = "Browse...";
    browseBtn.onclick = async e => {
      e.stopPropagation();
      closeAllDropdowns();
      const r = await api("POST", "/api/dialogs/pick-folder", { title: "Select Active Workspace Folder" });
      if (r.ok && !r.data.cancelled && r.data.path && selectWorkspaceFn) {
        await selectWorkspaceFn(r.data.path);
      }
    };
    emptyHead.appendChild(browseBtn);
    pop.appendChild(emptyHead);

    const msg = document.createElement("div");
    msg.className = "muted";
    msg.style.padding = "14px 12px";
    msg.textContent = "Pick an Active Workspace folder to inspect its directory tree.";
    pop.appendChild(msg);
    return;
  }

  let listing = dirCache.get(currentTreeRoot.toLowerCase());
  if (!listing) {
    listing = await fetchDirectoryListing(currentTreeRoot);
    if (!listing) return;
  }

  const head = document.createElement("div");
  head.className = "ws-tree-head";

  const canGoUp = !!(listing.parentPath && isWithinActiveWorkspace(listing.parentPath));
  const upBtn = document.createElement("button");
  upBtn.type = "button";
  upBtn.className = "btn btn-xs btn-ghost icon-only-btn ws-tree-up-btn";
  upBtn.innerHTML = `<span class="icon-slot">${getIcon("ui/arrow-up")}</span>`;
  upBtn.setAttribute("aria-label", "Go up one folder");
  upBtn.disabled = !canGoUp;
  upBtn.title = canGoUp ? `Up to ${listing.parentPath}` : "At Active Workspace root";
  upBtn.onclick = async e => {
    e.stopPropagation();
    if (!canGoUp) return;
    currentTreeRoot = listing.parentPath;
    await fetchDirectoryListing(currentTreeRoot);
    renderWorkspaceTree();
  };

  const pathSpan = document.createElement("span");
  pathSpan.className = "ws-tree-path";
  pathSpan.textContent = listing.currentPath;
  pathSpan.title = listing.currentPath;

  head.append(upBtn, pathSpan);
  pop.appendChild(head);

  const listEl = document.createElement("div");
  listEl.className = "ws-tree-list";
  appendDirectoryContents(listEl, listing);
  pop.appendChild(listEl);

  const foot = document.createElement("div");
  foot.className = "ws-tree-foot";
  foot.textContent = "Click folder to open \u2022 Double-click folder to switch workspace";
  pop.appendChild(foot);
}

export function wireWorkspaceTree({ selectWorkspace }) {
  selectWorkspaceFn = selectWorkspace;
  registerPopover("wsTreePopover", { wrapSelector: "#wsTreeWrap" });

  const btn = document.getElementById("activeWsBtn");
  const pop = document.getElementById("wsTreePopover");
  if (!btn || !pop) return;

  btn.onclick = async e => {
    e.stopPropagation();
    if (!pop.classList.contains("hidden")) {
      closeAllDropdowns();
      return;
    }

    closeAllDropdowns(["wsTreePopover"]);
    clearTimeout(clickNavTimer);
    dirCache.clear();
    currentTreeRoot = state.settings.activeWorkspace?.workspacePath || "";
    pop.classList.remove("hidden");
    await renderWorkspaceTree();
  };
}
