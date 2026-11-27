import { api, fetchAssetBlobUrl } from "../../api.js";
import { state, emit, withButtonBusy } from "../../store.js";
import { closeAllDropdowns } from "../../ui/dropdowns.js";
import { switchView, toast } from "../../ui/chrome.js";
import { getSpecModeStructure, materializeOrderedItems, syncSelectionsFromOrderedItems } from "./specModes.js";
import { getMarkdownTemplateForTopic } from "./specTemplates.js";

export const IMAGE_FILE_EXT_RE = /\.(png|jpe?g|gif|webp|bmp|svg)$/i;
export const IMAGE_CAPABLE_TOPICS = new Set([
  "image", "diagram-explore", "relabel", "flashcard", "mcq", "spot-error", "numeric", "case"
]);

const EMPTY_CONTENT = { mode: "none", rawText: null, markdownFilePath: null, imagePath: null, caption: null, altText: null };

let modalTarget = null;
let draftContentMode = "rawText";
let activeDirectPreviewUrl = null;
let wired = false;

const $id = id => document.getElementById(id);

function findSpecMeta(topic) {
  return state.specs.find(s => s.topic.toLowerCase() === (topic || "").toLowerCase()) || null;
}

function getLiveTarget() {
  if (!modalTarget) return { comp: null, inst: null, index: -1, total: 0 };
  const comp = state.prompt.components.find(c => c.id === modalTarget.compId) || null;
  const items = comp?.orderedItems || [];
  const index = items.findIndex(o => o.id === modalTarget.instId);
  return { comp, inst: index >= 0 ? items[index] : null, index, total: items.length };
}

function revokeDirectPreviewUrl() {
  if (activeDirectPreviewUrl) {
    URL.revokeObjectURL(activeDirectPreviewUrl);
    activeDirectPreviewUrl = null;
  }
}

async function renderDirectImagePreview(relPath) {
  const wrap = $id("specImageDirectPreviewWrap");
  const img = $id("specImageDirectPreview");
  const pathEl = $id("specImageDirectPreviewPath");
  revokeDirectPreviewUrl();

  const clean = (relPath || "").trim();
  $id("btnClearSpecImageFile")?.classList.toggle("hidden", !clean);

  const wsPath = state.settings.activeWorkspace?.workspacePath || "";
  if (!clean || !wsPath) {
    wrap?.classList.add("hidden");
    if (pathEl) pathEl.textContent = "";
    return;
  }

  const blobUrl = await fetchAssetBlobUrl(clean, wsPath);
  if (img && blobUrl) {
    activeDirectPreviewUrl = blobUrl;
    img.src = blobUrl;
    if (pathEl) pathEl.textContent = clean;
    wrap?.classList.remove("hidden");
  } else {
    wrap?.classList.add("hidden");
  }
}

export function closeSpecContentModal() {
  $id("specContentModal")?.classList.add("hidden");
  modalTarget = null;
  revokeDirectPreviewUrl();
}

export function isSpecContentModalOpen() {
  const modal = $id("specContentModal");
  return !!modal && !modal.classList.contains("hidden");
}

function syncModalTabs(topicOverride = "") {
  const { inst } = getLiveTarget();
  const topic = (topicOverride || inst?.topic || "").toLowerCase();
  const isImageTopic = topic === "image";
  const isImageCapable = IMAGE_CAPABLE_TOPICS.has(topic);

  $id("specContentModeBar")?.classList.toggle("hidden", isImageTopic);
  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.classList.toggle("active", btn.dataset.specContentMode === draftContentMode);
  });

  const showImgPane = isImageTopic || (isImageCapable && draftContentMode === "rawText");
  $id("specContentImagePane")?.classList.toggle("hidden", !showImgPane);
  $id("specContentImageMetaFields")?.classList.toggle("hidden", !isImageTopic);
  $id("specContentRawPane")?.classList.toggle("hidden", isImageTopic || draftContentMode !== "rawText");
  $id("specContentFilePane")?.classList.toggle("hidden", isImageTopic || draftContentMode !== "markdownFile");

  const imgPath = ($id("specContentImagePath")?.value || "").trim();
  $id("btnClearSpecImageFile")?.classList.toggle("hidden", !imgPath);

  const statusEl = $id("specContentModeStatus");
  if (statusEl) {
    statusEl.textContent = draftContentMode === "markdownFile"
      ? "Workspace .md file (auto-wrapped on Finalize)"
      : (isImageCapable && imgPath ? "Unified body + attached image" : "Direct Markdown / :::directive authoring");
  }
}

