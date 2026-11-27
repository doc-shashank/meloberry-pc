import { api, fetchAssetBlobUrl } from "../../api.js";
import { state, emit, withButtonBusy } from "../../store.js";
import { closeAllDropdowns } from "../../ui/dropdowns.js";
import { switchView, toast } from "../../ui/chrome.js";
import { materializeOrderedItems } from "./specModes.js";

export const IMAGE_FILE_EXT_RE = /\.(png|jpe?g|gif|webp|bmp|svg)$/i;

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
let activeDirectPreviewUrl = null;
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

function revokeDirectPreviewUrl() {
  if (activeDirectPreviewUrl) {
    URL.revokeObjectURL(activeDirectPreviewUrl);
    activeDirectPreviewUrl = null;
  }
}

async function renderDirectImagePreview(relPath) {
  const wrap = document.getElementById("specImageDirectPreviewWrap");
  const img = document.getElementById("specImageDirectPreview");
  const pathEl = document.getElementById("specImageDirectPreviewPath");
  revokeDirectPreviewUrl();

  const clean = (relPath || "").trim();
  const wsPath = state.settings.activeWorkspace?.workspacePath || "";
  if (!clean || !wsPath) {
    if (wrap) wrap.classList.add("hidden");
    if (pathEl) pathEl.textContent = "";
    return;
  }

  const blobUrl = await fetchAssetBlobUrl(clean, wsPath);
  if (img && blobUrl) {
    activeDirectPreviewUrl = blobUrl;
    img.src = blobUrl;
    if (pathEl) pathEl.textContent = clean;
    if (wrap) wrap.classList.remove("hidden");
  } else {
    if (wrap) wrap.classList.add("hidden");
  }
}

export function closeSpecContentModal() {
  const modal = document.getElementById("specContentModal");
  if (modal) modal.classList.add("hidden");
  modalTarget = null;
  revokePreviewUrl();
  revokeDirectPreviewUrl();
}

export function isSpecContentModalOpen() {
  const modal = document.getElementById("specContentModal");
  return !!modal && !modal.classList.contains("hidden");
}

function syncModalTabs(topic = "") {
  const t = (topic || "").toLowerCase();
  const isImageCapable = IMAGE_CAPABLE_TOPICS.has(t);
  const imageTab = document.getElementById("tabSpecContentImage");
  if (imageTab && topic) {
    imageTab.classList.toggle("hidden", !isImageCapable);
  }

  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.classList.toggle("active", btn.dataset.specContentMode === draftContentMode);
  });

  const imgPane = document.getElementById("specContentImagePane");
  const rawPane = document.getElementById("specContentRawPane");
  const filePane = document.getElementById("specContentFilePane");
  const metaFields = document.getElementById("specContentImageMetaFields");
  const bodyWrap = document.getElementById("specImageBodyWrap");
  const statusEl = document.getElementById("specContentModeStatus");
  if (imgPane) imgPane.classList.toggle("hidden", draftContentMode !== "imageFile");
  if (rawPane) rawPane.classList.toggle("hidden", draftContentMode !== "rawText");
  if (filePane) filePane.classList.toggle("hidden", draftContentMode !== "markdownFile");
  if (topic) {
    if (metaFields) metaFields.classList.toggle("hidden", t !== "image");
    if (bodyWrap) bodyWrap.classList.toggle("hidden", t === "image");
  }
  if (statusEl) {
    statusEl.textContent = draftContentMode === "imageFile"
      ? "Workspace image asset (no Markdown required)"
      : (draftContentMode === "rawText"
        ? "Direct text / :::directive authoring"
        : (draftContentMode === "markdownFile" ? "Workspace .md file" : "No content attached yet"));
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

  (state.settings.activeWorkspace?.lastCopiedRelativePaths || []).forEach(rel => {
    const clean = String(rel || "").replace(/^[/\\]+/, "");
    if (clean && /\.pdf$/i.test(clean)) {
      const full = wsPath ? `${wsPath}\\${clean.replace(/\//g, "\\")}` : clean;
      addCandidate(full, clean);
    }
  });

  (state.prompt.extraAttachments || []).forEach(att => {
    addCandidate(att.filePath, att.filePath.split(/[\\/]/).pop() || att.filePath);
  });

  return list;
}

