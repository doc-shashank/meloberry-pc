import { api } from "../../api.js";
import { getIcon } from "../../icons.js";
import { state, emit } from "../../store.js";
import { registerDropdown, closeAllDropdowns } from "../../ui/dropdowns.js";
import { toast } from "../../ui/chrome.js";
import { applyPresetObj, buildPromptDraftPayload, cloneComponent, loadPresets } from "./promptDraft.js";

export function buildCardPresetMenuControl(comp, onReloaded) {
  const presetWrap = document.createElement("div");
  presetWrap.className = "menu-wrap card-preset-wrap";

  const presetBtn = document.createElement("button");
  presetBtn.type = "button";
  presetBtn.className = "btn btn-h30 btn-ghost card-preset-btn";
  presetBtn.innerHTML = "<span>Presets</span><span>&#9662;</span>";

  const presetMenu = document.createElement("div");
  presetMenu.id = `cardPresetMenu_${comp.id}`;
  presetMenu.className = "dropdown-menu dropdown-menu-left dropdown-up hidden";

  const saveCardBtn = document.createElement("button");
  saveCardBtn.type = "button";
  saveCardBtn.className = "dropdown-item";
  saveCardBtn.innerHTML = `<span class="dropdown-item-head"><span class="icon-slot">${getIcon("ui/save")}</span><span>Save</span></span><small>Export this card as a reusable preset</small>`;
  saveCardBtn.onclick = async e => {
    e.stopPropagation();
    closeAllDropdowns();
    const res = await api("POST", "/api/presets/component/save", {
      component: {
        id: comp.id,
        kind: comp.kind,
        title: comp.title || (comp.kind === "article" ? "Study Article" : "Practice Quiz"),
        selections: (comp.selections || []).filter(s => s.enabled),
        orderedItems: comp.orderedItems || null
      },
      suggestedName: comp.title
    });
    if (res.ok && !res.data?.cancelled) toast(`Saved card preset to ${res.data}`, "ok");
    else if (!res.ok) toast(res.data?.error?.description || "Failed to save card preset.", "err");
  };

  const loadCardBtn = document.createElement("button");
  loadCardBtn.type = "button";
  loadCardBtn.className = "dropdown-item";
  loadCardBtn.innerHTML = `<span class="dropdown-item-head"><span class="icon-slot">${getIcon("ui/folder-open")}</span><span>Load</span></span><small>Load a saved ${comp.kind} preset into this card</small>`;
  loadCardBtn.onclick = async e => {
    e.stopPropagation();
    closeAllDropdowns();
    const res = await api("POST", "/api/presets/component/load", { expectedKind: comp.kind });
    if (res.ok && !res.data?.cancelled) {
      emit("prompt:preset-modified");
      const loaded = cloneComponent(res.data);
      comp.title = loaded.title;
      comp.selections = loaded.selections;
      comp.orderedItems = loaded.orderedItems;
      onReloaded();
      emit("prompt:refresh");
      toast(`Loaded "${comp.title}" preset.`, "ok");
    } else if (!res.ok) {
      toast(res.data?.error?.description || "Failed to load card preset.", "err");
    }
  };

  presetMenu.append(saveCardBtn, loadCardBtn);
  presetBtn.onclick = e => {
    e.stopPropagation();
    const isHidden = presetMenu.classList.contains("hidden");
    closeAllDropdowns();
    if (isHidden) presetMenu.classList.remove("hidden");
  };
  presetWrap.append(presetBtn, presetMenu);
  return presetWrap;
}

