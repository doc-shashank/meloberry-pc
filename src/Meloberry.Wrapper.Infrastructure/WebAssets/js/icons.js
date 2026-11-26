import { state } from "./store.js";

export function getIcon(key) {
  if (!key) return "";
  const norm = String(key).trim().replace(/^\/+|\/+$/g, "");
  if (state.icons[norm]) return state.icons[norm];
  const lower = norm.toLowerCase();
  const matchKey = Object.keys(state.icons).find(k => k.toLowerCase() === lower);
  return matchKey ? state.icons[matchKey] : "";
}

export function getSpecIcon(topic, subtype) {
  const t = String(topic || "text").trim().toLowerCase();
  if (subtype) {
    const sub = String(subtype).trim();
    const hit = getIcon(`specs/${t}-${sub}`);
    if (hit) return hit;
  }
  return getIcon(`specs/${t}`) || getIcon("specs/text") || "";
}

export function hydrateStaticIcons() {
  document.querySelectorAll("[data-icon]").forEach(el => {
    const svg = getIcon(el.getAttribute("data-icon"));
    if (svg) el.innerHTML = svg;
  });
}

export async function loadIconRegistry() {
  try {
    const resp = await fetch("/shell/icons.json");
    if (resp.ok) {
      const data = await resp.json();
      if (data && typeof data === "object") {
        state.icons = data;
        hydrateStaticIcons();
      }
    }
  } catch {
    // Fallback gracefully if icons endpoint is unreachable.
  }
}
