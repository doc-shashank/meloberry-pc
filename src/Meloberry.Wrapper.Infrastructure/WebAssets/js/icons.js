import { state } from "./store.js";

export function getIcon(key) {
  if (!key) return "";
  const norm = String(key).trim().replace(/^\/+|\/+$/g, "");
  if (state.icons[norm]) return state.icons[norm];
  const lower = norm.toLowerCase();
  const matchKey = Object.keys(state.icons).find(k => k.toLowerCase() === lower);
  return matchKey ? state.icons[matchKey] : "";
}

export function getCliSpecIcon(key) {
  if (!key || !state.cliSpecIcons) return "";
  const norm = String(key).trim().replace(/^\/+|\/+$/g, "");
  if (state.cliSpecIcons[norm]) return state.cliSpecIcons[norm];
  const lower = norm.toLowerCase();
  const matchKey = Object.keys(state.cliSpecIcons).find(k => k.toLowerCase() === lower);
  return matchKey ? state.cliSpecIcons[matchKey] : "";
}

export function getSpecIcon(topic, subtype, category) {
  const t = String(topic || "text").trim().toLowerCase();
  if (subtype) {
    const sub = String(subtype).trim();
    const hit = getCliSpecIcon(`${t}-${sub}`) || getIcon(`specs/${t}-${sub}`);
    if (hit) return hit;
  }
  const baseHit = getCliSpecIcon(t) || getIcon(`specs/${t}`);
  if (baseHit) return baseHit;

  const inferredCat = category
    || state.specs?.find(s => (s.topic || "").toLowerCase() === t)?.category
    || "block";
  return getIcon(inferredCat === "question" ? "ui/question-generic" : "ui/block-generic") || "";
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
