import { closeAllDropdowns } from "../../ui/dropdowns.js";
import { flipReorder } from "./organizeFlip.js";

function createDragGhost(sourceEl) {
  const rect = sourceEl.getBoundingClientRect();
  const ghost = sourceEl.cloneNode(true);
  ghost.classList.remove("dragging", "animate-add");
  ghost.classList.add("org-drag-ghost");
  ghost.style.left = `${rect.left}px`;
  ghost.style.top = `${rect.top}px`;
  ghost.style.width = `${rect.width}px`;
  ghost.style.height = `${rect.height}px`;
  document.body.appendChild(ghost);
  return { ghost, rect };
}

function findCardTargetBeforeNode(trackEl, draggedCard, clientX) {
  const trackRect = trackEl.getBoundingClientRect();
  const pointerX = (clientX - trackRect.left) + trackEl.scrollLeft;
  const others = Array.from(trackEl.querySelectorAll(".org-comp-card")).filter(c => c !== draggedCard);

  for (const sibling of others) {
    const centerX = sibling.offsetLeft + sibling.offsetWidth / 2;
    if (pointerX < centerX) {
      return sibling;
    }
  }

  return trackEl.querySelector(".org-add-comp-slot") || null;
}

export function attachCardDrag(headerEl, cardEl, { canDrag, onCommit }) {
  headerEl.addEventListener("pointerdown", downEvt => {
    if (downEvt.button !== 0 || downEvt.target.closest("input, button, a, select, textarea")) return;
    if (canDrag && !canDrag()) return;

    const trackEl = cardEl.closest("#orgCanvasTrack");
    if (!trackEl) return;

    const startX = downEvt.clientX;
    const startY = downEvt.clientY;
    let active = false;
    let ghost = null;
    let grabX = 0;
    let grabY = 0;
    let didReorder = false;

    const handleMove = moveEvt => {
      const dist = Math.hypot(moveEvt.clientX - startX, moveEvt.clientY - startY);
      if (!active) {
        if (dist < 5) return;
        active = true;
        closeAllDropdowns();
        const created = createDragGhost(cardEl);
        ghost = created.ghost;
        grabX = startX - created.rect.left;
        grabY = startY - created.rect.top;
        cardEl.classList.add("dragging");
        document.body.style.userSelect = "none";
      }

      if (ghost) {
        ghost.style.left = `${moveEvt.clientX - grabX}px`;
        ghost.style.top = `${moveEvt.clientY - grabY}px`;
      }

      const trackRect = trackEl.getBoundingClientRect();
      if (moveEvt.clientX < trackRect.left + 48) trackEl.scrollLeft -= 18;
      else if (moveEvt.clientX > trackRect.right - 48) trackEl.scrollLeft += 18;

      const beforeNode = findCardTargetBeforeNode(trackEl, cardEl, moveEvt.clientX);
      if (cardEl.nextElementSibling !== beforeNode) {
        didReorder = true;
        flipReorder(trackEl, ".org-comp-card, .org-add-comp-slot", cardEl, () => {
          trackEl.insertBefore(cardEl, beforeNode);
        });
      }
    };

    const cleanup = () => {
      window.removeEventListener("pointermove", handleMove);
      window.removeEventListener("pointerup", handleUp);
      window.removeEventListener("pointercancel", handleCancel);
      if (ghost) ghost.remove();
      document.body.style.userSelect = "";
      cardEl.classList.remove("dragging");
    };

    const handleUp = () => {
      cleanup();
      if (active && didReorder) {
        onCommit();
      }
    };

    const handleCancel = () => {
      cleanup();
    };

    window.addEventListener("pointermove", handleMove);
    window.addEventListener("pointerup", handleUp);
    window.addEventListener("pointercancel", handleCancel);
  });
}

function findMatchingCardAtPoint(trackEl, kind, clientX, clientY) {
  const cards = Array.from(trackEl.querySelectorAll(`.org-comp-card.kind-${kind}`));
  for (const card of cards) {
    const r = card.getBoundingClientRect();
    if (clientX >= r.left && clientX <= r.right && clientY >= r.top && clientY <= r.bottom) {
      return card;
    }
  }
  return null;
}

