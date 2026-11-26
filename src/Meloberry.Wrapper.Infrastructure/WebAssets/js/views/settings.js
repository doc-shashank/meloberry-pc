import { api } from "../api.js";
import { state, emit, escapeHtml, withButtonBusy } from "../store.js";
import { addNotification, applyTheme, switchView, toast } from "../ui/chrome.js";

export function renderEcosystemRepos() {
  const cards = document.getElementById("ecosystemRepoCards");
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
}

export function wireSettingsView() {
  document.getElementById("btnOpenSettings").onclick = () => {
    if (state.currentView === "settings") {
      switchView(state.lastNonSettingsView || "setup");
    } else {
      switchView("settings");
    }
  };
  document.getElementById("btnSettingsBack").onclick = () => {
    switchView(state.lastNonSettingsView || "setup");
  };

  const btnSaveSettings = document.getElementById("btnSaveSettings");
  btnSaveSettings.onclick = withButtonBusy(btnSaveSettings, "Saving...", async () => {
    const payload = {
      themeMode: document.getElementById("setThemeMode").value,
      defaultBuildOutputDir: document.getElementById("setDefaultBuildDir").value,
      mbyExecutableOverride: document.getElementById("setMbyOverride").value,
      melomgExecutableOverride: document.getElementById("setMelomgOverride").value,
      selfUpdateRepoOwner: document.getElementById("setUpdateOwner").value,
      selfUpdateRepoName: document.getElementById("setUpdateRepo").value
    };
    const res = await api("PUT", "/api/settings", payload);
    if (res.ok) {
      state.settings = res.data;
      applyTheme(state.settings.themeMode);
      emit("viewer:theme-changed");
      emit("apps:refresh", false);
      emit("specs:reload", true);
      emit("prompt:refresh");
      toast("Settings saved.", "ok");
    } else {
      toast(res.data?.error?.description || "Failed to save settings.", "err");
    }
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
