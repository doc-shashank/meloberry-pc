import { state } from "../store.js";

export function closeConfirmModal() {
  document.getElementById("confirmAgentsModal").classList.add("hidden");
  state.activeConfirmAction = null;
}

export function openConfirmModal({ title, body, preText = null, proceedText = "Confirm", showProceed = true, onProceed = null }) {
  document.getElementById("confirmModalTitle").textContent = title;
  document.getElementById("confirmModalBody").textContent = body;
  const pre = document.getElementById("agentsPreviewModalCode");
  if (preText !== null && preText !== undefined) {
    pre.textContent = preText;
    pre.classList.remove("hidden");
  } else {
    pre.classList.add("hidden");
  }

  const proceed = document.getElementById("btnConfirmModalProceed");
  proceed.textContent = proceedText;
  proceed.classList.toggle("hidden", !showProceed);
  state.activeConfirmAction = onProceed;
  document.getElementById("confirmAgentsModal").classList.remove("hidden");
}

export function openInspectModal({ title, body, preText }) {
  openConfirmModal({ title, body, preText, showProceed: false, onProceed: null });
}

export function openInputPromptModal(title, message, defaultValue = "") {
  return new Promise(resolve => {
    state.activeInputResolve = resolve;
    document.getElementById("inputModalTitle").textContent = title;
    document.getElementById("inputModalMessage").textContent = message;
    const field = document.getElementById("inputModalField");
    field.value = defaultValue;
    document.getElementById("inputPromptModal").classList.remove("hidden");
    field.focus();
    field.select();
  });
}

export function closeInputPromptModal(result = null) {
  document.getElementById("inputPromptModal").classList.add("hidden");
  const resolve = state.activeInputResolve;
  state.activeInputResolve = null;
  if (resolve) resolve(result);
}

export function showPathTooltip(targetEl, fullPath) {
  const tip = document.getElementById("hoverPathTooltip");
  tip.textContent = fullPath;
  tip.classList.remove("hidden");
  const rect = targetEl.getBoundingClientRect();
  const tipRect = tip.getBoundingClientRect();
  const left = Math.max(8, Math.min(window.innerWidth - tipRect.width - 8, rect.left));
  const preferredTop = rect.top - tipRect.height - 8;
  const top = preferredTop >= 8
    ? preferredTop
    : Math.min(window.innerHeight - tipRect.height - 8, rect.bottom + 8);
  tip.style.left = `${left}px`;
  tip.style.top = `${Math.max(8, top)}px`;
}

export function hidePathTooltip() {
  clearTimeout(state.hoverTimer);
  document.getElementById("hoverPathTooltip").classList.add("hidden");
}

export function wireModalControls() {
  document.getElementById("btnConfirmModalCancel").onclick = closeConfirmModal;
  document.getElementById("btnConfirmModalProceed").onclick = () => {
    if (state.activeConfirmAction) state.activeConfirmAction();
  };
  document.getElementById("confirmAgentsModal").addEventListener("click", e => {
    if (e.target === e.currentTarget) closeConfirmModal();
  });

  document.getElementById("btnInputModalCancel").onclick = () => closeInputPromptModal(null);
  document.getElementById("btnInputModalOk").onclick = () => closeInputPromptModal(document.getElementById("inputModalField").value);
  document.getElementById("inputModalField").addEventListener("keydown", e => {
    if (e.key === "Enter") {
      e.preventDefault();
      closeInputPromptModal(document.getElementById("inputModalField").value);
    }
  });
  document.getElementById("inputPromptModal").addEventListener("click", e => {
    if (e.target === e.currentTarget) closeInputPromptModal(null);
  });
}
