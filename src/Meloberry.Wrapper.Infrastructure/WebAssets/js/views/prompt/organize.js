import { state, emit, on } from "../../store.js";
import { getIcon, getSpecIcon } from "../../icons.js";
import { registerDropdown, registerPopover, toggleDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { cloneComponent, ensureComponentsForContainer } from "./promptDraft.js";
import { getSpecModeStructure, findOrInitSelection, buildOrganizationDisplayItems, materializeOrderedItems, syncSelectionsFromOrderedItems } from "./specModes.js";
import { animateFromPositions, capturePositions } from "./organizeFlip.js";
import { attachCardDrag, attachSpecDrag } from "./organizeDrag.js";
import { buildCardPresetMenuControl, renderPresetMenu, wireOrganizePresetToolbar } from "./organizePresets.js";
import { openAddSpecPopover, openSpecInspectorPopover, wireSpecPopovers } from "./specPopovers.js";

export function extractSpecPreviewSnippet(content) {
  if (!content) return "";
  if (content.mode === "markdownFile" && content.markdownFilePath) {
    const clean = String(content.markdownFilePath).trim();
    const parts = clean.split(/[\\/]/);
    return parts[parts.length - 1] || clean;
  }
  if (content.mode === "imageFile") {
    if (content.caption && content.caption.trim()) return content.caption.trim();
    if (content.imagePath) {
      const clean = String(content.imagePath).trim();
      const parts = clean.split(/[\\/]/);
      return parts[parts.length - 1] || clean;
    }
  }
  const raw = String(content.rawText || "").trim();
  if (!raw) return "";
  const lines = raw.split(/\r?\n/)
    .map(l => l.trim())
    .filter(l => l && !l.startsWith(":::") && l !== "---");
  if (!lines.length) return "";
  const first = lines[0]
    .replace(/^#+\s*/, "")
    .replace(/\*\*([^*]+)\*\*/g, "$1")
    .replace(/`([^`]+)`/g, "$1")
    .trim();
  return first.length > 64 ? `${first.slice(0, 61)}...` : first;
}

function transferTopicMetadata(sourceComp, targetComp, topic) {
  const srcSel = (sourceComp.selections || []).find(s => s.topic.toLowerCase() === topic.toLowerCase());
  if (!srcSel) return;
  const tgtSel = findOrInitSelection(targetComp, topic);
  if (!tgtSel.notes && srcSel.notes) tgtSel.notes = srcSel.notes;

  const { secondaryGroups } = getSpecModeStructure(topic);
  const secKeys = new Set(secondaryGroups.flatMap(g => g.options.map(o => o.key)));
  const inherited = (srcSel.modes || []).filter(m => secKeys.has(m));
  if (inherited.length > 0 && !(tgtSel.modes || []).some(m => secKeys.has(m))) {
    tgtSel.modes = Array.from(new Set([...(tgtSel.modes || []), ...inherited]));
  }
}

function commitCardOrderFromDom() {
  const track = document.getElementById("orgCanvasTrack");
  if (!track) return;
  const orderedIds = Array.from(track.querySelectorAll(".org-comp-card")).map(el => el.dataset.compId);
  const byId = new Map(state.prompt.components.map(c => [c.id, c]));
  const next = orderedIds.map(id => byId.get(id)).filter(Boolean);
  if (next.length !== state.prompt.components.length) return;

  emit("prompt:preset-modified");
  state.prompt.components = next;
  renderOrganizeCanvas();
  emit("prompt:refresh");
}

function commitSpecOrderFromDom(affectedCompIds) {
  const track = document.getElementById("orgCanvasTrack");
  if (!track || !Array.isArray(affectedCompIds) || !affectedCompIds.length) return;

  const uniqueIds = Array.from(new Set(affectedCompIds.filter(Boolean)));
  const sourceComp = state.prompt.components.find(c => c.id === affectedCompIds[0]);

  emit("prompt:preset-modified");
  state.prompt.components.forEach(c => materializeOrderedItems(c));

  const itemById = new Map();
  state.prompt.components.forEach(c => {
    (c.orderedItems || []).forEach(inst => itemById.set(inst.id, { inst, prevComp: c }));
  });

  uniqueIds.forEach(cid => {
    const comp = state.prompt.components.find(c => c.id === cid);
    const listEl = track.querySelector(`.org-comp-card[data-comp-id="${CSS.escape(cid)}"] .org-spec-list`);
    if (!comp || !listEl) return;

    const domSpecIds = Array.from(listEl.querySelectorAll(".org-spec-tile")).map(el => el.dataset.specId);
    const nextOrdered = [];
    domSpecIds.forEach(sid => {
      const entry = itemById.get(sid);
      if (!entry) return;
      if (sourceComp && entry.prevComp.id !== comp.id) {
        transferTopicMetadata(entry.prevComp, comp, entry.inst.topic);
      }
      nextOrdered.push(entry.inst);
    });

    comp.orderedItems = nextOrdered.length ? nextOrdered : null;
    syncSelectionsFromOrderedItems(comp);
  });

  renderOrganizeCanvas();
  emit("prompt:refresh");
}

function createEmptySpecHint(kind) {
  const hint = document.createElement("div");
  hint.className = "org-empty-hint";
  hint.textContent = kind === "quiz"
    ? "No question specs yet (Prompt defaults to 5 MCQs). Click + below to add."
    : "No content blocks yet. Click + below to add.";
  return hint;
}

function removeSpecWithAnimation(comp, specId) {
  const track = document.getElementById("orgCanvasTrack");
  const cardEl = track?.querySelector(`.org-comp-card[data-comp-id="${CSS.escape(comp.id)}"]`);
  const listEl = cardEl?.querySelector(".org-spec-list");
  const tileEl = listEl?.querySelector(`.org-spec-tile[data-spec-id="${CSS.escape(specId)}"]`);

  emit("prompt:preset-modified");
  const firstRects = listEl ? capturePositions(listEl, ".org-spec-tile", tileEl) : null;

  materializeOrderedItems(comp);
  const idx = (comp.orderedItems || []).findIndex(o => o.id === specId);
  const removedTopic = idx >= 0 ? comp.orderedItems[idx].topic : null;
  if (idx >= 0) comp.orderedItems.splice(idx, 1);
  if (!comp.orderedItems || !comp.orderedItems.length) comp.orderedItems = null;
  syncSelectionsFromOrderedItems(comp, removedTopic);

  if (cardEl && listEl && tileEl) {
    tileEl.remove();
    const remaining = comp.orderedItems ? comp.orderedItems.length : 0;
    const countTxt = cardEl.querySelector(".org-spec-count");
    if (countTxt) countTxt.textContent = `${remaining} ${remaining === 1 ? "spec" : "specs"}`;
    if (remaining === 0) listEl.appendChild(createEmptySpecHint(comp.kind));
    animateFromPositions(firstRects, 200);
  } else {
    renderOrganizeCanvas();
  }

  emit("prompt:refresh");
}

function removeComponentWithAnimation(comp, cardEl) {
  const track = document.getElementById("orgCanvasTrack");
  const isSession = state.prompt.container === "session";
  const firstRects = track ? capturePositions(track, ".org-comp-card, .org-add-comp-slot", cardEl) : null;

  emit("prompt:preset-modified");
  state.prompt.components = state.prompt.components.filter(c => c.id !== comp.id);
  ensureComponentsForContainer();

  if (track && cardEl) {
    cardEl.remove();
    const totalCards = state.prompt.components.length;
    track.classList.toggle("org-track-centered", !isSession || totalCards <= 1);
    if (totalCards <= 1) {
      track.querySelectorAll(".org-remove-btn").forEach(btn => btn.classList.add("hidden"));
    }
    animateFromPositions(firstRects, 220);
  } else {
    renderOrganizeCanvas();
  }

  emit("prompt:refresh");
}

export function getSpecContentBadge(content, topic = "") {
  if (!content || !content.mode || content.mode === "none") return null;
  if (content.mode === "imageFile" && content.imagePath?.trim()) {
    const t = String(topic || "").trim().toLowerCase();
    const needsBody = Boolean(t) && t !== "image" && !content.rawText?.trim();
    return needsBody
      ? { label: "Img*", complete: false, title: `Image: ${content.imagePath.trim()} — add directive body before finalizing` }
      : { label: "Img", complete: true, title: `Image: ${content.imagePath.trim()} — click to edit` };
  }
  if (content.mode === "markdownFile" && content.markdownFilePath?.trim()) {
    return { label: ".md", complete: true, title: `Markdown file: ${content.markdownFilePath.trim()} — click to edit` };
  }
  if (content.mode === "rawText" && content.rawText?.trim()) {
    return { label: "Raw", complete: true, title: "Raw text content attached — click to edit" };
  }
  return null;
}

function buildSpecTile(comp, inst, onMutated) {
  const tile = document.createElement("div");
  const isJustAdded = state.lastAddedSpecOrgId && inst.id === state.lastAddedSpecOrgId;
  const isTouched = state.live.agentTouchedIds?.has(inst.id);
  tile.className = `org-spec-tile${isJustAdded ? " animate-add" : ""}${isTouched ? " agent-touched" : ""}`;
  tile.dataset.compId = comp.id;
  tile.dataset.specId = inst.id;

  const { spec, primaryGroup } = getSpecModeStructure(inst.topic);
  const subOpt = inst.subtype && primaryGroup?.options.find(o => o.key === inst.subtype);
  const subLabel = subOpt ? subOpt.label : inst.subtype;
  const friendlyName = spec?.displayName ? `${spec.displayName} (${inst.topic})` : inst.topic;
  tile.title = subLabel ? `${friendlyName} - ${subLabel}` : friendlyName;

  const left = document.createElement("div");
  left.className = "org-spec-tile-left";

  const iconEl = document.createElement("span");
  iconEl.className = "org-spec-tile-icon";
  iconEl.innerHTML = getSpecIcon(inst.topic, inst.subtype, comp.kind === "quiz" ? "question" : "block");

  const meta = document.createElement("div");
  meta.className = "org-spec-tile-meta";
  const headRow = document.createElement("div");
  headRow.className = "org-spec-tile-head-row";
  const labelEl = document.createElement("span");
  labelEl.className = "org-spec-tile-label";
  labelEl.textContent = inst.topic;
  headRow.appendChild(labelEl);

  if (subLabel) {
    const subEl = document.createElement("span");
    subEl.className = "org-spec-tile-sub";
    subEl.textContent = subLabel;
    headRow.appendChild(subEl);
  }
  meta.appendChild(headRow);

  const snippet = extractSpecPreviewSnippet(inst.content);
  if (snippet) {
    const snipEl = document.createElement("span");
    snipEl.className = "org-spec-tile-snippet";
    snipEl.textContent = snippet;
    meta.appendChild(snipEl);
  }
  left.append(iconEl, meta);

  const actions = document.createElement("div");
  actions.className = "org-spec-tile-actions";
  const openDirectModal = () => {
    closeAllDropdowns();
    materializeOrderedItems(comp);
    const live = (comp.orderedItems || []).find(o => o.id === inst.id) || inst;
    emit("spec:open-content-modal", { compId: comp.id, instId: live.id, focusMelomg: false });
  };

  const badge = getSpecContentBadge(inst.content, inst.topic);
  if (badge) {
    const cBadge = document.createElement("button");
    cBadge.type = "button";
    cBadge.className = `spec-content-badge${badge.complete === false ? " is-incomplete" : ""}`;
    cBadge.textContent = badge.label;
    cBadge.title = badge.title;
    cBadge.onpointerdown = e => e.stopPropagation();
    cBadge.onclick = e => {
      e.stopPropagation();
      openDirectModal();
    };
    actions.appendChild(cBadge);
  }

  const sel = (comp.selections || []).find(s => s.topic.toLowerCase() === inst.topic.toLowerCase());
  const noteDot = document.createElement("span");
  noteDot.className = `has-note-dot${sel?.notes?.trim() ? "" : " hidden"}`;
  noteDot.title = "Has custom instructions";

  const editContentBtn = document.createElement("button");
  editContentBtn.type = "button";
  editContentBtn.className = "org-spec-edit";
  editContentBtn.title = badge ? "Edit spec content" : "Add spec content";
  editContentBtn.innerHTML = getIcon("ui/pencil");
  editContentBtn.onpointerdown = e => e.stopPropagation();
  editContentBtn.onclick = e => {
    e.stopPropagation();
    openDirectModal();
  };

  const rmBtn = document.createElement("button");
  rmBtn.type = "button";
  rmBtn.className = "org-spec-remove";
  rmBtn.title = `Remove ${inst.topic}`;
  rmBtn.textContent = "\u00d7";
  rmBtn.onclick = e => {
    e.stopPropagation();
    closeAllDropdowns();
    removeSpecWithAnimation(comp, inst.id);
  };

  actions.append(noteDot, editContentBtn, rmBtn);
  tile.append(left, actions);

  tile.addEventListener("dblclick", e => {
    if (e.target?.closest?.("button")) return;
    e.stopPropagation();
    openDirectModal();
  });

  tile.addEventListener("dragenter", () => {
    state.live.hoveredSpecTarget = { compId: comp.id, specId: inst.id };
  });

  attachSpecDrag(tile, comp, {
    onClick: el => openSpecInspectorPopover(el, comp, inst, onMutated),
    onCommit: commitSpecOrderFromDom
  });

  return tile;
}

export function highlightAgentTargets(compId, specId) {
  if (!compId && !specId) return;
  if (compId) state.live.agentTouchedIds.add(compId);
  if (specId) state.live.agentTouchedIds.add(specId);

  const track = document.getElementById("orgCanvasTrack");
  if (!track) return;

  const specEl = specId ? track.querySelector(`.org-spec-tile[data-spec-id="${CSS.escape(specId)}"]`) : null;
  const cardEl = compId ? track.querySelector(`.org-comp-card[data-comp-id="${CSS.escape(compId)}"]`) : null;
  if (cardEl) cardEl.classList.add("agent-touched");
  if (specEl) specEl.classList.add("agent-touched");

  const scrollEl = specEl || cardEl;
  if (scrollEl) {
    requestAnimationFrame(() => scrollEl.scrollIntoView({ behavior: "smooth", block: "nearest", inline: "nearest" }));
  }

  setTimeout(() => {
    if (compId) state.live.agentTouchedIds.delete(compId);
    if (specId) state.live.agentTouchedIds.delete(specId);
    cardEl?.classList.remove("agent-touched");
    specEl?.classList.remove("agent-touched");
  }, 2200);
}

function buildComponentCard(comp, isSession, totalCards, onMutated) {
  const card = document.createElement("section");
  const isJustAdded = state.lastAddedOrgId && comp.id === state.lastAddedOrgId;
  const isTouched = state.live.agentTouchedIds?.has(comp.id);
  card.className = `org-comp-card kind-${comp.kind}${isJustAdded ? " animate-add" : ""}${isTouched ? " agent-touched" : ""}`;
  card.dataset.compId = comp.id;

  const header = document.createElement("div");
  header.className = "org-comp-header";

  const left = document.createElement("div");
  left.className = "org-comp-header-left";
  const iconBox = document.createElement("span");
  iconBox.className = "org-comp-icon";
  iconBox.innerHTML = getIcon(comp.kind === "article" ? "ui/article" : "ui/quiz");

  const meta = document.createElement("div");
  meta.className = "org-comp-meta";
  const kindLbl = document.createElement("span");
  kindLbl.className = "org-comp-kind";
  kindLbl.textContent = comp.kind === "article" ? "Article" : "Quiz";

  const titleInput = document.createElement("input");
  titleInput.type = "text";
  titleInput.className = "org-comp-title-input";
  titleInput.value = comp.title || "";
  titleInput.placeholder = comp.kind === "article" ? "Study Article" : "Practice Quiz";
  titleInput.onpointerdown = e => e.stopPropagation();
  titleInput.onclick = e => e.stopPropagation();
  titleInput.oninput = e => {
    emit("prompt:preset-modified");
    comp.title = e.target.value;
    emit("prompt:schedule-preview", 120);
  };
  meta.append(kindLbl, titleInput);
  left.append(iconBox, meta);

  const right = document.createElement("div");
  right.className = "org-comp-header-right";
  if (isSession && totalCards > 1) {
    const rmBtn = document.createElement("button");
    rmBtn.type = "button";
    rmBtn.className = "org-remove-btn";
    rmBtn.title = "Remove component";
    rmBtn.textContent = "\u00d7";
    rmBtn.onpointerdown = e => e.stopPropagation();
    rmBtn.onclick = e => {
      e.stopPropagation();
      closeAllDropdowns();
      removeComponentWithAnimation(comp, card);
    };
    right.appendChild(rmBtn);
  }
  header.append(left, right);

  const body = document.createElement("div");
  body.className = "org-comp-body";
  const list = document.createElement("div");
  list.className = "org-spec-list";

  const items = buildOrganizationDisplayItems(comp);
  if (!items.length) {
    list.appendChild(createEmptySpecHint(comp.kind));
  } else {
    items.forEach(inst => {
      list.appendChild(buildSpecTile(comp, inst, onMutated));
    });
  }

  const footer = document.createElement("div");
  footer.className = "org-card-footer";

  const footerActions = document.createElement("div");
  footerActions.className = "org-card-footer-actions";

  const addBtn = document.createElement("button");
  addBtn.type = "button";
  addBtn.className = "org-add-spec-btn";
  addBtn.innerHTML = `<span>+</span><span>${comp.kind === "article" ? "Add Block" : "Add Question"}</span>`;
  addBtn.onclick = e => {
    e.stopPropagation();
    openAddSpecPopover(addBtn, comp, onMutated);
  };

  const presetWrap = buildCardPresetMenuControl(comp, () => renderOrganizeCanvas());
  footerActions.append(addBtn, presetWrap);

  const countTxt = document.createElement("span");
  countTxt.className = "muted org-spec-count";
  countTxt.textContent = `${items.length} ${items.length === 1 ? "spec" : "specs"}`;
  footer.append(footerActions, countTxt);

  body.append(list, footer);
  card.append(header, body);

  attachCardDrag(header, card, {
    canDrag: () => state.prompt.components.length > 1 && document.activeElement !== titleInput,
    onCommit: commitCardOrderFromDom
  });

  return card;
}

function addNewComponent(kind, title) {
  closeAllDropdowns();
  emit("prompt:preset-modified");
  const created = cloneComponent({ kind, title, selections: [] });
  state.prompt.components.push(created);
  state.lastAddedOrgId = created.id;
  renderOrganizeCanvas();
  emit("prompt:refresh");
}

function buildAddComponentSlot() {
  const slot = document.createElement("div");
  slot.className = "org-add-comp-slot";

  const wrap = document.createElement("div");
  wrap.className = "menu-wrap";
  wrap.id = "addCompMenuWrap";

  const btn = document.createElement("button");
  btn.type = "button";
  btn.id = "btnAddComponentSlot";
  btn.className = "btn btn-h32 btn-primary";
  btn.innerHTML = "<span>+ Add Component</span><span>&#9662;</span>";
  btn.onclick = e => {
    e.stopPropagation();
    toggleDropdown("addComponentMenu");
  };

  const menu = document.createElement("div");
  menu.id = "addComponentMenu";
  menu.className = "dropdown-menu dropdown-menu-left hidden";

  const artBtn = document.createElement("button");
  artBtn.type = "button";
  artBtn.className = "dropdown-item dropdown-item-row";
  artBtn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getIcon("ui/article")}</span><span>Article</span></span><small>Teaching blocks</small>`;
  artBtn.onclick = e => {
    e.stopPropagation();
    addNewComponent("article", "New Article");
  };

  const quizBtn = document.createElement("button");
  quizBtn.type = "button";
  quizBtn.className = "dropdown-item dropdown-item-row";
  quizBtn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getIcon("ui/quiz")}</span><span>Quiz</span></span><small>Practice questions</small>`;
  quizBtn.onclick = e => {
    e.stopPropagation();
    addNewComponent("quiz", "New Quiz");
  };

  menu.append(artBtn, quizBtn);
  wrap.append(btn, menu);

  const hint = document.createElement("div");
  hint.className = "org-add-comp-hint";
  hint.textContent = "Append another Article or Quiz card to this study session.";

  slot.onclick = e => {
    if (e.target.closest("#addComponentMenu")) return;
    e.stopPropagation();
    toggleDropdown("addComponentMenu");
  };

  slot.append(wrap, hint);
  return slot;
}

export function renderOrganizeCanvas() {
  ensureComponentsForContainer();
  const track = document.getElementById("orgCanvasTrack");
  if (!track) return;

  const isSession = state.prompt.container === "session";
  const addNewWrap = document.getElementById("orgAddNewWrap");
  if (addNewWrap) addNewWrap.classList.toggle("hidden", !isSession);

  const prevScrollLeft = track.scrollLeft;
  const totalCards = state.prompt.components.length;
  track.classList.toggle("org-track-centered", !isSession || totalCards <= 1);

  const onMutated = arg => {
    if (arg?.deleteSpecId && arg?.comp) {
      removeSpecWithAnimation(arg.comp, arg.deleteSpecId);
      return;
    }
    renderOrganizeCanvas();
    emit("prompt:refresh");
  };

  track.innerHTML = "";
  let scrollTargetCard = null;
  state.prompt.components.forEach(comp => {
    const card = buildComponentCard(comp, isSession, totalCards, onMutated);
    if (state.lastAddedOrgId && comp.id === state.lastAddedOrgId) scrollTargetCard = card;
    track.appendChild(card);
  });

  if (isSession) track.appendChild(buildAddComponentSlot());
  track.scrollLeft = prevScrollLeft;
  if (scrollTargetCard) requestAnimationFrame(() => scrollTargetCard.scrollIntoView({ behavior: "smooth", inline: "nearest", block: "nearest" }));
  if (state.lastAddedSpecOrgId) {
    const t = track.querySelector(`.org-spec-tile[data-spec-id="${CSS.escape(state.lastAddedSpecOrgId)}"]`);
    if (t) requestAnimationFrame(() => t.scrollIntoView({ behavior: "smooth", block: "nearest" }));
  }

  state.lastAddedOrgId = null;
  state.lastAddedSpecOrgId = null;
}

export function wireOrganizeView() {
  wireSpecPopovers();
  wireOrganizePresetToolbar();
  registerPopover("addComponentMenu", { wrapSelector: ".org-add-comp-slot" });
  registerDropdown("btnOrgAddNew", "orgAddNewMenu", { wrapSelector: "#orgAddNewWrap" });

  document.addEventListener("click", e => {
    if (!e.target.closest(".card-preset-wrap")) {
      document.querySelectorAll(".card-preset-wrap .dropdown-menu").forEach(m => m.classList.add("hidden"));
    }
  });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape") {
      document.querySelectorAll(".card-preset-wrap .dropdown-menu").forEach(m => m.classList.add("hidden"));
    }
  });

  document.getElementById("optOrgAddArticle").onclick = () => addNewComponent("article", "New Article");
  document.getElementById("optOrgAddQuiz").onclick = () => addNewComponent("quiz", "New Quiz");
  document.getElementById("btnRefreshSpecs").onclick = () => emit("specs:reload", true);

  on("organize:presets-loaded", renderPresetMenu);
  on("organize:render", () => renderOrganizeCanvas());
}
