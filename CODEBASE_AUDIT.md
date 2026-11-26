Here is a comprehensive technical review of **`meloberry-pc` v1.1.1**, covering **UI/UX bugs** (including the exact root cause of the overflowing Organizer boxes), **backend & Win32 bugs**, **code duplication & debloating opportunities**, a **modularization architecture** (zero-bundler), and **high-value feature recommendations**.

---

## 1. UI / UX & Layout Bugs

### 1.1 Overflowing Organizer Boxes & Grid Column Blowout (Root Cause)
* **Files:** `shell.css` (`.studio-row-top`, `.studio-row-bottom`, `.studio-page-grid`, `.org-card`, `.drag-track`)
* **Root Cause:**
  1. `.studio-row-top` uses `grid-template-columns: 1fr 1.15fr;` and `.studio-row-bottom` uses `grid-template-columns: 1fr 1fr;`. In CSS Grid, `1fr` expands to `minmax(auto, 1fr)`, meaning grid children refuse to shrink below their intrinsic content width unless `min-width: 0` is set or `minmax(0, 1fr)` is used. As soon as `#orgTrack` accumulates enough `.drag-box` items, `.org-card` **expands horizontally beyond its column and overflows the page** instead of triggering `overflow-x: auto` on `.drag-track`.
  2. `.studio-page-grid` is a `flex-direction: column` container whose `<section class="card">` children also lack `min-width: 0; max-width: 100%;`. When `#specOrgTrack` (Row 2) fills up with `.drag-box.spec-org-box` items, the flex item stretches horizontally past the viewport instead of scrolling inside `#specOrgTrack`.
* **Fix (`shell.css`):**
  ```css
  .studio-row-top { grid-template-columns: minmax(0, 1fr) minmax(0, 1.15fr); }
  .studio-row-bottom { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); }
  .studio-page-grid > *, .studio-row-top > *, .studio-row-bottom > * { min-width: 0; }
  .drag-track { width: 100%; max-width: 100%; overflow-x: auto; }
  ```

### 1.2 Zero Responsive Breakpoints for Narrow / Half-Screen Windows
* **File:** `shell.css` (`.studio-row-top`, `.studio-row-bottom`, `.shop-row`)
* **Issue:** `.shop-row` uses fixed columns (`175px 72px 1fr 34px 28px`), and both `.studio-row-top` and `.studio-row-bottom` are permanently 2-column grids. Snapping Meloberry PC to half of a 1080p monitor (~960px) or resizing to ~800px cramps `.mode-cell`, clips `.ws-strip-label`, and causes horizontal overflow.
* **Fix:** Add `@media (max-width: 980px)` rules collapsing `.studio-row-top` and `.studio-row-bottom` to `1fr`, and wrapping `.shop-row` mode chips onto a second line on narrow viewports.

### 1.3 SSE `fschange` Silently Wipes Unsaved Setup State
* **File:** `shell.js` (`startRealtimeEvents` → `restoreActiveWorkspaceUi`)
* **Issue:** Whenever any `.exe` or `AGENTS.md` changes in the Active Workspace, the `fschange` SSE handler calls `restoreActiveWorkspaceUi()`. That function unconditionally resets:
  * `document.getElementById("setupWsPath").value = aws.workspacePath;`
  * `state.selectedAppNames = new Set(aws.selectedAppFileNames || []);`
  * `state.stagedItems = [...(aws.stagedItems || [])];`
  
  If a user has staged files/folders or toggled apps on the Setup page (before clicking **Initialize**) and an external change triggers SSE, **all their unsaved staging selections and typed path are silently wiped**.
* **Fix:** Split `restoreActiveWorkspaceUi()` into `syncWorkspaceChrome()` (updates header strip, `AGENTS.md` pill/banner, and apps presence badges only) and `loadWorkspaceDraftState()` (called only on bootstrap and explicit workspace switch).

