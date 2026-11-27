# Meloberry CLI Workspace Signal Protocol Specification (`.mby-signal.txt`)

> **Audience:** Developers building **`mby` CLI** and **Meloberry PC (`mby-pc`)**. This document defines the lightweight file-based IPC contract enabling `mby` CLI to automatically notify Meloberry PC when a study WebApp has been built so the Viewer loads it immediately.

---

## 1. Motivation & Design Rationale

When an AI coding agent or terminal user runs `mby build` inside an Active Workspace, watching `index.html` or `app.js` directly via Windows `FileSystemWatcher` suffers from several reliability issues:

1. **Partial / mid-build reads:** Static bundlers write `index.html`, `app.js`, and figure assets across multiple I/O operations; opening `index.html` too early breaks the Viewer or caches stale assets.
2. **No metadata context:** Raw directory writes do not carry the Meloberry session ID, human title, or explicit event intent (`webapp_built` vs unrelated edits).
3. **Hidden vs visible clutter:** Signal metadata should not pollute the user's Active Workspace directory tree popup.

Using a single root dot-file **`.mby-signal.txt`** written **atomically as the final step** after `mby build` completes gives Meloberry PC a clean, race-free handshake over its existing `FileSystemWatcher` + SSE pipeline.

---

## 2. File Location & Visibility Rules

- **Path:** `<ActiveWorkspace>\.mby-signal.txt` (must reside directly in the Active Workspace root directory).
- **Name:** Exactly `.mby-signal.txt` (case-insensitive on Windows).
- **Visibility:** Leading `.` hides the file from Meloberry PC's directory tree popup (`WorkspaceDirectoryBrowser` ignores dot-prefixed entries). Optionally set `FileAttributes.Hidden` on Windows if desired, though dot-prefix alone suffices and keeps `git` / editor inspection simple.

---

## 3. Atomic Write Protocol (`mby` CLI Requirement)

To guarantee `WorkspaceFileWatcherService` never reads a truncated signal file:

1. Ensure `<ActiveWorkspace>` directory exists.
2. Write the complete UTF-8 payload to a sibling temp file named **`.mby-signal.txt.tmp`** (or any `*.tmp` / `*.mbytmp` suffix — Meloberry PC explicitly ignores `.tmp` and `.mbytmp` watcher events).
3. Flush the stream to disk (`Flush(flushToDisk: true)` / close handle).
4. Atomically move/replace `.mby-signal.txt.tmp` over `.mby-signal.txt` (`File.Move(tmpPath, signalPath, overwrite: true)` or POSIX `rename`).

Meloberry PC keeps **`.mby-signal.txt`** in place (does **not** delete it) and deduplicates by the signal's `id` field, so concurrent editors or build retries remain inspectable while duplicate OS `Created` + `Changed` bursts never trigger double loads.

---

## 4. File Format & Grammar

- **Encoding:** UTF-8 text (LF `\n` or CRLF `\r\n` line endings accepted).
- **Structure:** Simple `key=value` lines:
  - Blank lines and lines beginning with `#` (after trimming leading whitespace) are ignored as comments.
  - Keys are **case-insensitive**, trimmed, and split on the **first `=`** character (so values may contain `=` or Windows/POSIX paths).
  - Do **not** wrap values in quotes unless the quotes are literally part of the value.

### Common Envelope Fields

| Key | Required | Format / Example | Description |
| :--- | :--- | :--- | :--- |
| `version` | Recommended (`1`) | `1` | Protocol version integer. |
| `event` | **Yes** | `webapp_built` | Event type identifier. |
| `id` | **Recommended** | UUID v4 or monotonic nonce (e.g., `550e8400-e29b-41d4-a716-446655440000`) | Unique per emission. Used by Meloberry PC to deduplicate repeated Windows `FileSystemWatcher` bursts. If omitted, a fallback composite key of `event|path|timestamp` is synthesized. |
| `timestamp` | Optional | ISO-8601 UTC (`2026-11-27T12:15:30Z`) | Event emission timestamp. |
| `producer` | Optional | `mby/2.1.0` | Originating CLI name & version. |

---

## 5. Event Catalog

### 5.1 `webapp_built` (Active in v1)

Emitted at the end of `mby build` once `index.html`, `app.js`, and all static assets have been flushed to the WebApp output folder.

