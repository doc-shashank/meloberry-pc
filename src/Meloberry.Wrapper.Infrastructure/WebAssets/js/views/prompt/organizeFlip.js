const cleanupTimers = new WeakMap();

export function capturePositions(containers, itemSelector, excludeEl = null) {
  const containerList = Array.isArray(containers) ? containers.filter(Boolean) : [containers].filter(Boolean);
  const firstRects = new Map();
  containerList.forEach(c => {
    c.querySelectorAll(itemSelector).forEach(el => {
      if (el !== excludeEl) {
        firstRects.set(el, el.getBoundingClientRect());
      }
    });
  });
  return firstRects;
}

export function animateFromPositions(firstRects, durationMs = 190) {
  if (!firstRects || !firstRects.size) return;

  const moved = [];
  firstRects.forEach((first, el) => {
    if (!el.isConnected) return;
    const last = el.getBoundingClientRect();
    const dx = first.left - last.left;
    const dy = first.top - last.top;
    if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) return;

    const prevTimer = cleanupTimers.get(el);
    if (prevTimer) clearTimeout(prevTimer);

    el.style.transition = "none";
    el.style.transform = `translate3d(${dx}px, ${dy}px, 0)`;
    moved.push(el);
  });

  if (!moved.length) return;

  void moved[0].offsetWidth;

  requestAnimationFrame(() => {
    moved.forEach(el => {
      el.style.transition = `transform ${durationMs}ms cubic-bezier(0.22, 1, 0.36, 1)`;
      el.style.transform = "translate3d(0, 0, 0)";
      const tid = setTimeout(() => {
        el.style.transition = "";
        el.style.transform = "";
        cleanupTimers.delete(el);
      }, durationMs + 25);
      cleanupTimers.set(el, tid);
    });
  });
}

export function flipReorder(containers, itemSelector, draggedEl, mutateDom, durationMs = 175) {
  const containerList = Array.isArray(containers) ? containers.filter(Boolean) : [containers].filter(Boolean);
  if (!containerList.length) return;

  const firstRects = capturePositions(containerList, itemSelector, draggedEl);
  mutateDom();
  animateFromPositions(firstRects, durationMs);
}
