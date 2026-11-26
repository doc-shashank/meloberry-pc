import { state, emit, escapeHtml } from "../../store.js";
import { getSpecIcon } from "../../icons.js";
import { registerDropdown } from "../../ui/dropdowns.js";
import {
  getSpecModeStructure,
  findOrInitSelection,
  getActiveModeCounts,
  buildOrganizationDisplayItems,
  materializeOrderedItems,
  syncSelectionsFromOrderedItems
} from "./specModes.js";

export function renderOrgSubtypePicker(topic) {
  const wrap = document.getElementById("orgSubtypeDropWrap");
  const chosenDisplay = document.getElementById("orgSubtypeChosenDisplay");
  const menu = document.getElementById("orgSubtypeMenu");
  const { primaryGroup } = getSpecModeStructure(topic);

  if (!primaryGroup || !primaryGroup.options.length) {
    state.prompt.organizerChosenSubtype = "";
    wrap.classList.add("hidden");
    menu.classList.add("hidden");
    return;
  }

  wrap.classList.remove("hidden");
  const validKeys = primaryGroup.options.map(o => o.key);
  if (state.prompt.organizerChosenSubtype && !validKeys.includes(state.prompt.organizerChosenSubtype)) {
    state.prompt.organizerChosenSubtype = "";
  }

  const curSub = state.prompt.organizerChosenSubtype;
  const curOpt = primaryGroup.options.find(o => o.key === curSub);
  chosenDisplay.innerHTML = curOpt
    ? `<span class="spec-icon-wrap">${getSpecIcon(topic, curOpt.key)}</span><span>${escapeHtml(curOpt.label)}</span>`
    : `<span class="spec-icon-wrap">${getSpecIcon(topic)}</span><span>Generic</span>`;

  menu.innerHTML = "";
  const genBtn = document.createElement("button");
  genBtn.type = "button";
  genBtn.className = `dropdown-item dropdown-item-row ${!curSub ? "active" : ""}`;
  genBtn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(topic)}</span><span>Generic</span></span>`;
  genBtn.onclick = () => {
    state.prompt.organizerChosenSubtype = "";
    menu.classList.add("hidden");
    renderOrgSubtypePicker(topic);
  };
  menu.appendChild(genBtn);

  primaryGroup.options.forEach(opt => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `dropdown-item dropdown-item-row ${curSub === opt.key ? "active" : ""}`;
    btn.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(topic, opt.key)}</span><span>${escapeHtml(opt.label)}</span></span>`;
    btn.onclick = () => {
      state.prompt.organizerChosenSubtype = opt.key;
      menu.classList.add("hidden");
      renderOrgSubtypePicker(topic);
    };
    menu.appendChild(btn);
  });
}

