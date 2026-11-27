import { api } from "../api.js";
import { state, emit, extractFolderName } from "../store.js";
import { toast } from "./chrome.js";
import { previewPlan, renderStagedItems, selectWorkspace } from "../views/setup.js";
import { refreshPromptPreview } from "../views/prompt/promptDraft.js";
import { renderPromptAttachments } from "../views/prompt/format.js";
import { IMAGE_CAPABLE_TOPICS, IMAGE_FILE_EXT_RE } from "../views/prompt/specContentModal.js";
import { materializeOrderedItems } from "../views/prompt/specModes.js";
import { openWebAppPath } from "../views/viewer.js";

function clearDropHighlights() {
  document.querySelectorAll(".drop-target-active").forEach(el => el.classList.remove("drop-target-active"));
}

function isExternalFileDrag(e) {
  return !!(e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files"));
}

export function resolveExternalDropTarget(eventTarget) {
  if (state.currentView === "setup") {
    if (eventTarget && eventTarget.closest?.("#setupWsCard")) {
      return { view: "setup", target: "setup-workspace", el: document.getElementById("setupWsCard") };
    }
    return { view: "setup", target: "setup-staged", el: document.getElementById("setupStagedCard") };
  }
  if (state.currentView === "format") {
    return { view: "format", target: "prompt-notes", el: document.querySelector("#viewFormat .notes-card") };
  }
  if (state.currentView === "organize") {
    const tileEl = eventTarget?.closest?.(".org-spec-tile");
    if (tileEl) {
      state.live.hoveredSpecTarget = { compId: tileEl.dataset.compId || "", specId: tileEl.dataset.specId || "" };
      return { view: "organize", target: "spec-tile", el: tileEl };
    }
    state.live.hoveredSpecTarget = null;
    return { view: "organize", target: "organize-canvas", el: document.getElementById("orgCanvasTrack") };
  }
  if (state.currentView === "viewer") {
    return { view: "viewer", target: "viewer", el: document.querySelector("#viewViewer .viewer-full") };
  }
  return null;
}

export async function handleOrganizeNativeDrop(target, items) {
  const mdFile = items.find(i => i.kind === "file" && /\.(md|markdown)$/i.test(i.path));
  const imgFile = items.find(i => i.kind === "file" && IMAGE_FILE_EXT_RE.test(i.path));
  const hover = state.live.hoveredSpecTarget;
  state.live.hoveredSpecTarget = null;

  if (target !== "spec-tile" || !hover?.compId || !hover?.specId) {
    if (mdFile || imgFile) {
      toast("Drop a .md or image file directly onto a spec tile in Organize to attach it.", "warn");
    }
    return;
  }

  const comp = state.prompt.components.find(c => c.id === hover.compId);
  if (!comp) return;
  materializeOrderedItems(comp);
  const inst = (comp.orderedItems || []).find(o => o.id === hover.specId);
  if (!inst) return;

  const wsPath = state.settings.activeWorkspace?.workspacePath || "";
  if (!wsPath) {
    toast("Select an Active Workspace first before dropping files onto specs.", "err");
    return;
  }

  if (imgFile) {
    if (!IMAGE_CAPABLE_TOPICS.has((inst.topic || "").toLowerCase())) {
      toast(`The '${inst.topic}' spec does not accept direct image attachments.`, "warn");
      return;
    }
    const imp = await api("POST", "/api/workspace/import-asset", {
      workspacePath: wsPath,
      sourcePath: imgFile.path,
      kind: "image"
    });
    if (!imp.ok) {
      toast(imp.data?.error?.description || "Failed to import dropped image.", "err");
      return;
    }
    const topicLower = (inst.topic || "").toLowerCase();
    inst.content = {
      mode: "imageFile",
      rawText: topicLower === "image" ? null : (inst.content?.rawText || null),
      markdownFilePath: null,
      imagePath: imp.data.relativePath,
      caption: inst.content?.caption || null,
      altText: inst.content?.altText || null
    };
    emit("prompt:preset-modified");
    emit("organize:render");
    await refreshPromptPreview();
    toast(`${imp.data.copied ? "Imported & attached" : "Attached"} ${imp.data.relativePath} to ${inst.topic}.`, "ok");
    return;
  }

  if (mdFile) {
    const imp = await api("POST", "/api/workspace/import-asset", {
      workspacePath: wsPath,
      sourcePath: mdFile.path,
      kind: "markdown"
    });
    if (!imp.ok) {
      toast(imp.data?.error?.description || "Failed to import dropped Markdown file.", "err");
      return;
    }
    inst.content = {
      mode: "markdownFile",
      rawText: null,
      markdownFilePath: imp.data.relativePath,
      imagePath: null,
      caption: null,
      altText: null
    };
    emit("prompt:preset-modified");
    emit("organize:render");
    await refreshPromptPreview();
    toast(`${imp.data.copied ? "Imported & attached" : "Attached"} ${imp.data.relativePath} to ${inst.topic}.`, "ok");
  }
}

export function wireDragAndDrop() {
  document.addEventListener("dragover", e => {
    if (!isExternalFileDrag(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
    const info = resolveExternalDropTarget(e.target);
    clearDropHighlights();
    if (info && info.el) {
      info.el.classList.add("drop-target-active");
      if (info.view === "format" && !state.promptDragScrolled) {
        state.promptDragScrolled = true;
        info.el.scrollIntoView({ behavior: "smooth", block: "center" });
      }
    }
  });

  document.addEventListener("dragleave", e => {
    if (e.relatedTarget === null) {
      clearDropHighlights();
      state.promptDragScrolled = false;
      state.live.hoveredSpecTarget = null;
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

    if (view === "format") {
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

    if (view === "organize") {
      await handleOrganizeNativeDrop(target, items);
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