function populateMelomgSection(topic, subtype, { focusMelomg }) {
  const box = document.getElementById("specMelomgDetails");
  if (!box) return;

  const show = IMAGE_CAPABLE_TOPICS.has((topic || "").toLowerCase());
  box.classList.toggle("hidden", !show);
  if (!show) {
    box.open = false;
    return;
  }

  box.open = Boolean(focusMelomg);

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
  const imgPath = (document.getElementById("specContentImagePath")?.value || "").trim();
  const imgCaption = (document.getElementById("specContentImageCaption")?.value || "").trim();
  const imgAlt = (document.getElementById("specContentImageAlt")?.value || "").trim();
  const imgBody = document.getElementById("specContentImageBody")?.value || "";

  if (draftContentMode === "imageFile" && imgPath) {
    const isImageTopic = (inst.topic || "").toLowerCase() === "image";
    inst.content = {
      mode: "imageFile",
      rawText: !isImageTopic && imgBody.trim() ? imgBody : null,
      markdownFilePath: null,
      imagePath: imgPath,
      caption: imgCaption || null,
      altText: imgAlt || null
    };
  } else if (draftContentMode === "rawText" && rawText.trim()) {
    inst.content = {
      mode: "rawText",
      rawText,
      markdownFilePath: null,
      imagePath: null,
      caption: null,
      altText: null
    };
  } else if (draftContentMode === "markdownFile" && mdPath) {
    inst.content = {
      mode: "markdownFile",
      rawText: null,
      markdownFilePath: mdPath,
      imagePath: null,
      caption: null,
      altText: null
    };
  } else {
    inst.content = {
      mode: "none",
      rawText: null,
      markdownFilePath: null,
      imagePath: null,
      caption: null,
      altText: null
    };
  }

  emit("prompt:preset-modified");
  closeSpecContentModal();
  emit("organize:render");
  emit("prompt:refresh");
}

