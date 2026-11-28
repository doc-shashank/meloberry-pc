export const VIEW_ORDER = { setup: 0, format: 1, organize: 2, review: 3, viewer: 4 };
export const WORKFLOW_STEPS = ["setup", "format", "organize", "review", "viewer"];

export const state = {
  metadata: { version: "", productName: "Meloberry PC", selfUpdateRepoOwner: "doc-shashank", selfUpdateRepoName: "meloberry-pc" },
  icons: {},
  cliSpecIcons: {},
  mbyCompatibility: null,
  appsDirectory: "",
  viewerOrigin: "",
  settings: { themeMode: "berry", overrideWebAppTheme: true, recentWebApps: [] },
  apps: [],
  resolvedTools: { mby: null, melomg: null },
  appUpdateChecks: {},
  outdatedTools: [],
  conflictPolicy: "skipExisting",
  currentView: "setup",
  selectedAppNames: new Set(),
  stagedItems: [],
  specs: [],
  isLiveSpecs: false,
  activeMbyPath: "",
  presets: [],
  activePresetName: "Full Chapter Study Session",
  ecosystemRepos: [],
  agentsMdStatus: null,
  discoveredWebApps: [],
  discoveredMbyProjects: [],
  notifications: [],
  eventSource: null,
  previewSeq: 0,
  previewDebounceTimer: null,
  prompt: {
    container: "session",
    title: "",
    description: "",
    authoringFormat: "markdownDirectory",
    sourceText: "",
    extraAttachments: [],
    useMelomgForPdfs: true,
    agentsMdExported: false,
    workflowMode: "direct",
    allowPlaceholdersOnFinalize: false,
    components: [],
    containerCache: { session: null, standaloneArticle: null, standaloneQuiz: null }
  },
  live: {
    agentTouchedIds: new Set(),
    activities: [],
    activitySeq: 0,
    lastAgentActivity: null,
    activityTimer: null,
    suppressDraftPush: false,
    draftSaveTimer: null,
    hoveredSpecTarget: null
  },
  composedPrompt: { markdown: "", plainText: "", characterCount: 0, approximateTokens: 0 },
  activeConfirmAction: null,
  activeInputResolve: null,
  activeAddSpecCompId: null,
  activeInspectorTarget: null,
  hoverTimer: null,
  dragCard: null,
  dragSpec: null,
  lastAddedOrgId: null,
  lastAddedSpecOrgId: null,
  promptDragScrolled: false,
  viewerLoadToken: 0,
  viewerLoadFadeTimer: null,
  viewerHideDelayTimer: null
};

const listeners = new Map();

export function on(eventName, handler) {
  if (!listeners.has(eventName)) {
    listeners.set(eventName, new Set());
  }
  listeners.get(eventName).add(handler);
  return () => listeners.get(eventName)?.delete(handler);
}

export function emit(eventName, payload) {
  const set = listeners.get(eventName);
  if (!set) return;
  for (const fn of set) {
    fn(payload);
  }
}

export function escapeHtml(str) {
  return String(str || "").replace(/[&<>"']/g, ch => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;"
  }[ch]));
}

export function formatTruncatedWorkspace(fullPath) {
  if (!fullPath) return "No Active Workspace";
  const parts = fullPath.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
  if (parts.length <= 2) return `Active Workspace : ${fullPath}`;
  return `Active Workspace : ...\\${parts[parts.length - 2]}\\${parts[parts.length - 1]}`;
}

export function extractFolderName(fullPath) {
  if (!fullPath) return "";
  const trimmed = fullPath.replace(/[\\/]+$/, "");
  const parts = trimmed.split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : trimmed;
}

export function withButtonBusy(btn, busyLabel, action) {
  return async (...args) => {
    if (btn && btn.disabled) return;
    const origHtml = btn ? btn.innerHTML : "";
    if (btn) {
      btn.disabled = true;
      if (busyLabel) btn.textContent = busyLabel;
    }
    try {
      return await action(...args);
    } finally {
      if (btn) {
        btn.disabled = false;
        if (busyLabel) btn.innerHTML = origHtml;
      }
    }
  };
}
