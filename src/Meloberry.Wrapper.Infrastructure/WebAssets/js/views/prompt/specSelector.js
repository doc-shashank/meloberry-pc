import { state, emit, escapeHtml } from "../../store.js";
import { getIcon, getSpecIcon } from "../../icons.js";
import { registerDropdown, registerPopover, closeAllDropdowns, positionAnchoredPopover } from "../../ui/dropdowns.js";
import {
  getSpecModeStructure,
  findOrInitSelection,
  getActiveModeCounts,
  getPrimarySubtypeKeys,
  syncModesFromDistribution,
  applyTotalQtyChange,
  handlePrimaryChipClick,
  handleSecondaryChipToggle,
  syncOrderedItemsFromSelections
} from "./specModes.js";

export function closeDistPopover() {
  document.getElementById("distPopover")?.classList.add("hidden");
  state.activeDistTarget = null;
}

export function openDistPopover(anchorBtn, comp, spec, sel, qtyInputEl = null) {
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
      emit("prompt:preset-modified");
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
      }
      renderSpecSelectorPane(comp);
      emit("prompt:refresh");
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
          emit("prompt:preset-modified");
          handleSecondaryChipToggle(spec, sel, opt.key, g);
          openDistPopover(anchorBtn, comp, spec, sel, qtyInputEl);
          renderSpecSelectorPane(comp);
          emit("prompt:refresh");
        };
        modRow.appendChild(btn);
      });
    });
    pop.appendChild(modRow);
  }

  positionAnchoredPopover(pop, anchorBtn);
}

export function openNotePopover(anchorBtn, comp, sel) {
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

export function closeNotePopover() {
  document.getElementById("notePopover").classList.add("hidden");
  state.activeNoteTarget = null;
}

export function renderSpecSelectorRowBadges(rowEl, spec, sel) {
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

export function renderSpecSelectorPane(comp) {
  const targetCategory = comp.kind === "article" ? "block" : "question";
  const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

  document.getElementById("selectorGroupTitle").textContent = comp.kind === "article" ? "Article Blocks" : "Questions";
  document.getElementById("selectorAddSpecLabel").textContent = comp.kind === "article" ? "+ Add Block" : "+ Add Question";

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
      emit("prompt:preset-modified");
      const target = findOrInitSelection(comp, spec.topic);
      const wasEnabled = !!target.enabled;
      target.enabled = true;
      if (!target.desiredCount || target.desiredCount <= 0) target.desiredCount = 1;
      if (!wasEnabled) state.lastAddedSelectorTopic = spec.topic;
      if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
      renderSpecSelectorPane(comp);
      emit("prompt:org-render");
      emit("prompt:refresh");
    };
    menu.appendChild(item);
  });

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

    const { primaryGroup } = getSpecModeStructure(spec);
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
      emit("prompt:preset-modified");
      const raw = cnt.value.trim();
      const parsed = raw === "" ? NaN : parseInt(raw, 10);
      if (raw === "" || !Number.isFinite(parsed) || parsed <= 0) {
        applyTotalQtyChange(sel, spec, null);
        if (comp.orderedItems) {
          comp.orderedItems = comp.orderedItems.filter(o => o.topic.toLowerCase() !== spec.topic.toLowerCase());
          if (!comp.orderedItems.length) comp.orderedItems = null;
        }
        renderSpecSelectorRowBadges(row, spec, sel);
        if (state.activeDistTarget?.sel === sel) openDistPopover(state.activeDistTarget.anchorBtn, comp, spec, sel, cnt);
        emit("prompt:refresh");
        return;
      }
      const val = Math.min(99, parsed);
      if (val !== parsed) cnt.value = String(val);
      applyTotalQtyChange(sel, spec, val);
      if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
      renderSpecSelectorRowBadges(row, spec, sel);
      if (state.activeDistTarget?.sel === sel) openDistPopover(state.activeDistTarget.anchorBtn, comp, spec, sel, cnt);
      emit("prompt:refresh");
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
            emit("prompt:preset-modified");
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
            emit("prompt:refresh");
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
      emit("prompt:preset-modified");
      sel.enabled = false;
      sel.desiredCount = null;
      sel.modeCounts = null;
      if (comp.orderedItems) syncOrderedItemsFromSelections(comp);
      renderSpecSelectorPane(comp);
      emit("prompt:org-render");
      emit("prompt:refresh");
    };
    rmCell.appendChild(rm);

    row.append(lbl, qtyCell, distCell, modeCell, pencilCell, rmCell);
    root.appendChild(row);
  });
  state.lastAddedSelectorTopic = null;
}

export function wireSpecSelector() {
  registerDropdown("btnSelectorAddSpecDrop", "selectorSpecMenu", { wrapSelector: "#selectorSpecDropWrap" });

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

  document.getElementById("notePopoverText").oninput = e => {
    if (!state.activeNoteTarget) return;
    emit("prompt:preset-modified");
    state.activeNoteTarget.sel.notes = e.target.value;
    emit("prompt:schedule-preview", 120);
  };
  document.getElementById("btnCloseNotePopover").onclick = () => {
    if (state.activeNoteTarget) renderSpecSelectorPane(state.activeNoteTarget.comp);
    closeNotePopover();
  };
}
