import { state, emit, escapeHtml } from "../../store.js";
import { getSpecIcon } from "../../icons.js";
import { registerPopover, closeAllDropdowns, positionAnchoredPopover } from "../../ui/dropdowns.js";
import {
  findSpecMeta,
  getSpecModeStructure,
  findOrInitSelection,
  handleSecondaryChipToggle,
  materializeOrderedItems,
  syncSelectionsFromOrderedItems
} from "./specModes.js";

let addQtyChoice = 1;
let expandedAddTopic = null;

export function closeOrganizePopovers() {
  document.getElementById("addSpecPopover")?.classList.add("hidden");
  document.getElementById("specInspectorPopover")?.classList.add("hidden");
  state.activeAddSpecCompId = null;
  state.activeInspectorTarget = null;
}

function appendSpecInstances(comp, topic, subtype, qty, onMutated) {
  emit("prompt:preset-modified");
  materializeOrderedItems(comp);
  if (!comp.orderedItems) comp.orderedItems = [];

  const sel = findOrInitSelection(comp, topic);
  sel.enabled = true;
  let lastId = null;
  for (let i = 0; i < qty; i++) {
    lastId = `${comp.id}_${topic}_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`;
    comp.orderedItems.push({
      id: lastId,
      topic,
      instanceNumber: 1,
      subtype: subtype || null
    });
  }

  syncSelectionsFromOrderedItems(comp, topic);
  state.lastAddedSpecOrgId = lastId;
  closeOrganizePopovers();
  onMutated({ scrollSpecId: lastId });
}