### 1.4 Viewing the Organizer Tab Silently Mutates State & Breaks `Auto` Qty
* **File:** `shell.js` (`renderSpecOrganizerPane` → `syncOrderedItemsFromSelections`)
* **Issue:**
  1. Simply clicking the **Organizer** tab (without adding or dragging anything) calls `syncOrderedItemsFromSelections(comp)`, which sets `comp.orderedItems` to a non-null array using `Math.max(1, s.desiredCount || 1)`.
  2. If a spec had `Qty = Auto` (`desiredCount: null`), switching to Organizer silently fabricates `#1` for it and causes **Prompt Preview** to start emitting `- Exact Sequence: text #1` even though the user never ordered anything.
  3. Deleting or reordering *any* other item in Organizer runs `syncSelectionsFromOrderedItems(comp)`, permanently overwriting `Auto` (`null`) with `1`.
* **Fix:** Only materialize `comp.orderedItems` when the user actually adds, removes, or reorders in Organizer—or display a read-only preview of auto-expanded items until the user interacts. Also normalize keys with `.toLowerCase()` inside `syncOrderedItemsFromSelections` / `syncSelectionsFromOrderedItems` (currently case-sensitive object keys vs. case-insensitive `findOrInitSelection`).

### 1.5 Low Scannability in Organization & Spec Organizer Cards
* **File:** `shell.js` (`renderOrganizationCard`, `renderSpecOrganizerPane`)
* **Issue:**
  * In **Organization** (`#orgTrack`), boxes only display `A1` and `4 specs`—`comp.title` is only in the HTML `title` tooltip. With 3 articles (`A1`, `A2`, `A3`), you can't tell which is which without clicking or hovering.
  * In **Spec Organizer** (`#specOrgTrack`), `.spec-org-box` only renders `#1` and a tiny 18px icon—no topic label (e.g., `mcq`, `diff`, `fill-blank`). Several SVG icons are visually similar at a glance.
* **Fix:** Show a truncated 1-line component title inside `.drag-box`, and a compact `10.5px` topic tag below the icon inside `.spec-org-box`.

### 1.6 Silent Failures & Native `window.prompt()` Jarring UX
* **File:** `shell.js`
* **Issues:**
  1. **`btnSaveSettings.onclick`**: Has `if (res.ok) { ... }` with **no `else` branch**. Entering an invalid GitHub owner/repo (HTTP 400 `Settings.InvalidGitHubRepo`) fails completely silently.
  2. **`btnSavePreset.onclick`**: Uses native `window.prompt(...)` (renders an unstyled Win32 gray dialog ignoring Berry/Dark/Paper themes) and also lacks an `else` error toast branch.
  3. **`btnOpenSettings.onclick`**: Never calls `syncSettingsInputs()`, so discarded/unsaved edits from a previous modal visit persist when reopening Settings.
  4. **No `Escape` key or backdrop-click dismissal** for `#settingsModal`, `#confirmAgentsModal`, `#notePopover`, or dropdown menus.
  5. **No in-flight button disabling/loading states** on `btnInitializeWorkspace`, `btnReadyWorkspace`, and `btnUpdateOutdatedTools`—double-clicking triggers concurrent file-copy / CLI / settings write races.
  6. **Stale preset label**: `state.activePresetName` stays displayed on the Preset dropdown button even after the user manually changes container, components, or selections.