function renderModalSubtypes(comp, inst) {
  const row = $id("specModalSubtypesRow");
  if (!row) return;
  row.innerHTML = "";

  const { primaryGroup } = getSpecModeStructure(inst.topic);
  if (!primaryGroup?.options?.length) {
    row.classList.add("hidden");
    return;
  }

  row.classList.remove("hidden");
  const lbl = document.createElement("span");
  lbl.className = "inspector-label";
  lbl.textContent = `${primaryGroup.label || "Subtype"}:`;
  row.appendChild(lbl);

  const applySubtype = nextSub => {
    emit("prompt:preset-modified");
    inst.subtype = nextSub || null;
    syncSelectionsFromOrderedItems(comp, inst.topic);
    const badgeEl = $id("specContentTopicBadge");
    if (badgeEl) badgeEl.textContent = inst.subtype ? `${inst.topic}:${inst.subtype}` : inst.topic;
    const maskChk = $id("specMelomgMaskAll");
    if (maskChk && (inst.topic || "").toLowerCase() === "flashcard") maskChk.checked = inst.subtype === "diagram";
    renderModalSubtypes(comp, inst);
    emit("organize:render");
    emit("prompt:refresh");
  };

  const choices = [{ key: null, label: "Generic" }, ...primaryGroup.options];
  choices.forEach(opt => {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = `mode-chip${(inst.subtype || null) === opt.key ? " active" : ""}`;
    chip.textContent = opt.label;
    chip.onclick = () => applySubtype(opt.key);
    row.appendChild(chip);
  });
}

function updateModalStepper(index, total) {
  const safeTotal = Math.max(1, total);
  const safeIdx = Math.max(0, Math.min(index, safeTotal - 1));
  const counterEl = $id("specModalCounter");
  const prevBtn = $id("btnSpecModalPrev");
  const nextBtn = $id("btnSpecModalNext");
  if (counterEl) counterEl.textContent = `${safeIdx + 1}/${safeTotal}`;
  if (prevBtn) prevBtn.disabled = safeIdx <= 0;
  if (nextBtn) nextBtn.disabled = safeIdx >= safeTotal - 1;
}

function collectCandidatePdfPaths() {
  const seen = new Set();
  const list = [];
  const wsPath = state.settings.activeWorkspace?.workspacePath || "";
  const add = (path, label) => {
    if (!path || !/\.pdf$/i.test(path)) return;
    const key = path.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    list.push({ path, label });
  };

  (state.stagedItems || []).forEach(it => {
    if (it.kind === "file" && /\.pdf$/i.test(it.targetRelativePath || it.sourcePath || "")) {
      const full = wsPath && it.targetRelativePath ? `${wsPath}\\${it.targetRelativePath}` : it.sourcePath;
      add(full, it.targetRelativePath || it.displayName || full);
    }
  });
  (state.settings.activeWorkspace?.lastCopiedRelativePaths || []).forEach(rel => {
    const clean = String(rel || "").replace(/^[/\\]+/, "");
    if (clean && /\.pdf$/i.test(clean)) add(wsPath ? `${wsPath}\\${clean.replace(/\//g, "\\")}` : clean, clean);
  });
  (state.prompt.extraAttachments || []).forEach(att => add(att.filePath, att.filePath.split(/[\\/]/).pop() || att.filePath));
  return list;
}

function populateMelomgSection(topic, subtype, { focusMelomg }) {
  const box = $id("specMelomgDetails");
  if (!box) return;

  const show = IMAGE_CAPABLE_TOPICS.has((topic || "").toLowerCase());
  box.classList.toggle("hidden", !show);
  if (!show) { box.open = false; return; }

  box.open = Boolean(focusMelomg);
  const hasMelomg = Boolean(state.resolvedTools?.melomg || state.apps.some(a => a.kind === "melomg" && a.isInstalled));
  $id("specMelomgMissing")?.classList.toggle("hidden", hasMelomg);
  $id("specMelomgControls")?.classList.toggle("hidden", !hasMelomg);
  $id("specMelomgPreviewWrap")?.classList.add("hidden");

  const statusEl = $id("specMelomgStatus");
  if (statusEl) statusEl.textContent = hasMelomg ? "Uses cached Doc_* or ingests PDF" : "";

  const pdfSel = $id("specMelomgPdfSelect");
  if (pdfSel) {
    pdfSel.innerHTML = "";
    const candidates = collectCandidatePdfPaths();
    const opts = candidates.length ? candidates : [{ path: "", label: "No workspace PDFs yet — click Browse" }];
    opts.forEach(c => {
      const opt = document.createElement("option");
      opt.value = c.path;
      opt.textContent = c.label;
      pdfSel.appendChild(opt);
    });
  }

  const maskChk = $id("specMelomgMaskAll");
  if (maskChk) maskChk.checked = topic === "relabel" || subtype === "diagram";
  const queryInput = $id("specMelomgQuery");
  if (queryInput && focusMelomg) requestAnimationFrame(() => queryInput.focus());
}