export function openSpecContentModal(compId, instId, { focusMelomg = false } = {}) {
  const comp = state.prompt.components.find(c => c.id === compId);
  if (comp && (!comp.orderedItems || !comp.orderedItems.length)) {
    materializeOrderedItems(comp);
  }
  const inst = comp?.orderedItems?.find(o => o.id === instId);
  if (!comp || !inst) return;

  closeAllDropdowns();
  modalTarget = { compId, instId };

  const spec = findSpecMeta(inst.topic);
  const titleEl = document.getElementById("specContentModalTitle");
  const badgeEl = document.getElementById("specContentTopicBadge");
  const syntaxEl = document.getElementById("specContentSyntaxHint");
  const syntaxToggleBtn = document.getElementById("btnToggleSpecSyntax");

  if (titleEl) titleEl.textContent = spec?.displayName || inst.topic;
  if (badgeEl) {
    badgeEl.textContent = inst.subtype ? `${inst.topic}:${inst.subtype}` : inst.topic;
  }
  const sig = (spec?.compactSignature || "").trim();
  if (syntaxEl) {
    syntaxEl.textContent = sig;
    syntaxEl.classList.add("hidden");
  }
  if (syntaxToggleBtn) {
    syntaxToggleBtn.classList.toggle("hidden", !sig);
    syntaxToggleBtn.setAttribute("aria-expanded", "false");
  }

  const content = inst.content || { mode: "none", rawText: "", markdownFilePath: "", imagePath: "", caption: "", altText: "" };
  const topicLower = (inst.topic || "").toLowerCase();
  const isImageCapable = IMAGE_CAPABLE_TOPICS.has(topicLower);
  draftContentMode = content.mode || "none";
  if (draftContentMode === "none") {
    draftContentMode = (topicLower === "image" || (focusMelomg && isImageCapable))
      ? "imageFile"
      : "rawText";
  }

  const rawEl = document.getElementById("specContentRawInput");
  const fileEl = document.getElementById("specContentFilePath");
  const imgPathEl = document.getElementById("specContentImagePath");
  const imgCapEl = document.getElementById("specContentImageCaption");
  const imgAltEl = document.getElementById("specContentImageAlt");
  const imgBodyEl = document.getElementById("specContentImageBody");

  if (rawEl) rawEl.value = content.mode === "rawText" ? (content.rawText || "") : "";
  if (fileEl) fileEl.value = content.markdownFilePath || "";
  if (imgPathEl) imgPathEl.value = content.imagePath || "";
  if (imgCapEl) imgCapEl.value = content.caption || "";
  if (imgAltEl) imgAltEl.value = content.altText || "";
  if (imgBodyEl) imgBodyEl.value = content.mode === "imageFile" ? (content.rawText || "") : "";

  syncModalTabs(inst.topic);
  populateMelomgSection(inst.topic, inst.subtype, { focusMelomg });
  if (content.imagePath) {
    renderDirectImagePreview(content.imagePath);
  } else {
    renderDirectImagePreview("");
  }

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
  document.getElementById("btnToggleSpecSyntax")?.addEventListener("click", () => {
    const syntaxEl = document.getElementById("specContentSyntaxHint");
    const btn = document.getElementById("btnToggleSpecSyntax");
    if (!syntaxEl || !btn) return;
    const willShow = syntaxEl.classList.contains("hidden");
    syntaxEl.classList.toggle("hidden", !willShow);
    btn.setAttribute("aria-expanded", willShow ? "true" : "false");
  });
  document.getElementById("btnClearSpecContent")?.addEventListener("click", () => {
    draftContentMode = "none";
    const rawEl = document.getElementById("specContentRawInput");
    const fileEl = document.getElementById("specContentFilePath");
    const imgPathEl = document.getElementById("specContentImagePath");
    const imgCapEl = document.getElementById("specContentImageCaption");
    const imgAltEl = document.getElementById("specContentImageAlt");
    const imgBodyEl = document.getElementById("specContentImageBody");
    if (rawEl) rawEl.value = "";
    if (fileEl) fileEl.value = "";
    if (imgPathEl) imgPathEl.value = "";
    if (imgCapEl) imgCapEl.value = "";
    if (imgAltEl) imgAltEl.value = "";
    if (imgBodyEl) imgBodyEl.value = "";
    renderDirectImagePreview("");
    commitModalToSpec();
  });
  document.getElementById("btnSaveSpecContent")?.addEventListener("click", commitModalToSpec);

  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      draftContentMode = btn.dataset.specContentMode || "none";
      syncModalTabs();
    });
  });

  document.getElementById("btnPickSpecImageFile")?.addEventListener("click", async () => {
    const wsPath = state.settings.activeWorkspace?.workspacePath || null;
    if (!wsPath) {
      toast("Select an Active Workspace first.", "err");
      return;
    }

    const res = await api("POST", "/api/dialogs/pick-files", {
      title: "Select Image Asset",
      filterLabel: "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.svg)",
      filterPattern: "*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.svg",
      allowMultiple: false,
      initialDir: wsPath
    });
    if (!res.ok || res.data.cancelled || !res.data.paths?.length) return;

    const imp = await api("POST", "/api/workspace/import-asset", {
      workspacePath: wsPath,
      sourcePath: res.data.paths[0],
      kind: "image"
    });
    if (!imp.ok) {
      toast(imp.data?.error?.description || "Failed to import image into workspace.", "err");
      return;
    }

    const relPath = imp.data.relativePath;
    const imgPathEl = document.getElementById("specContentImagePath");
    if (imgPathEl) imgPathEl.value = relPath;
    draftContentMode = "imageFile";
    syncModalTabs();
    await renderDirectImagePreview(relPath);
    if (imp.data.copied) {
      toast(`Imported image to ${relPath}`, "ok");
    }
  });

  document.getElementById("btnPickSpecMdFile")?.addEventListener("click", async () => {
    const wsPath = state.settings.activeWorkspace?.workspacePath || null;
    if (!wsPath) {
      toast("Select an Active Workspace first.", "err");
      return;
    }

    const res = await api("POST", "/api/dialogs/pick-files", {
      title: "Select Markdown Content File",
      filterLabel: "Markdown Files (*.md;*.markdown)",
      filterPattern: "*.md;*.markdown",
      allowMultiple: false,
      initialDir: wsPath
    });
    if (!res.ok || res.data.cancelled || !res.data.paths?.length) return;

    const imp = await api("POST", "/api/workspace/import-asset", {
      workspacePath: wsPath,
      sourcePath: res.data.paths[0],
      kind: "markdown"
    });
    if (!imp.ok) {
      toast(imp.data?.error?.description || "Failed to import Markdown file into workspace.", "err");
      return;
    }

    const relPath = imp.data.relativePath;
    const fileEl = document.getElementById("specContentFilePath");
    if (fileEl) fileEl.value = relPath;
    draftContentMode = "markdownFile";
    syncModalTabs();
    if (imp.data.copied) {
      toast(`Imported Markdown file to ${relPath}`, "ok");
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
      const previewWrap = document.getElementById("specMelomgPreviewWrap");
      let usedDirectImagePane = false;

      if (inst) {
        const topicLower = (inst.topic || "").toLowerCase();
        const isImageCapable = IMAGE_CAPABLE_TOPICS.has(topicLower);
        if (isImageCapable && (topicLower === "image" || draftContentMode === "imageFile" || draftContentMode === "none")) {
          const imgPathEl = document.getElementById("specContentImagePath");
          const imgCapEl = document.getElementById("specContentImageCaption");
          if (imgPathEl) imgPathEl.value = d.relativeImagePath;
          if (imgCapEl && !imgCapEl.value.trim()) imgCapEl.value = d.caption || query;
          draftContentMode = "imageFile";
          syncModalTabs(inst.topic);
          await renderDirectImagePreview(d.relativeImagePath);
          usedDirectImagePane = true;
        } else {
          injectImageIntoRawText(inst.topic, d.relativeImagePath, d.caption || query);
        }
      }

      revokePreviewUrl();
      if (usedDirectImagePane) {
        if (previewWrap) previewWrap.classList.add("hidden");
      } else {
        const blobUrl = await fetchAssetBlobUrl(d.relativeImagePath, wsPath);
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
      }

      toast(`Extracted ${d.relativeImagePath}`, "ok");
    });
  }
}