### 1.7 Overlay / Popover Clipping & Chrome Offset Drift
* **Files:** `shell.css`, `shell.js`, `shell.html`
* **Issues:**
  1. **`updateChromeOffset()` is never called on `window.resize`**: When the window narrows and `#outdatedToolsBar` wraps onto two lines, `--chrome-offset` stays stale and the banner overlaps the top cards in Setup and Prompt Studio.
  2. **`.chained-submenu` (WebApp Viewer FAB menu)**: Has no `max-height` or `overflow-y: auto`. A workspace with 10+ compiled WebApps / `mby` projects pushes the menu above the top of the window where it is cut off.
  3. **`#notePopover` positioning**: `top = Math.min(window.innerHeight - 165, rect.bottom + 6)` hardcodes `165px` without measuring `pop.offsetHeight` and lacks `Math.max(8, ...)`.
  4. **`#hoverPathTooltip` flicker**: When hovering near the top edge, `Math.max(8, rect.top - tipRect.height - 8)` places the tooltip over the hovered element itself.
  5. **`.init-footer-card`**: Uses `position: sticky; bottom: 0;` with no `z-index`, allowing positioned cards/elements to stack above it.
  6. **Startup version flash**: `shell.html` hardcodes `v1.0.0` in `#versionBadge` and `#settingsMetaLine` until `bootstrap()` resolves.
  7. **Blank `AGENTS.md` metrics after restart**: `restoreActiveWorkspaceUi()` leaves `#agentsBannerDetail` as `"AGENTS.md Ready"` instead of showing `agentsMdStatus.sizeBytes` and token count.

### 1.8 Broken / Unintuitive Drag-and-Drop Behavior
* **Files:** `shell.js` (`window.__onWin32Drop`), `Win32WindowHost.cs` (`HandleDropFiles`), `WebView2Host.cs` (`AllowExternalDrop = false`)
* **Issues:**
  1. `window.__onWin32Drop` blindly invokes `openWebAppPath(path)` regardless of the active tab—dropping a PDF on Prompt Studio or a folder on Setup fails with `"does not contain a compiled Meloberry WebApp"`.
  2. `HandleDropFiles` only extracts file index `0`, ignoring multi-file drops.
  3. Because WebView2's child HWND covers 100% of `Win32WindowHost`'s client area and sets `AllowExternalDrop = false`, Win32 `WM_DROPFILES` on the parent HWND is frequently intercepted/blocked by WebView2 rather than delivered to `WindowProc`.

---

## 2. Backend, C# & Win32 Bugs

### 2.1 Lost-Update Concurrency Race on `wrapper-settings.json`
* **Files:** `PersistenceStores.cs` (`JsonSettingsStore`), `WorkspaceSetupHandler.cs`, `WorkspaceMaintenanceHandler.cs`, `ReadyWorkspaceHandler.cs`, `ViewerAndWorkspaceHandlers.cs`, `SelfUpdateAndSettingsHandlers.cs`
* **Issue:** `JsonSettingsStore` locks `LoadAsync` and `SaveAsync` individually, **not the read-modify-write transaction**. Concurrent requests (e.g., `OpenWebAppAsync` updating `RecentWebApps` while `UpdateOverrideWebAppThemeAsync` toggles theme override) cause one save to overwrite the other's changes.
* **Fix:** Add `Task<Result<WrapperSettings>> UpdateAsync(Func<WrapperSettings, WrapperSettings> mutator, CancellationToken ct)` to `ISettingsStore`, executing Load → Mutate → Save inside `_lock`.

### 2.2 `ReadyWorkspaceHandler`: Stale Staged Files & Tool Precedence Mismatch
* **File:** `ReadyWorkspaceHandler.cs` (lines 176–240)
* **Issues:**
  1. Lists `settings.ActiveWorkspace.StagedItems` under **Workspace Files (root)** and **Workspace Subfolders** in `AGENTS.md` **without checking whether those files/folders actually exist in `fullWs`**. If the user hasn't initialized yet or deleted a staged file, `AGENTS.md` instructs the AI agent to read nonexistent `./` files.
  2. `WorkspaceMbyFileName: workspaceMby ?? (!string.IsNullOrWhiteSpace(mbyExePath) ? mbyExePath : null)` gives a workspace `mby.exe` higher priority than `settings.MbyExecutableOverride`, contradicting `ManagedAppsStore.ResolveToolExecutableAsync` and `shell.js` (where `explicitOverride` wins).

