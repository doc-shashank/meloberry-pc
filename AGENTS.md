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
│   │   │   ├── CliSignalModels.cs
│   │   │   ├── EcosystemModels.cs
│   │   │   ├── FinalizeModels.cs
│   │   │   ├── LibraryAndSettingsModels.cs
│   │   │   ├── PromptModels.cs
│   │   │   ├── SpecModels.cs
│   │   │   └── WorkspaceModels.cs
│   │   └── Services/
│   │       ├── AgentsMdComposer.cs
│   │       ├── MelomgFigureQueryNormalizer.cs
│   │       ├── PromptComposer.cs
│   │       ├── PromptWorkflowModeComposers.cs
│   │       ├── SessionScaffoldGenerator.cs
│   │       ├── SpecOrderExpander.cs
│   │       └── WorkspaceMarkdownAssembler.cs
│   ├── Meloberry.Wrapper.Application/
│   │   ├── DependencyInjection.cs
│   │   ├── Meloberry.Wrapper.Application.csproj
│   │   ├── Abstractions/
│   │   │   ├── Ports.cs
│   │   │   └── WrapperMetadata.cs
│   │   └── UseCases/
│   │       ├── CaptureAnalysisHandler.cs
│   │       ├── ExtractFigureWithMelomgHandler.cs
│   │       ├── FinalizeWorkspaceHandler.cs
│   │       ├── ImportWorkspaceAssetHandler.cs
│   │       ├── LiveDraftHandler.cs
│   │       ├── LoadSpecCatalogHandler.cs
│   │       ├── ManagedAppsHandler.cs
│   │       ├── ManageLibraryHandler.cs
│   │       ├── ProcessCliSignalHandler.cs
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
│   │   │   ├── LiveGuiIpcClient.cs
│   │   │   ├── ProcessMbyCliClient.cs
│   │   │   ├── ProcessMelomgCliClient.cs
│   │   │   ├── ToolProxyRunner.cs
│   │   │   ├── WrapperCliAbstractions.cs
│   │   │   └── WrapperCliDispatcher.cs
│   │   ├── Common/
│   │   │   └── AtomicFileOps.cs
│   │   ├── GitHub/
│   │   │   ├── GitHubHttpClientHelper.cs
│   │   │   ├── GitHubSelfUpdater.cs
│   │   │   └── GitHubToolDownloader.cs
│   │   ├── Persistence/
│   │   │   ├── MbyPresetFileStore.cs
│   │   │   ├── PersistenceStores.cs
│   │   │   └── WorkspaceDraftStore.cs
│   │   ├── Server/
│   │   │   ├── ApiContracts.cs
│   │   │   ├── HttpEnvelope.cs
│   │   │   ├── LocalHttpWrapperServer.cs
│   │   │   ├── RuntimeIpcRegistry.cs
│   │   │   ├── SseEventHub.cs
│   │   │   ├── StaticAssetHost.cs
│   │   │   ├── WebAppMountRegistry.cs
│   │   │   └── Endpoints/
│   │   │       ├── AppsEndpoints.cs
│   │   │       ├── LiveControlEndpoints.cs
│   │   │       ├── PromptEndpoints.cs
│   │   │       ├── SystemEndpoints.cs
│   │   │       └── WorkspaceEndpoints.cs
│   │   ├── WebAssets/
│   │   │   ├── shell.html
│   │   │   ├── icons/
│   │   │   │   ├── fallback/ (19 embedded fallback SVG spec icons for legacy mby-cli)
│   │   │   │   └── ui/       (27 embedded SVG UI navigation/action/fallback icons)
│   │   │   ├── js/
│   │   │   │   ├── api.js
│   │   │   │   ├── icons.js
│   │   │   │   ├── main.js
│   │   │   │   ├── store.js
│   │   │   │   ├── ui/
│   │   │   │   │   ├── chrome.js
│   │   │   │   │   ├── dropdowns.js
│   │   │   │   │   ├── modal.js
│   │   │   │   │   ├── nativeDrop.js
│   │   │   │   │   └── workspaceTree.js
│   │   │   │   └── views/
│   │   │   │       ├── settings.js
│   │   │   │       ├── setup.js
│   │   │   │       ├── viewer.js
│   │   │   │       └── prompt/
│   │   │   │           ├── format.js
│   │   │   │           ├── organize.js
│   │   │   │           ├── organizeDrag.js
│   │   │   │           ├── organizeFlip.js
│   │   │   │           ├── organizePresets.js
│   │   │   │           ├── promptDraft.js
│   │   │   │           ├── readyWorkspace.js
│   │   │   │           ├── review.js
│   │   │   │           ├── specContentModal.js
│   │   │   │           ├── specModes.js
│   │   │   │           ├── specPopovers.js
│   │   │   │           └── specTemplates.js
│   │   │   └── styles/
│   │   │       ├── base.css
│   │   │       ├── chrome.css
│   │   │       ├── organize.css
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
│   │       ├── WorkspaceDirectoryBrowser.cs
│   │       ├── WorkspaceFileWatcherService.cs
│   │       ├── WorkspaceInitializer.cs
│   │       ├── WorkspaceMaintenanceService.cs
│   │       ├── WorkspaceShimWriter.cs
│   │       └── WrapperDataPaths.cs
│   └── Meloberry.Wrapper.Desktop/
│       ├── Meloberry.Wrapper.Desktop.csproj
│       ├── Program.cs
│       ├── Cli/
│       │   └── ConsoleBridge.cs
│       └── Win32/
│           ├── ExternalUrlLauncher.cs
│           ├── NativeFileDialogs.cs
│           ├── WebView2Host.cs
│           ├── Win32Native.cs
│           └── Win32WindowHost.cs
└── tests/
    ├── frontend/
    │   └── frontend-behavior.test.mjs
    ├── Meloberry.Wrapper.ArchitectureTests/
    │   ├── Meloberry.Wrapper.ArchitectureTests.csproj
    │   └── WrapperArchitectureTests.cs
    └── Meloberry.Wrapper.UnitTests/
        ├── AgentsMdComposerAndReadyWorkspaceTests.cs
        ├── GitHubDownloadAndSelfUpdateTests.cs
        ├── LiveDraftAndControlTests.cs
        ├── LocalServerAndAnalysisTests.cs
        ├── ManagedAppsStoreTests.cs
        ├── MarkdownAssemblerAndFinalizeTests.cs
        ├── Meloberry.Wrapper.UnitTests.csproj
        ├── MelomgGuiExtractionTests.cs
        ├── SettingsAndCliTests.cs
        ├── TestDoubles.cs
        ├── TestRepoPaths.cs
        ├── TokenAndScaffoldTests.cs
        ├── WorkspaceFileWatcherTests.cs
        ├── WorkspaceInitializerTests.cs
        ├── WorkspaceMaintenanceTests.cs
        └── WrapperCliDispatcherTests.cs
