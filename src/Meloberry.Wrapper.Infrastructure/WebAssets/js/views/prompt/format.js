import { api } from "../../api.js";
import { state, emit, on } from "../../store.js";
import {
  cloneComponent,
  ensureComponentsForContainer,
  markPresetModified,
  refreshPromptPreview,
  schedulePromptPreview
} from "./promptDraft.js";

export function syncWorkflowModeUi() {
  const mode = state.prompt.workflowMode || "direct";
  document.querySelectorAll("#workflowModeTabs .seg-btn, #reviewWorkflowModeTabs .seg-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.wfMode === mode);
  });

  const hintEl = document.getElementById("workflowModeHint");
  if (hintEl) {
    if (mode === "direct") {
      hintEl.textContent = "Direct authoring: fill spec contents directly in Organize and build with zero prompt overhead.";
    } else if (mode === "liveAgent") {
      hintEl.textContent = "Live Agent: share your live blueprint and let an AI agent edit Organize & build via mby-pc.bat.";
    } else {
      hintEl.textContent = "Classic Prompt: compose full AI authoring prompts and workspace AGENTS.md playbooks.";
    }
  }

  const notesCard = document.getElementById("formatNotesCard");
  if (notesCard) {
    notesCard.classList.toggle("hidden", mode === "direct");
  }
}

export function syncFormatInputs() {
  document.querySelectorAll("#containerTabs .seg-btn").forEach(b => {
    b.classList.toggle("active", b.dataset.container === state.prompt.container);
  });
  const fmt = document.getElementById("promptFormat");
  if (fmt) fmt.value = state.prompt.authoringFormat;
  syncWorkflowModeUi();
}

export function renderPromptAttachments() {
  const box = document.getElementById("promptAttachmentsList");
  if (!box) return;
  box.innerHTML = "";
  const items = state.prompt.extraAttachments || [];
  box.classList.toggle("hidden", !items.length);
  items.forEach((a, idx) => {
    const chip = document.createElement("span");
    chip.className = "chip";
    chip.textContent = a.filePath.split(/[\\/]/).pop();
    const rm = document.createElement("button");
    rm.textContent = "x";
    rm.onclick = () => {
      state.prompt.extraAttachments.splice(idx, 1);
      renderPromptAttachments();
      refreshPromptPreview();
    };
    chip.appendChild(rm);
    box.appendChild(chip);
  });
}

export function wireFormatView() {
  on("format:sync", syncFormatInputs);
  on("prompt:attachments-changed", renderPromptAttachments);

  document.querySelectorAll("#containerTabs .seg-btn").forEach(btn => {
    btn.onclick = () => {
      const prev = state.prompt.container;
      const next = btn.dataset.container;
      if (prev === next) return;

      markPresetModified();
      state.prompt.containerCache[prev] = {
        components: state.prompt.components.map(cloneComponent)
      };

      state.prompt.container = next;
      syncFormatInputs();

      const cached = state.prompt.containerCache[next];
      if (cached && Array.isArray(cached.components) && cached.components.length > 0) {
        state.prompt.components = cached.components.map(cloneComponent);
      } else {
        state.prompt.components = state.prompt.components.map(cloneComponent);
        ensureComponentsForContainer();
      }

      emit("organize:render");
      refreshPromptPreview();
    };
  });

  ["promptTitle", "promptDescription", "promptSourceText"].forEach(id => {
    const el = document.getElementById(id);
    el.addEventListener("input", () => schedulePromptPreview(120));
    el.addEventListener("change", () => refreshPromptPreview());
  });

  const fmtEl = document.getElementById("promptFormat");
  fmtEl.addEventListener("input", () => {
    state.prompt.authoringFormat = fmtEl.value;
    markPresetModified();
    refreshPromptPreview();
  });
  fmtEl.addEventListener("change", () => {
    state.prompt.authoringFormat = fmtEl.value;
    markPresetModified();
    refreshPromptPreview();
  });

  document.getElementById("btnAttachPromptFiles").onclick = async () => {
    const r = await api("POST", "/api/dialogs/pick-files", { title: "Attach Files", allowMultiple: true });
    if (r.ok && !r.data.cancelled) {
      (r.data.paths || []).forEach(p => {
        if (!state.prompt.extraAttachments.some(a => a.filePath.toLowerCase() === p.toLowerCase())) {
          state.prompt.extraAttachments.push({ filePath: p, kind: p.toLowerCase().endsWith(".pdf") ? "pdf" : "file" });
        }
      });
      renderPromptAttachments();
      refreshPromptPreview();
    }
  };

  document.querySelectorAll("#workflowModeTabs .seg-btn, #reviewWorkflowModeTabs .seg-btn").forEach(btn => {
    btn.addEventListener("click", () => {
      const next = btn.dataset.wfMode || "direct";
      if (state.prompt.workflowMode === next) return;
      state.prompt.workflowMode = next;
      emit("format:sync");
      refreshPromptPreview();
    });
  });

  syncWorkflowModeUi();
}