function buildPresetMenuRow(preset, canDelete) {
  const item = document.createElement("button");
  item.type = "button";
  item.className = `dropdown-item dropdown-item-row ${state.activePresetName === preset.name ? "active" : ""}`;
  const span = document.createElement("span");
  span.textContent = preset.name;
  item.appendChild(span);

  if (canDelete) {
    const del = document.createElement("button");
    del.type = "button";
    del.className = "preset-del-btn";
    del.textContent = "\u00d7";
    del.onclick = async e => {
      e.stopPropagation();
      await api("DELETE", `/api/presets?id=${encodeURIComponent(preset.id)}`);
      if (state.activePresetName === preset.name) state.activePresetName = "";
      await loadPresets();
    };
    item.appendChild(del);
  }

  item.onclick = () => {
    document.getElementById("presetDropdownMenu").classList.add("hidden");
    state.activePresetName = preset.name;
    applyPresetObj(preset);
    renderPresetMenu();
  };
  return item;
}

export function renderPresetMenu() {
  const menu = document.getElementById("presetDropdownMenu");
  if (!menu) return;
  menu.innerHTML = "";

  const groups = [
    { label: "SESSION", items: state.presets.filter(p => p.isBuiltIn && p.container === "session") },
    { label: "ARTICLE", items: state.presets.filter(p => p.isBuiltIn && p.container === "standaloneArticle") },
    { label: "QUIZ", items: state.presets.filter(p => p.isBuiltIn && p.container === "standaloneQuiz") }
  ];

  groups.forEach((g, idx) => {
    if (!g.items.length) return;
    if (idx > 0) {
      const div = document.createElement("div");
      div.className = "menu-divider";
      menu.appendChild(div);
    }
    const hdr = document.createElement("div");
    hdr.className = "dropdown-heading";
    hdr.textContent = g.label;
    menu.appendChild(hdr);
    g.items.forEach(p => menu.appendChild(buildPresetMenuRow(p, false)));
  });

  const custom = state.presets.filter(p => !p.isBuiltIn);
  const custDiv = document.createElement("div");
  custDiv.className = "menu-divider";
  menu.appendChild(custDiv);

  const custHdr = document.createElement("div");
  custHdr.className = "dropdown-heading";
  custHdr.textContent = "CUSTOM";
  menu.appendChild(custHdr);

  custom.forEach(p => menu.appendChild(buildPresetMenuRow(p, true)));

  const impBtn = document.createElement("button");
  impBtn.type = "button";
  impBtn.className = "dropdown-item";
  impBtn.innerHTML = `<span class="dropdown-item-head"><span class="icon-slot">${getIcon("ui/folder-open")}</span><span>Load Preset...</span></span><small>Load a saved workflow preset file</small>`;
  impBtn.onclick = async () => {
    closeAllDropdowns();
    const res = await api("POST", "/api/presets/import-workflow", {});
    if (res.ok && !res.data?.cancelled) {
      await loadPresets();
      state.activePresetName = res.data.name;
      applyPresetObj(res.data);
      renderPresetMenu();
      toast(`Loaded preset "${res.data.name}"`, "ok");
    } else if (!res.ok) {
      toast(res.data?.error?.description || "Load failed.", "err");
    }
  };
  menu.appendChild(impBtn);

  const lbl = document.getElementById("presetBtnLabel");
  if (lbl) lbl.textContent = state.activePresetName || "Preset";
}

export function wireOrganizePresetToolbar() {
  registerDropdown("btnPresetDropdown", "presetDropdownMenu", { wrapSelector: "#presetMenuWrap" });

  const btnSavePreset = document.getElementById("btnSavePreset");
  btnSavePreset.onclick = async () => {
    closeAllDropdowns();
    const draft = buildPromptDraftPayload();
    const res = await api("POST", "/api/presets/workflow/save", {
      id: "",
      name: state.activePresetName || draft.title || "Custom Preset",
      description: "Saved workflow preset",
      container: draft.container,
      authoringFormat: draft.authoringFormat,
      selections: draft.selections,
      components: draft.components
    });
    if (res.ok && !res.data?.cancelled) {
      state.activePresetName = res.data.preset?.name || state.activePresetName;
      await loadPresets();
      toast(`Saved preset "${res.data.preset?.name || "Preset"}"`, "ok");
    } else if (!res.ok) {
      toast(res.data?.error?.description || "Failed to save preset.", "err");
    }
  };
}