```

*(Local build/publish outputs such as `bin/`, `obj/`, and `app/` are gitignored via `.gitignore`.)*

---

## 3. Source & Documentation File Index

### Root & Installer
- `.gitignore`: Excludes `.NET` build/publish artifacts (`bin/`, `obj/`, `/app/`, `installer/Output/`), binaries, WebView2 runtime state, and local IDE files.
- `AGENTS.md`: Workspace guide defining repository structure, per-file summaries, Clean Architecture summaries, and mandatory agent directives.
- `Directory.Build.props`: Single source of truth for version (`1.2.0`), product metadata, self-update repo coordinates, nullable/implicit usings, and `TreatWarningsAsErrors`.
- `Meloberry.Wrapper.sln`: Visual Studio solution uniting all four `src/` projects and both `tests/` projects.
- `installer/build-installer.ps1`: Publishes the single-file Win64 desktop executable and compiles the Inno Setup installer using the version from `Directory.Build.props`.
- `installer/mby-pc.iss`: Inno Setup script packaging `mby-pc-<version>.exe` into `mby-pc-setup-<version>.exe`.

### Folded Reference Specifications

> The former standalone `MBY_CLI_SIGNAL_SPEC.md`, `MBYPRESET_SPEC.md`, and `WRAPPER_EVOLUTION_REPORT.md` documents are condensed below so this guide stays the single source of truth.

#### `.mby-signal.txt` CLI signal protocol
- Purpose: atomic file-based IPC emitted by `mby` at the end of `mby build`, so the Viewer auto-loads a compiled WebApp without partial/mid-build reads. The signal carries metadata and the dot-prefixed file stays out of the workspace directory tree popup.
- Location: `<ActiveWorkspace>\.mby-signal.txt` directly in the Active Workspace root. The wrapper keeps the file and deduplicates by `id` (bounded 128-entry cache), so duplicate watcher bursts are harmless.
- Writer contract: `mby` writes UTF-8 to `.mby-signal.txt.tmp`, flushes, then atomically move/replaces the signal file; `WorkspaceFileWatcherService` ignores `.tmp`/`.mbytmp` events and applies a 120ms debounce (bypassing the workspace suppression scope).
- Grammar: UTF-8 `key=value` lines; blank lines and `#` comments ignored; keys case-insensitive and split on the first `=`; values unquoted.
- Envelope: `version` (1), `event` (required; `webapp_built` is the only active event), `id` (UUID/nonce for dedup; fallback `event|path|timestamp`), `timestamp` (ISO-8601 UTC), `producer`.
- `webapp_built`: `path` (required; WebApp directory with `index.html` + `app.js`, relative to the workspace or absolute), optional `workspace_id` and `title`.
- Flow: watcher → `ProcessCliSignalHandler` (up to 4 reads with 50ms backoff, parse, dedupe, resolve `path`) → `ManageLibraryHandler.OpenWebAppAsync` validates and mounts `/viewer/<slotId>/` + prepends `RecentWebApps` → SSE `webapp-built` (`{ slotId, url, entry, workspaceId, title, signalId }`) → Shell loads `#webAppFrame` and switches to Step 5 (Viewer).
- Reserved future events (`template_applied`, `pkg_exported`, `build_failed`) are ignored safely by v1.

