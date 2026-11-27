const cleanupTimers = new WeakMap();
const wipeTimers = new WeakMap();

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
  if (!firstRects || !firstRects.size) return [];

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

  if (!moved.length) return [];

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

  return moved;
}

export function flipReorder(containers, itemSelector, draggedEl, mutateDom, durationMs = 175) {
  const containerList = Array.isArray(containers) ? containers.filter(Boolean) : [containers].filter(Boolean);
  if (!containerList.length) return;

  const firstRects = capturePositions(containerList, itemSelector, draggedEl);
  mutateDom();
  animateFromPositions(firstRects, durationMs);
}

export function capturePositionsByKey(container, itemSelector, datasetKey) {
  const map = new Map();
  if (!container) return map;
  container.querySelectorAll(itemSelector).forEach(el => {
    const key = el.dataset?.[datasetKey];
    if (key) {
      map.set(key, el.getBoundingClientRect());
    }
  });
  return map;
}

export function animateFromKeyedPositions(container, itemSelector, datasetKey, firstRectsByKey, durationMs = 240, skipEl = null) {
  const movedKeys = new Set();
  if (!container || !firstRectsByKey || !firstRectsByKey.size) return movedKeys;
  const matched = new Map();
  container.querySelectorAll(itemSelector).forEach(el => {
    if (typeof skipEl === "function" && skipEl(el)) return;
    const key = el.dataset?.[datasetKey];
    if (key && firstRectsByKey.has(key)) {
      matched.set(el, firstRectsByKey.get(key));
    }
  });
  const moved = animateFromPositions(matched, durationMs);
  moved.forEach(el => {
    const key = el.dataset?.[datasetKey];
    if (key) movedKeys.add(key);
  });
  return movedKeys;
}

export function animateLabelWipe(inputEl, prevText, nextText) {
  if (!inputEl || prevText === nextText) return;
  if (document.activeElement === inputEl) return;

  const prevTimer = wipeTimers.get(inputEl);
  if (prevTimer) {
    clearTimeout(prevTimer);
    wipeTimers.delete(inputEl);
  }

  inputEl.value = prevText;
  inputEl.classList.remove("label-wipe-in", "label-wipe-out");
  void inputEl.offsetWidth;
  inputEl.classList.add("label-wipe-out");

  const outTid = setTimeout(() => {
    if (!inputEl.isConnected || document.activeElement === inputEl) {
      wipeTimers.delete(inputEl);
      return;
    }
    inputEl.value = nextText;
    inputEl.classList.remove("label-wipe-out");
    void inputEl.offsetWidth;
    inputEl.classList.add("label-wipe-in");
    const inTid = setTimeout(() => {
      inputEl.classList.remove("label-wipe-in");
      wipeTimers.delete(inputEl);
    }, 260);
    wipeTimers.set(inputEl, inTid);
  }, 140);
  wipeTimers.set(inputEl, outTid);
}