### 2.3 `WorkspaceInitializer`: Silent Intra-Batch Name Collisions & Inconsistent `CopiedRelativePaths`
* **File:** `WorkspaceInitializer.cs` (lines 77–84, 211–263)
* **Issues:**
  1. Staging two files with the same name from different folders (e.g., `A\notes.pdf` and `B\notes.pdf`) or two folders with the same leaf name (`Term1\Figs` and `Term2\Figs`) is **not flagged as a conflict in `PlanAsync`**. During `InitializeAsync`, the second silently overwrites (or skips) the first.
  2. When a pre-existing file is skipped due to `ConflictPolicy.SkipExisting`, its path is added to `CopiedRelativePaths`; when skipped because it is SHA-256 identical, it is **omitted** from `CopiedRelativePaths`.

### 2.4 `SessionScaffoldGenerator`: `"mixed"` MCQ Bug, Stale `OrderedItems`, and Dead Parameter
* **File:** `SessionScaffoldGenerator.cs`
* **Issues:**
  1. **Line 310 (`mcq`)**: `var multi = modes.Contains("multi", ...)` evaluates to `false` when mode is `"mixed"`, so `"mixed"` generates 100% single-correct MCQs instead of alternating single/multi-correct items.
  2. **Line 356 (`BuildInstanceSequence`)**: When `comp.OrderedItems` is present, `sel.DesiredCount` is ignored if they disagree (e.g., `DesiredCount = 2` with 1 item in `OrderedItems` emits only 1 item).
  3. **Lines 113, 229–232**: `bool includeSessionName` is always passed `false`—dead code.

### 2.5 `Win32WindowHost`: Missing Top-Edge Resize Hit-Test, Window Icon, Cursor & Background Brush
* **File:** `Win32WindowHost.cs` (`Create`, `WindowProc`, `Show`)
* **Issues:**
  1. In `WM_NCCALCSIZE`, when restored (not maximized), `rgrc0.top` is intentionally left at `0` for the custom titlebar, **but `WM_NCHITTEST` is not handled**. Consequently, users cannot resize the restored window from the top edge (`HTTOP` / `HTTOPLEFT` / `HTTOPRIGHT`).
  2. `WNDCLASSEXW` does not set `hIcon`, `hIconSm`, `hCursor`, or `hbrBackground`—resulting in the generic Windows default taskbar/Alt-Tab icon and a brief white/unpainted flash on startup (especially noticeable in Dark theme).
  3. `Show()` always forces `SW_SHOWMAXIMIZED` rather than restoring the user's previous window bounds/state.