function commitModalToSpec({ close = true } = {}) {
  const { comp, inst } = getLiveTarget();
  if (!comp || !inst) {
    if (close) closeSpecContentModal();
    return;
  }

  const topicLower = (inst.topic || "").toLowerCase();
  const isImageTopic = topicLower === "image";
  const isImageCapable = IMAGE_CAPABLE_TOPICS.has(topicLower);
  const rawText = $id("specContentRawInput")?.value || "";
  const mdPath = ($id("specContentFilePath")?.value || "").trim();
  const imgPath = ($id("specContentImagePath")?.value || "").trim();
  const caption = ($id("specContentImageCaption")?.value || "").trim() || null;
  const altText = ($id("specContentImageAlt")?.value || "").trim() || null;

  if (isImageTopic) {
    inst.content = imgPath ? { ...EMPTY_CONTENT, mode: "imageFile", imagePath: imgPath, caption, altText } : { ...EMPTY_CONTENT };
  } else if (draftContentMode === "markdownFile") {
    inst.content = mdPath ? { ...EMPTY_CONTENT, mode: "markdownFile", markdownFilePath: mdPath } : { ...EMPTY_CONTENT };
  } else if (isImageCapable && imgPath) {
    inst.content = { ...EMPTY_CONTENT, mode: "imageFile", rawText: rawText.trim() ? rawText : null, imagePath: imgPath, caption, altText };
  } else if (rawText.trim()) {
    inst.content = { ...EMPTY_CONTENT, mode: "rawText", rawText };
  } else {
    inst.content = { ...EMPTY_CONTENT };
  }

  emit("prompt:preset-modified");
  if (close) closeSpecContentModal();
  emit("organize:render");
  emit("prompt:refresh");
}

function stepSpec(delta) {
  const { comp, index, total } = getLiveTarget();
  const nextIdx = index + delta;
  if (!comp || index < 0 || nextIdx < 0 || nextIdx >= total) return;
  commitModalToSpec({ close: false });
  const nextInst = comp.orderedItems[nextIdx];
  if (nextInst) openSpecContentModal(comp.id, nextInst.id, { focusMelomg: false });
}

export function openSpecContentModal(compId, instId, { focusMelomg = false } = {}) {
  const comp = state.prompt.components.find(c => c.id === compId);
  if (comp && (!comp.orderedItems || !comp.orderedItems.length)) materializeOrderedItems(comp);
  const index = comp?.orderedItems?.findIndex(o => o.id === instId) ?? -1;
  const inst = index >= 0 ? comp.orderedItems[index] : null;
  if (!comp || !inst) return;

  closeAllDropdowns();
  modalTarget = { compId, instId };

  const spec = findSpecMeta(inst.topic);
  const tpl = getMarkdownTemplateForTopic(inst.topic);
  const titleEl = $id("specContentModalTitle");
  const badgeEl = $id("specContentTopicBadge");
  const syntaxEl = $id("specContentSyntaxHint");
  const syntaxToggleBtn = $id("btnToggleSpecSyntax");

  if (titleEl) titleEl.textContent = spec?.displayName || inst.topic;
  if (badgeEl) badgeEl.textContent = inst.subtype ? `${inst.topic}:${inst.subtype}` : inst.topic;

  const syntaxText = tpl.starter ? `${tpl.hint}\n\n${tpl.starter}` : tpl.hint;
  if (syntaxEl) { syntaxEl.textContent = syntaxText; syntaxEl.classList.add("hidden"); }
  if (syntaxToggleBtn) {
    syntaxToggleBtn.classList.toggle("hidden", !syntaxText);
    syntaxToggleBtn.setAttribute("aria-expanded", "false");
  }
  $id("btnInsertSpecStarter")?.classList.toggle("hidden", !tpl.starter);

  const content = inst.content || EMPTY_CONTENT;
  const topicLower = (inst.topic || "").toLowerCase();
  draftContentMode = content.mode === "markdownFile" && topicLower !== "image" ? "markdownFile" : "rawText";

  const rawEl = $id("specContentRawInput");
  if (rawEl) rawEl.value = content.rawText || "";
  if ($id("specContentFilePath")) $id("specContentFilePath").value = content.markdownFilePath || "";
  if ($id("specContentImagePath")) $id("specContentImagePath").value = content.imagePath || "";
  if ($id("specContentImageCaption")) $id("specContentImageCaption").value = content.caption || "";
  if ($id("specContentImageAlt")) $id("specContentImageAlt").value = content.altText || "";

  updateModalStepper(index, comp.orderedItems.length);
  renderModalSubtypes(comp, inst);
  syncModalTabs(inst.topic);
  populateMelomgSection(inst.topic, inst.subtype, { focusMelomg });
  renderDirectImagePreview(content.imagePath || "");

  $id("specContentModal")?.classList.remove("hidden");
  if (!focusMelomg && topicLower !== "image" && draftContentMode === "rawText" && rawEl) {
    requestAnimationFrame(() => rawEl.focus());
  }
}

