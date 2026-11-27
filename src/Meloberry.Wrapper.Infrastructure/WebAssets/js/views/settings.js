import { api } from "../api.js";
import { state, emit, escapeHtml } from "../store.js";
import { addNotification, applyTheme, toast } from "../ui/chrome.js";
import { closeAllDropdowns } from "../ui/dropdowns.js";

let debounceTimer = null;
let saveSeq = 0;
let lastSavedSignature = "";
let lastToolSignature = "";

function buildSettingsPayload() {
  return {
    themeMode: document.getElementById("setThemeMode")?.value || "berry",
    defaultBuildOutputDir: document.getElementById("setDefaultBuildDir")?.value || "",
    mbyExecutableOverride: document.getElementById("setMbyOverride")?.value || "",
    melomgExecutableOverride: document.getElementById("setMelomgOverride")?.value || "",
    selfUpdateRepoOwner: document.getElementById("setUpdateOwner")?.value || "",
    selfUpdateRepoName: document.getElementById("setUpdateRepo")?.value || ""
  };
}

function signatureOf(payload) {
  return JSON.stringify(payload);
}

function toolSignatureOf(payload) {
  return `${payload.mbyExecutableOverride || ""}||${payload.melomgExecutableOverride || ""}`;
}

function showSettingsSavedWipe() {
  const el = document.getElementById("settingsSavedStatus");
  if (!el) return;
  el.textContent = "Settings saved";
  el.classList.remove("hidden", "sync-wipe-in");
  void el.offsetWidth;
  el.classList.add("sync-wipe-in");
}

async function persistSettingsNow() {
  clearTimeout(debounceTimer);
  const payload = buildSettingsPayload();
  const sig = signatureOf(payload);
  if (sig === lastSavedSignature) return;

  const mySeq = ++saveSeq;
  const toolsChanged = toolSignatureOf(payload) !== lastToolSignature;
  const res = await api("PUT", "/api/settings", payload);
  if (mySeq !== saveSeq) return;

  if (res.ok) {
    state.settings = res.data;
    const synced = buildSettingsPayload();
    lastSavedSignature = signatureOf(synced);
    lastToolSignature = toolSignatureOf(synced);
    applyTheme(state.settings.themeMode);
    emit("viewer:theme-changed");
    if (toolsChanged) {
      emit("apps:refresh", false);
      emit("specs:reload", true);
    }
    emit("prompt:refresh");
    showSettingsSavedWipe();
  } else {
    toast(res.data?.error?.description || "Failed to save settings.", "err");
  }
}

function scheduleAutoSave(delayMs = 450) {
  clearTimeout(debounceTimer);
  debounceTimer = setTimeout(() => {
    persistSettingsNow();
  }, delayMs);
}

export function renderEcosystemRepos() {
  const cards = document.getElementById("ecosystemRepoCards");
  if (!cards) return;
  cards.innerHTML = "";
  state.ecosystemRepos.forEach(r => {
    const card = document.createElement("div");
    card.className = "repo-card";
    card.innerHTML = `<strong>${escapeHtml(r.displayName)}</strong><div class="muted">${escapeHtml(r.description)}</div>`;
    const btn = document.createElement("button");
    btn.className = "btn btn-xs btn-ghost";
    btn.textContent = r.slug;
    btn.onclick = () => api("POST", "/api/shell/open-url", { url: r.repoUrl });
    card.appendChild(btn);
    cards.appendChild(card);
  });
}

export function syncSettingsInputs() {
  document.getElementById("setThemeMode").value = state.settings.themeMode || "berry";
  document.getElementById("setDefaultBuildDir").value = state.settings.defaultBuildOutputDir || "";
  document.getElementById("setMbyOverride").value = state.settings.mbyExecutableOverride || "";
  document.getElementById("setMelomgOverride").value = state.settings.melomgExecutableOverride || "";
  document.getElementById("setUpdateOwner").value = state.settings.selfUpdateRepoOwner || state.metadata.selfUpdateRepoOwner || "doc-shashank";
  document.getElementById("setUpdateRepo").value = state.settings.selfUpdateRepoName || state.metadata.selfUpdateRepoName || "meloberry-pc";
  const initial = buildSettingsPayload();
  lastSavedSignature = signatureOf(initial);
  lastToolSignature = toolSignatureOf(initial);
}

