# Meloberry PC (`Wrapper`) Evolution — Living Architecture & Alignment Report

> **Status:** Research & Design Interview Complete (`/grill-me` Decisions D1–D6 Locked)  
> **Date:** 2026-11-27  
> **Scope:** Quiz/monospace theme color fixes, `mby` v2.1.2 spec parity, `mby-pc.bat` workspace CLI-mode shim & tool proxy, Optional Prompt System & Live Agent mode, Per-Spec Direct Content authoring (Raw Text & `.md` files), and transactional Finalize-to-WebApp pipeline.

---

## 1. Executive Summary & Goal

Meloberry PC (`Wrapper`, v1.2.0) currently acts primarily as a workspace file-copier and Prompt Studio that copies full `mby` and `melomg` `.exe` binaries into every study workspace. This evolution transforms Meloberry PC into a **true Wrapper** and bidirectional host:

1. **Fix Screenshot #1 Theme Discrepancies:** Align Quiz cards, popovers, note dots, drop targets, and monospace `code` badges with `--select` / `--select-soft` across all four themes (`berry`, `light`, `dark`, `paper`).
2. **Replace Per-Workspace `.exe` Duplication with `mby-pc.bat` CLI Mode:** Store `mby` and `melomg` centrally in `%LOCALAPPDATA%\Meloberry\apps\` and emit a lightweight `mby-pc.bat` shim in each workspace. Agents invoke `.\mby-pc.bat` both to proxy `mby`/`melomg` commands (without copying binaries or risking stale workspace executables) and to inspect/manipulate Meloberry PC's live draft state via loopback HTTP + SSE or headless workspace files.
3. **Optional Prompt System & "Live Agent" Mode:** Support a 3-way workflow mode (`Direct` | `Live Agent` | `Classic Prompt`) so users can author and compile WebApps without AI prompts (`Direct`), hand off a live-synced workspace contract and current component organization to an AI agent (`Live Agent`), or use the classic Prompt Studio (`Classic Prompt`).
4. **Per-Spec Direct Content & Transactional Finalize Pipeline:** Allow each individual block/question spec tile in Step 3 (`Organize`) to carry direct content (`None`, `RawText`, or `MarkdownFile`), smart-wrapped into `mby` v2.1.2 Markdown directives, and compiled atomically via `mby batch -` (`apply` → `check` → `build --verify --static-only`) straight into Step 5 (`Viewer`).

---

## 2. Cross-Codebase Research Baseline

### 2.1 `Wrapper` (`Meloberry PC v1.2.0`)
- **Stack & Clean Architecture:** `.NET 10` (`net10.0`, `win-x64`, `OutputType=WinExe` in `Meloberry.Wrapper.Desktop.csproj`). Enforced by `WrapperArchitectureTests`:
  - `Domain` has zero third-party package references and no references to outer layers.
  - `Application` references only `Domain` and `Microsoft.Extensions.DependencyInjection.Abstractions`.
  - `Infrastructure` references `Application` (never `Desktop`).
  - All four assemblies share unified metadata from `Directory.Build.props` (`Version=1.2.0`, `AssemblyName=mby-pc`, `SelfUpdateRepoOwner=doc-shashank`, `SelfUpdateRepoName=meloberry-pc`).
  - Banned packages checked by `WrapperArchitectureTests`: `Microsoft.WindowsAppSDK`, `PresentationFramework`, `System.Windows.Forms`, `Microsoft.AspNetCore`, `Microsoft.ML`, `Microsoft.Data.Sqlite`.
- **Single-Instance Entrypoint (`src/Meloberry.Wrapper.Desktop/Program.cs`):**
  - Currently checks `--verify-loader` early (L25–32), then immediately acquires `SingleInstanceMutexName = "MeloberryPcSingleInstanceMutex"`. If another instance holds the mutex, it focuses the existing Win32 window and returns `0`.
  - **Required Hook:** CLI mode must intercept arguments **before** acquiring `SingleInstanceMutex` so `mby-pc.bat` commands succeed both while the GUI is running (talking to the live instance over loopback IPC) and while the GUI is closed (running tool proxies or headless draft/finalize flows).
  - **WinExe Console Caveat:** Because `OutputType` is `WinExe`, stdout/stderr only appear in a parent terminal if the process attaches to the parent console (`AttachConsole(ATTACH_PARENT_PROCESS)`) when handles are not already redirected (`Console.IsOutputRedirected` / `Console.IsErrorRedirected` / `Console.IsInputRedirected`).
- **Dual-Port Server & SSE (`LocalHttpWrapperServer.cs`, `SseEventHub.cs`, `StaticAssetHost.cs`):**
  - `ShellPort` serves `shell.html`, bundled `/shell/shell.css`, `/shell/js/*.js`, `/shell/icons.json`, `/api/events` (SSE with `?token=`), and `X-Mby-Token`-protected `/api/*`.
  - `ViewerPort` serves mounted compiled WebApps under `/viewer/<slotId>/` with theme-bridge injection and `POST /api/analysis`.
- **Workspace Tool Resolution & Rule #5:**
  - `WrapperDataPaths` resolves `%LOCALAPPDATA%\Meloberry` (overridable via `MBY_WRAPPER_DATA`), with `apps\`, `presets\`, `updates\`, `webview2-userdata\`, and `wrapper-settings.json`.
  - `IManagedAppsStore.ResolveToolExecutableAsync(toolKey, workspacePath, explicitOverride, ct)` is the mandatory single source of truth for locating `mby` and `melomg`.
  - Currently `WorkspaceInitializer` copies `mby` and `melomg` `.exe`s into every workspace root, and `WorkspaceMaintenanceService` scans for outdated workspace copies and resets executables.
- **Client-Only Prompt State (`store.js`):**
  - Today `state.prompt` lives purely in browser JS memory (`store.js`), while `/api/prompt/*` and `/api/workspace/ready` accept `PromptDraft` statelessly per request.
  - To enable external CLI inspection and live mutation synced to the open GUI, Meloberry PC needs a persistent per-workspace draft file (`<Workspace>\.meloberry\pc-draft.json`) backed by an Application-layer `LiveDraftHandler` and SSE push events (`draft-updated`, `view-requested`, `viewer-open-requested`).

### 2.2 Screenshot #1 Color Discrepancies (Exact Root Causes)
Inspected `tokens.css`, `paper-theme.css`, `base.css`, `organize.css`, and `overlays.css`:

| Element | Current CSS Behavior | Root Cause |
| :--- | :--- | :--- |
| Quiz Popover Monospace Labels (`mcq`, `flashcard`, `fill-blank`, etc.) & Inspector `<code>` | Renders terracotta/orange (`--accent` / `--accent-soft`) | `base.css:18` sets `code { background: var(--accent-soft); color: var(--accent); }`. `overlays.css:87-106` overrides `.spec-icon-wrap`, `.qty-pill-btn.active`, `.add-spec-item`, and `.mode-chip` inside `.add-spec-popover.kind-quiz` / `.spec-inspector-popover.kind-quiz`, **but omits `code`**. |
| Quiz Card `+ Add Question` Button (`.org-add-spec-btn`) | Renders terracotta dashed border/fill/text | `organize.css:323-338` hardcodes `--accent` and `--accent-soft` with no `.org-comp-card.kind-quiz .org-add-spec-btn` override. |
| Quiz Tile Note Indicator Dot (`.has-note-dot`) | Renders terracotta dot inside blue Quiz tiles | `organize.css:286-292` hardcodes `background: var(--accent)` with no `.kind-quiz` override. |
| Quiz Card Title Focus Ring (`.org-comp-title-input:focus`) | Focus border flashes terracotta | `organize.css:135-139` hardcodes `border-color: var(--accent)` with no `.kind-quiz` override. |
| Quiz Card Spec Drop Target (`.org-comp-body.spec-drop-target`) | Dragging a spec over a Quiz card highlights terracotta | `organize.css:175-178` hardcodes `--accent-soft` and `--accent` with no `.kind-quiz` override. |
| Missing `--select-soft` in Base Themes | `paper-theme.css:9` defines `--select-soft: #e0e7ff`, but Berry/Light/Dark do not | `tokens.css` lacks `--select-soft` across `:root`, `html[data-theme="light"]`, and `html[data-theme="dark"]`. |

### 2.3 `CLI` (`mby` v2.1.2) Capabilities & Gaps
- **Commands (14 + `help`):** `prime`, `help`, `spec`, `apply`, `patch`, `batch`, `get`, `ls`, `rm`, `check`, `build`, `pkg`, `layout`, `analyze`, `version`. Purely declarative (no `init`/`new`/`add`).
- **Global Flags & Envelope:** `--json` or `MBY_AGENT=1` outputs single-line camelCase JSON envelopes (`{"success":true,"data":{...}}` / `{"success":false,"error":{...}}`). Exit codes: `0` Success, `1` General/IO, `2` Validation/Lint/Verify, `3` NotFound, `4` Conflict, `5` Unauthorized.
- **Native Markdown & Modular Directory Support (`DeclarativeSpecLoader.cs`, `MarkdownSpecParser.cs`):**
  - `mby apply [addr] <file|dir|->` accepts strict JSON, `.md` with YAML frontmatter + `:::` directives, modular directories (`_session.md` or `session.json` + top-level numbered `*.md`/`*.json`), or stdin (`-`, auto-detects JSON vs Markdown).
  - Supports 6 block directives (`text`, `image`, `callout`, `table`, `diagram-explore`, `reveal` + `:::link`) and 12 question directives (`mcq`, `flashcard`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `case`, `classify`, `match`, `numeric`).
- **Atomic Batch Scripting (`BatchOperationsUseCase.cs`, `ChainTransactionManager.cs`):**
  - `mby batch -` reads a non-empty JSON array of ops (`apply`, `patch`, `rm`, `check`, `build`, `pkg`, `layout`).
  - Supports `$0.id`..`$N.id` and `$prev.id` substitution in `addr`/`target`, with full transactional rollback of DBs, assets, output dirs, and `app_settings.db`.
  - In `apply` batch step: uses `"file"` (or `"spec"`), `"out"`, `"build"`, `"verify"`, `"staticOnly"`, `"dryRun"`. In `build` batch step: uses `"addr"`, `"out"`, `"verify"`, `"staticOnly"`, `"timeout"`.
- **6 Newer Topics Missing from Wrapper's Fallback Catalog (`SpecModels.cs`):**
  - `diagram-explore` (`block`), `reveal` (`block`), `case` (`question`), `classify` (`question`), `match` (`question`), `numeric` (`question`).
- **`.mby-signal.txt` Status:** Confirmed not implemented anywhere in `CLI/src/**/*.cs`. Because agents will invoke `mby` through `mby-pc.bat mby ...`, Meloberry PC's tool proxy itself can detect successful `mby build` runs and auto-open the compiled WebApp in Step 5 (`Viewer`) without modifying the `CLI` repo.

### 2.4 `toolings/melomg` (`v2.2.1`) Capabilities
- **Commands (16):** `prime`, `spec`, `version`, `ingest`, `get`, `ls`, `extract`, `patch`, `add`, `mask`, `render`, `rm`, `check`, `batch`, `model`, `runtime`.
- **Location-Independent Execution:** Runs cleanly from `%LOCALAPPDATA%\Meloberry\apps\` with `WorkingDirectory` set to the Active Workspace. Honors `MBY_AGENT=1`, stores ONNX runtime/models/global registry in `%LOCALAPPDATA%\Meloberry`, writes workspace DBs into `<CWD>\.meloberry\melomg\Doc_<id>\`, and emits CWD-relative forward-slash paths in `manifest.json`.

---

## 3. Locked `/grill-me` Design Decisions (D1–D8)

| ID | Topic | Locked Decision |
| :--- | :--- | :--- |
| **D1** | **Active-Workspace-Only `.bat` Shim vs `.exe` Copying** | Replace copying `mby`/`melomg` `.exe`s with a single `mby-pc.bat` shim (marked with `:: Meloberry PC Workspace Shim`) placed strictly in the **currently Active Workspace** directory (resolving `mby-pc.exe` via `%LOCALAPPDATA%\Meloberry\mby-pc-path.txt` with fallback to `%LOCALAPPDATA%\Programs\Meloberry PC\mby-pc.exe`). Whenever the Active Workspace changes to another directory, `RemoveShimIfManagedAsync(previousWorkspacePath)` automatically deletes the old workspace's `mby-pc.bat` (only if the marker comment is present) and writes `mby-pc.bat` in the new Active Workspace—leaving zero orphaned `.bat` files behind. Keep optional `.exe` copying only for custom imported tools. |
| **D2** | **CLI Mode & Headless Fallback** | `mby-pc.bat` provides tool proxies (`mby`, `melomg`) and app-control commands (`prime`, `status`, `draft`, `finalize`, `ui`). Tool proxies, `draft`, and `finalize` work headlessly via `<Workspace>\.meloberry\pc-draft.json` when GUI is closed (and sync live via loopback HTTP + SSE when GUI is open). GUI-only commands (`ui viewer open`, `ui view`) return structured `Gui.NotRunning` (exit `3`) unless `--launch` is passed. |
| **D3** | **Prompt System Optionality & Live Agent Visual Authoring** | Add a 3-way workflow mode selector (`Direct` \| `Live Agent` \| `Classic Prompt`) in Step 2 (`Format`) and synced in Step 4 (`Review`), keeping the 5-step stepper intact. `Direct` hides prompt clutter and turns Step 4 into a Build Readiness & Finalize view. `Live Agent` generates a lean `.\mby-pc.bat` handoff prompt + current organization AND streams **live visual agentic authoring in the GUI**: SSE `draft-updated` (`{ source: "agent", action, summary, targetCompId, targetSpecId }`) and `agent-activity` update the Organize cards/tiles in real time, flash an animated `.agent-touched` highlight glow + smooth scroll on modified cards/tiles, display a live **Agent Activity** pill in the workspace strip, and transition smoothly into Step 5 (`Viewer`) when the agent builds or finalizes. `Classic Prompt` preserves full Prompt Studio. |
| **D4** | **Per-Spec Direct Content Authoring** | Store per-instance content (`None` / `RawText` / `MarkdownFile`) on `OrderedSpecInstance`. Smart-wrap plain body content into the tile's `:::spec{subtype}` directive (passing pre-fenced `:::` or JSON through unchanged). Provide a quick `Raw` / `.md File` toggle in the Spec Inspector plus an "Expand Editor" modal with live syntax hints from `mby spec`, and support `.md` drag-and-drop onto spec tiles. |
| **D5** | **Finalize Pipeline** | Clicking **Finalize** (or running `mby-pc.bat finalize`) materializes assembled modular Markdown into `<Workspace>\.meloberry\build-src\<slug>\`, executes transactional `mby batch -` (`apply` → `check` → `build` with `verify: true, staticOnly: true`) into `<Workspace>\<slug>_web`, auto-opens in Step 5 (`Viewer`), and blocks/warns on empty specs unless an explicit "Allow Placeholders" toggle is enabled. |
| **D6** | **Spec Icons & Display Names Owned by `mby-cli`** | Move all 47 existing spec SVG icons from `Wrapper/WebAssets/icons/specs/` + add the 6 newer v2.1.2 SVGs (`diagram-explore`, `reveal`, `case`, `classify`, `match`, `numeric`) into `CLI/src/Meloberry.Infrastructure/Web/Assets/SpecIcons/` (53 total). Add `displayName` on `SchemaTopicDefinition` and an opt-in `--icons` flag on `mby spec` (`mby spec --icons --json`) so normal AI agent `mby spec --json` calls stay zero-bloat (`icons` omitted when null), while Wrapper fetches both spec display names and SVG icons dynamically from `mby-cli` (retrying without `--icons` if an older `mby` binary rejects `--icons`). Wrapper retains only its 25 `ui/` icons plus 2 generic fallback SVGs (`ui/block-generic.svg`, `ui/question-generic.svg`). `toolings/melomg` remains 100% untouched. |
| **D7** | **Graceful Degradation When `mby` / `melomg` Not Installed** | **Without `mby`:** Setup shows `Not Installed` + 1-click GitHub download / `.exe` import; Format/Organize remain usable offline via minimal built-in fallback spec metadata + generic block/question icons + `"Fallback specs (mby not found)"` notice; `Finalize` and `Generate Template` disable cleanly with `"Requires mby CLI"` tooltip; Viewer still opens existing compiled WebApps; `mby-pc.bat mby ...` exits `3` (`Cli.NotFound`). **Without `melomg`:** all authoring, Finalize, and Viewer flows work normally; GUI `melomg` figure extractor shows an inline `"Install melomg in Setup"` CTA while manual image browsing still works; `mby-pc.bat melomg ...` exits `3` (`Cli.NotFound`). |
| **D8** | **Direct GUI `melomg` Figure Resolution** | Verified `toolings/melomg/src/Melomg.Application/Addressing/AddressResolver.cs`: `melomg extract` expects bare selectors (`1.1`, `2.3a`, `f1`, `p4`, `all`), not prefixed strings like `"Figure 1.1"`. Wrapper adds `MelomgFigureQueryNormalizer` (`"Figure 1.1"` / `"Fig. 1.1"` / `"Diagram 2.3"` → `"1.1"` / `"2.3"`), reuses an already-ingested workspace `Doc_*` via `melomg ls --json` when available (or invokes `melomg extract <pdf> <selector> -o <ws>\assets --quality high --retain-db --offline --json` on first use so subsequent figures from the same PDF skip re-ingestion), parses `data.artifacts[]`, attaches the workspace-relative image path (`assets/fig_1.1.png`) directly into image-capable specs (`image`, `diagram-explore`, `relabel`, `flashcard`, `mcq`, `spot-error`, `numeric`, `case`), optionally passes `--mask-all --meta` for `relabel`/diagram flashcards, and shows a live thumbnail preview in the Inspector/Editor. |

---

## 4. Architectural Constraints & Risk Mitigations

1. **Clean Architecture & Test Rules:**
   - All `Domain` additions (`WorkflowMode`, `SpecContentMode`, `WorkspaceMarkdownAssembler`, updated `PromptModels` / `SpecModels`) remain pure C# with zero external packages.
   - Follow Rule #2 (TDD-first): write tests before each phase's implementation (`Phase<n>Tests<m>.cs`), then consolidate into permanent topic test files and delete temporary phase test files in Phase 7.
   - Run `dotnet build Meloberry.Wrapper.sln` (`TreatWarningsAsErrors`) and `dotnet test Meloberry.Wrapper.sln` after edits (Rule #6).
   - Update `Wrapper/AGENTS.md` Sections 2 & 3 whenever files are added/moved/deleted (Rule #4), and keep source files under ~400–500 lines (Rule #7).
2. **Loopback IPC Security:**
   - `runtime-ipc.json` written to `%LOCALAPPDATA%\Meloberry\runtime-ipc.json` on GUI startup (and cleaned up on shutdown) holds `{ pid, shellPort, viewerPort, token, startedUtc }`. Because `LocalHttpWrapperServer` already enforces loopback `Host` header + `X-Mby-Token`, local CLI processes running as the same Windows user can authenticate cleanly without exposing unauthenticated endpoints.
3. **WinExe Console Attachment:**
   - `ConsoleBridge` must attach via `AttachConsole(ATTACH_PARENT_PROCESS)` only when CLI mode is detected **and** standard handles are not redirected; otherwise piped invocations (`mby-pc.bat mby apply - < file.md` or PowerShell JSON capture) could lose redirected streams.

---

## 5. Simplicity, Tool Proxy Flow & Independence Audit

### 5.1 Simplifications Applied
1. **4-Endpoint `/api/live/*` API:** Replaced 8+ fragmented routes with `GET/PUT /api/live/draft`, `POST /api/live/mutate` (single unified draft mutation endpoint), `POST /api/live/finalize`, and `GET /api/live/status` + `POST /api/live/ui`.
2. **Thin 5-Verb CLI Controller:** `WrapperCliDispatcher` exposes only `mby`, `melomg`, `draft` (including component/spec mutations), `finalize`, and `status`/`prime`/`ui` — zero duplicated `mby` validation or compilation logic.
3. **Uniform Data-Driven Smart-Wrapping:** `WorkspaceMarkdownAssembler` formats topics uniformly via `:::<topic>{<attrs>}\n<body>\n:::` (special cases only for `text`, `image`, and `link`), keeping the assembler compact (~60–100 lines) and forward-compatible with new `mby` directive topics.

### 5.2 Tool Proxy Pipeline (`mby-pc.bat mby ...` / `mby-pc.bat melomg ...`)
1. Agent invokes `.\mby-pc.bat mby <args...>` (or `melomg <args...>`) inside the Active Workspace.
2. `mby-pc.bat` resolves `mby-pc.exe` from `%LOCALAPPDATA%\Meloberry\mby-pc-path.txt` (fallback `%LOCALAPPDATA%\Programs\Meloberry PC\mby-pc.exe`) and runs `"%MBY_PC_EXE%" --cli --workspace "%~dp0." %*`.
3. `Program.Main` detects `--cli` **before** acquiring `SingleInstanceMutex` (no Win32 window or WebView2 initialized).
4. `ToolProxyRunner` resolves the target binary exclusively via `IManagedAppsStore.ResolveToolExecutableAsync(...)` (`%LOCALAPPDATA%\Meloberry\apps\`) and spawns it with `WorkingDirectory` equal to the caller's CWD, exact 1:1 `ArgumentList`, inherited `MBY_AGENT`, transparent `stdin`/`stdout`/`stderr` forwarding, and exact child exit-code return (`exit /b %ERRORLEVEL%`).
5. When `runtime-ipc.json` shows the GUI is running, `ToolProxyRunner` posts a non-blocking `agent-activity` notice to `/api/live/ui` and — on exit `0` of `mby build` — reads `outputDirectory` from teed JSON stdout and triggers `/api/live/ui` (`viewer-open`) so Step 5 Viewer opens automatically.

### 5.3 Independent Development Guarantee (`mby-cli` & `melomg`)
- **Zero compile-time or binary coupling:** All three repositories (`Wrapper`, `CLI`, `toolings/melomg`) stay completely separate with zero shared assemblies or project references.
- **Opaque pass-through proxy:** New flags, subcommands, question types, or ONNX models added to `mby` or `melomg` work immediately through `mby-pc.bat` without modifying `Wrapper`.
- **Dynamic schema & uniform wrapping:** `Wrapper` discovers new `mby` topics via `mby spec --json` and wraps bodies uniformly via `:::<topic>{...}` when not already fenced.
- **Instant central upgrades:** Updating `mby` or `melomg` in `%LOCALAPPDATA%\Meloberry\apps\` immediately applies to all workspaces without re-copying binaries.
