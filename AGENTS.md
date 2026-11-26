# AGENTS.md — Meloberry PC (`Wrapper`)

> **Repository:** Meloberry PC (`mby-pc`) — Lightweight `.NET 10` Win32 + WebView2 desktop companion for initializing Meloberry workspaces, authoring AI agent prompts, exporting targeted `AGENTS.md` schemas, and viewing compiled study WebApps.

---

## 1. Mandatory Agent Directives

### Workflow & Planning Directives
1. **Always Invoke `/grill-me` Before Planning:** Whenever the user asks you to write a `/plan` implementation plan artifact, **always** run `/grill-me` first (inspect the codebase, then ask clarifying design questions one at a time with recommended answers) before writing the plan artifact.
2. **Test-First Development (TDD) & Phase Test Integration:** Always write automated tests for any new planned feature **first** before implementing the feature itself. When an implementation plan is structured phase-wise (explicitly or implicitly), temporary test files named `Phase<phase number>Tests<test number>` may be created during execution; however, once all phases of a plan are completely implemented, **integrate all phase tests into permanent topic-based test files and delete the temporary phase test files** so they do not accumulate.
3. **Git Commit & Push Prompt After Every Plan:** After completing each plan, **always** ask the user whether to commit the changes to GitHub. If the user agrees and the commit succeeds, **always** ask a follow-up question asking whether to push to remote.