export function renderSpecOrganizerPane(comp) {
  const targetCategory = comp.kind === "article" ? "block" : "question";
  const allowedSpecs = state.specs.filter(s => (s.category || "").toLowerCase() === targetCategory);

  if (!state.prompt.organizerChosenTopic || !allowedSpecs.some(s => s.topic.toLowerCase() === state.prompt.organizerChosenTopic.toLowerCase())) {
    state.prompt.organizerChosenTopic = allowedSpecs[0]?.topic || "";
    state.prompt.organizerChosenSubtype = "";
  }

  const chosen = state.prompt.organizerChosenTopic;
  document.getElementById("orgSpecChosenDisplay").innerHTML =
    `<span class="spec-icon-wrap">${getSpecIcon(chosen)}</span><span>${escapeHtml(chosen)}</span>`;
  renderOrgSubtypePicker(chosen);

  const hasCustomOrder = !!(comp.orderedItems && comp.orderedItems.length > 0);
  document.getElementById("btnSpecOrgResetOrder").classList.toggle("hidden", !hasCustomOrder);
  document.getElementById("specOrgHint").textContent = hasCustomOrder
    ? "Custom sequence active — drag boxes to reorder"
    : "Preview order — drag or + Add to customize exact sequence";

  const menu = document.getElementById("orgSpecMenu");
  menu.innerHTML = "";
  allowedSpecs.forEach(spec => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = `dropdown-item dropdown-item-row ${spec.topic.toLowerCase() === chosen.toLowerCase() ? "active" : ""}`;
    item.innerHTML = `<span class="row gap-8"><span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span></span>`;
    item.onclick = () => {
      state.prompt.organizerChosenTopic = spec.topic;
      state.prompt.organizerChosenSubtype = "";
      menu.classList.add("hidden");
      menu.querySelectorAll(".dropdown-item").forEach(b => b.classList.toggle("active", b === item));
      document.getElementById("orgSpecChosenDisplay").innerHTML =
        `<span class="spec-icon-wrap">${getSpecIcon(spec.topic)}</span><span>${escapeHtml(spec.topic)}</span>`;
      renderOrgSubtypePicker(spec.topic);
    };
    menu.appendChild(item);
  });

  const track = document.getElementById("specOrgTrack");
  track.innerHTML = "";
  const items = buildOrganizationDisplayItems(comp);
  if (!items.length) {
    state.lastAddedSpecOrgId = null;
    track.innerHTML = '<div class="muted">No items selected yet. Use + Add or switch to Selector.</div>';
    return;
  }

  items.forEach((inst, idx) => {
    const box = document.createElement("div");
    const isJustAdded = state.lastAddedSpecOrgId && inst.id === state.lastAddedSpecOrgId;
    box.className = `drag-box spec-org-box${isJustAdded ? " animate-add" : ""}`;
    box.title = inst.subtype ? `${inst.topic}:${inst.subtype} #${inst.instanceNumber}` : `${inst.topic} #${inst.instanceNumber}`;
    box.draggable = items.length > 1;

    const top = document.createElement("div");
    top.className = "drag-box-top";
    const badge = document.createElement("span");
    badge.className = inst.isAuto ? "drag-badge-auto" : "drag-badge";
    badge.textContent = inst.isAuto ? "Auto" : `#${inst.instanceNumber}`;

    const rm = document.createElement("button");
    rm.className = "drag-remove-btn";
    rm.textContent = "\u00d7";
    rm.onclick = e => {
      e.stopPropagation();
      emit("prompt:preset-modified");
      materializeOrderedItems(comp);
      comp.orderedItems.splice(idx, 1);
      if (!comp.orderedItems.length) comp.orderedItems = null;
      syncSelectionsFromOrderedItems(comp, inst.topic);
      renderSpecOrganizerPane(comp);
      emit("prompt:org-render");
      emit("prompt:refresh");
    };
    top.append(badge, rm);

    const sub = document.createElement("div");
    sub.className = "drag-box-sub";
    const labelText = inst.subtype || inst.topic;
    sub.innerHTML = `<span class="spec-icon-wrap spec-icon-org">${getSpecIcon(inst.topic, inst.subtype)}</span><span class="spec-org-label">${escapeHtml(labelText)}</span>`;

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
      emit("prompt:preset-modified");
      materializeOrderedItems(comp);
      const [moved] = comp.orderedItems.splice(state.dragSpecOrgIndex, 1);
      comp.orderedItems.splice(idx, 0, moved);
      syncSelectionsFromOrderedItems(comp, moved?.topic || inst.topic);
      renderSpecOrganizerPane(comp);
      emit("prompt:refresh");
    });

    track.appendChild(box);
    if (isJustAdded) {
      requestAnimationFrame(() => box.scrollIntoView({ behavior: "smooth", inline: "nearest", block: "nearest" }));
    }
  });
  state.lastAddedSpecOrgId = null;
}

export function wireSpecOrganizer({ getActiveComponent }) {
  registerDropdown("btnOrgSpecDrop", "orgSpecMenu", { wrapSelector: "#orgSpecDropWrap" });
  registerDropdown("btnOrgSubtypeDrop", "orgSubtypeMenu", { wrapSelector: "#orgSubtypeDropWrap" });

  document.getElementById("btnSpecOrgResetOrder").onclick = () => {
    emit("prompt:preset-modified");
    const comp = getActiveComponent();
    comp.orderedItems = null;
    renderSpecOrganizerPane(comp);
    emit("prompt:org-render");
    emit("prompt:refresh");
  };

  document.getElementById("btnSpecOrgAdd").onclick = () => {
    const topic = state.prompt.organizerChosenTopic;
    if (!topic) return;
    emit("prompt:preset-modified");
    const comp = getActiveComponent();
    const subtype = state.prompt.organizerChosenSubtype || null;
    if (!comp.orderedItems) {
      comp.orderedItems = [];
      comp.selections.filter(s => s.enabled).forEach(s => {
        if (s.desiredCount && s.desiredCount > 0) {
          const { primaryGroup } = getSpecModeStructure(s.topic);
          const counts = getActiveModeCounts(s, primaryGroup);
          let n = 0;
          Object.entries(counts).forEach(([sub, cnt]) => {
            for (let i = 0; i < cnt && n < s.desiredCount; i++) {
              n++;
              comp.orderedItems.push({ id: `${s.topic}_${n}_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: n, subtype: sub });
            }
          });
          while (n < s.desiredCount) {
            n++;
            comp.orderedItems.push({ id: `${s.topic}_${n}_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: n, subtype: null });
          }
        } else {
          comp.orderedItems.push({ id: `${s.topic}_1_${Math.random().toString(36).slice(2, 6)}`, topic: s.topic, instanceNumber: 1, subtype: null });
        }
      });
    }

    const sel = findOrInitSelection(comp, topic);
    const currentCount = comp.orderedItems.filter(i => i.topic.toLowerCase() === topic.toLowerCase()).length;
    const nextCount = currentCount + 1;
    sel.enabled = true;
    sel.desiredCount = nextCount;
    const newId = `${topic}_${Date.now()}_${Math.random().toString(36).slice(2, 5)}`;
    comp.orderedItems.push({ id: newId, topic, instanceNumber: nextCount, subtype });
    syncSelectionsFromOrderedItems(comp, topic);
    state.lastAddedSpecOrgId = newId;
    renderSpecOrganizerPane(comp);
    emit("prompt:org-render");
    emit("prompt:refresh");
  };
}
