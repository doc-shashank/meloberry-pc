import { api, fetchAssetBlobUrl } from "../../api.js";
import { state, emit, escapeHtml, withButtonBusy } from "../../store.js";
import { closeAllDropdowns } from "../../ui/dropdowns.js";
import { switchView, toast } from "../../ui/chrome.js";

export const IMAGE_CAPABLE_TOPICS = new Set([
  "image",
  "diagram-explore",
  "relabel",
  "flashcard",
  "mcq",
  "spot-error",
  "numeric",
  "case"
]);

let modalTarget = null;
let draftContentMode = "none";
let activePreviewObjectUrl = null;
let wired = false;

function findSpecMeta(topic) {
  return state.specs.find(s => s.topic.toLowerCase() === (topic || "").toLowerCase()) || null;
}

function revokePreviewUrl() {
  if (activePreviewObjectUrl) {
    URL.revokeObjectURL(activePreviewObjectUrl);
    activePreviewObjectUrl = null;
  }
}

export function closeSpecContentModal() {
  const modal = document.getElementById("specContentModal");
  if (modal) modal.classList.add("hidden");
  modalTarget = null;
  revokePreviewUrl();
}

export function isSpecContentModalOpen() {
  const modal = document.getElementById("specContentModal");
  return !!modal && !modal.classList.contains("hidden");
}

function syncModalTabs() {
  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.classList.toggle("active", btn.dataset.specContentMode === draftContentMode);
  });

  const rawPane = document.getElementById("specContentRawPane");
  const filePane = document.getElementById("specContentFilePane");
  const statusEl = document.getElementById("specContentModeStatus");
  if (rawPane) rawPane.classList.toggle("hidden", draftContentMode !== "rawText");
  if (filePane) filePane.classList.toggle("hidden", draftContentMode !== "markdownFile");
  if (statusEl) {
    statusEl.textContent = draftContentMode === "rawText"
      ? "Direct text / :::directive authoring"
      : (draftContentMode === "markdownFile" ? "External or workspace .md file" : "No content attached yet");
  }
}

function collectCandidatePdfPaths() {
  const seen = new Set();
  const list = [];
  const wsPath = state.settings.activeWorkspace?.workspacePath || "";

  const addCandidate = (path, label) => {
    if (!path || !/\.pdf$/i.test(path)) return;
    const key = path.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    list.push({ path, label });
  };

  (state.stagedItems || []).forEach(it => {
    if (it.kind === "file" && /\.pdf$/i.test(it.targetRelativePath || it.sourcePath || "")) {
      const full = wsPath && it.targetRelativePath ? `${wsPath}\\${it.targetRelativePath}` : it.sourcePath;
      addCandidate(full, it.targetRelativePath || it.displayName || full);
    }
  });

  (state.prompt.extraAttachments || []).forEach(att => {
    addCandidate(att.filePath, att.filePath.split(/[\\/]/).pop() || att.filePath);
  });

  return list;
}

function populateMelomgSection(topic, subtype, { focusMelomg }) {
  const box = document.getElementById("specMelomgExtractor");
  if (!box) return;

  const show = IMAGE_CAPABLE_TOPICS.has((topic || "").toLowerCase());
  box.classList.toggle("hidden", !show);
  if (!show) return;

  const hasMelomg = Boolean(state.resolvedTools?.melomg || state.apps.some(a => a.kind === "melomg" && a.isInstalled));
  const missingEl = document.getElementById("specMelomgMissing");
  const controlsEl = document.getElementById("specMelomgControls");
  if (missingEl) missingEl.classList.toggle("hidden", hasMelomg);
  if (controlsEl) controlsEl.classList.toggle("hidden", !hasMelomg);

  const statusEl = document.getElementById("specMelomgStatus");
  if (statusEl) statusEl.textContent = hasMelomg ? "Uses cached Doc_* or ingests PDF" : "";

  const previewWrap = document.getElementById("specMelomgPreviewWrap");
  if (previewWrap) previewWrap.classList.add("hidden");
  revokePreviewUrl();

  const pdfSel = document.getElementById("specMelomgPdfSelect");
  if (pdfSel) {
    pdfSel.innerHTML = "";
    const candidates = collectCandidatePdfPaths();
    if (candidates.length === 0) {
      const opt = document.createElement("option");
      opt.value = "";
      opt.textContent = "No workspace PDFs yet — click Browse";
      pdfSel.appendChild(opt);
    } else {
      candidates.forEach(c => {
        const opt = document.createElement("option");
        opt.value = c.path;
        opt.textContent = c.label;
        pdfSel.appendChild(opt);
      });
    }
  }

  const maskChk = document.getElementById("specMelomgMaskAll");
  if (maskChk) {
    maskChk.checked = topic === "relabel" || subtype === "diagram";
  }

  const queryInput = document.getElementById("specMelomgQuery");
  if (queryInput && focusMelomg) {
    requestAnimationFrame(() => queryInput.focus());
  }
}