### 2.6 `LocalHttpWrapperServer`: Port Binding TOCTOU Race & Unbounded Update Accumulation
* **Files:** `LocalHttpWrapperServer.cs`, `GitHubClients.cs`, `Program.cs`
* **Issues:**
  1. `FindFreeTcpPort()` closes a `TcpListener` and then binds `HttpListener` without retry. If another process grabs the ephemeral port in between (or `_viewerListener.Start()` throws after `_listener.Start()`), startup crashes and leaks `_listener`.
  2. `Program.cs` line 74 passes an `async void` lambda to `windowHost.EnqueueCallback(async () => ...)`.
  3. `GitHubSelfUpdater.DownloadInstallerAsync` never cleans up older `mby-pc-setup-*.exe` installers left in `%LOCALAPPDATA%\Meloberry\updates\`.
  4. `GitHubToolDownloader.TrySelectReleaseAsset` returns the **first** matching `.exe` asset instead of selecting the highest SemVer if a release contains multiple binaries.

---

## 3. Code Duplication & Debloating Audit

| Duplication / Bloat | Occurrences | Recommended Consolidation |
|---|---|---|
| **Atomic `.tmp` write + `File.Move(..., overwrite: true)` + cleanup** | **8 places:** `PromptAndSpecHandlers`, `SessionTemplateHandler`, `PersistenceStores`, `AgentsMdFileWriter`, `ManagedAppsStore`, `WorkspaceInitializer`, `WorkspaceMaintenanceService`, `WebView2Host` | Create a single `AtomicFileOps` utility (`WriteAllTextAsync`, `CopyFile`, `WriteStream`) in `Domain`/`Infrastructure`. |
| **`AreFilesIdenticalAsync` (SHA-256 comparison)** | **2 identical copies:** `WorkspaceInitializer.cs` & `WorkspaceMaintenanceService.cs` | Extract `FileIdentity.AreIdenticalAsync` in `Infrastructure.Workspace`. |
| **`SemverRegex()` compiled regex** | **2 identical copies:** `GitHubClients.cs` & `ManagedAppsStore.cs` | Move `ExtractVersion(string)` into `Domain.Common.SemverComparer`. |
| **Workspace apps rescan + `ActiveWorkspaceState` sync** | **4 copies:** `WorkspaceSetupHandler` (×2) & `WorkspaceMaintenanceHandler` (×2) | Extract a shared `WorkspaceStateSync` helper in `Application`. |
| **CLI executable resolution (`override` → `workspace` → `apps\`)** | **3 divergent copies:** `ManagedAppsStore.ResolveToolExecutableAsync`, `ReadyWorkspaceHandler`, and `shell.js` (`buildPromptDraftPayload`) | Make `IManagedAppsStore.ResolveToolExecutableAsync` the single source of truth and return resolved tool paths from `/api/bootstrap` & `/api/apps`. |
| **Topic-to-category fallback table** | `ReadyWorkspaceHandler.InferCategory` duplicates `SpecCatalogDefaults.BuiltInFallbackCatalog` | Remove `InferCategory`; look up `SpecCatalogDefaults.BuiltInFallbackCatalog`. |
| **Dropdown open/close/outside-click boilerplate** | **8 repeated blocks** in `shell.js` (`resetMenu`, `promptOptionsMenu`, `conflictMenu`, `presetDropdownMenu`, `selectorSpecMenu`, `orgSpecMenu`, `notifPopover`, `viewerUpMenu`) | Create a single declarative `registerDropdown(triggerEl, menuEl, opts)` helper with automatic Escape + outside-click dismissal. |
| **Paper theme CSS variables** | Duplicated in `shell.css` and `PAPER_THEME_FALLBACK_CSS` string in `shell.js` | Share/derive or serve from a single theme token definition. |
| **Unused CSS / Dead code / Repo clutter** | `.split-wrap` & `.btn-warn-ghost` in `shell.css`; `includeSessionName` in `SessionScaffoldGenerator.cs`; `BUGS_LEFT_FOR_FIXING.md` in root; duplicate `NoopShutdown`/`NoopDialogs`/`NoopLauncher` test stubs across test files | Remove dead CSS/params/scratchpad doc; extract `TestDoubles.cs` in `UnitTests`. |

*(Note: Keep the re-declared base COM methods on `IFileOpenDialog` and `IFileSaveDialog` in `NativeFileDialogs.cs`—C# `InterfaceIsIUnknown` COM interop requires them for proper vtable layout unless migrating to .NET `[GeneratedComInterface]`.)*

---

## 4. Modularizing `shell.js`, `shell.css`, and `LocalHttpWrapperServer.cs`

You do **not** need Node.js, npm, Vite, or Webpack to eliminate the monoliths. WebView2 runs modern Chromium with full native ES Module support (`<script type="module">`), preserving your lightweight single-file `.exe` build.

### 4.1 Splitting `shell.js` (~1,320 lines) into Native ES Modules
Update `LocalHttpWrapperServer` to map `/shell/*` to an in-memory dictionary of embedded `WebAssets` manifest resources (built once at startup), and replace `<script src="/shell/shell.js">` with `<script type="module" src="/shell/js/main.js">`:

```text
src/Meloberry.Wrapper.Infrastructure/WebAssets/
  shell.html
  styles/
    tokens.css        # Theme CSS variables (berry, light, dark, paper)
    base.css          # Reset, typography, buttons, inputs, pills, dropdowns
    chrome.css        # Titlebar, notifications, workspace strip, outdated bar
    setup.css         # Setup view cards, apps cart, staged list, sticky footer
    studio.css        # Prompt Studio grid, drag tracks, spec selector, preview
    viewer.css        # Full-bleed iframe, floating FAB, chained submenus
    overlays.css      # Modals, popovers, tooltips, toasts
  js/
    api.js            # Token-scoped fetch() wrapper + SSE EventSource client
    store.js          # Centralized app state + tiny event emitter (on/emit)
    icons.js          # SPEC_ICONS, DOWNLOAD_SVG, PENCIL_SVG, getSpecIcon()
    ui/
      chrome.js       # Titlebar drag/buttons, notifications, toasts, updateChromeOffset()
      dropdowns.js    # Generic dropdown/popover controller (outside-click + Escape)
      modal.js        # Reusable confirm(), alert(), inspect(), and themed prompt() modal
    views/
      setup.js        # Workspace selection, Apps store cards, Staged items, Plan & Init
      promptStudio.js # Container/Format, Presets, Organization, Selector/Organizer, Preview
      viewer.js       # WebApp iframe, theme override bridge, FAB & chained menus
      settings.js     # Settings modal, self-update flow, ecosystem links
    main.js           # Bootstrap, view transitions, keyboard shortcuts, context-aware DnD
```

* **CSS Performance Tip:** Instead of 7 separate `<link>` requests (or `@import` waterfalls), concatenate the `styles/*.css` embedded resources in order once in memory on the C# server and serve them as a single `/shell/shell.css` response. You get full file-level authoring modularity with **zero runtime HTTP overhead**.
* **Security Bonus:** With ES modules, `api.js` can read and delete the token once from a `<meta name="mby-token">` or inline bootstrap script, keeping it completely private inside module closure scope.

### 4.2 Splitting `LocalHttpWrapperServer.cs` (~835 lines)
Currently a single class handles TCP listeners, security middleware, static files, HTML bridge injection, SSE streaming, 32 API routes, and 13 DTO records. Refactor into cohesive components inside `Infrastructure/Server/`:

```text
src/Meloberry.Wrapper.Infrastructure/Server/
  LocalHttpWrapperServer.cs   # Dual-port lifecycle, retryable port bind, Host/Token guards, dispatch
  HttpEnvelope.cs             # ReadBoundedUtf8BodyAsync, ReadJsonAsync, WriteJson/Result/Error
  SseEventHub.cs              # Channel<byte[]> subscriber registry, ping loop, Broadcast()
  StaticAssetHost.cs          # Embedded WebAssets cache, CSS bundler, Mounted WebApp + theme bridge
  ApiContracts.cs             # Request/Response record DTOs
  Endpoints/
    AppsEndpoints.cs          # /api/apps/*
    WorkspaceEndpoints.cs     # /api/workspace/*, /api/workspaces/*
    PromptEndpoints.cs        # /api/specs, /api/prompt/*, /api/presets
    SystemEndpoints.cs        # /api/bootstrap, /api/viewer/*, /api/self-update/*, /api/dialogs/*, /api/settings*, /api/analysis
```

### 4.3 Splitting Multi-Class Infrastructure & Application Files
* `GitHubClients.cs` (~500 lines) → `GitHubHttpClientHelper.cs`, `GitHubToolDownloader.cs`, `GitHubSelfUpdater.cs`
* `CliProcessClients.cs` (~340 lines) → `CliProcessRunner.cs`, `ProcessMbyCliClient.cs`, `ProcessMelomgCliClient.cs`
* `ViewerAndWorkspaceHandlers.cs`, `SelfUpdateAndSettingsHandlers.cs`, `PromptAndSpecHandlers.cs` → one handler per file for clean discoverability.

---

## 5. High-Value Feature Ideas (Aligned with Meloberry PC's Lightweight Mission)

1. **Closed-Loop Study Reports Drawer & "Remediation Prompt" Generator**
   * You already persist `<webDir>/reports/*.analysis.json` and `*.analysis.md`, but only show a numeric badge. Add a slide-over **Reports** drawer in WebApp Viewer to view past quiz scores/weak spots and click **"Create Remediation Prompt"**, which pre-fills Prompt Studio with the missed concepts so an AI agent can generate a targeted follow-up drill.
2. **One-Click "Ready Workspace & Copy Prompt" + Open AI Terminal**
   * Users almost always click **Ready Workspace** and then **Prompt Options → Copy Markdown**. Offer a primary combo action, plus a quick **"Open Terminal in Workspace"** button that launches PowerShell/Windows Terminal pre-configured with `cd "<workspace>"` and `$env:MBY_AGENT='1'`.
3. **Auto-Saved Prompt Studio Drafts (Per Workspace)**
   * Closing or restarting Meloberry PC currently discards the active Prompt Studio draft (title, notes, custom component order, spec notes). Auto-save the current draft (debounced) into `%LOCALAPPDATA%\Meloberry\drafts\` keyed by workspace path so work is never lost.
4. **Collapsible Live CLI Output & Validation Drawer (`mby check` / `mby analyze`)**
   * Let users run `mby check <wsId>`, `mby build`, and `mby analyze --headless` directly from the Workspace menu with streamed stdout/stderr and clickable lint error summaries—no need to bounce out to a terminal just to see why a build failed.
5. **Context-Aware Drag & Drop**
   * Route drops based on the active view:
     * **Setup**: drop a folder onto card 1 to set Active Workspace, or drop files/folders anywhere else to stage them.
     * **Prompt Studio**: drop PDFs/notes to attach them as source material.
     * **WebApp Viewer**: drop a compiled WebApp folder to open it immediately.
6. **Send `.mbypkg` to Meloberry Android over Local Wi-Fi (QR Code)**
   * Since Meloberry PC already exports `.mbypkg` archives targeting `meloberry-android-viewer`, an opt-in, time-limited single-file LAN endpoint with a generated SVG QR code lets users pull a freshly exported `.mbypkg` straight onto their Android device without USB cables or cloud uploads.
7. **Live Spec Schema Quick-Inspect Popover**
   * Add a tiny `i` button next to each Spec row in Prompt Studio that displays the live `mby spec <topic>` compact signature and field constraints—helpful when writing custom instructions in the pencil popover.
8. **Keyboard Shortcuts & Window State Persistence**
   * Persist window position/size/maximized state in `wrapper-settings.json`.
   * Add `Ctrl+1`/`2`/`3` (switch tabs), `Ctrl+Shift+C` (copy prompt), `Ctrl+Shift+R` (Ready Workspace), `Ctrl+,` (Settings), and `Esc` (close active modal/popover).

---

## Suggested Priority Order

1. **Immediate (UX & Correctness):** Fix CSS Grid `minmax(0, 1fr)` / `min-width: 0` overflow on Organizer tracks; decouple SSE refresh from unsaved Setup state; stop Organizer tab view from mutating `Auto` Qty; add missing error toasts on Settings/Presets and disable buttons during async runs; fix `JsonSettingsStore.UpdateAsync` race and `SessionScaffoldGenerator` `"mixed"` MCQ mode.
2. **Short-Term (Polish & Modularity):** Split `shell.js` into native ES modules and `LocalHttpWrapperServer.cs` into endpoint modules; deduplicate `AtomicFileOps`, `FileIdentity`, and `SemverComparer`; add `WM_NCHITTEST` top-edge resize, app icon, and Escape/backdrop modal dismissal.
3. **Next Release (Features):** Auto-saved Prompt drafts, Reports Viewer + Remediation Prompt flow, Open Terminal with `MBY_AGENT=1`, and context-aware Drag & Drop.