#### `.mbypreset` portable preset format
- Purpose: portable UTF-8 JSON preset capturing a whole workflow (`"scope": "workflow"`) or a single Article/Quiz card (`"scope": "component"`).
- Encoding: `.mbypreset`, UTF-8 (no BOM preferred), camelCase properties and enum strings, nulls omitted on write, case-insensitive reads with missing optionals treated as `null`, and atomic temp-file + replace writes.
- Envelope `MbyPresetFile`: `formatVersion` (int, currently `1`), `scope`, non-empty `name`, optional `description`, plus `workflowPreset` (required for `workflow`) or `componentPreset` (required for `component`).
- `PromptPreset`: `id`, `name`, `description`, `container` (`session` | `standaloneArticle` | `standaloneQuiz`), `authoringFormat` (`markdownDirectory` | `singleMarkdown` | `json`), `selections[]`, `isBuiltIn = false`, recommended `components[]`.
- `PromptComponentPlan`: `id`, `kind` (`article` | `quiz`), `title`, `selections[]`, optional `orderedItems[]`; explicit `orderedItems` take precedence over expanding `selections`.
- `ShoppingListSelection`: `topic`, `enabled`, `desiredCount?`, `notes?`, `modes?`, `modeCounts?`. `OrderedSpecInstance`: `id`, `topic`, 1-based `instanceNumber`, optional `subtype`.
- Enums: `PresetFileScope`, `ContainerKind`, `ComponentKind`, `AuthoringFormat`.
- Storage: `%LOCALAPPDATA%\Meloberry\presets\*.mbypreset` (honoring `%MBY_WRAPPER_DATA%`), component presets under `presets\components\`; saving/loading from an arbitrary path mirrors/registers a library copy and applies it immediately.
- Validation: missing/unreadable file or empty `name` → `Preset.InvalidFile`; workflow import requires `scope == "workflow"` + `workflowPreset`; card load requires `scope == "component"` + `componentPreset.kind` matching the target card. Future `mby` preset commands must honor `orderedItems` before reconciling `selections`.

#### Wrapper evolution & locked design decisions
- Status: research/design interview complete with decisions D1–D8 locked (2026-11-27, v1.2.0). Goal: turn Wrapper into a true bidirectional host — theme color parity across Berry/Light/Dark/Paper, central tools + `mby-pc.bat` shim instead of per-workspace `.exe` copies, an optional 3-way prompt system, per-spec direct content, and a transactional Finalize-to-Viewer pipeline.
- D1 Shim: a marker-tagged `mby-pc.bat` lives strictly in the Active Workspace and is removed from the previous workspace on switch; only custom imported tools are copied as `.exe`s.
- D2 CLI mode: `--cli` is handled before the single-instance mutex; tool proxies, `draft`, and `finalize` work headlessly via `<Workspace>\.meloberry\pc-draft.json` and live via loopback HTTP + SSE; GUI-only commands return `Gui.NotRunning` (exit `3`) unless `--launch`.
- D3 Modes: `Direct` hides prompt clutter and turns Review into Build Readiness; `Live Agent` emits a lean `.\mby-pc.bat` handoff plus live `draft-updated` / `agent-activity` SSE, `.agent-touched` highlights, and auto Viewer transition; `Classic Prompt` keeps the full Prompt Studio.
- D4 Content: per-spec `None` / `RawText` / `MarkdownFile` smart-wrapped into `:::<topic>{...}` directives, with a Raw/`.md` toggle, expanded editor using `mby spec` hints, and `.md` drag-and-drop.
- D5 Finalize: assemble into `<Workspace>\.meloberry\build-src\<slug>\`, run transactional `mby batch -` (`apply` → `check` → `build` with `verify` + `staticOnly`), then open `<slug>_web` in Step 5; empty specs are blocked unless "Allow Placeholders" is set.
- D6 Icons: spec display names and SVGs are owned by `mby-cli` (`mby spec --icons --json`, opt-in flag with retry without `--icons` for older binaries); Wrapper keeps only `ui/` icons plus generic fallbacks; `melomg` is untouched.
- D7 Degradation: without `mby` show install CTA + fallback specs with Finalize disabled; without `melomg` keep authoring/Finalize/Viewer working with an inline install CTA; missing tool proxies exit `3` (`Cli.NotFound`).
- D8 Figures: `MelomgFigureQueryNormalizer` strips `Figure`/`Fig.` prefixes, reuses indexed `Doc_*` via `melomg ls`, otherwise runs `melomg extract <pdf> <selector> -o <ws>\assets --quality high --retain-db --offline --json`; attach the workspace-relative path to image-capable specs with optional `--mask-all --meta` and a live thumbnail preview.
- Constraints: Clean Architecture + TDD phase tests consolidated in Phase 7, `dotnet build`/`dotnet test` gate (Rule #6), AGENTS.md structure index sync (Rule #4), and ~400–500 line file guardrail (Rule #7). Loopback IPC uses `%LOCALAPPDATA%\Meloberry\runtime-ipc.json` (`{ pid, shellPort, viewerPort, token, startedUtc }`) with Host/token guards; `ConsoleBridge` attaches only when standard handles are NOT redirected.
- Simplifications: 5-endpoint `/api/live/*`, thin 5-verb CLI dispatcher, uniform data-driven smart-wrapping. Tool proxy flow: `mby-pc.bat` resolves `mby-pc.exe` via `mby-pc-path.txt` → `--cli --workspace` → `ToolProxyRunner` resolves binaries only through `IManagedAppsStore.ResolveToolExecutableAsync` → spawn with caller CWD, exact args, inherited `MBY_AGENT`, transparent I/O, and exact exit code → `agent-activity` + auto Viewer on successful `mby build`.
- Independence: Wrapper, CLI, and `melomg` have zero compile-time coupling; new `mby`/`melomg` flags work through the opaque proxy without Wrapper changes; central upgrades apply to all workspaces instantly.

### Documentation (`IMPORTANT/`)
- `IMPORTANT/*`: Authoritative C#/.NET Clean Architecture (`C_SHARP_CLEAN_ARCHITECTURE.md`) and Clean CLI (`C_SHARP_CLEAN_ARCHITECTURE_FOR_CLI.md`) engineering constraint guides that all C# changes must obey.

### `src/Meloberry.Wrapper.Domain`
- `Meloberry.Wrapper.Domain.csproj`: Zero-dependency `net10.0` core Domain project file.
- `Common/PathNormalization.cs`: Safe directory path normalization, equality, and descendant containment helpers preventing trailing-slash mismatches.
- `Common/Result.cs`: `ErrorType` enum, `Error` record, and non-throwing `Result` / `Result<T>` outcome primitives.
- `Common/SemverComparer.cs`: Zero-dependency SemVer 2.0.0 extractor and comparator supporting prerelease precedence.
- `Errors/WrapperErrors.cs`: Central catalog of typed domain error factories across CLI, workspace, update, and security concerns.
- `Models/AgentsMdModels.cs`: Domain records for `mby spec` schemas (`AgentsMdSpecField`, `AgentsMdSpecDetail`) and `AGENTS.md` render status/results.
- `Models/CliSignalModels.cs`: Domain record and `key=value` parser (`MbyCliSignal`, `MbyCliSignalParser`) for `.mby-signal.txt` CLI events.
- `Models/EcosystemModels.cs`: Official sibling Meloberry GitHub repository catalog and allowlist URL validation (`EcosystemCatalog`).
- `Models/FinalizeModels.cs`: Domain contracts for spec content wrapping, `FinalizeRequest`, and `FinalizeResult`.
- `Models/LibraryAndSettingsModels.cs`: Persisted `WrapperSettings`, `WindowPlacementState`, `WebAppLibraryEntry`, `WorkspaceSummary`, and `AppUpdateCheckResult` models.
- `Models/PromptModels.cs`: Prompt Studio draft, workflow modes (`Direct`, `LiveAgent`, `ClassicPrompt`), per-spec authoring content (`SpecContentSource`), component organization, shopping-list selection, preset, and built-in preset models.
- `Models/SpecModels.cs`: Spec topic metadata, mode groups/options, display names, and built-in fallback catalog (`SpecCatalogDefaults`).
- `Models/WorkspaceModels.cs`: Managed app entries, staged copy plans/conflicts/results, workspace reset plans, and outdated-tool detection models.
- `Services/AgentsMdComposer.cs`: Deterministic renderer for lean, spec-targeted `AGENTS.md` files (with `.\mby-pc.bat` shim support) and BPE-aware token estimation.
- `Services/MelomgFigureQueryNormalizer.cs`: Normalizes natural-language figure queries (e.g., `"Figure 1.1"`, `"Fig. 1.1a"`) into concise `melomg` selectors.
- `Services/PromptComposer.cs`: Pure Markdown prompt composer and `ToPlainText` converter driving Prompt Studio preview and exports.
- `Services/PromptWorkflowModeComposers.cs`: Deterministic renderers for `Direct` (Build Readiness) and `LiveAgent` (`.\mby-pc.bat` contract) workflow modes.
- `Services/SessionScaffoldGenerator.cs`: Generates strict camelCase `mby` JSON session/article/quiz scaffolds with mode/ordering validation and reconciliation.
- `Services/SpecOrderExpander.cs`: Single source of truth for expanding component `Selections` into `OrderedSpecInstance` lists while preserving primary subtype quotas (`ModeCounts`) and active primary `Modes`.
- `Services/WorkspaceMarkdownAssembler.cs`: Assembles modular `mby` Markdown directories from organized specs and `.md`/raw/image contents with uniform smart-wrapping, angle-bracketed paths, YAML `imagePath:` promotion, and placeholder guards.

### `src/Meloberry.Wrapper.Application`
- `Meloberry.Wrapper.Application.csproj`: `net10.0` Application layer project referencing only `Domain` and `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `DependencyInjection.cs`: Registers all Application use-case handlers and `IWrapperMetadataProvider`.
- `Abstractions/Ports.cs`: Application-owned port interfaces (`IWrapperDataPaths`, `IManagedAppsStore`, `IWorkspaceInitializer`, `IWorkspaceShimWriter`, `IAgentsMdFileWriter`, `IMbyCliClient`, `IMelomgCliClient`, `IGitHubToolDownloader`, `IGitHubSelfUpdater`, `ISettingsStore`, `IWorkspaceDraftStore`, `IRuntimeIpcRegistry`, `IWebAppAnalysisStore`, `INativeDialogService`, `IExternalUrlLauncher`, `IWebAppMountRegistry`, `IWorkspaceMaintenanceService`, `IWorkspaceFileWatcher`, `IAtomicFileWriter`, `IAppShutdownCoordinator`).
- `Abstractions/WrapperMetadata.cs`: Assembly-backed metadata record and provider (`AssemblyWrapperMetadataProvider`).
- `UseCases/CaptureAnalysisHandler.cs`: Persists `POST /api/analysis` study reports from the active or referer-matched mounted WebApp.
- `UseCases/ExtractFigureWithMelomgHandler.cs`: Reuses indexed `Doc_*` or runs one-shot `melomg extract --retain-db --offline` to crop PDF figures directly for specs.
- `UseCases/FinalizeWorkspaceHandler.cs`: Validates organized specs, writes modular Markdown + referenced assets, executes `mby batch -`, and mounts the compiled WebApp.
- `UseCases/ImportWorkspaceAssetHandler.cs`: Validates and atomically imports external image (`.png/.jpg/.jpeg/.gif/.webp/.bmp/.svg`) or Markdown (`.md/.markdown`) files into `<Workspace>\assets\` or `<Workspace>\notes\` without destructive overwrites.
- `UseCases/LiveDraftHandler.cs`: Coordinates thread-safe `<Workspace>\.meloberry\pc-draft.json` reads, full saves, structured CLI/UI mutations with human-first activity summaries (card titles, 1-based positions, friendly labels), and `DraftChanged` events.
- `UseCases/LoadSpecCatalogHandler.cs`: Loads, caches (by exe mtime), and falls back `mby spec --icons --json` topics/icons plus the virtual `link` helper.
- `UseCases/ManagedAppsHandler.cs`: Scans `apps\`, resolves `mby`/`melomg` executables, imports local `.exe`s, uninstalls tools, and tracks GitHub tool downloads/updates.
- `UseCases/ManageLibraryHandler.cs`: Validates and mounts compiled WebApp folders and updates `RecentWebApps` atomically.
- `UseCases/ProcessCliSignalHandler.cs`: Parses, deduplicates, and mounts WebApps reported via `.mby-signal.txt`.
- `UseCases/PromptHandlers.cs`: Composes Prompt Studio Markdown via `LoadSpecCatalogHandler` + `PromptComposer` and saves prompt files atomically.
- `UseCases/ReadyWorkspaceHandler.cs`: Exports targeted `AGENTS.md` playbooks (checking disk existence and overwrite confirmation) into the workspace or chosen subfolder.
- `UseCases/SelfUpdateHandler.cs`: Checks GitHub Releases for newer Inno Setup installers and triggers verified download + graceful shutdown-and-launch.
- `UseCases/SessionTemplateHandler.cs`: Generates strict scaffold JSON (plus placeholder image when required) and runs `mby apply` to create draft workspaces.
- `UseCases/SettingsAndPresetsHandler.cs`: Validates and updates persisted settings, WebApp theme override flag, and custom presets via `ISettingsStore.UpdateAsync`.
- `UseCases/WorkspaceActionsHandler.cs`: Invokes `mby ls`, `mby build` (+ mount in viewer), and `mby pkg export` inside the Active Workspace.
- `UseCases/WorkspaceMaintenanceHandler.cs`: Coordinates recursive workspace executable/artifact reset plans/executions and outdated-tool updates.
- `UseCases/WorkspaceSetupHandler.cs`: Handles Active Workspace selection (moving `mby-pc.bat` cleanly), initialization conflict planning, and non-destructive copy execution.
- `UseCases/WorkspaceStateSync.cs`: Shared helper rescanning workspace executables and synchronizing `ActiveWorkspaceState` in settings.

### `src/Meloberry.Wrapper.Infrastructure`
- `Meloberry.Wrapper.Infrastructure.csproj`: `net10.0` Infrastructure project embedding all `WebAssets\**\*` resources and referencing `Application`.
- `DependencyInjection.cs`: Registers all Infrastructure stores, CLI/GitHub clients, file watchers, server endpoints, hubs, CLI dispatcher, and `LocalHttpWrapperServer`.
- `Cli/CliProcessRunner.cs`: Shared out-of-process runner setting `MBY_AGENT=1`, optional `stdin`, UTF-8 streams, timeouts, and structured JSON error parsing.
- `Cli/LiveGuiIpcClient.cs`: Reads `runtime-ipc.json`, verifies active GUI PID, and relays `--cli` mutations/queries to loopback `/api/live/*`.
- `Cli/ProcessMbyCliClient.cs`: Invokes `mby spec --icons --json` (with legacy retry), `mby ls`, `mby build`, `mby pkg export`, `mby apply`, and `mby batch -`.
- `Cli/ProcessMelomgCliClient.cs`: Invokes `melomg spec`, `melomg ls`, and `melomg extract` to inspect PDFs and extract figures.
- `Cli/ToolProxyRunner.cs`: Proxies `mby` and `melomg` from `apps\`, forwards live GUI activity, and auto-loads Viewer on `mby build`.
- `Cli/WrapperCliAbstractions.cs`: `IWrapperConsole` and `SystemWrapperConsole` abstractions keeping headless/piped CLI mode testable.
- `Cli/WrapperCliDispatcher.cs`: Headless/live `--cli` dispatcher for `mby`, `melomg`, `prime`, `status`, `draft`, `finalize`, and `ui`.
- `Common/AtomicFileOps.cs`: Static atomic temp-file write/copy/stream primitives (`AtomicFileOps`) and `AtomicFileWriter` adapter.
- `GitHub/GitHubHttpClientHelper.cs`: Enforces HTTPS GitHub host allowlisting, manual redirect validation, 250 MB cap, PE (`MZ`) header verification, and SHA-256 verification.
- `GitHub/GitHubSelfUpdater.cs`: Queries wrapper releases, selects highest SemVer `mby-pc-setup-*.exe`, downloads, and cleans up stale installers.
- `GitHub/GitHubToolDownloader.cs`: Queries `releases/latest` (or `/contents/app` fallback) and downloads verified `mby`/`melomg` binaries.
- `Persistence/MbyPresetFileStore.cs`: Thread-safe atomic `.mbypreset` file store managing whole-workflow and single-component presets.
- `Persistence/PersistenceStores.cs`: Thread-safe `JsonSettingsStore` (with atomic `UpdateAsync`) and `WebAppAnalysisStore` writing `<webDir>/reports/*.analysis.{json,md}`.
- `Persistence/WorkspaceDraftStore.cs`: Thread-safe atomic store persisting `<Workspace>\.meloberry\pc-draft.json`.
- `Server/ApiContracts.cs`: Internal HTTP JSON request DTO records for shell and live-control API endpoints.
- `Server/HttpEnvelope.cs`: Bounded UTF-8 JSON body reader, camelCase serializer options, `ErrorType`-to-HTTP status mapper, and response writers.
- `Server/LocalHttpWrapperServer.cs`: Pure BCL dual-port `HttpListener` host (`ShellPort` + `ViewerPort`) with retryable port binding, Host/token guards, live SSE broadcasts, and request dispatch.
- `Server/RuntimeIpcRegistry.cs`: Writes and PID-validates `%LOCALAPPDATA%\Meloberry\runtime-ipc.json` for CLI-to-GUI coordination.
- `Server/SseEventHub.cs`: Bounded `Channel<byte[]>` SSE client registry with 15s keep-alive pings and concurrent broadcasts.
- `Server/StaticAssetHost.cs`: In-memory embedded asset host serving `shell.html`, bundled `/shell/shell.css`, `/shell/js/*` ES modules, fallback UI SVG icons, and WebApp theme-bridge HTML injection.
- `Server/WebAppMountRegistry.cs`: Thread-safe registry mapping compiled WebApp folders to `/viewer/<slotId>/` slots.
- `Server/Endpoints/AppsEndpoints.cs`: HTTP route handler for `/api/apps/*`.
- `Server/Endpoints/LiveControlEndpoints.cs`: HTTP route handler for `/api/live/{draft,mutate,finalize,status,ui}`.
- `Server/Endpoints/PromptEndpoints.cs`: HTTP route handler for `/api/specs`, `/api/prompt/*`, and `/api/presets`.
- `Server/Endpoints/SystemEndpoints.cs`: HTTP route handler for `/api/bootstrap`, `/api/viewer/open`, `/api/self-update/*`, `/api/dialogs/*`, `/api/shell/open-url`, `/api/settings*`, and ViewerPort `/api/analysis`.
- `Server/Endpoints/WorkspaceEndpoints.cs`: HTTP route handler for `/api/workspace/*`, `/api/workspaces/*`, `/api/melomg/extract-figure`, and `/api/workspace/asset`.
- `WebAssets/shell.html`: Shell UI HTML host with 5-step Option B workflow (`Setup → Details → Organize → Finalize → Viewer`), zero-mode Step 2 Project Details with collapsible study notes, two-tab Step 4 Finalize Hub, streamlined `#specContentModal` (Prev/Next stepper, inline subtypes, `+ Starter`, `? Syntax` toggle, 2-tab Write/Link `.md` + inline image attachment strip, collapsible `<details id="specMelomgDetails">`), `<meta name="mby-token">`, and `<script type="module" src="/shell/js/main.js">`.
- `WebAssets/styles/tokens.css`: Root Berry, Light, and Dark CSS theme custom properties (including `--select-soft` Quiz accent).
- `WebAssets/styles/paper-theme.css`: Paper theme CSS variables shared between the shell bundle and mounted WebApp fallback injection.
- `WebAssets/styles/base.css`: Global reset, minimalist scrollbars, typography, `.step-centered-grid`/light cursive `.step-hero` classes, `.sync-wipe-in`, cards, buttons, inputs, chips, dropdowns, and progress bars.
- `WebAssets/styles/chrome.css`: Custom 50px titlebar with centered 5-step dots-and-lines stepper, notification center, live agent activity pill, clickable workspace tree strip, `AGENTS.md` pill, outdated banner, and sliding view-panel styles.
- `WebAssets/styles/setup.css`: Setup view cards, compact `.app-card-central` central tool rows, staged file list, sticky init footer, and Settings block styles.
- `WebAssets/styles/studio.css`: Step 2 (Details) `.notes-disclosure`, Step 3 toolbar, and Step 4 (Finalize) two-tab readiness/handoff styles.
- `WebAssets/styles/organize.css`: Step 3 (Organize) horizontal portrait Article/Quiz cards, Quiz/Article theme highlights, content badges, hover-revealed `.org-spec-edit` pencil button, live agent pulse animations, and pointer drag ghost styles.
- `WebAssets/styles/viewer.css`: Full-bleed WebApp viewer iframe, loading overlay, floating action menu, theme override switch, and chained submenus.
- `WebAssets/styles/overlays.css`: Hover path tooltips, Add Spec & Spec Inspector popovers (with collapsible `.inspector-ai-notes`), Spec Content Modal, collapsible `melomg` `<details>` figure extractor, Active Workspace directory tree popup, Settings modal, confirmation modals, and toast notifications.
- `WebAssets/js/api.js`: Consumes `<meta name="mby-token">` into private module scope; exports `api()`, `openEventStream()`, and `fetchAssetBlobUrl()`.
- `WebAssets/js/store.js`: Centralized reactive app state, live draft sync flags, monotonic `live.activitySeq`, `VIEW_ORDER`/`WORKFLOW_STEPS`, pub/sub event bus (`on`/`emit`), formatters, and `withButtonBusy`.
- `WebAssets/js/icons.js`: Fetches `/shell/icons.json` and resolves UI icons, live CLI spec icons, and generic fallback icons.
- `WebAssets/js/main.js`: Entry module wiring views, SSE live events (`fschange`, `webapp-built`, `draft-updated`, `view-requested` with `details`/`finalize` aliases, `agent-activity`), and `bootstrap()`.
- `WebAssets/js/ui/chrome.js`: Titlebar drag/caption controls, 5-step stepper sync, deduplicated severity-styled notifications (with `[Error.Code]` pills & structured missing-spec lists), live agent activity pill (45s stale-running expiry, hover tooltip, idle action-count summary), toasts, view switching, theme application, and workspace strip sync.
- `WebAssets/js/ui/dropdowns.js`: Declarative dropdown/popover registry with positioning, outside-click dismissal, and Escape handling.
- `WebAssets/js/ui/modal.js`: Reusable confirmation, `AGENTS.md` inspection, themed text-input prompt modal, and hover path tooltip helpers.
- `WebAssets/js/ui/nativeDrop.js`: Context-aware external file/folder drag-and-drop router (`window.__onNativeDrop`), clearing stale hover state and materializing preset `orderedItems` on Organize tile drops.
- `WebAssets/js/ui/workspaceTree.js`: Active-workspace-bounded directory tree popup with single-click folder drill-in, icon-only Up navigation, and double-click workspace switching confirmation.
- `WebAssets/js/views/settings.js`: Modal Settings popup with automatic debounced/immediate persistence, self-update checks/downloads, and ecosystem links.
- `WebAssets/js/views/setup.js`: Workspace selection, recent workspaces, compact central tool cards (`app-card-central`), custom app staging, and `mby-pc.bat` shim initialization.
- `WebAssets/js/views/viewer.js`: WebApp viewer iframe lifecycle, CLI signal auto-loading, theme-override `postMessage` bridge, discovery submenus, build-and-view, and `.mbypkg` export.
- `WebAssets/js/views/prompt/format.js`: Step 2 (Details) zero-mode container switching, metadata inputs, attachments, and collapsible `#studyNotesDisclosure` wiring.
- `WebAssets/js/views/prompt/organize.js`: Step 3 (Organize) horizontal portrait Article/Quiz cards, live agent highlights, 1-line tile preview snippets, content status badges (warning-styled incomplete `Img*` badges), 1-click pencil & double-click modal editing, inline title editing, and Add Component controls.
- `WebAssets/js/views/prompt/organizeDrag.js`: Pointer-event drag-and-drop controller for horizontal card and vertical/cross-card spec rearrangement with double-click debounce preventing inspector popover flicker.
- `WebAssets/js/views/prompt/organizeFlip.js`: Live FLIP animation helpers (`capturePositions`, `animateFromPositions`, `flipReorder`, keyed `capturePositionsByKey`/`animateFromKeyedPositions`, and `animateLabelWipe` rename transitions) for user and autonomous agent updates.
- `WebAssets/js/views/prompt/organizePresets.js`: Organize header grouped Preset dropdown (3 Session / 3 Article / 3 Quiz / Custom) and portable workflow/card preset handlers.
- `WebAssets/js/views/prompt/promptDraft.js`: Shared prompt state, live `/api/live/draft` load/save with SSE echo suppression, `applyAnimatedAgentDraft` keyed FLIP/pop-in/wipe orchestration, preset/spec loaders, draft payload builder, and debounced Markdown preview composer.
- `WebAssets/js/views/prompt/readyWorkspace.js`: Ready Workspace (`AGENTS.md`) export workflow, subfolder picker, and overwrite confirmation modal.
- `WebAssets/js/views/prompt/review.js`: Step 4 (Finalize) two-tab Hub (`Build Directly` | `Hand Off to AI`), empty-component readiness blocking with `Fix in Organize` action, inline AI sub-toggle (`Live Agent` | `One-Shot Prompt`) remembering last AI style, single-CTA finalize trigger (`source: "ui"`), fresh-prompt copy/save (`ensureFreshComposedPrompt`), Ready Workspace, and Export menu.
- `WebAssets/js/views/prompt/specContentModal.js`: Streamlined per-spec content authoring modal (`Write Content` | `Link .md File` + unified inline Image Attachment strip preserving body text), Prev/Next card stepper, inline subtype chips, `+ Starter`, Markdown `? Syntax`, keyboard shortcuts (`Ctrl+S` / `Ctrl+Enter`), backdrop dismissal, and collapsible `melomg` PDF figure extraction.
- `WebAssets/js/views/prompt/specModes.js`: Pure spec mode-group structure, round-robin multi-subtype preset expansion, subtype distribution math, and ordered item synchronization helpers.
- `WebAssets/js/views/prompt/specPopovers.js`: Clean single-line anchored `+ Add Spec` picker (tooltip-only `displayName`) and Spec Tile Inspector popover (`Spec Content` first, subtype/modifiers next, and collapsible `AI Prompt Instructions` disclosure).
- `WebAssets/js/views/prompt/specTemplates.js`: Markdown syntax reference hints, starter templates for all 19 spec topics, and 1-line Organize tile preview snippet extractor.
- `WebAssets/icons/fallback/*`: Embedded 24x24 fallback SVG spec icons (19 core topics) used only when live `mby spec --icons --json` returns no icon for a topic.
- `WebAssets/icons/ui/*`: Embedded SVG icon assets (27 UI navigation/action/fallback icons; live spec/subtype SVG icons live in `mby-cli`).
- `Workspace/AgentsMdFileWriter.cs`: Confined atomic reader/writer for `AGENTS.md` inside the Active Workspace root or a validated subfolder.
- `Workspace/FileIdentity.cs`: Byte-length + SHA-256 identity comparison helper (`FileIdentity.AreIdenticalAsync`).
- `Workspace/ManagedAppsStore.cs`: Canonical `%LOCALAPPDATA%\Meloberry\apps\` store with PE metadata inspection, import, uninstall, and precedence-ordered tool resolution.
- `Workspace/WorkspaceDirectoryBrowser.cs`: Safe non-hidden folder and file enumerator driving the Active Workspace directory tree popup.
- `Workspace/WorkspaceFileWatcherService.cs`: Debounced `FileSystemWatcher` over `apps\` and Active Workspace (ignoring `.meloberry/`) with suppression scopes.
- `Workspace/WorkspaceInitializer.cs`: Non-destructive workspace staging planner/copier, writes `mby-pc.bat` shim (without copying central CLI exes), and discovers compiled WebApps.
- `Workspace/WorkspaceMaintenanceService.cs`: Recursive executable/artifact reset planner/executor and outdated workspace CLI detector/updater.
- `Workspace/WorkspaceShimWriter.cs`: Manages owned `mby-pc.bat` shims strictly in the Active Workspace and safely removes owned shims on switch.
- `Workspace/WrapperDataPaths.cs`: Resolves `%LOCALAPPDATA%\Meloberry\` paths (including `mby-pc-path.txt` and `runtime-ipc.json`) with `%MBY_WRAPPER_DATA%` override support.

### `src/Meloberry.Wrapper.Desktop`
- `Meloberry.Wrapper.Desktop.csproj`: Win64 `WinExe` composition-root project bundling `WebView2Loader.dll` and publishing single-file `mby-pc-<version>.exe`.
- `Program.cs`: Dual-mode composition root handling early `--cli` attach/dispatch (before `SingleInstanceMutex`), `mby-pc-path.txt` registration, GUI `LocalHttpWrapperServer` + `runtime-ipc.json` lifecycle, `Win32WindowHost`, and `WebView2Host`.
- `Cli/ConsoleBridge.cs`: Selective Win32 `AttachConsole(ATTACH_PARENT_PROCESS)` bridge enabling interactive and piped `--cli` UTF-8 I/O.
- `Win32/ExternalUrlLauncher.cs`: Validates allowlisted HTTPS URLs and opens URLs/folders safely via `ShellExecuteW`.
- `Win32/NativeFileDialogs.cs`: Direct COM `IFileOpenDialog` / `IFileSaveDialog` wrapper for folder, multi-file, and save pickers without WinForms/WPF.
- `Win32/WebView2Host.cs`: Extracts/loads `WebView2Loader.dll`, hosts `CoreWebView2` in the borderless Win32 client area, locks navigation origins, and bridges `window.__onWindowState` and `window.__onNativeDrop`.
- `Win32/Win32Native.cs`: P/Invoke declarations, window messages, DWM attributes, and COM interop structs.
- `Win32/Win32WindowHost.cs`: Borderless Win32 HWND host with custom titlebar drag, `WM_NCHITTEST` resize edges, dark/light DWM backdrop/titlebar theming, and STA message loop.

### `tests/`
- `frontend/frontend-behavior.test.mjs`: Node (`node --test`) behavioral test suite exercising real ES modules (`chrome.js`, `format.js`, `review.js`, `organize.js`, `organizeDrag.js`, `organizeFlip.js`, `promptDraft.js`, `specModes.js`, `specPopovers.js`, `specContentModal.js`, `specTemplates.js`, `setup.js`, `nativeDrop.js`) against a lightweight DOM harness.
- `Meloberry.Wrapper.ArchitectureTests/Meloberry.Wrapper.ArchitectureTests.csproj`: Architecture test project referencing `Domain`, `Application`, `Infrastructure`, and `Desktop`.
- `Meloberry.Wrapper.ArchitectureTests/WrapperArchitectureTests.cs`: Enforces strict Clean Architecture layer dependency direction, zero third-party packages in `Domain`, zero banned UI/DB frameworks, and unified version metadata.
- `Meloberry.Wrapper.UnitTests/Meloberry.Wrapper.UnitTests.csproj`: xUnit unit and integration test project for Domain, Application, and Infrastructure.
- `Meloberry.Wrapper.UnitTests/AgentsMdComposerAndReadyWorkspaceTests.cs`: Verifies `AgentsMdComposer`, `PromptComposer`, and `ReadyWorkspaceHandler` overwrite/missing-path/override/shim behavior.
- `Meloberry.Wrapper.UnitTests/GitHubDownloadAndSelfUpdateTests.cs`: Verifies GitHub host allowlisting, SemVer release asset selection, PE/SHA-256 verification, and self-update installer cleanup.
- `Meloberry.Wrapper.UnitTests/LiveDraftAndControlTests.cs`: Verifies `WorkspaceDraftStore`, `RuntimeIpcRegistry`, and `LiveDraftHandler` live mutations and security confinement.
- `Meloberry.Wrapper.UnitTests/LocalServerAndAnalysisTests.cs`: Verifies dual-port origin isolation, `/api/analysis` guards, CSS bundling, `<meta name="mby-token">`, ES module import graph integrity, `HttpEnvelope`, `SseEventHub`, and DI resolution.
- `Meloberry.Wrapper.UnitTests/ManagedAppsStoreTests.cs`: Verifies PE metadata import detection, safe upgrades, uninstall behavior, and `ResolveToolExecutableAsync` precedence.
- `Meloberry.Wrapper.UnitTests/MarkdownAssemblerAndFinalizeTests.cs`: Verifies `WorkspaceMarkdownAssembler` uniform smart-wrap rules and `FinalizeWorkspaceHandler` `mby batch -` execution.
- `Meloberry.Wrapper.UnitTests/MelomgGuiExtractionTests.cs`: Verifies `MelomgFigureQueryNormalizer` and `ExtractFigureWithMelomgHandler` `Doc_*` reuse and one-shot extraction.
- `Meloberry.Wrapper.UnitTests/SettingsAndCliTests.cs`: Verifies `JsonSettingsStore` concurrency (`UpdateAsync`), workspace isolation, custom presets, live CLI spec/icon parsing, and fallback catalogs.
- `Meloberry.Wrapper.UnitTests/TestDoubles.cs`: Shared `NoopShutdown`, `NoopDialogs`, and `NoopLauncher` test doubles.
- `Meloberry.Wrapper.UnitTests/TestRepoPaths.cs`: Locates sibling `mby` and `melomg` binaries in the parent workspace for optional live CLI tests.
- `Meloberry.Wrapper.UnitTests/TokenAndScaffoldTests.cs`: Verifies token estimation, `SemverComparer`, `SpecCatalogDefaults`, 3-way `PromptComposer` modes, `SessionScaffoldGenerator`, and live `mby apply`.
- `Meloberry.Wrapper.UnitTests/WorkspaceFileWatcherTests.cs`: Verifies real-time `apps\`/`workspace` file change events and suppression scopes.
- `Meloberry.Wrapper.UnitTests/WorkspaceInitializerTests.cs`: Verifies `WorkspaceShimWriter` (`mby-pc.bat` active-workspace lifecycle), workspace copy planning, conflict policies, intra-batch collision detection, `AtomicFileOps`, and `FileIdentity`.
- `Meloberry.Wrapper.UnitTests/WorkspaceMaintenanceTests.cs`: Verifies recursive executable/artifact resets, `UndoReadyWorkspace`, and outdated-tool updates.
- `Meloberry.Wrapper.UnitTests/WrapperCliDispatcherTests.cs`: Verifies headless/live `--cli` dispatch (`prime`, `status`, `draft`, `ui`, tool proxy missing/build detection).

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