function injectImageIntoRawText(topic, relPath, caption) {
  const ta = document.getElementById("specContentRawInput");
  if (!ta) return;

  const safeCap = (caption || "").replace(/[\[\]\r\n]/g, " ").trim();
  const current = ta.value || "";

  if ((topic || "").toLowerCase() === "image") {
    ta.value = current.trim()
      ? `${current.trimEnd()}\n\n![${safeCap}](${relPath})`
      : `![${safeCap}](${relPath})`;
  } else if (/^imagePath\s*:/m.test(current)) {
    ta.value = current.replace(/^imagePath\s*:.*$/m, `imagePath: ${relPath}`);
  } else {
    const line = `imagePath: ${relPath}`;
    ta.value = current.trim() ? `${line}\n${current}` : `${line}\n`;
  }

  draftContentMode = "rawText";
  syncModalTabs();
}

function commitModalToSpec() {
  if (!modalTarget) return;
  const { compId, instId } = modalTarget;
  const comp = state.prompt.components.find(c => c.id === compId);
  const inst = comp?.orderedItems?.find(o => o.id === instId);
  if (!comp || !inst) {
    closeSpecContentModal();
    return;
  }

  const rawText = document.getElementById("specContentRawInput")?.value || "";
  const mdPath = (document.getElementById("specContentFilePath")?.value || "").trim();

  if (draftContentMode === "rawText") {
    inst.content = { mode: "rawText", rawText, markdownFilePath: null };
  } else if (draftContentMode === "markdownFile") {
    inst.content = { mode: "markdownFile", rawText: null, markdownFilePath: mdPath };
  } else {
    inst.content = { mode: "none", rawText: null, markdownFilePath: null };
  }

  emit("prompt:preset-modified");
  closeSpecContentModal();
  emit("organize:render");
  emit("prompt:refresh");
}

export function openSpecContentModal(compId, instId, { focusMelomg = false } = {}) {
  const comp = state.prompt.components.find(c => c.id === compId);
  const inst = comp?.orderedItems?.find(o => o.id === instId);
  if (!comp || !inst) return;

  closeAllDropdowns();
  modalTarget = { compId, instId };

  const spec = findSpecMeta(inst.topic);
  const titleEl = document.getElementById("specContentModalTitle");
  const badgeEl = document.getElementById("specContentTopicBadge");
  const syntaxEl = document.getElementById("specContentSyntaxHint");

  if (titleEl) titleEl.textContent = spec?.displayName || inst.topic;
  if (badgeEl) {
    badgeEl.textContent = inst.subtype ? `${inst.topic}:${inst.subtype}` : inst.topic;
  }
  if (syntaxEl) {
    const sig = (spec?.compactSignature || "").trim();
    syntaxEl.textContent = sig;
    syntaxEl.classList.toggle("hidden", !sig);
  }

  const content = inst.content || { mode: "none", rawText: "", markdownFilePath: "" };
  draftContentMode = content.mode || "none";
  if (draftContentMode === "none" && !focusMelomg) {
    draftContentMode = "rawText";
  }

  const rawEl = document.getElementById("specContentRawInput");
  const fileEl = document.getElementById("specContentFilePath");
  if (rawEl) rawEl.value = content.rawText || "";
  if (fileEl) fileEl.value = content.markdownFilePath || "";

  syncModalTabs();
  populateMelomgSection(inst.topic, inst.subtype, { focusMelomg });

  const modal = document.getElementById("specContentModal");
  if (modal) modal.classList.remove("hidden");

  if (!focusMelomg && draftContentMode === "rawText" && rawEl) {
    requestAnimationFrame(() => rawEl.focus());
  }
}

