const overlayRegistry = [];

export function registerPopover(menuId, opts = {}) {
  const entry = {
    menuId,
    triggerId: opts.triggerId || null,
    wrapSelector: opts.wrapSelector || `#${menuId}`,
    beforeOpen: opts.beforeOpen || null,
    onClose: opts.onClose || null
  };
  overlayRegistry.push(entry);
  return entry;
}

export function closeAllDropdowns(exceptIds = []) {
  const keep = new Set(exceptIds);
  const toNotify = [];
  overlayRegistry.forEach(entry => {
    if (keep.has(entry.menuId)) return;
    const el = document.getElementById(entry.menuId);
    if (el && !el.classList.contains("hidden")) {
      el.classList.add("hidden");
      if (entry.onClose) toNotify.push(entry.onClose);
    }
  });
  toNotify.forEach(fn => fn());
}

export function openDropdown(menuId) {
  const entry = overlayRegistry.find(e => e.menuId === menuId);
  const el = document.getElementById(menuId);
  if (!el) return;
  closeAllDropdowns([menuId]);
  if (entry?.beforeOpen) entry.beforeOpen();
  el.classList.remove("hidden");
}

export function toggleDropdown(menuId) {
  const el = document.getElementById(menuId);
  if (!el) return;
  if (el.classList.contains("hidden")) {
    openDropdown(menuId);
  } else {
    closeAllDropdowns();
  }
}

export function registerDropdown(triggerId, menuId, opts = {}) {
  const entry = registerPopover(menuId, { ...opts, triggerId });
  if (opts.autoWireClick !== false) {
    const trigger = document.getElementById(triggerId);
    if (trigger) {
      trigger.onclick = e => {
        e.stopPropagation();
        toggleDropdown(menuId);
      };
    }
  }
  return entry;
}

export function positionAnchoredPopover(pop, anchorEl) {
  pop.classList.remove("hidden");
  const rect = anchorEl.getBoundingClientRect();
  const popRect = pop.getBoundingClientRect();
  const left = Math.max(8, Math.min(window.innerWidth - popRect.width - 8, rect.left));
  const fitsBelow = rect.bottom + 6 + popRect.height <= window.innerHeight - 8;
  const top = fitsBelow
    ? rect.bottom + 6
    : Math.max(8, rect.top - popRect.height - 6);
  pop.style.left = `${left}px`;
  pop.style.top = `${top}px`;
}

export function wireGlobalOverlayDismissal(onEscapeExtra) {
  document.addEventListener("click", e => {
    const insideMenu = overlayRegistry.some(entry => entry.wrapSelector && e.target.closest(entry.wrapSelector));
    if (!insideMenu) {
      closeAllDropdowns();
    }
  });

  document.addEventListener("keydown", e => {
    if (e.key === "Escape") {
      closeAllDropdowns();
      if (onEscapeExtra) onEscapeExtra();
    }
  });
}