| Key | Required | Description |
| :--- | :--- | :--- |
| `event` | **Yes** | Must be `webapp_built`. |
| `path` | **Yes** | Path to the compiled WebApp directory (containing `index.html` and `app.js`). May be **relative to `<ActiveWorkspace>`** (recommended, e.g., `neuro_01_web` or `builds\neuro_01_web`) or an **absolute path**. |
| `workspace_id` | Optional | Meloberry study workspace ID (e.g., `neuro_01`). |
| `title` | Optional | Human-readable study title displayed in the Viewer loading overlay and toast notification. |

#### Example `.mby-signal.txt` Payload

```ini
# Written atomically by mby CLI after build completion
version=1
event=webapp_built
id=9b2c1d4e-7f0a-4a62-93c1-2d8e4a1f6c90
timestamp=2026-11-27T12:15:30Z
workspace_id=neuro_01
path=neuro_01_web
title=Neurophysiology: Action Potentials
producer=mby/2.1.0
```

#### Meloberry PC Behavior on `webapp_built`
1. `WorkspaceFileWatcherService` receives `Created` or `Changed` for `<ActiveWorkspace>\.mby-signal.txt` (bypassing `SuppressScope("workspace")` so even CLI runs near wrapper actions succeed) with a short 120ms debounce.
2. `ProcessCliSignalHandler` reads `.mby-signal.txt` (up to 4 read attempts with 50ms backoff to absorb transient locks), parses `MbyCliSignal`, checks its bounded 128-entry `id` deduplication cache, and resolves `path`.
3. `ManageLibraryHandler.OpenWebAppAsync(resolvedDir)` validates `index.html` + `app.js`, mounts the WebApp on `ViewerPort` (`/viewer/<slotId>/index.html`), and prepends it to `RecentWebApps`.
4. Only after successful validation/mount is `signal.Id` marked seen, and `LocalHttpWrapperServer` broadcasts SSE event `webapp-built` (`{ slotId, url, entry, workspaceId, title, signalId }`) to the Shell UI.
5. The Shell UI loads the WebApp into `#webAppFrame`, refreshes discovery menus, **immediately switches to Step 5 (`Viewer`)**, and displays a toast + Notification Center entry.

---

### 5.2 Forward-Compatible Reserved Events (`mby` Roadmap)

Future `mby` CLI commands MAY emit these events without breaking v1 parsers (Meloberry PC v1 ignores unknown event names safely):

- **`template_applied`**: `event=template_applied`, `workspace_id=...`, `path=...` (signals a new draft study session folder was created by `mby apply`).
- **`pkg_exported`**: `event=pkg_exported`, `workspace_id=...`, `path=...` (signals `.mbypkg` bundle creation completed).
- **`build_failed`**: `event=build_failed`, `workspace_id=...`, `message=...` (signals compilation failure for toast/notification surfacing).

---

## 6. Reference C# Writer for `mby` CLI

```csharp
public static async Task EmitWebAppBuiltSignalAsync(
    string activeWorkspaceRoot,
    string webAppOutputDir,
    string? workspaceId = null,
    string? title = null,
    string? producerVersion = null,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(activeWorkspaceRoot) || !Directory.Exists(activeWorkspaceRoot))
        return;

    var relativeOrFull = Path.GetRelativePath(activeWorkspaceRoot, webAppOutputDir);
    var pathValue = relativeOrFull.StartsWith("..", StringComparison.Ordinal)
        ? Path.GetFullPath(webAppOutputDir)
        : relativeOrFull;

    var lines = new List<string>
    {
        "version=1",
        "event=webapp_built",
        $"id={Guid.NewGuid():N}",
        $"timestamp={DateTimeOffset.UtcNow:O}",
        $"path={pathValue}"
    };

    if (!string.IsNullOrWhiteSpace(workspaceId)) lines.Add($"workspace_id={workspaceId.Replace('\n', ' ').Trim()}");
    if (!string.IsNullOrWhiteSpace(title))       lines.Add($"title={title.Replace('\n', ' ').Trim()}");
    if (!string.IsNullOrWhiteSpace(producerVersion)) lines.Add($"producer=mby/{producerVersion}");

    var signalPath = Path.Combine(activeWorkspaceRoot, ".mby-signal.txt");
    var tmpPath    = Path.Combine(activeWorkspaceRoot, ".mby-signal.txt.tmp");
    await File.WriteAllTextAsync(tmpPath, string.Join("\n", lines) + "\n", Encoding.UTF8, ct);
    File.Move(tmpPath, signalPath, overwrite: true);
}
```
