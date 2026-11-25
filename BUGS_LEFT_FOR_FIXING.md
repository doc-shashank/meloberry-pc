`meloberry-pc-1.1.0` is a **massive upgrade** over `1.0.0`. Nearly every critical/high bug from `1.0.0` was properly resolved, and you went even further with genuine architectural & security hardening (dual-port Shell/Viewer origin isolation, `postMessage` theme bridge, passive-only PE metadata inspection, `SemverComparer`, `PathNormalization`, `Channel<byte[]>` SSE, and post-shutdown installer handoff).

Here is the exact breakdown of **what was fixed**, **new bugs introduced in `1.1.0`**, and **remaining bugs from `1.0.0`**, with exact line-level fixes.

---

# 1. Scorecard: v1.0.0 Bugs Status in v1.1.0

| # | v1.0.0 Bug | Status in v1.1.0 | Notes |
|---|---|---|---|
| 1 | `.\C:\...` invalid CLI commands in `AGENTS.md` | **FIXED** | `AgentsMdComposer.FormatCliCommand` + `MakeCodeFence` (bonus fix for backticks!). |
| 2 | Stale `selectedAppNames` breaking Plan/Init after upgrade/uninstall | **FIXED** | `pruneSelectedAppNames()`, `selectUpgradedApp()`, and scoped `WorkspaceSetupHandler.SelectWorkspaceAsync`. |
| 3 | Infinite "Outdated CLI" loop for unversioned `mby.exe` | **FIXED** | `WorkspaceMaintenanceService` now uses SHA-256 identity check + `ManagedAppsStore.ExtractVersion` + `SemverComparer`. |
| 4 | Leaked `.tmp` on failed verification & 1-byte TCP PE check | **FIXED** | `try/finally` cleanup + 2-byte `peHeader` accumulator in `GitHubClients.cs`, plus unit test with `OneByteStream`. |
| 5 | URL allowlist prefix bypass | **FIXED** | `EcosystemCatalog.TryValidateAllowlistedUrl` checks host, path boundary, port, `UserInfo`, and `..`. |
| 6 | Concurrent unsynchronized writes to SSE `OutputStream` | **FIXED** | Per-client bounded `Channel<byte[]>` drained solely in `HandleEventsStreamAsync`. |
| 7 | Zombie child processes & unhandled `Win32Exception` | **FIXED** (mostly) | Removed executing arbitrary `.exe`s on import (huge security win!), added 120s timeout + `Kill(entireProcessTree: true)` in `CliProcessRunner`. *Minor gap:* stdin still not redirected/closed. |
| 8 | Drag-and-drop hijacking WebView2 to `file:///` | **FIXED** | `AllowExternalDrop = false` + `NavigationStarting` + `FrameNavigationStarting` + `NewWindowRequested` guards. |
| 9 | Rebuilt WebApps serving stale cache / not reloading | **FIXED** (mostly) | `Cache-Control: no-store` on `/viewer/` + `?v=${Date.now()}` in `shell.js`. *Minor gap:* `shell.html` still lacks `Cache-Control: no-store`. |
| 10 | Semver prerelease comparison & `.exe` regex capture | **PARTIALLY FIXED** | `SemverComparer` is great, **but `GitHubHttpClientHelper.ExtractVersionFromText` still captures `.exe` on prereleases!** |
| 11 | Organizer `+ Add` failing when `Qty` was empty | **FIXED** | Computes count from `comp.orderedItems`. *New minor issue:* clearing `Qty` in Selector leaves stale `orderedItems`. |
| 12 | Prompt preview out-of-order race & Qty bloat | **PARTIALLY FIXED** | `state.previewSeq` prevents out-of-order overwrites and Qty is clamped to 99; still lacks debounce and clearing Qty leaves stale sequence. |
| 13 | `AGENTS.md` **Inspect** button broken after restart / SSE | **NOT FIXED** | `AgentsMdStatus` still has no `Preview` field. |
| 14 | Single-instance Mutex ignored `createdNew` | **FIXED** (with minor quirk) | Checks `createdNew` and focuses existing window, **but `SW_RESTORE` un-maximizes a maximized window**. |
| 15 | `TokenAndScaffoldTests` failing standalone `dotnet test` | **FIXED** | `TestRepoPaths` searches dynamically and skips cleanly when absent. |
| 16 | Drive-root staging (`C:\`) & unhandled IO 500s | **PARTIALLY FIXED** | IO `try/catch` added to `InitializeAsync`, `ManagedAppsStore`, and `WorkspaceMaintenanceService`. Drive-root staging and `PlanAsync` (`GetFiles` inaccessible dir / locked file) still unguarded. |
| 17 | Dismissing Outdated Tools bar left top gap | **FIXED** | Calls `updateChromeOffset()`. |
| 18 | Leaked `setInterval` pollers & double-click on self-update | **NOT FIXED** | Still no `try/finally` around `clearInterval` and `btnInstallSelfUpdate` isn't disabled while running. |
| 19 | Malformed `|*.*` filter in COM file picker | **FIXED** | Cleaned up label and `NativeFileDialogs.ApplySingleFilter` (and added deterministic COM `FinalReleaseComObject`!). |
| 20 | `ReadyWorkspaceHandler` dropping live topics & misreporting external files | **FIXED** | Injects `LoadSpecCatalogHandler`, separates `ExternalAttachments`, and surfaces CLI `Warnings`! |
| 21 | `SelfUpdateHandler` stale `_lastCheck` on repo change | **FIXED** | Compares `RepoSlug` + validates GitHub owner/repo identifiers. |
| 22 | `POST /api/analysis` `localhost` Origin & MD-only path | **FIXED** | Allows `localhost` on `ViewerPort`, rejects missing Origin+Referer, adds 10 MB cap (413), returns `.md` path & counts both. |
| 23 | Build/Cache/UI hygiene (`csproj`, spec cache mtime, preset delete, DPI) | **PARTIALLY FIXED** | Spec cache is now thread-safe (`SemaphoreSlim`), `Win32WindowHost` drains queue on `WM_DESTROY`. `csproj`, mtime cache check, preset delete label, and `GetSystemMetricsForDpi` remain. |

**Result: ~17 of 23 bugs fully fixed, 4 partially fixed, 2 untouched — plus several bonus fixes (`DefaultBuildOutputDir` subfolder, dual-port origin isolation, `assertion-reason` `both-false` mode, `containerCache`, markdown fence escaping, COM cleanup).**

---

# 2. New Bugs Introduced in v1.1.0

### NEW-1 (Critical — Breaks `dotnet test`): `WrapperArchitectureTests` still asserts version `"1.0.0"`
* **File & Line:** `tests/Meloberry.Wrapper.ArchitectureTests/WrapperArchitectureTests.cs` (line 66)
* **Cause & Consequence:** `Directory.Build.props` was bumped to `<Version>1.1.0</Version>`, so `AssemblyWrapperMetadataProvider` returns `"1.1.0"`, causing `AllAssemblies_ContainZeroRedundantFrameworksAndUnifiedVersion` to **fail `dotnet test`**.
* **Exact Fix (`WrapperArchitectureTests.cs`, replace line 66):**
  ```csharp
  Assert.Single(versions);
  Assert.Matches(@"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$", versions.Single());
  ```
  *(This still guarantees all 4 assemblies share one unified SemVer without breaking every release bump.)*

---

### NEW-2 (High — Protocol Violation): `HEAD /viewer/<slotId>/...` writes full response body
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/Server/LocalHttpWrapperServer.cs` (lines 191–195, 612–664)
* **Cause & Consequence:** Line 191 was expanded to accept `method is "GET" or "HEAD"`, but `ServeMountedWebAppAsync` unconditionally writes the entire HTML/file bytes to `resp.OutputStream` even when `method == "HEAD"`. Under `HttpListener`, writing a body to a `HEAD` response violates HTTP/1.1 and can throw `ProtocolViolationException` or corrupt keep-alive connections.
* **Exact Fix (`LocalHttpWrapperServer.cs`):**
  Pass `bool isHead` into `ServeMountedWebAppAsync(ctx.Response, path, method == "HEAD", ct)`:
  ```csharp
  // Inside ServeMountedWebAppAsync, right after setting ContentType, Cache-Control, and StatusCode = 200:
  if (isHead)
  {
      resp.Close();
      return;
  }
  ```

---

### NEW-3 (Medium — Malformed HTML DOM): Missing closing `</div>` for `<div id="app" class="app-shell">`
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/WebAssets/shell.html` (lines 10, 65, 129, 387–390)
* **Cause & Consequence:** Three nested wrapper `<div>`s are opened (`#app.app-shell`, `.workspace-stage`, `.views-viewport`), but line 388–389 only closes two of them (`</div>` × 2 instead of × 3). As a result, all modals (`#settingsModal`, `#confirmAgentsModal`, `#notePopover`, `#toastContainer`) are parsed **inside** `#app` (`height: 100vh; overflow: hidden`) instead of as direct children of `<body>`.
* **Exact Fix (`shell.html`, replace lines 387–390):**
  ```html
        </main>
      </div>
    </div>
  </div>
  ```

---

### NEW-4 (Medium — UX Regression): Launching a second instance un-maximizes a maximized window
* **File & Lines:**
  * `src/Meloberry.Wrapper.Desktop/Program.cs` (lines 37–41)
  * `src/Meloberry.Wrapper.Desktop/Win32/Win32Native.cs`
* **Cause & Consequence:** Meloberry PC launches maximized (`SW_SHOWMAXIMIZED`). When a second instance runs, `Program.cs` unconditionally calls `Win32Native.ShowWindow(existing, Win32Native.SW_RESTORE)`, which shrinks an already-maximized window down to restored size instead of keeping it maximized.
* **Exact Fix:**
  Add P/Invoke to `Win32Native.cs`:
  ```csharp
  [DllImport("user32.dll")]
  public static extern bool IsIconic(IntPtr hWnd);
  ```
  Replace lines 39–40 in `Program.cs`:
  ```csharp
  if (Win32Native.IsIconic(existing))
  {
      Win32Native.ShowWindow(existing, Win32Native.SW_RESTORE);
  }
  Win32Native.SetForegroundWindow(existing);
  ```

---

### NEW-5 (Medium — Contradictory Prompt Output): Clearing `Qty` to empty in Selector leaves stale `Exact Sequence`
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/WebAssets/shell.js` (lines 688–693)
* **Cause & Consequence:** Because `cnt.oninput` now returns early when `raw === ""` without updating `comp.orderedItems`, if a user sets `mcq` to `Qty = 3` (populating `orderedItems` with 3 items) and then clears `Qty` back to empty (`Auto`), the composed prompt displays `| mcq | Auto |` in the table while **still printing `- Exact Sequence: mcq #1 -> mcq #2 -> mcq #3`** directly beneath it.
* **Exact Fix (`shell.js`, replace lines 688–693):**
  ```javascript
  if (raw === "") {
    sel.desiredCount = null;
    if (comp.orderedItems) {
      comp.orderedItems = comp.orderedItems.filter(o => o.topic.toLowerCase() !== spec.topic.toLowerCase());
      if (!comp.orderedItems.length) comp.orderedItems = null;
    }
    refreshPromptPreview();
    return;
  }
  ```

---

### NEW-6 (Low — Potential WebApp Compatibility): `<iframe sandbox>` omits `allow-modals` & dead `LaunchInstallerSilent`
* **Files & Lines:**
  * `src/Meloberry.Wrapper.Infrastructure/WebAssets/shell.html` (line 353)
  * `src/Meloberry.Wrapper.Application/Abstractions/Ports.cs` (line 78) & `src/Meloberry.Wrapper.Infrastructure/GitHub/GitHubClients.cs` (lines 480–500)
* **Cause & Consequence:**
  1. `sandbox="allow-scripts allow-same-origin allow-forms allow-downloads"` silently suppresses `window.confirm()` / `alert()` / `prompt()` if a compiled `mby` Study WebApp uses them (e.g., "Reset quiz progress?"). If `mby` WebApps use native confirm dialogs, add `allow-modals`.
  2. `IGitHubSelfUpdater.LaunchInstallerSilent` is now unused dead code (superseded by `IAppShutdownCoordinator.RequestShutdownAndLaunch`). Removing it keeps the port interface clean.

---

# 3. Remaining Bugs from v1.0.0 (Still Present in v1.1.0)

### REM-1 (High — Infinite Update Loop on Prereleases): `ExtractVersionFromText` captures `.exe`
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/GitHub/GitHubClients.cs` (lines 214–218)
* **Cause & Consequence:** `SemverRegex()` (`(\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?)`) greedily consumes `.exe` on prerelease filenames (`mby-cli-2.0.0-rc.1.exe` → `"2.0.0-rc.1.exe"`). Meanwhile, `ManagedAppsStore.ExtractVersion` strips `.exe`, returning `"2.0.0-rc.1"`. With the new `SemverComparer`, `"2.0.0-rc.1.exe"` (3 prerelease segments) compares **greater than** `"2.0.0-rc.1"` (2 segments), creating an **infinite update loop** for any prerelease tool or self-update!
* **Exact Fix (`GitHubClients.cs`, replace lines 214–218):**
  ```csharp
  public static string? ExtractVersionFromText(string text)
  {
      var cleaned = text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
      var m = SemverRegex().Match(cleaned);
      return m.Success ? m.Groups[1].Value : null;
  }
  ```

---

### REM-2 (Medium — Broken Feature): `AGENTS.md` **Inspect** button shows placeholder after restart / SSE / workspace switch
* **Files & Lines:**
  * `src/Meloberry.Wrapper.Domain/Models/AgentsMdModels.cs` (lines 26–32)
  * `src/Meloberry.Wrapper.Infrastructure/Workspace/AgentsMdFileWriter.cs` (lines 11–32)
  * `src/Meloberry.Wrapper.Infrastructure/WebAssets/shell.js` (line 1098)
* **Cause & Consequence:** `AgentsMdStatus` still omits `Preview`. Whenever `bootstrap()`, `selectWorkspace()`, or an SSE `fschange` refreshes `state.agentsMdStatus`, `.preview` is lost and clicking **Inspect** prints `"(Click Ready Workspace to inspect freshly rendered content.)"` instead of the actual `AGENTS.md`.
* **Exact Fix:**
  Add `string? Preview = null` to `AgentsMdStatus` (`AgentsMdModels.cs`, line 31), and in `AgentsMdFileWriter.GetStatusAsync` (make `async`), read the file text when `info.Length <= 512 * 1024`:
  ```csharp
  var preview = info.Length <= 512 * 1024
      ? await File.ReadAllTextAsync(agentsMdPath, ct).ConfigureAwait(false)
      : "(AGENTS.md exceeds 512 KB preview limit — open the workspace folder to inspect.)";
  return Result<AgentsMdStatus>.Success(new AgentsMdStatus(
      fullWs, agentsMdPath, Exists: true, SizeBytes: info.Length,
      LastModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
      Preview: preview));
  ```

---

### REM-3 (Medium — Safety / Crash): Staging a drive root (`D:\`) or inaccessible subfolder crashes `PlanAsync`
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/Workspace/WorkspaceInitializer.cs` (lines 224–239)
* **Cause & Consequence:**
  1. Picking a drive root (`D:\`) gives `folderName == ""`, bypassing the recursive check and trying to copy the whole drive into `fullWs`.
  2. `Directory.GetFiles(fullSrc, "*", SearchOption.AllDirectories)` lacks `EnumerationOptions { IgnoreInaccessible = true }` and `try/catch`, crashing `PlanAsync` with HTTP 500 on any protected subfolder or locked file in `AreFilesIdenticalAsync`.
* **Exact Fix (`WorkspaceInitializer.cs`, replace lines 224–239):**
  ```csharp
  var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSrc));
  if (string.IsNullOrWhiteSpace(folderName))
  {
      return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
          WrapperErrors.InvalidPath($"Cannot stage a drive root directory: '{fullSrc}'."));
  }

  var destFolderRoot = Path.Combine(fullWs, folderName);
  if (IsSameOrDescendant(fullSrc, destFolderRoot) || IsSameOrDescendant(fullSrc, fullWs))
  {
      return Result<(string, List<PlannedFileCopy>, int, List<WorkspaceCopyConflict>)>.Failure(
          WrapperErrors.RecursiveCopyBlocked(fullSrc, fullWs));
  }

  var enumOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
  foreach (var srcFile in Directory.EnumerateFiles(fullSrc, "*", enumOpts))
  ```
  *(Also wrap `AreFilesIdenticalAsync` in `try/catch (IOException)` returning `false`, or return `WrapperErrors.IoFailure` in `BuildOperationsAsync`.)*

---

### REM-4 (Medium — Poller Leaks & Double-Click Race): `downloadToolFromGitHub` & `btnInstallSelfUpdate`
* **File & Lines:** `src/Meloberry.Wrapper.Infrastructure/WebAssets/shell.js` (lines 555–577, 1216–1233)
* **Cause & Consequence:** If `api(...)` rejects, `clearInterval(poller)` never runs (leaking a permanent 300–350ms polling loop), and `btnInstallSelfUpdate` can be double-clicked to fire concurrent self-update downloads.
* **Exact Fix (`shell.js`):** Wrap both `await api(...)` calls in `try { ... } finally { clearInterval(poller); btn.disabled = false; }`, and disable `btnInstallSelfUpdate` at the start of its `onclick` handler.

---

### REM-5 (Low — Minor Remaining Polish Items)
1. **`LocalHttpWrapperServer.cs` (line 635 & line 691):**
   * Wrap `Path.GetFullPath(Path.Combine(fullRoot, decoded))` in `try/catch` returning `404` instead of `500` on invalid path chars.
   * Add `resp.Headers["Cache-Control"] = "no-store";` to `ServeEmbeddedShellHtmlAsync` (line 691) so the embedded auth token is never cached.
2. **`CliProcessClients.cs` (line 27):**
   * Add `RedirectStandardInput = true` and `proc.StandardInput.Close();` after `proc.Start()` so any CLI attempting stdin reads fails fast instead of hanging 120 seconds.
3. **`PromptAndSpecHandlers.cs` (line 15):**
   * Store `File.GetLastWriteTimeUtc(mbyPath)` in `SpecCacheEntry` so replacing `mby.exe` in-place invalidates the spec cache.
4. **`Meloberry.Wrapper.Desktop.csproj` (lines 31–37):**
   * Use `GeneratePathProperty="true"` on `Microsoft.Web.WebView2` and `$(PkgMicrosoft_Web_WebView2)\runtimes\win-x64\native\WebView2Loader.dll` instead of hardcoding `$(NuGetPackageRoot)microsoft.web.webview2\1.0.3240.44\...`.
5. **`shell.js`:**
   * Add ~120ms debounce for `refreshPromptPreview()` on text inputs (lines 1066–1070) instead of firing an HTTP POST on every keystroke.
   * Reset `state.activePresetName` when deleting the active custom preset (lines 625–630), trim preset name in `btnSavePreset` (`if (!name || !name.trim()) return;`, line 1005), and wrap `navigator.clipboard.writeText` in `try/catch` (lines 916–925).
6. **`Win32WindowHost.cs` (lines 226–227):**
   * Use `GetSystemMetricsForDpi` for multi-monitor mixed-DPI border insets.

---

# 4. Updated Quality Assessment

Your `1.1.0` iteration jumped the codebase from **8/10 to ~9/10** in architectural & security rigor:
* **Dual-port isolation (`ShellPort` vs. `ViewerPort`) + `sandbox` iframe + `X-Frame-Options: DENY` + `frame-ancestors 'none'` + deleting `window.__MBY_TOKEN__` + `postMessage` origin-checked theme bridge** is genuinely textbook desktop-web security. Even a compromised Study WebApp running inside the Viewer can no longer reach the Shell APIs or steal the auth token.
* **Passive PE metadata import** eliminates arbitrary code execution on `.exe` import.
* **Post-shutdown installer launch** after `instanceMutex.ReleaseMutex()` cleanly avoids Inno Setup mutex contention.

Once you fix the **1 failing architecture test (`WrapperArchitectureTests.cs:66`)**, the **`HEAD` body write**, the **missing `</div>` in `shell.html`**, the **prerelease `.exe` regex**, and the **`AGENTS.md` Inspect preview**, `v1.1.0` will be rock-solid for release.