### Engineering & Architecture Guardrails
4. **Keep Workspace Structure Up to Date:** Whenever files or directories are added, moved, renamed, or deleted anywhere in this repository, **always update Section 2 (Complete Workspace Structure) and Section 3 (File Index)** in this `AGENTS.md`.
5. **Single Source of Truth (Version & Tool Resolution):** Keep product, version, and self-update repository metadata solely in `Directory.Build.props` (never hardcode version strings in HTML, JS, or C#), and resolve `mby` / `melomg` executables exclusively via `IManagedAppsStore.ResolveToolExecutableAsync(...)`.
6. **Pre-Completion Verification Gate:** Always run `dotnet build` (`TreatWarningsAsErrors` is enabled) and `dotnet test` (covering both `Meloberry.Wrapper.UnitTests` and `Meloberry.Wrapper.ArchitectureTests`) after edits and confirm zero warnings and zero test failures before finishing a task or proposing a commit.
7. **Modularity & File-Size Guardrail:** Keep one class, handler, endpoint group, or view module per file and avoid growing individual source files past ~400–500 lines.

---

## 2. Complete Workspace Structure

> **Directive:** This structure reflects the current repository layout and **must always be updated whenever the workspace structure changes**.

```text
Wrapper/
├── .gitignore
├── AGENTS.md
├── Directory.Build.props
├── Meloberry.Wrapper.sln
├── IMPORTANT/
│   ├── C_SHARP_CLEAN_ARCHITECTURE.md
│   └── C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md
├── installer/
│   ├── build-installer.ps1
│   └── mby-pc.iss
├── src/
│   ├── Meloberry.Wrapper.Domain/
│   │   ├── Meloberry.Wrapper.Domain.csproj
│   │   ├── Common/
│   │   │   ├── PathNormalization.cs
│   │   │   ├── Result.cs
│   │   │   └── SemverComparer.cs
│   │   ├── Errors/
│   │   │   └── WrapperErrors.cs
│   │   ├── Models/
│   │   │   ├── AgentsMdModels.cs
│   │   │   ├── EcosystemModels.cs
│   │   │   ├── LibraryAndSettingsModels.cs
│   │   │   ├── PromptModels.cs
│   │   │   ├── SpecModels.cs
│   │   │   └── WorkspaceModels.cs
│   │   └── Services/
│   │       ├── AgentsMdComposer.cs
│   │       ├── PromptComposer.cs
│   │       └── SessionScaffoldGenerator.cs
│   ├── Meloberry.Wrapper.Application/
│   │   ├── DependencyInjection.cs
│   │   ├── Meloberry.Wrapper.Application.csproj
│   │   ├── Abstractions/
│   │   │   ├── Ports.cs
│   │   │   └── WrapperMetadata.cs
│   │   └── UseCases/
│   │       ├── CaptureAnalysisHandler.cs
│   │       ├── LoadSpecCatalogHandler.cs
│   │       ├── ManagedAppsHandler.cs
│   │       ├── ManageLibraryHandler.cs
│   │       ├── PromptHandlers.cs
│   │       ├── ReadyWorkspaceHandler.cs
│   │       ├── SelfUpdateHandler.cs
│   │       ├── SessionTemplateHandler.cs
│   │       ├── SettingsAndPresetsHandler.cs
│   │       ├── WorkspaceActionsHandler.cs
│   │       ├── WorkspaceMaintenanceHandler.cs
│   │       ├── WorkspaceSetupHandler.cs
│   │       └── WorkspaceStateSync.cs
│   ├── Meloberry.Wrapper.Infrastructure/
│   │   ├── DependencyInjection.cs
│   │   ├── Meloberry.Wrapper.Infrastructure.csproj
│   │   ├── Cli/
│   │   │   ├── CliProcessRunner.cs
│   │   │   ├── ProcessMbyCliClient.cs
│   │   │   └── ProcessMelomgCliClient.cs
│   │   ├── Common/
│   │   │   └── AtomicFileOps.cs
│   │   ├── GitHub/
│   │   │   ├── GitHubHttpClientHelper.cs
│   │   │   ├── GitHubSelfUpdater.cs
│   │   │   └── GitHubToolDownloader.cs
│   │   ├── Persistence/
│   │   │   └── PersistenceStores.cs
│   │   ├── Server/
│   │   │   ├── ApiContracts.cs
│   │   │   ├── HttpEnvelope.cs
│   │   │   ├── LocalHttpWrapperServer.cs
│   │   │   ├── SseEventHub.cs
│   │   │   ├── StaticAssetHost.cs
│   │   │   ├── WebAppMountRegistry.cs
│   │   │   └── Endpoints/
│   │   │       ├── AppsEndpoints.cs
│   │   │       ├── PromptEndpoints.cs
│   │   │       ├── SystemEndpoints.cs
│   │   │       └── WorkspaceEndpoints.cs
│   │   ├── WebAssets/
│   │   │   ├── shell.html
│   │   │   ├── icons/
│   │   │   │   ├── specs/ (47 embedded SVG spec/subtype icons)
│   │   │   │   └── ui/    (16 embedded SVG UI navigation/action icons)
│   │   │   ├── js/
│   │   │   │   ├── api.js
│   │   │   │   ├── icons.js
│   │   │   │   ├── main.js
│   │   │   │   ├── store.js
│   │   │   │   ├── ui/
│   │   │   │   │   ├── chrome.js
│   │   │   │   │   ├── dropdowns.js
│   │   │   │   │   └── modal.js
│   │   │   │   └── views/
│   │   │   │       ├── settings.js
│   │   │   │       ├── setup.js
│   │   │   │       ├── viewer.js
│   │   │   │       └── prompt/
│   │   │   │           ├── index.js
│   │   │   │           ├── readyWorkspace.js
│   │   │   │           ├── specModes.js
│   │   │   │           ├── specOrganizer.js
│   │   │   │           └── specSelector.js
│   │   │   └── styles/
│   │   │       ├── base.css
│   │   │       ├── chrome.css
│   │   │       ├── overlays.css
│   │   │       ├── paper-theme.css
│   │   │       ├── setup.css
│   │   │       ├── studio.css
│   │   │       ├── tokens.css
│   │   │       └── viewer.css
│   │   └── Workspace/
│   │       ├── AgentsMdFileWriter.cs
│   │       ├── FileIdentity.cs
│   │       ├── ManagedAppsStore.cs
│   │       ├── WorkspaceFileWatcherService.cs
│   │       ├── WorkspaceInitializer.cs
│   │       ├── WorkspaceMaintenanceService.cs
│   │       └── WrapperDataPaths.cs
│   └── Meloberry.Wrapper.Desktop/
│       ├── Meloberry.Wrapper.Desktop.csproj
│       ├── Program.cs
│       └── Win32/
│           ├── ExternalUrlLauncher.cs
│           ├── NativeFileDialogs.cs
│           ├── WebView2Host.cs
│           ├── Win32Native.cs
│           └── Win32WindowHost.cs
└── tests/
    ├── Meloberry.Wrapper.ArchitectureTests/
    │   ├── Meloberry.Wrapper.ArchitectureTests.csproj
    │   └── WrapperArchitectureTests.cs
    └── Meloberry.Wrapper.UnitTests/
        ├── AgentsMdComposerAndReadyWorkspaceTests.cs
        ├── GitHubDownloadAndSelfUpdateTests.cs
        ├── LocalServerAndAnalysisTests.cs
        ├── ManagedAppsStoreTests.cs
        ├── Meloberry.Wrapper.UnitTests.csproj
        ├── SettingsAndCliTests.cs
        ├── TestDoubles.cs
        ├── TestRepoPaths.cs
        ├── TokenAndScaffoldTests.cs
        ├── WorkspaceFileWatcherTests.cs
        ├── WorkspaceInitializerTests.cs
        └── WorkspaceMaintenanceTests.cs
```

*(Local build/publish outputs such as `bin/`, `obj/`, and `app/` are gitignored via `.gitignore`.)*

---

## 3. Source & Documentation File Index

### Root & Installer
- `.gitignore`: Excludes `.NET` build/publish artifacts (`bin/`, `obj/`, `/app/`, `installer/Output/`), binaries, WebView2 runtime state, and local IDE files.
- `AGENTS.md`: Workspace guide defining repository structure, per-file summaries, Clean Architecture summaries, and mandatory agent directives.
- `Directory.Build.props`: Single source of truth for version (`1.1.1`), product metadata, self-update repo coordinates, nullable/implicit usings, and `TreatWarningsAsErrors`.
- `Meloberry.Wrapper.sln`: Visual Studio solution uniting all four `src/` projects and both `tests/` projects.
- `installer/build-installer.ps1`: Publishes the single-file Win64 desktop executable and compiles the Inno Setup installer using the version from `Directory.Build.props`.
- `installer/mby-pc.iss`: Inno Setup script packaging `mby-pc-<version>.exe` into `mby-pc-setup-<version>.exe`.

### Documentation (`IMPORTANT/`)
- `IMPORTANT/*`: Authoritative C#/.NET Clean Architecture (`C_SHARP_CLEAN_ARCHITECTURE.md`) and Clean CLI (`C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md`) engineering constraint guides that all C# changes must obey.

### `src/Meloberry.Wrapper.Domain`
- `Meloberry.Wrapper.Domain.csproj`: Zero-dependency `net10.0` core Domain project file.
- `Common/PathNormalization.cs`: Safe directory path normalization and case-insensitive equality helpers preventing trailing-slash mismatches.
- `Common/Result.cs`: `ErrorType` enum, `Error` record, and non-throwing `Result` / `Result<T>` outcome primitives.
- `Common/SemverComparer.cs`: Zero-dependency SemVer 2.0.0 extractor and comparator supporting prerelease precedence.
- `Errors/WrapperErrors.cs`: Central catalog of typed domain error factories across CLI, workspace, update, and security concerns.
- `Models/AgentsMdModels.cs`: Domain records for `mby spec` schemas (`AgentsMdSpecField`, `AgentsMdSpecDetail`) and `AGENTS.md` render status/results.
- `Models/EcosystemModels.cs`: Official sibling Meloberry GitHub repository catalog and allowlist URL validation (`EcosystemCatalog`).
- `Models/LibraryAndSettingsModels.cs`: Persisted `WrapperSettings`, `WindowPlacementState`, `WebAppLibraryEntry`, `WorkspaceSummary`, and `AppUpdateCheckResult` models.
- `Models/PromptModels.cs`: Prompt Studio draft, component organization, shopping-list selection, preset, and built-in preset models.
- `Models/SpecModels.cs`: Spec topic metadata, mode groups/options, and built-in fallback catalog (`SpecCatalogDefaults`).
- `Models/WorkspaceModels.cs`: Managed app entries, staged copy plans/conflicts/results, workspace reset plans, and outdated-tool detection models.
- `Services/AgentsMdComposer.cs`: Deterministic renderer for lean, spec-targeted `AGENTS.md` files and BPE-aware token estimation.
- `Services/PromptComposer.cs`: Pure Markdown prompt composer and `ToPlainText` converter driving Prompt Studio preview and exports.
- `Services/SessionScaffoldGenerator.cs`: Generates strict camelCase `mby` JSON session/article/quiz scaffolds with mode/ordering validation and reconciliation.

### `src/Meloberry.Wrapper.Application`
- `Meloberry.Wrapper.Application.csproj`: `net10.0` Application layer project referencing only `Domain` and `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `DependencyInjection.cs`: Registers all Application use-case handlers and `IWrapperMetadataProvider`.
- `Abstractions/Ports.cs`: Application-owned port interfaces (`IWrapperDataPaths`, `IManagedAppsStore`, `IWorkspaceInitializer`, `IAgentsMdFileWriter`, `IMbyCliClient`, `IMelomgCliClient`, `IGitHubToolDownloader`, `IGitHubSelfUpdater`, `ISettingsStore`, `IWebAppAnalysisStore`, `INativeDialogService`, `IExternalUrlLauncher`, `IWebAppMountRegistry`, `IWorkspaceMaintenanceService`, `IWorkspaceFileWatcher`, `IAtomicFileWriter`, `IAppShutdownCoordinator`).
- `Abstractions/WrapperMetadata.cs`: Assembly-backed metadata record and provider (`AssemblyWrapperMetadataProvider`).
- `UseCases/CaptureAnalysisHandler.cs`: Persists `POST /api/analysis` study reports from the active or referer-matched mounted WebApp.
- `UseCases/LoadSpecCatalogHandler.cs`: Loads, caches (by exe mtime), and falls back `mby spec` topics plus the virtual `link` helper.
- `UseCases/ManagedAppsHandler.cs`: Scans `apps\`, resolves `mby`/`melomg` executables, imports local `.exe`s, uninstalls tools, and tracks GitHub tool downloads/updates.
- `UseCases/ManageLibraryHandler.cs`: Validates and mounts compiled WebApp folders and updates `RecentWebApps` atomically.
- `UseCases/PromptHandlers.cs`: Composes Prompt Studio Markdown via `LoadSpecCatalogHandler` + `PromptComposer` and saves prompt files atomically.
- `UseCases/ReadyWorkspaceHandler.cs`: Exports targeted `AGENTS.md` playbooks (checking disk existence and overwrite confirmation) into the workspace or chosen subfolder.
- `UseCases/SelfUpdateHandler.cs`: Checks GitHub Releases for newer Inno Setup installers and triggers verified download + graceful shutdown-and-launch.
- `UseCases/SessionTemplateHandler.cs`: Generates strict scaffold JSON (plus placeholder image when required) and runs `mby apply` to create draft workspaces.
- `UseCases/SettingsAndPresetsHandler.cs`: Validates and updates persisted settings, WebApp theme override flag, and custom presets via `ISettingsStore.UpdateAsync`.
- `UseCases/WorkspaceActionsHandler.cs`: Invokes `mby ls`, `mby build` (+ mount in viewer), and `mby pkg export` inside the Active Workspace.
- `UseCases/WorkspaceMaintenanceHandler.cs`: Coordinates recursive workspace executable/artifact reset plans/executions and outdated-tool updates.
- `UseCases/WorkspaceSetupHandler.cs`: Handles Active Workspace selection, initialization conflict planning, and non-destructive copy execution.
- `UseCases/WorkspaceStateSync.cs`: Shared helper rescanning workspace executables and synchronizing `ActiveWorkspaceState` in settings.

### `src/Meloberry.Wrapper.Infrastructure`
- `Meloberry.Wrapper.Infrastructure.csproj`: `net10.0` Infrastructure project embedding all `WebAssets\**\*` resources and referencing `Application`.
- `DependencyInjection.cs`: Registers all Infrastructure stores, CLI/GitHub clients, file watchers, server endpoints, hubs, and `LocalHttpWrapperServer`.
- `Cli/CliProcessRunner.cs`: Shared out-of-process runner setting `MBY_AGENT=1`, UTF-8 streams, timeouts, and structured JSON error parsing.
- `Cli/ProcessMbyCliClient.cs`: Invokes `mby spec`, `mby ls`, `mby build`, `mby pkg export`, and `mby apply`.
- `Cli/ProcessMelomgCliClient.cs`: Invokes `melomg spec` to fetch PDF figure/masking documentation.
- `Common/AtomicFileOps.cs`: Static atomic temp-file write/copy/stream primitives (`AtomicFileOps`) and `AtomicFileWriter` adapter.
- `GitHub/GitHubHttpClientHelper.cs`: Enforces HTTPS GitHub host allowlisting, manual redirect validation, 250 MB cap, PE (`MZ`) header verification, and SHA-256 verification.
- `GitHub/GitHubSelfUpdater.cs`: Queries wrapper releases, selects highest SemVer `mby-pc-setup-*.exe`, downloads, and cleans up stale installers.
- `GitHub/GitHubToolDownloader.cs`: Queries `releases/latest` (or `/contents/app` fallback) and downloads verified `mby`/`melomg` binaries.
- `Persistence/PersistenceStores.cs`: Thread-safe `JsonSettingsStore` (with atomic `UpdateAsync`) and `WebAppAnalysisStore` writing `<webDir>/reports/*.analysis.{json,md}`.
- `Server/ApiContracts.cs`: Internal HTTP JSON request DTO records for shell API endpoints.
- `Server/HttpEnvelope.cs`: Bounded UTF-8 JSON body reader, camelCase serializer options, `ErrorType`-to-HTTP status mapper, and response writers.
- `Server/LocalHttpWrapperServer.cs`: Pure BCL dual-port `HttpListener` host (`ShellPort` + `ViewerPort`) with retryable port binding, Host/token guards, and request dispatch.
- `Server/SseEventHub.cs`: Bounded `Channel<byte[]>` SSE client registry with 15s keep-alive pings and concurrent broadcasts.
- `Server/StaticAssetHost.cs`: In-memory embedded asset host serving `shell.html`, bundled `/shell/shell.css`, `/shell/js/*` ES modules, SVG icons, and WebApp theme-bridge HTML injection.
- `Server/WebAppMountRegistry.cs`: Thread-safe registry mapping compiled WebApp folders to `/viewer/<slotId>/` slots.
- `Server/Endpoints/AppsEndpoints.cs`: HTTP route handler for `/api/apps/*`.
- `Server/Endpoints/PromptEndpoints.cs`: HTTP route handler for `/api/specs`, `/api/prompt/*`, and `/api/presets`.
- `Server/Endpoints/SystemEndpoints.cs`: HTTP route handler for `/api/bootstrap`, `/api/viewer/open`, `/api/self-update/*`, `/api/dialogs/*`, `/api/shell/open-url`, `/api/settings*`, and ViewerPort `/api/analysis`.
- `Server/Endpoints/WorkspaceEndpoints.cs`: HTTP route handler for `/api/workspace/*` and `/api/workspaces/*`.
- `WebAssets/shell.html`: Shell UI HTML host using `<meta name="mby-token">` and `<script type="module" src="/shell/js/main.js">`.
- `WebAssets/styles/tokens.css`: Root Berry, Light, and Dark CSS theme custom properties.
- `WebAssets/styles/paper-theme.css`: Paper theme CSS variables shared between the shell bundle and mounted WebApp fallback injection.
- `WebAssets/styles/base.css`: Global reset, typography, utility classes, cards, buttons, inputs, chips, dropdowns, and progress bars.
- `WebAssets/styles/chrome.css`: Custom 40px titlebar, notification center, pull-up workspace strip, `AGENTS.md` pill, outdated banner, and sliding view-panel styles.
- `WebAssets/styles/setup.css`: Setup view cards, managed apps cart, staged file list, sticky init footer, and Settings view styles.
- `WebAssets/styles/studio.css`: Prompt Studio 3-row layout, organization/spec drag tracks, spec selector rows, subtype chips, and responsive breakpoints.
- `WebAssets/styles/viewer.css`: Full-bleed WebApp viewer iframe, loading overlay, floating action menu, theme override switch, and chained submenus.
- `WebAssets/styles/overlays.css`: Hover path tooltips, subtype distribution popover, spec notes popover, modals, and toast notifications.
- `WebAssets/js/api.js`: Consumes and deletes `<meta name="mby-token">` into private module scope; exports `api()` and `openEventStream()`.
- `WebAssets/js/store.js`: Centralized reactive app state, lightweight pub/sub event bus (`on`/`emit`), string/path formatters, and `withButtonBusy`.
- `WebAssets/js/icons.js`: Fetches `/shell/icons.json` and resolves UI and spec/subtype SVG icons.
- `WebAssets/js/main.js`: Entry module wiring UI/views, cross-view events, SSE `fschange` handling, context-aware Drag-and-Drop (`window.__onNativeDrop`), and `bootstrap()`.
- `WebAssets/js/ui/chrome.js`: Titlebar drag/caption controls (`window.__onWindowState`), notifications, toasts, view switching, theme application, and workspace strip sync.
- `WebAssets/js/ui/dropdowns.js`: Declarative dropdown/popover registry with positioning, outside-click dismissal, and Escape handling.
- `WebAssets/js/ui/modal.js`: Reusable confirmation, `AGENTS.md` inspection, themed text-input prompt modal, and hover path tooltip helpers.
- `WebAssets/js/views/setup.js`: Workspace selection, recent workspaces, apps cart downloads/updates, staging, initialization planning/execution, and reset confirmation.
- `WebAssets/js/views/viewer.js`: WebApp viewer iframe lifecycle, theme-override `postMessage` bridge, discovery submenus, build-and-view, and `.mbypkg` export.
- `WebAssets/js/views/settings.js`: Full-page Settings bindings, self-update checks/downloads, and ecosystem repository links.
- `WebAssets/js/views/prompt/index.js`: Prompt Studio orchestration (presets, container tabs, organization track, draft payload, live preview, attachments, and export/copy actions).
- `WebAssets/js/views/prompt/readyWorkspace.js`: Ready Workspace (`AGENTS.md`) export workflow, subfolder picker, and overwrite confirmation modal.
- `WebAssets/js/views/prompt/specModes.js`: Pure spec mode-group structure, subtype count distribution math, and Selector/Organizer item synchronization.
- `WebAssets/js/views/prompt/specOrganizer.js`: Spec Organizer drag-and-drop track, subtype picker, and custom order controls.
- `WebAssets/js/views/prompt/specSelector.js`: Spec Selector rows, total Qty inputs, subtype distribution popover, mode chips, and spec notes popover.
- `WebAssets/icons/specs/*` & `WebAssets/icons/ui/*`: Embedded SVG icon assets (47 spec/subtype icons and 16 UI chrome icons) served via `/shell/icons.json` and `/shell/icons/<key>.svg`.
- `Workspace/AgentsMdFileWriter.cs`: Confined atomic reader/writer for `AGENTS.md` inside the Active Workspace root or a validated subfolder.
- `Workspace/FileIdentity.cs`: Byte-length + SHA-256 identity comparison helper (`FileIdentity.AreIdenticalAsync`).
- `Workspace/ManagedAppsStore.cs`: Canonical `%LOCALAPPDATA%\Meloberry\apps\` store with PE metadata inspection, import, uninstall, and precedence-ordered tool resolution.
- `Workspace/WorkspaceFileWatcherService.cs`: Debounced `FileSystemWatcher` over `apps\` and Active Workspace with suppression scopes for internal writes.
- `Workspace/WorkspaceInitializer.cs`: Non-destructive workspace staging planner/copier detecting intra-batch and disk conflicts and discovering compiled WebApps.
- `Workspace/WorkspaceMaintenanceService.cs`: Recursive executable/artifact reset planner/executor and outdated workspace CLI detector/updater.
- `Workspace/WrapperDataPaths.cs`: Resolves `%LOCALAPPDATA%\Meloberry\` paths with `%MBY_WRAPPER_DATA%` override support.

### `src/Meloberry.Wrapper.Desktop`
- `Meloberry.Wrapper.Desktop.csproj`: Win64 `WinExe` composition-root project bundling `WebView2Loader.dll` and publishing single-file `mby-pc-<version>.exe`.
- `Program.cs`: STA composition root enforcing single-instance mutex, starting `LocalHttpWrapperServer`, creating `Win32WindowHost`, launching `WebView2Host`, persisting window bounds, and handing off Inno Setup installers on exit.
- `Win32/ExternalUrlLauncher.cs`: Validates allowlisted HTTPS URLs and opens URLs/folders safely via `ShellExecuteW`.
- `Win32/NativeFileDialogs.cs`: Direct COM `IFileOpenDialog` / `IFileSaveDialog` wrapper for folder, multi-file, and save pickers without WinForms/WPF.
- `Win32/WebView2Host.cs`: Extracts/loads `WebView2Loader.dll`, hosts `CoreWebView2` in the borderless Win32 client area, locks navigation origins, and bridges `window.__onWindowState` and `window.__onNativeDrop`.
- `Win32/Win32Native.cs`: P/Invoke declarations, window messages, DWM attributes, and COM interop structs.
- `Win32/Win32WindowHost.cs`: Borderless Win32 HWND host with custom titlebar drag, `WM_NCHITTEST` resize edges, dark/light DWM backdrop/titlebar theming, and STA message loop.

### `tests/`
- `Meloberry.Wrapper.ArchitectureTests/Meloberry.Wrapper.ArchitectureTests.csproj`: Architecture test project referencing `Domain`, `Application`, `Infrastructure`, and `Desktop`.
- `Meloberry.Wrapper.ArchitectureTests/WrapperArchitectureTests.cs`: Enforces strict Clean Architecture layer dependency direction, zero third-party packages in `Domain`, zero banned UI/DB frameworks, and unified version metadata.
- `Meloberry.Wrapper.UnitTests/Meloberry.Wrapper.UnitTests.csproj`: xUnit unit and integration test project for Domain, Application, and Infrastructure.
- `Meloberry.Wrapper.UnitTests/AgentsMdComposerAndReadyWorkspaceTests.cs`: Verifies `AgentsMdComposer`, `PromptComposer`, and `ReadyWorkspaceHandler` overwrite/missing-path/override behavior.
- `Meloberry.Wrapper.UnitTests/GitHubDownloadAndSelfUpdateTests.cs`: Verifies GitHub host allowlisting, SemVer release asset selection, PE/SHA-256 verification, and self-update installer cleanup.
- `Meloberry.Wrapper.UnitTests/LocalServerAndAnalysisTests.cs`: Verifies dual-port origin isolation, `/api/analysis` guards, CSS bundling, `<meta name="mby-token">`, ES module import graph integrity, `HttpEnvelope`, `SseEventHub`, and DI resolution.
- `Meloberry.Wrapper.UnitTests/ManagedAppsStoreTests.cs`: Verifies PE metadata import detection, safe upgrades, uninstall behavior, and `ResolveToolExecutableAsync` precedence.
- `Meloberry.Wrapper.UnitTests/SettingsAndCliTests.cs`: Verifies `JsonSettingsStore` concurrency (`UpdateAsync`), workspace isolation, custom presets, and live CLI spec parsing.
- `Meloberry.Wrapper.UnitTests/TestDoubles.cs`: Shared `NoopShutdown`, `NoopDialogs`, and `NoopLauncher` test doubles.
- `Meloberry.Wrapper.UnitTests/TestRepoPaths.cs`: Locates sibling `mby` and `melomg` binaries in the parent workspace for optional live CLI tests.
- `Meloberry.Wrapper.UnitTests/TokenAndScaffoldTests.cs`: Verifies token estimation, `SemverComparer`, `SpecCatalogDefaults`, `PromptComposer`, `SessionScaffoldGenerator`, and live `mby apply`.
- `Meloberry.Wrapper.UnitTests/WorkspaceFileWatcherTests.cs`: Verifies real-time `apps\`/`workspace` file change events and suppression scopes.
- `Meloberry.Wrapper.UnitTests/WorkspaceInitializerTests.cs`: Verifies workspace copy planning, conflict policies, intra-batch collision detection, `AtomicFileOps`, and `FileIdentity`.
- `Meloberry.Wrapper.UnitTests/WorkspaceMaintenanceTests.cs`: Verifies recursive executable/artifact resets, `UndoReadyWorkspace`, and outdated-tool updates.

---

## 4. Clean Architecture Reference Summaries

> **IMPORTANT:** The summaries below are quick orientation only. **Always read [IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE.md](file:///IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE.md) and [IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md](file:///IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md) in full** before designing or reviewing C# code so you have every rule, template, anti-pattern, and pre-output checklist in context.

### Summary of `IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE.md`
- **Strict Inward Dependency Rule:** `*.Domain` depends on nothing (zero project or third-party NuGet references). `*.Application` depends only on `*.Domain` (plus DI abstractions). `*.Infrastructure` and outer hosts (`*.Api` / `*.Desktop`) depend inward on `*.Application`, and outer composition roots reference `*.Infrastructure` solely for DI registration.
- **Rich Domain & Non-Throwing Business Failures:** Entities keep constructors `private`/`protected` and property setters `private set`/`init`, mutating state only via invariant-enforcing methods. Expected business rule failures return `Result` / `Result<T>` with typed `Error` values (never exceptions); exceptions are reserved for programmer bugs or outages.
- **Thin Orchestrating Handlers & Narrow Ports:** One use case per handler; handlers sequence `load via Port -> invoke Domain -> save -> return DTO` without evaluating business rules or leaking persistence frameworks (`IQueryable<T>` and generic `IRepository<T>` are forbidden). All I/O ports accept `CancellationToken`.
- **Thin Outer Edge & Automated Enforcement:** Outer endpoints/hosts map inputs to Commands/Queries and map failed `Result`s centrally to HTTP/transport problems, backed by both fast fake-port Unit Tests and automated Architecture Tests.

### Summary of `IMPORTANT/C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md`
- **Clean CLI Topology:** Treat `*.Cli` (`System.CommandLine` + `.NET Generic Host`) identically to `*.Api` as a thin outer presentation adapter over `*.Application` and `*.Domain`; `Program.cs` is strictly the composition root and CLI handlers never inject `DbContext` or concrete `*.Infrastructure` types.
- **Zero Console Leakage in Core:** Never call `System.Console`, `Spectre.Console`, or `Environment.Exit` inside `*.Domain` or `*.Application`; handlers return strongly typed `Result<TResponse>` DTOs so the CLI edge can format tables, `--json`, or `--silent` output.
- **Self-Contained `*CliCommand` + Stream/Exit Code Discipline:** Suffix CLI commands `*CliCommand` with a nested `public new sealed class Handler : ICommandHandler`, write primary output to `IConsole.Out` and errors to `IConsole.Error` (never `System.Console` directly), honor `--silent` and `CancellationToken`, and map `Result` failures centrally to deterministic POSIX exit codes (`0`–`5`).
- **Verification:** Pair Domain/Application unit tests with in-memory `TestConsole` CLI tests and `NetArchTest.Rules` architecture tests.