export function initSpecContentModal() {
  if (wired) return;
  wired = true;

  document.getElementById("btnCloseSpecContentModal")?.addEventListener("click", closeSpecContentModal);
  document.getElementById("btnClearSpecContent")?.addEventListener("click", () => {
    draftContentMode = "none";
    const rawEl = document.getElementById("specContentRawInput");
    const fileEl = document.getElementById("specContentFilePath");
    if (rawEl) rawEl.value = "";
    if (fileEl) fileEl.value = "";
    commitModalToSpec();
  });
  document.getElementById("btnSaveSpecContent")?.addEventListener("click", commitModalToSpec);

  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      draftContentMode = btn.dataset.specContentMode || "none";
      syncModalTabs();
    });
  });

  document.getElementById("btnPickSpecMdFile")?.addEventListener("click", async () => {
    const wsPath = state.settings.activeWorkspace?.workspacePath || null;
    const res = await api("POST", "/api/dialogs/pick-files", {
      title: "Select Markdown Content File",
      filterLabel: "Markdown Files (*.md;*.markdown)",
      filterPattern: "*.md;*.markdown",
      allowMultiple: false,
      initialDir: wsPath
    });
    if (res.ok && !res.data.cancelled && res.data.paths?.length) {
      const picked = res.data.paths[0];
      const fileEl = document.getElementById("specContentFilePath");
      if (fileEl) fileEl.value = picked;
      draftContentMode = "markdownFile";
      syncModalTabs();
    }
  });

  document.getElementById("btnSpecMelomgGoSetup")?.addEventListener("click", () => {
    closeSpecContentModal();
    switchView("setup");
  });

  document.getElementById("btnSpecMelomgBrowsePdf")?.addEventListener("click", async () => {
    const wsPath = state.settings.activeWorkspace?.workspacePath || null;
    const res = await api("POST", "/api/dialogs/pick-files", {
      title: "Select PDF for Figure Extraction",
      filterLabel: "PDF Files (*.pdf)",
      filterPattern: "*.pdf",
      allowMultiple: false,
      initialDir: wsPath
    });
    if (res.ok && !res.data.cancelled && res.data.paths?.length) {
      const picked = res.data.paths[0];
      const pdfSel = document.getElementById("specMelomgPdfSelect");
      if (pdfSel) {
        let opt = Array.from(pdfSel.options).find(o => o.value.toLowerCase() === picked.toLowerCase());
        if (!opt) {
          opt = document.createElement("option");
          opt.value = picked;
          opt.textContent = picked.split(/[\\/]/).pop() || picked;
          pdfSel.appendChild(opt);
        }
        pdfSel.value = opt.value;
      }
    }
  });

  const extractBtn = document.getElementById("btnSpecMelomgExtract");
  if (extractBtn) {
    extractBtn.onclick = withButtonBusy(extractBtn, "Extracting...", async () => {
      const wsPath = state.settings.activeWorkspace?.workspacePath;
      if (!wsPath) {
        toast("Select an Active Workspace first.", "err");
        return;
      }

      const pdfPath = document.getElementById("specMelomgPdfSelect")?.value || "";
      const query = (document.getElementById("specMelomgQuery")?.value || "").trim();
      const quality = document.getElementById("specMelomgQuality")?.value || "high";
      const maskAll = !!document.getElementById("specMelomgMaskAll")?.checked;
      const statusEl = document.getElementById("specMelomgStatus");

      if (!pdfPath) {
        toast("Select or browse a PDF file first.", "warn");
        return;
      }
      if (!query) {
        toast("Enter a figure query such as 'Figure 1.1', 'p4', or 'f1'.", "warn");
        return;
      }

      if (statusEl) statusEl.textContent = "Running melomg...";
      const res = await api("POST", "/api/melomg/extract-figure", {
        workspacePath: wsPath,
        pdfPath,
        query,
        quality,
        maskAll,
        emitMeta: false
      });

      if (!res.ok) {
        const msg = res.data?.error?.description || "Figure extraction failed.";
        if (statusEl) statusEl.textContent = msg;
        toast(msg, "err");
        return;
      }

      const d = res.data;
      if (statusEl) statusEl.textContent = `Extracted ${d.selector} (${d.pixelWidth}x${d.pixelHeight})`;

      const comp = state.prompt.components.find(c => c.id === modalTarget?.compId);
      const inst = comp?.orderedItems?.find(o => o.id === modalTarget?.instId);
      if (inst) {
        injectImageIntoRawText(inst.topic, d.relativeImagePath, d.caption || query);
      }

      revokePreviewUrl();
      const blobUrl = await fetchAssetBlobUrl(d.relativeImagePath, wsPath);
      const previewWrap = document.getElementById("specMelomgPreviewWrap");
      const previewImg = document.getElementById("specMelomgPreviewImg");
      const previewPath = document.getElementById("specMelomgPreviewPath");
      const previewMeta = document.getElementById("specMelomgPreviewMeta");
      if (previewImg && blobUrl) {
        activePreviewObjectUrl = blobUrl;
        previewImg.src = blobUrl;
      }
      if (previewPath) previewPath.textContent = d.relativeImagePath;
      if (previewMeta) previewMeta.textContent = d.caption ? `${d.caption} (${d.pixelWidth}x${d.pixelHeight})` : `${d.pixelWidth}x${d.pixelHeight}`;
      if (previewWrap) previewWrap.classList.remove("hidden");

      toast(`Extracted ${d.relativeImagePath}`, "ok");
    });
  }
}