export function openAddSpecPopover(anchorBtn, comp, onMutated) {
  closeAllDropdowns(["addSpecPopover"]);
  const pop = document.getElementById("addSpecPopover");
  if (!pop) return;

  pop.classList.toggle("kind-quiz", comp.kind === "quiz");
  state.activeAddSpecCompId = comp.id;
  expandedAddTopic = null;

  const targetCategory = comp.kind === "article" ? "block" : "question";
  const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

  const renderBody = () => {
    pop.innerHTML = "";

    const head = document.createElement("div");
    head.className = "popover-head";
    head.innerHTML = `<strong>${comp.kind === "article" ? "Add Article Block" : "Add Quiz Question"}</strong>`;
    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "btn btn-xs btn-ghost";
    closeBtn.textContent = "Close";
    closeBtn.onclick = () => closeOrganizePopovers();
    head.appendChild(closeBtn);
    pop.appendChild(head);

    const qtyRow = document.createElement("div");
    qtyRow.className = "add-spec-qty-row";
    qtyRow.innerHTML = "<span>Quick Qty</span>";
    const pills = document.createElement("div");
    pills.className = "qty-pill-group";
    [1, 2, 3, 5].forEach(n => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = `qty-pill-btn${addQtyChoice === n ? " active" : ""}`;
      b.textContent = `\u00d7${n}`;
      b.onclick = e => {
        e.stopPropagation();
        addQtyChoice = n;
        renderBody();
      };
      pills.appendChild(b);
    });
    qtyRow.appendChild(pills);
    pop.appendChild(qtyRow);

    const list = document.createElement("div");
    list.className = "add-spec-list";

    allowedSpecs.forEach(spec => {
      const { primaryGroup } = getSpecModeStructure(spec);
      const hasSubtypes = !!(primaryGroup && primaryGroup.options.length > 0);
      const isExpanded = expandedAddTopic && expandedAddTopic.toLowerCase() === spec.topic.toLowerCase();

      const item = document.createElement("button");
      item.type = "button";
      item.className = `add-spec-item${isExpanded ? " active" : ""}`;
      item.title = spec.displayName ? `${spec.displayName}: ${spec.summary || ""}` : (spec.summary || "");
      const nameBadge = spec.displayName && spec.displayName.toLowerCase() !== spec.topic.toLowerCase()
        ? `<small class="muted" style="font-size:11px;">${escapeHtml(spec.displayName)}</small>`
        : "";
      item.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(spec.topic, null, targetCategory)}</span><code>${escapeHtml(spec.topic)}</code>${nameBadge}</span><span class="muted">${hasSubtypes ? (isExpanded ? "\u25be" : "Subtypes \u25b8") : `+ Add \u00d7${addQtyChoice}`}</span>`;
      item.onclick = e => {
        e.stopPropagation();
        if (!hasSubtypes) {
          appendSpecInstances(comp, spec.topic, null, addQtyChoice, onMutated);
          return;
        }
        expandedAddTopic = isExpanded ? null : spec.topic;
        renderBody();
      };
      list.appendChild(item);

      if (hasSubtypes && isExpanded) {
        const subBox = document.createElement("div");
        subBox.className = "add-spec-subtypes";

        const genChip = document.createElement("button");
        genChip.type = "button";
        genChip.className = "mode-chip";
        genChip.innerHTML = `<span class="spec-icon-wrap">${getSpecIcon(spec.topic, null, targetCategory)}</span><span>Generic</span>`;
        genChip.onclick = e => {
          e.stopPropagation();
          appendSpecInstances(comp, spec.topic, null, addQtyChoice, onMutated);
        };
        subBox.appendChild(genChip);

        primaryGroup.options.forEach(opt => {
          const chip = document.createElement("button");
          chip.type = "button";
          chip.className = "mode-chip";
          chip.innerHTML = `<span class="spec-icon-wrap">${getSpecIcon(spec.topic, opt.key, targetCategory)}</span><span>${escapeHtml(opt.label)}</span>`;
          chip.onclick = e => {
            e.stopPropagation();
            appendSpecInstances(comp, spec.topic, opt.key, addQtyChoice, onMutated);
          };
          subBox.appendChild(chip);
        });
        list.appendChild(subBox);
      }
    });

    pop.appendChild(list);
  };

  renderBody();
  positionAnchoredPopover(pop, anchorBtn);
}

export function openSpecInspectorPopover(anchorBtn, comp, inst, onMutated) {
  closeAllDropdowns(["specInspectorPopover"]);
  const pop = document.getElementById("specInspectorPopover");
  if (!pop) return;

  pop.classList.toggle("kind-quiz", comp.kind === "quiz");

  const spec = findSpecMeta(inst.topic) || { topic: inst.topic, modeGroups: [], summary: "" };
  const { primaryGroup, secondaryGroups } = getSpecModeStructure(spec);

  materializeOrderedItems(comp);
  const liveInst = comp.orderedItems?.find(o => o.id === inst.id) || inst;
  const sel = findOrInitSelection(comp, liveInst.topic);
  state.activeInspectorTarget = { compId: comp.id, specId: liveInst.id };

  const renderInspector = () => {
    pop.innerHTML = "";

    const targetCategory = comp.kind === "quiz" ? "question" : "block";
    const head = document.createElement("div");
    head.className = "popover-head";
    const displayTag = spec.displayName ? `<span class="muted" style="font-size:11.5px;font-weight:600;">${escapeHtml(spec.displayName)}</span>` : "";
    head.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(liveInst.topic, liveInst.subtype, targetCategory)}</span><strong><code>${escapeHtml(liveInst.topic)}</code></strong>${displayTag}</span>`;
    const doneBtn = document.createElement("button");
    doneBtn.type = "button";
    doneBtn.className = "btn btn-xs btn-ghost";
    doneBtn.textContent = "Done";
    doneBtn.onclick = () => closeOrganizePopovers();
    head.appendChild(doneBtn);
    pop.appendChild(head);

    if (primaryGroup && primaryGroup.options.length > 0) {
      const sec = document.createElement("div");
      sec.className = "inspector-section";
      sec.innerHTML = `<div class="inspector-label">${escapeHtml(primaryGroup.label || "Subtype")}</div>`;
      const chips = document.createElement("div");
      chips.className = "inspector-chips";

      const genBtn = document.createElement("button");
      genBtn.type = "button";
      genBtn.className = `mode-chip${!liveInst.subtype ? " active" : ""}`;
      genBtn.textContent = "Generic";
      genBtn.onclick = e => {
        e.stopPropagation();
        emit("prompt:preset-modified");
        liveInst.subtype = null;
        syncSelectionsFromOrderedItems(comp, liveInst.topic);
        renderInspector();
        onMutated({ keepPopovers: true });
      };
      chips.appendChild(genBtn);

      primaryGroup.options.forEach(opt => {
        const b = document.createElement("button");
        b.type = "button";
        b.className = `mode-chip${liveInst.subtype === opt.key ? " active" : ""}`;
        b.innerHTML = `<span class="spec-icon-wrap">${getSpecIcon(liveInst.topic, opt.key, targetCategory)}</span><span>${escapeHtml(opt.label)}</span>`;
        b.onclick = e => {
          e.stopPropagation();
          emit("prompt:preset-modified");
          liveInst.subtype = opt.key;
          syncSelectionsFromOrderedItems(comp, liveInst.topic);
          renderInspector();
          onMutated({ keepPopovers: true });
        };
        chips.appendChild(b);
      });

      sec.appendChild(chips);
      pop.appendChild(sec);
    }

    secondaryGroups.forEach(g => {
      const sec = document.createElement("div");
      sec.className = "inspector-section";
      sec.innerHTML = `<div class="inspector-label">${escapeHtml(g.label)}</div>`;
      const chips = document.createElement("div");
      chips.className = "inspector-chips";

      g.options.forEach(opt => {
        const b = document.createElement("button");
        b.type = "button";
        const isActive = (sel.modes || []).includes(opt.key);
        b.className = `mode-chip${isActive ? " active" : ""}`;
        b.textContent = opt.label;
        b.onclick = e => {
          e.stopPropagation();
          emit("prompt:preset-modified");
          handleSecondaryChipToggle(spec, sel, opt.key, g);
          syncSelectionsFromOrderedItems(comp, liveInst.topic);
          renderInspector();
          onMutated({ keepPopovers: true });
        };
        chips.appendChild(b);
      });

      sec.appendChild(chips);
      pop.appendChild(sec);
    });

    const noteSec = document.createElement("div");
    noteSec.className = "inspector-section";
    noteSec.innerHTML = `<div class="inspector-label">Instructions for <code>${escapeHtml(liveInst.topic)}</code></div>`;
    const ta = document.createElement("textarea");
    ta.className = "textarea fixed-textarea";
    ta.rows = 3;
    ta.placeholder = "Specific instructions for this spec...";
    ta.value = sel.notes || "";
    ta.oninput = e => {
      emit("prompt:preset-modified");
      sel.notes = e.target.value;
      const tile = document.querySelector(`.org-spec-tile[data-spec-id="${CSS.escape(liveInst.id)}"]`);
      const dot = tile?.querySelector(".has-note-dot");
      if (dot) dot.classList.toggle("hidden", !sel.notes.trim());
      emit("prompt:schedule-preview", 120);
    };
    noteSec.appendChild(ta);
    pop.appendChild(noteSec);

    const contentSec = document.createElement("div");
    contentSec.className = "inspector-section";
    const cMode = liveInst.content?.mode || "none";
    const cLabel = cMode === "rawText" ? "Raw Text" : (cMode === "markdownFile" ? ".md File" : "Empty");
    const cBar = document.createElement("div");
    cBar.className = "inspector-content-bar";
    cBar.innerHTML = `<span class="inspector-label" style="margin:0">Spec Content: <strong>${escapeHtml(cLabel)}</strong></span>`;
    const cBtns = document.createElement("div");
    cBtns.className = "row gap-6";

    const editContentBtn = document.createElement("button");
    editContentBtn.type = "button";
    editContentBtn.className = "btn btn-xs btn-primary";
    editContentBtn.textContent = cMode === "none" ? "Add Content" : "Edit Content";
    editContentBtn.onclick = e => {
      e.stopPropagation();
      emit("spec:open-content-modal", { compId: comp.id, instId: liveInst.id, focusMelomg: false });
    };
    cBtns.appendChild(editContentBtn);

    const imgTopics = new Set(["image", "diagram-explore", "relabel", "flashcard", "mcq", "spot-error", "numeric", "case"]);
    if (imgTopics.has((liveInst.topic || "").toLowerCase())) {
      const figBtn = document.createElement("button");
      figBtn.type = "button";
      figBtn.className = "btn btn-xs btn-ghost";
      figBtn.textContent = "Extract Figure";
      figBtn.onclick = e => {
        e.stopPropagation();
        emit("spec:open-content-modal", { compId: comp.id, instId: liveInst.id, focusMelomg: true });
      };
      cBtns.appendChild(figBtn);
    }

    cBar.appendChild(cBtns);
    contentSec.appendChild(cBar);
    pop.appendChild(contentSec);

    const foot = document.createElement("div");
    foot.className = "inspector-foot";

    const dupBtn = document.createElement("button");
    dupBtn.type = "button";
    dupBtn.className = "btn btn-xs btn-ghost";
    dupBtn.textContent = "Duplicate";
    dupBtn.onclick = e => {
      e.stopPropagation();
      emit("prompt:preset-modified");
      const idx = comp.orderedItems.findIndex(o => o.id === liveInst.id);
      const copyId = `${liveInst.topic}_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`;
      const clone = {
        id: copyId,
        topic: liveInst.topic,
        instanceNumber: 1,
        subtype: liveInst.subtype || null,
        content: liveInst.content ? { ...liveInst.content } : null
      };
      comp.orderedItems.splice(idx >= 0 ? idx + 1 : comp.orderedItems.length, 0, clone);
      syncSelectionsFromOrderedItems(comp, liveInst.topic);
      state.lastAddedSpecOrgId = copyId;
      closeOrganizePopovers();
      onMutated({ scrollSpecId: copyId });
    };

    const delBtn = document.createElement("button");
    delBtn.type = "button";
    delBtn.className = "btn btn-xs btn-danger-ghost";
    delBtn.textContent = "Delete";
    delBtn.onclick = e => {
      e.stopPropagation();
      closeOrganizePopovers();
      onMutated({ deleteSpecId: liveInst.id, comp });
    };

    foot.append(dupBtn, delBtn);
    pop.appendChild(foot);
  };

  renderInspector();
  positionAnchoredPopover(pop, anchorBtn);
}

export function wireSpecPopovers() {
  registerPopover("addSpecPopover", {
    wrapSelector: "#addSpecPopover, .org-add-spec-btn",
    onClose: () => { state.activeAddSpecCompId = null; }
  });
  registerPopover("specInspectorPopover", {
    wrapSelector: "#specInspectorPopover, .org-spec-tile",
    onClose: () => { state.activeInspectorTarget = null; }
  });
}