async function pickAndImportWorkspaceFile(kind, title, filterLabel, filterPattern) {
  const wsPath = state.settings.activeWorkspace?.workspacePath || null;
  if (!wsPath) { toast("Select an Active Workspace first.", "err"); return null; }

  const res = await api("POST", "/api/dialogs/pick-files", { title, filterLabel, filterPattern, allowMultiple: false, initialDir: wsPath });
  if (!res.ok || res.data.cancelled || !res.data.paths?.length) return null;

  const imp = await api("POST", "/api/workspace/import-asset", { workspacePath: wsPath, sourcePath: res.data.paths[0], kind });
  if (!imp.ok) { toast(imp.data?.error?.description || `Failed to import ${kind} into workspace.`, "err"); return null; }
  return imp.data;
}

export function initSpecContentModal() {
  if (wired) return;
  wired = true;

  const modal = $id("specContentModal");
  modal?.addEventListener("click", e => { if (e.target === e.currentTarget) closeSpecContentModal(); });
  modal?.addEventListener("keydown", e => {
    if (!isSpecContentModalOpen()) return;
    if ((e.ctrlKey || e.metaKey) && e.key?.toLowerCase() === "s") {
      e.preventDefault();
      commitModalToSpec({ close: false });
      toast("Saved", "ok");
    } else if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
      e.preventDefault();
      const { index, total } = getLiveTarget();
      if (index >= 0 && index < total - 1) stepSpec(1);
      else commitModalToSpec({ close: true });
    }
  });

  $id("btnSpecModalPrev")?.addEventListener("click", () => stepSpec(-1));
  $id("btnSpecModalNext")?.addEventListener("click", () => stepSpec(1));
  $id("btnCloseSpecContentModal")?.addEventListener("click", closeSpecContentModal);
  $id("btnSaveSpecContent")?.addEventListener("click", () => commitModalToSpec({ close: true }));

  $id("btnToggleSpecSyntax")?.addEventListener("click", () => {
    const syntaxEl = $id("specContentSyntaxHint");
    const btn = $id("btnToggleSpecSyntax");
    if (!syntaxEl || !btn) return;
    const willShow = syntaxEl.classList.contains("hidden");
    syntaxEl.classList.toggle("hidden", !willShow);
    btn.setAttribute("aria-expanded", willShow ? "true" : "false");
  });

  $id("btnInsertSpecStarter")?.addEventListener("click", () => {
    const { inst } = getLiveTarget();
    const rawEl = $id("specContentRawInput");
    const starter = inst ? getMarkdownTemplateForTopic(inst.topic).starter : "";
    if (!rawEl || !starter) return;
    draftContentMode = "rawText";
    syncModalTabs(inst.topic);
    rawEl.value = rawEl.value.trim() ? `${rawEl.value.trimEnd()}\n\n${starter}` : starter;
    rawEl.focus();
  });

  $id("btnClearSpecImageFile")?.addEventListener("click", () => {
    ["specContentImagePath", "specContentImageCaption", "specContentImageAlt"].forEach(id => { const el = $id(id); if (el) el.value = ""; });
    renderDirectImagePreview("");
    syncModalTabs();
  });

  $id("btnClearSpecContent")?.addEventListener("click", () => {
    ["specContentRawInput", "specContentFilePath", "specContentImagePath", "specContentImageCaption", "specContentImageAlt"]
      .forEach(id => { const el = $id(id); if (el) el.value = ""; });
    renderDirectImagePreview("");
    commitModalToSpec({ close: true });
  });

  document.querySelectorAll("#specContentModeTabs .seg-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      draftContentMode = btn.dataset.specContentMode === "markdownFile" ? "markdownFile" : "rawText";
      syncModalTabs();
    });
  });

  $id("btnPickSpecImageFile")?.addEventListener("click", async () => {
    const data = await pickAndImportWorkspaceFile("image", "Select Image Asset", "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.svg)", "*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.svg");
    if (!data) return;
    if ($id("specContentImagePath")) $id("specContentImagePath").value = data.relativePath;
    draftContentMode = "rawText";
    syncModalTabs();
    await renderDirectImagePreview(data.relativePath);
    if (data.copied) toast(`Imported image to ${data.relativePath}`, "ok");
  });

  $id("btnPickSpecMdFile")?.addEventListener("click", async () => {
    const data = await pickAndImportWorkspaceFile("markdown", "Select Markdown Content File", "Markdown Files (*.md;*.markdown)", "*.md;*.markdown");
    if (!data) return;
    if ($id("specContentFilePath")) $id("specContentFilePath").value = data.relativePath;
    draftContentMode = "markdownFile";
    syncModalTabs();
    if (data.copied) toast(`Imported Markdown file to ${data.relativePath}`, "ok");
  });

  $id("btnSpecMelomgGoSetup")?.addEventListener("click", () => { closeSpecContentModal(); switchView("setup"); });

  $id("btnSpecMelomgBrowsePdf")?.addEventListener("click", async () => {
    const wsPath = state.settings.activeWorkspace?.workspacePath || null;
    const res = await api("POST", "/api/dialogs/pick-files", { title: "Select PDF for Figure Extraction", filterLabel: "PDF Files (*.pdf)", filterPattern: "*.pdf", allowMultiple: false, initialDir: wsPath });
    if (!res.ok || res.data.cancelled || !res.data.paths?.length) return;
    const picked = res.data.paths[0];
    const pdfSel = $id("specMelomgPdfSelect");
    if (!pdfSel) return;
    let opt = Array.from(pdfSel.options).find(o => o.value.toLowerCase() === picked.toLowerCase());
    if (!opt) {
      opt = document.createElement("option");
      opt.value = picked;
      opt.textContent = picked.split(/[\\/]/).pop() || picked;
      pdfSel.appendChild(opt);
    }
    pdfSel.value = opt.value;
  });

  const extractBtn = $id("btnSpecMelomgExtract");
  if (extractBtn) {
    extractBtn.onclick = withButtonBusy(extractBtn, "Extracting...", async () => {
      const wsPath = state.settings.activeWorkspace?.workspacePath;
      if (!wsPath) return toast("Select an Active Workspace first.", "err");

      const pdfPath = $id("specMelomgPdfSelect")?.value || "";
      const query = ($id("specMelomgQuery")?.value || "").trim();
      const quality = $id("specMelomgQuality")?.value || "high";
      const maskAll = !!$id("specMelomgMaskAll")?.checked;
      const statusEl = $id("specMelomgStatus");
      if (!pdfPath) return toast("Select or browse a PDF file first.", "warn");
      if (!query) return toast("Enter a figure query such as 'Figure 1.1', 'p4', or 'f1'.", "warn");

      if (statusEl) statusEl.textContent = "Running melomg...";
      const res = await api("POST", "/api/melomg/extract-figure", { workspacePath: wsPath, pdfPath, query, quality, maskAll, emitMeta: false });
      if (!res.ok) {
        const msg = res.data?.error?.description || "Figure extraction failed.";
        if (statusEl) statusEl.textContent = msg;
        return toast(msg, "err");
      }

      const d = res.data;
      if (statusEl) statusEl.textContent = `Extracted ${d.selector} (${d.pixelWidth}x${d.pixelHeight})`;
      if ($id("specContentImagePath")) $id("specContentImagePath").value = d.relativeImagePath;
      const capEl = $id("specContentImageCaption");
      if (capEl && !capEl.value.trim()) capEl.value = d.caption || query;
      $id("specMelomgPreviewWrap")?.classList.add("hidden");

      draftContentMode = "rawText";
      syncModalTabs();
      await renderDirectImagePreview(d.relativeImagePath);
      toast(`Extracted ${d.relativeImagePath}`, "ok");
    });
  }
}