function findSpecTargetBeforeNode(listEl, draggedTile, clientY) {
  const listRect = listEl.getBoundingClientRect();
  const pointerY = (clientY - listRect.top) + listEl.scrollTop;
  const others = Array.from(listEl.querySelectorAll(".org-spec-tile")).filter(t => t !== draggedTile);

  for (const sibling of others) {
    const centerY = sibling.offsetTop + sibling.offsetHeight / 2;
    if (pointerY < centerY) {
      return sibling;
    }
  }

  return null;
}

export function attachSpecDrag(tileEl, comp, { onClick, onCommit }) {
  tileEl.addEventListener("pointerdown", downEvt => {
    if (downEvt.button !== 0 || downEvt.target.closest("button, .org-spec-remove")) return;

    const trackEl = tileEl.closest("#orgCanvasTrack");
    if (!trackEl) return;

    const startX = downEvt.clientX;
    const startY = downEvt.clientY;
    let active = false;
    let ghost = null;
    let grabX = 0;
    let grabY = 0;
    let didReorder = false;

    const clearHighlights = () => {
      trackEl.querySelectorAll(".org-comp-body.spec-drop-target").forEach(b => b.classList.remove("spec-drop-target"));
    };

    const handleMove = moveEvt => {
      const dist = Math.hypot(moveEvt.clientX - startX, moveEvt.clientY - startY);
      if (!active) {
        if (dist < 5) return;
        active = true;
        closeAllDropdowns();
        const created = createDragGhost(tileEl);
        ghost = created.ghost;
        grabX = startX - created.rect.left;
        grabY = startY - created.rect.top;
        tileEl.classList.add("dragging");
        document.body.style.userSelect = "none";
      }

      if (ghost) {
        ghost.style.left = `${moveEvt.clientX - grabX}px`;
        ghost.style.top = `${moveEvt.clientY - grabY}px`;
      }

      const targetCard = findMatchingCardAtPoint(trackEl, comp.kind, moveEvt.clientX, moveEvt.clientY);
      clearHighlights();
      if (!targetCard) return;

      targetCard.querySelector(".org-comp-body")?.classList.add("spec-drop-target");
      const targetList = targetCard.querySelector(".org-spec-list");
      if (!targetList) return;

      const listRect = targetList.getBoundingClientRect();
      if (moveEvt.clientY < listRect.top + 32) targetList.scrollTop -= 14;
      else if (moveEvt.clientY > listRect.bottom - 32) targetList.scrollTop += 14;

      const beforeNode = findSpecTargetBeforeNode(targetList, tileEl, moveEvt.clientY);
      const srcList = tileEl.parentElement;
      if (srcList !== targetList || tileEl.nextElementSibling !== beforeNode) {
        didReorder = true;
        flipReorder([srcList, targetList], ".org-spec-tile", tileEl, () => {
          targetList.querySelector(".org-empty-hint")?.remove();
          targetList.insertBefore(tileEl, beforeNode);
        });
      }
    };

    const cleanup = () => {
      window.removeEventListener("pointermove", handleMove);
      window.removeEventListener("pointerup", handleUp);
      window.removeEventListener("pointercancel", handleCancel);
      if (ghost) ghost.remove();
      document.body.style.userSelect = "";
      tileEl.classList.remove("dragging");
      clearHighlights();
    };

    const handleUp = () => {
      cleanup();
      if (!active) {
        if (onClick) onClick(tileEl);
        return;
      }

      if (didReorder) {
        const finalCompId = tileEl.closest(".org-comp-card")?.dataset.compId || comp.id;
        onCommit([comp.id, finalCompId]);
      }
    };

    const handleCancel = () => {
      cleanup();
    };

    window.addEventListener("pointermove", handleMove);
    window.addEventListener("pointerup", handleUp);
    window.addEventListener("pointercancel", handleCancel);
  });
}