export function isSettingsModalOpen() {
  const modal = document.getElementById("settingsModal");
  return !!(modal && !modal.classList.contains("hidden"));
}

export function openSettingsModal() {
  closeAllDropdowns();
  syncSettingsInputs();
  document.getElementById("settingsSavedStatus")?.classList.add("hidden");
  document.getElementById("settingsModal")?.classList.remove("hidden");
  document.getElementById("btnOpenSettings")?.classList.add("active");
}

export function closeSettingsModal() {
  clearTimeout(debounceTimer);
  persistSettingsNow();
  document.getElementById("settingsModal")?.classList.add("hidden");
  document.getElementById("btnOpenSettings")?.classList.remove("active");
}

export function wireSettingsView() {
  document.getElementById("btnOpenSettings").onclick = () => {
    if (isSettingsModalOpen()) closeSettingsModal();
    else openSettingsModal();
  };
  document.getElementById("btnCloseSettingsModal").onclick = closeSettingsModal;

  const modal = document.getElementById("settingsModal");
  modal?.addEventListener("click", e => {
    if (e.target === modal) closeSettingsModal();
  });

  document.getElementById("setThemeMode").addEventListener("change", e => {
    applyTheme(e.target.value);
    emit("viewer:theme-changed");
    persistSettingsNow();
  });

  ["setDefaultBuildDir", "setMbyOverride", "setMelomgOverride", "setUpdateOwner", "setUpdateRepo"].forEach(id => {
    const el = document.getElementById(id);
    if (!el) return;
    el.addEventListener("input", () => scheduleAutoSave(450));
    el.addEventListener("change", () => persistSettingsNow());
    el.addEventListener("blur", () => persistSettingsNow());
  });

  document.getElementById("btnOpenAppsDir").onclick = () =>
    api("POST", "/api/workspace/open-folder", { path: state.appsDirectory });

  document.getElementById("btnCheckSelfUpdate").onclick = async () => {
    const statusText = document.getElementById("selfUpdateStatusText");
    const installBtn = document.getElementById("btnInstallSelfUpdate");
    statusText.textContent = "Checking GitHub Releases...";
    installBtn.classList.add("hidden");
    const res = await api("POST", "/api/self-update/check", {});
    if (res.ok) {
      statusText.textContent = res.data.statusMessage;
      installBtn.classList.toggle("hidden", !res.data.updateAvailable);
      if (res.data.updateAvailable) {
        addNotification("App Update Available", res.data.statusMessage, "info");
      }
    } else {
      statusText.textContent = res.data?.error?.description || "Update check failed.";
    }
  };

  document.getElementById("btnInstallSelfUpdate").onclick = async () => {
    const installBtn = document.getElementById("btnInstallSelfUpdate");
    const wrap = document.getElementById("selfUpdateProgressWrap");
    const bar = document.getElementById("selfUpdateProgressBar");
    const statusText = document.getElementById("selfUpdateStatusText");
    installBtn.disabled = true;
    wrap.classList.remove("hidden");
    const poller = setInterval(async () => {
      const st = await api("GET", "/api/self-update/status");
      if (st.ok && st.data.status) {
        statusText.textContent = st.data.status.message;
        if (st.data.status.percent != null) bar.style.width = `${st.data.status.percent}%`;
      }
    }, 300);

    try {
      const res = await api("POST", "/api/self-update/install", {});
      if (!res.ok) toast(res.data?.error?.description || "Self-update failed.", "err");
    } finally {
      clearInterval(poller);
      installBtn.disabled = false;
    }
  };
}
