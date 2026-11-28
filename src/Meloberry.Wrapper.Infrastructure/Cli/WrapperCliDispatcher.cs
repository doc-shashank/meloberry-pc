using System.Text.Json;
using Meloberry.Wrapper.Application.Abstractions;
using Meloberry.Wrapper.Application.UseCases;
using Meloberry.Wrapper.Domain.Common;
using Meloberry.Wrapper.Domain.Models;
using Meloberry.Wrapper.Infrastructure.Server;
using Meloberry.Wrapper.Infrastructure.Workspace;

namespace Meloberry.Wrapper.Infrastructure.Cli;

/// <summary>
/// Dual-mode `--cli` dispatcher invoked via active-workspace `mby-pc.bat`.
/// Operates live over loopback IPC + SSE when Meloberry PC GUI is open, or headlessly when closed.
/// </summary>
public sealed class WrapperCliDispatcher(
    ISettingsStore settingsStore,
    IManagedAppsStore appsStore,
    IWorkspaceDraftStore draftStore,
    LiveDraftHandler liveDraft,
    FinalizeWorkspaceHandler finalizeHandler,
    LiveGuiIpcClient liveGui,
    ToolProxyRunner toolProxy)
{
    private static readonly HashSet<string> ValueTakingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--title", "--desc", "--target", "--container", "--format", "--mode",
        "--subtype", "--qty", "--notes", "--modes",
        "--image", "--caption", "--alt", "--file", "--raw", "--out", "-o"
    };

    private const string PrimeGrammarText = """
        mby-pc.bat (Meloberry PC Workspace Shim & Live Control)
        Tool Proxy (uses %LOCALAPPDATA%\Meloberry\apps\ in caller CWD):
          .\mby-pc.bat mby <args...>
          .\mby-pc.bat melomg <args...>
        Live Blueprint & Build Control:
          .\mby-pc.bat status [--json]
          .\mby-pc.bat draft get [--json]
          .\mby-pc.bat draft patch [--title T] [--desc D] [--container session|article|quiz] [--format markdownDirectory|singleMarkdown|json] [--mode direct|live-agent|classic] [--allow-placeholders true|false]
          .\mby-pc.bat draft comp add <article|quiz> [--title T] | rename <compId> --title T | rm <compId> | move <compId> <index>
          .\mby-pc.bat draft spec add <compId> <topic> [--subtype S] [--qty N] | update <specId> [--subtype S] [--notes N] [--modes a,b] | clone <specId> | move <specId> <index> | move-to <specId> <targetCompId> [index] | rm <specId>
          .\mby-pc.bat draft spec set-content <specId> [--raw TEXT | --file REL_MD_PATH | --image REL_IMG_PATH [--caption C] [--alt A] | --clear | -]
          .\mby-pc.bat finalize [--allow-placeholders] [--out DIR] [--json]
          .\mby-pc.bat ui view <setup|details|format|organize|finalize|review|viewer> | viewer-open <webDir>
        """;

    public async Task<int> RunAsync(
        string[] args,
        IWrapperConsole console,
        Func<string, Task<bool>>? launchGuiCallback = null,
        CancellationToken ct = default)
    {
        var envAgent = string.Equals(Environment.GetEnvironmentVariable("MBY_AGENT"), "1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Environment.GetEnvironmentVariable("MBY_AGENT"), "true", StringComparison.OrdinalIgnoreCase);
        var jsonMode = envAgent;
        var launchIfClosed = false;
        string? explicitWs = null;
        var commandArgs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (string.Equals(token, "--cli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Once the first non-option verb is `mby` or `melomg`, forward all remaining tokens verbatim.
            if (commandArgs.Count == 0 &&
                (string.Equals(token, "mby", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(token, "melomg", StringComparison.OrdinalIgnoreCase)))
            {
                commandArgs.Add(token);
                for (var j = i + 1; j < args.Length; j++)
                {
                    if (string.Equals(args[j], "--json", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonMode = true;
                    }
                    commandArgs.Add(args[j]);
                }
                break;
            }

            if (ValueTakingFlags.Contains(token) && i + 1 < args.Length)
            {
                commandArgs.Add(token);
                commandArgs.Add(args[++i]);
                continue;
            }

            // --allow-placeholders takes an explicit bool value in `draft patch`, or acts as a switch in `finalize`.
            if (string.Equals(token, "--allow-placeholders", StringComparison.OrdinalIgnoreCase))
            {
                commandArgs.Add(token);
                if (i + 1 < args.Length &&
                    (string.Equals(args[i + 1], "true", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(args[i + 1], "false", StringComparison.OrdinalIgnoreCase)))
                {
                    commandArgs.Add(args[++i]);
                }
                continue;
            }

            if (string.Equals(token, "--workspace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                explicitWs = args[++i];
                continue;
            }

            if (string.Equals(token, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonMode = true;
                continue;
            }

            if (string.Equals(token, "--launch", StringComparison.OrdinalIgnoreCase))
            {
                launchIfClosed = true;
                continue;
            }

            commandArgs.Add(token);
        }

        if (commandArgs.Count == 0 || string.Equals(commandArgs[0], "prime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandArgs[0], "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandArgs[0], "-h", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSuccessAsync(console, jsonMode, new { grammar = PrimeGrammarText.TrimEnd() }, PrimeGrammarText.TrimEnd()).ConfigureAwait(false);
            return 0;
        }

        var verb = commandArgs[0].ToLowerInvariant();
        var rest = commandArgs.Skip(1).ToArray();
        var workspaceDir = ResolveWorkspaceRoot(explicitWs);

        try
        {
            return verb switch
            {
                "mby" or "melomg" => await toolProxy.RunAsync(verb, workspaceDir, rest, console, jsonMode, ct).ConfigureAwait(false),
                "status" => await HandleStatusAsync(workspaceDir, console, jsonMode, ct).ConfigureAwait(false),
                "draft" => await HandleDraftAsync(workspaceDir, rest, console, jsonMode, ct).ConfigureAwait(false),
                "finalize" => await HandleFinalizeAsync(workspaceDir, rest, console, jsonMode, ct).ConfigureAwait(false),
                "ui" => await HandleUiAsync(rest, workspaceDir, launchIfClosed, launchGuiCallback, console, jsonMode, ct).ConfigureAwait(false),
                _ => await WriteErrorAsync(console, jsonMode, 2, "Cli.UnknownCommand", $"Unknown command '{verb}'. Run 'mby-pc.bat prime'.").ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            return await WriteErrorAsync(console, jsonMode, 1, "Cli.Cancelled", "Operation cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await WriteErrorAsync(console, jsonMode, 1, "Cli.UnexpectedError", ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<int> HandleStatusAsync(string workspaceDir, IWrapperConsole console, bool jsonMode, CancellationToken ct)
    {
        if (liveGui.IsGuiAlive())
        {
            var (ok, statusCode, body) = await liveGui.SendAsync(HttpMethod.Get, $"/api/live/status?workspacePath={Uri.EscapeDataString(workspaceDir)}", null, ct).ConfigureAwait(false);
            if (ok && statusCode is >= 200 and < 300)
            {
                await WriteRawLiveResponseAsync(console, jsonMode, body).ConfigureAwait(false);
                return 0;
            }
        }

        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var mby = await appsStore.ResolveToolExecutableAsync("mby", workspaceDir, settings.MbyExecutableOverride, ct).ConfigureAwait(false);
        var melomg = await appsStore.ResolveToolExecutableAsync("melomg", workspaceDir, settings.MelomgExecutableOverride, ct).ConfigureAwait(false);
        var draft = await draftStore.LoadAsync(workspaceDir, ct).ConfigureAwait(false);

        var payload = new
        {
            guiRunning = false,
            activeWorkspace = workspaceDir,
            shimPresent = File.Exists(Path.Combine(workspaceDir, WorkspaceShimWriter.ShimFileName)),
            resolvedTools = new { mby, melomg },
            draft
        };
        var summary = $"GUI: closed | Workspace: {workspaceDir} | mby: {mby ?? "not installed"} | melomg: {melomg ?? "not installed"}";
        await WriteSuccessAsync(console, jsonMode, payload, summary).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> HandleDraftAsync(string workspaceDir, string[] rest, IWrapperConsole console, bool jsonMode, CancellationToken ct)
    {
        if (rest.Length == 0 || string.Equals(rest[0], "get", StringComparison.OrdinalIgnoreCase))
        {
            if (liveGui.IsGuiAlive())
            {
                var (ok, code, body) = await liveGui.SendAsync(HttpMethod.Get, $"/api/live/draft?workspacePath={Uri.EscapeDataString(workspaceDir)}", null, ct).ConfigureAwait(false);
                if (ok)
                {
                    if (code is >= 200 and < 300)
                    {
                        await WriteRawLiveResponseAsync(console, jsonMode, body).ConfigureAwait(false);
                        return 0;
                    }
                    return await RelayErrorBodyAsync(console, jsonMode, code, body).ConfigureAwait(false);
                }
            }

            var res = await liveDraft.GetDraftAsync(workspaceDir, ct).ConfigureAwait(false);
            return await WriteDomainResultAsync(console, jsonMode, res.IsSuccess ? Result<object>.Success(new { workspacePath = res.Value.WorkspacePath, draft = res.Value.Draft }) : Result<object>.Failure(res.Error!), res.IsSuccess ? $"Draft '{res.Value.Draft.Title}'" : null).ConfigureAwait(false);
        }

        var sub = rest[0].ToLowerInvariant();
        var subArgs = rest.Skip(1).ToArray();

        LiveDraftMutationRequest? mut = sub switch
        {
            "patch" => ParsePatchArgs(subArgs),
            "comp" => ParseCompArgs(subArgs),
            "spec" => await ParseSpecArgsAsync(subArgs, console, ct).ConfigureAwait(false),
            _ => null
        };

        if (mut is null)
        {
            return await WriteErrorAsync(console, jsonMode, 2, "Cli.InvalidArgs", "Invalid `draft` subcommand or missing arguments.").ConfigureAwait(false);
        }

        if (liveGui.IsGuiAlive())
        {
            var payload = new LiveMutateApiRequest(
                WorkspacePath: workspaceDir,
                Op: mut.Op,
                Source: mut.Source,
                TargetId: mut.TargetId,
                Container: mut.Container,
                Title: mut.Title,
                Description: mut.Description,
                AuthoringFormat: mut.AuthoringFormat,
                WorkflowMode: mut.WorkflowMode,
                AllowPlaceholdersOnFinalize: mut.AllowPlaceholdersOnFinalize,
                ComponentKind: mut.ComponentKind,
                Topic: mut.Topic,
                Subtype: mut.Subtype,
                Index: mut.Index,
                Count: mut.Count,
                ContentMode: mut.ContentMode,
                RawText: mut.RawText,
                MarkdownFilePath: mut.MarkdownFilePath,
                Notes: mut.Notes,
                ImagePath: mut.ImagePath,
                Caption: mut.Caption,
                AltText: mut.AltText,
                Modes: mut.Modes,
                TargetContainerId: mut.TargetContainerId);

            var (ok, code, body) = await liveGui.SendAsync(HttpMethod.Post, "/api/live/mutate", payload, ct).ConfigureAwait(false);
            if (ok)
            {
                if (code is >= 200 and < 300)
                {
                    await WriteRawLiveResponseAsync(console, jsonMode, body).ConfigureAwait(false);
                    return 0;
                }
                return await RelayErrorBodyAsync(console, jsonMode, code, body).ConfigureAwait(false);
            }
        }

        var mutRes = await liveDraft.MutateAsync(workspaceDir, mut, ct).ConfigureAwait(false);
        return await WriteDomainResultAsync(
            console,
            jsonMode,
            mutRes.IsSuccess ? Result<object>.Success(mutRes.Value) : Result<object>.Failure(mutRes.Error!),
            mutRes.IsSuccess ? mutRes.Value.Summary : null).ConfigureAwait(false);
    }

    private async Task<int> HandleFinalizeAsync(string workspaceDir, string[] rest, IWrapperConsole console, bool jsonMode, CancellationToken ct)
    {
        var allowPlaceholders = rest.Any(a => string.Equals(a, "--allow-placeholders", StringComparison.OrdinalIgnoreCase));
        var outDir = GetFlagValue(rest, "--out") ?? GetFlagValue(rest, "-o");

        if (liveGui.IsGuiAlive())
        {
            var payload = new LiveFinalizeApiRequest(workspaceDir, allowPlaceholders, outDir, true);
            var (ok, code, body) = await liveGui.SendAsync(HttpMethod.Post, "/api/live/finalize", payload, ct).ConfigureAwait(false);
            if (ok)
            {
                if (code is >= 200 and < 300)
                {
                    await WriteRawLiveResponseAsync(console, jsonMode, body).ConfigureAwait(false);
                    return 0;
                }
                return await RelayErrorBodyAsync(console, jsonMode, code, body).ConfigureAwait(false);
            }
        }

        var finRes = await finalizeHandler.FinalizeAsync(new Domain.Models.FinalizeRequest(workspaceDir, allowPlaceholders, outDir), ct).ConfigureAwait(false);
        return await WriteDomainResultAsync(
            console,
            jsonMode,
            finRes.IsSuccess ? Result<object>.Success(finRes.Value) : Result<object>.Failure(finRes.Error!),
            finRes.IsSuccess ? $"Built {finRes.Value.WorkspaceId} -> {finRes.Value.WebAppDirectory}" : null).ConfigureAwait(false);
    }

    private async Task<int> HandleUiAsync(
        string[] rest,
        string workspaceDir,
        bool launchIfClosed,
        Func<string, Task<bool>>? launchGuiCallback,
        IWrapperConsole console,
        bool jsonMode,
        CancellationToken ct)
    {
        if (rest.Length < 2)
        {
            return await WriteErrorAsync(console, jsonMode, 2, "Cli.InvalidArgs", "Usage: mby-pc.bat ui <view|viewer-open> <target>").ConfigureAwait(false);
        }

        if (!liveGui.IsGuiAlive())
        {
            if (launchIfClosed && launchGuiCallback is not null)
            {
                var launched = await launchGuiCallback(workspaceDir).ConfigureAwait(false);
                if (!launched)
                {
                    return await WriteErrorAsync(console, jsonMode, 3, "Gui.NotRunning", "Failed to launch Meloberry PC GUI.").ConfigureAwait(false);
                }
            }
            else
            {
                return await WriteErrorAsync(console, jsonMode, 3, "Gui.NotRunning", "Meloberry PC GUI is not running. Pass --launch or open Meloberry PC.").ConfigureAwait(false);
            }
        }

        var action = rest[0].ToLowerInvariant();
        var target = rest[1];
        var payload = action switch
        {
            "view" => new LiveUiApiRequest("view", View: NormalizeUiViewAlias(target)),
            "viewer-open" => new LiveUiApiRequest("viewer-open", Path: target),
            "activity" => new LiveUiApiRequest("activity", Message: target),
            _ => null
        };

        if (payload is null)
        {
            return await WriteErrorAsync(console, jsonMode, 2, "Cli.InvalidArgs", $"Unknown ui action '{action}'.").ConfigureAwait(false);
        }

        var (ok, code, body) = await liveGui.SendAsync(HttpMethod.Post, "/api/live/ui", payload, ct).ConfigureAwait(false);
        if (!ok)
        {
            return await WriteErrorAsync(console, jsonMode, 3, "Gui.NotRunning", "Meloberry PC GUI IPC unreachable.").ConfigureAwait(false);
        }

        if (code is >= 200 and < 300)
        {
            await WriteRawLiveResponseAsync(console, jsonMode, body).ConfigureAwait(false);
            return 0;
        }

        return await RelayErrorBodyAsync(console, jsonMode, code, body).ConfigureAwait(false);
    }

    private static LiveDraftMutationRequest? ParsePatchArgs(string[] args)
    {
        var title = GetFlagValue(args, "--title");
        var desc = GetFlagValue(args, "--desc");
        var target = GetFlagValue(args, "--target");
        var rawContainer = GetFlagValue(args, "--container");
        ContainerKind? container = rawContainer?.ToLowerInvariant() switch
        {
            "session" => ContainerKind.Session,
            "article" or "standalonearticle" => ContainerKind.StandaloneArticle,
            "quiz" or "standalonequiz" => ContainerKind.StandaloneQuiz,
            _ => null
        };

        var rawFormat = GetFlagValue(args, "--format");
        AuthoringFormat? authoringFormat = rawFormat?.Trim().ToLowerInvariant() switch
        {
            "markdowndirectory" or "markdown-directory" or "md-dir" => AuthoringFormat.MarkdownDirectory,
            "singlemarkdown" or "single-markdown" or "md" => AuthoringFormat.SingleMarkdown,
            "json" => AuthoringFormat.Json,
            _ => null
        };

        var rawMode = GetFlagValue(args, "--mode");
        WorkflowMode? mode = rawMode?.ToLowerInvariant() switch
        {
            "direct" => WorkflowMode.Direct,
            "live-agent" or "liveagent" => WorkflowMode.LiveAgent,
            "classic" or "classicprompt" => WorkflowMode.ClassicPrompt,
            _ => null
        };

        var rawPh = GetFlagValue(args, "--allow-placeholders");
        bool? allowPh = bool.TryParse(rawPh, out var parsedPh) ? parsedPh : null;

        return new LiveDraftMutationRequest(
            Op: "patch-meta",
            TargetId: target,
            Container: container,
            Title: title,
            Description: desc,
            AuthoringFormat: authoringFormat,
            WorkflowMode: mode,
            AllowPlaceholdersOnFinalize: allowPh);
    }

    private static LiveDraftMutationRequest? ParseCompArgs(string[] args)
    {
        if (args.Length == 0) return null;
        var op = args[0].ToLowerInvariant();
        if (op == "add" && args.Length >= 2)
        {
            var kind = string.Equals(args[1], "quiz", StringComparison.OrdinalIgnoreCase) ? ComponentKind.Quiz : ComponentKind.Article;
            return new LiveDraftMutationRequest("add-comp", ComponentKind: kind, Title: GetFlagValue(args, "--title"));
        }

        if (op == "rename" && args.Length >= 2)
        {
            var newTitle = GetFlagValue(args, "--title");
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                return new LiveDraftMutationRequest("patch-meta", TargetId: args[1], Title: newTitle);
            }
        }

        if (op == "rm" && args.Length >= 2)
        {
            return new LiveDraftMutationRequest("rm-comp", TargetId: args[1]);
        }

        if (op == "move" && args.Length >= 3 && int.TryParse(args[2], out var idx))
        {
            return new LiveDraftMutationRequest("move-comp", TargetId: args[1], Index: idx);
        }

        return null;
    }

    private static async Task<LiveDraftMutationRequest?> ParseSpecArgsAsync(string[] args, IWrapperConsole console, CancellationToken ct)
    {
        if (args.Length == 0) return null;
        var op = args[0].ToLowerInvariant();
        if (op == "add" && args.Length >= 3)
        {
            var qty = int.TryParse(GetFlagValue(args, "--qty"), out var parsedQty) ? parsedQty : 1;
            return new LiveDraftMutationRequest(
                "add-spec",
                TargetId: args[1],
                Topic: args[2],
                Subtype: GetFlagValue(args, "--subtype"),
                Count: qty,
                Notes: GetFlagValue(args, "--notes"));
        }

        if (op == "update" && args.Length >= 2)
        {
            var subtype = GetFlagValue(args, "--subtype");
            var notes = GetFlagValue(args, "--notes");
            var rawModes = GetFlagValue(args, "--modes");
            IReadOnlyList<string>? modes = rawModes is not null
                ? rawModes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;
            return new LiveDraftMutationRequest(
                "update-spec",
                TargetId: args[1],
                Subtype: subtype,
                Notes: notes,
                Modes: modes);
        }

        if (op == "rm" && args.Length >= 2)
        {
            return new LiveDraftMutationRequest("rm-spec", TargetId: args[1]);
        }

        if (op == "move" && args.Length >= 3 && int.TryParse(args[2], out var idx))
        {
            return new LiveDraftMutationRequest("move-spec", TargetId: args[1], Index: idx);
        }

        if (op == "clone" && args.Length >= 2)
        {
            return new LiveDraftMutationRequest("clone-spec", TargetId: args[1]);
        }

        if (op == "move-to" && args.Length >= 3)
        {
            int? targetIdx = args.Length >= 4 && int.TryParse(args[3], out var parsedIdx) ? parsedIdx : null;
            return new LiveDraftMutationRequest("move-spec-to", TargetId: args[1], TargetContainerId: args[2], Index: targetIdx);
        }

        if (op == "set-content" && args.Length >= 2)
        {
            var specId = args[1];
            if (args.Any(a => string.Equals(a, "--clear", StringComparison.OrdinalIgnoreCase)))
            {
                return new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.None);
            }

            var imgFile = GetFlagValue(args, "--image");
            if (!string.IsNullOrWhiteSpace(imgFile))
            {
                return new LiveDraftMutationRequest(
                    "set-content",
                    TargetId: specId,
                    ContentMode: SpecContentMode.ImageFile,
                    RawText: GetFlagValue(args, "--raw"),
                    ImagePath: imgFile,
                    Caption: GetFlagValue(args, "--caption"),
                    AltText: GetFlagValue(args, "--alt"));
            }

            var mdFile = GetFlagValue(args, "--file");
            if (!string.IsNullOrWhiteSpace(mdFile))
            {
                return new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.MarkdownFile, MarkdownFilePath: mdFile);
            }

            var raw = GetFlagValue(args, "--raw");
            if (args.Any(a => a == "-") && console.IsInputRedirected)
            {
                raw = await console.In.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            if (raw is not null)
            {
                return new LiveDraftMutationRequest("set-content", TargetId: specId, ContentMode: SpecContentMode.RawText, RawText: raw);
            }
        }

        return null;
    }

    internal static string NormalizeUiViewAlias(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "details" or "project" => "format",
        "finalize" => "review",
        var other => other
    };

    private static string ResolveWorkspaceRoot(string? explicitWs)
    {
        var candidate = !string.IsNullOrWhiteSpace(explicitWs) ? explicitWs : Environment.CurrentDirectory;
        return PathNormalization.TryNormalizeDirectoryPath(candidate, out var full) ? full : Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static string? GetFlagValue(string[] args, string flagName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flagName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static async Task WriteSuccessAsync(IWrapperConsole console, bool jsonMode, object data, string textFallback)
    {
        if (jsonMode)
        {
            await console.Out.WriteLineAsync(JsonSerializer.Serialize(new { success = true, data }, HttpEnvelope.JsonOptions)).ConfigureAwait(false);
        }
        else
        {
            await console.Out.WriteLineAsync(textFallback).ConfigureAwait(false);
        }
    }

    private static async Task WriteRawLiveResponseAsync(IWrapperConsole console, bool jsonMode, string rawBody)
    {
        if (jsonMode)
        {
            using var doc = JsonDocument.Parse(rawBody);
            await console.Out.WriteLineAsync(JsonSerializer.Serialize(new { success = true, data = doc.RootElement }, HttpEnvelope.JsonOptions)).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.String)
            {
                await console.Out.WriteLineAsync(sum.GetString()!).ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("workspaceId", out var wsId) && wsId.ValueKind == JsonValueKind.String)
            {
                var webDir = root.TryGetProperty("webAppDirectory", out var wd) && wd.ValueKind == JsonValueKind.String ? wd.GetString() : string.Empty;
                await console.Out.WriteLineAsync($"Built {wsId.GetString()} -> {webDir}").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("guiRunning", out var guiRun) && guiRun.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var activeWs = root.TryGetProperty("activeWorkspace", out var aw) && aw.ValueKind == JsonValueKind.String ? aw.GetString() : string.Empty;
                var mby = root.TryGetProperty("resolvedTools", out var rt) && rt.TryGetProperty("mby", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() : "not installed";
                var melomg = root.TryGetProperty("resolvedTools", out var rt2) && rt2.TryGetProperty("melomg", out var mlv) && mlv.ValueKind == JsonValueKind.String ? mlv.GetString() : "not installed";
                await console.Out.WriteLineAsync($"GUI: {(guiRun.GetBoolean() ? "running" : "closed")} | Workspace: {activeWs} | mby: {mby} | melomg: {melomg}").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.Object)
            {
                var title = draftEl.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : string.Empty;
                await console.Out.WriteLineAsync($"Draft '{title}'").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("view", out var viewEl) && viewEl.ValueKind == JsonValueKind.String)
            {
                await console.Out.WriteLineAsync($"Switched view to {viewEl.GetString()}").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                await console.Out.WriteLineAsync($"Opened viewer: {urlEl.GetString()}").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True)
            {
                await console.Out.WriteLineAsync("OK").ConfigureAwait(false);
                return;
            }
        }
        catch { }

        await console.Out.WriteLineAsync(rawBody).ConfigureAwait(false);
    }

    private static Task<int> WriteDomainResultAsync<T>(IWrapperConsole console, bool jsonMode, Result<T> res, string? summary)
    {
        if (res.IsSuccess)
        {
            return WriteSuccessResultAsync();
        }

        var code = res.Error!.Type switch
        {
            ErrorType.Validation => 2,
            ErrorType.NotFound => 3,
            ErrorType.Conflict => 4,
            ErrorType.Security => 5,
            _ => 1
        };
        return WriteErrorAsync(console, jsonMode, code, res.Error.Code, res.Error.Description);

        async Task<int> WriteSuccessResultAsync()
        {
            await WriteSuccessAsync(console, jsonMode, res.Value!, summary ?? "OK").ConfigureAwait(false);
            return 0;
        }
    }

    private static async Task<int> RelayErrorBodyAsync(IWrapperConsole console, bool jsonMode, int httpCode, string rawBody)
    {
        var exitCode = httpCode switch { 400 => 2, 404 => 3, 409 => 4, 403 => 5, _ => 1 };
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var errCode = err.TryGetProperty("code", out var c) ? c.GetString() : "Server.Error";
                var desc = err.TryGetProperty("description", out var d) ? d.GetString() : rawBody;
                return await WriteErrorAsync(console, jsonMode, exitCode, errCode ?? "Server.Error", desc ?? rawBody).ConfigureAwait(false);
            }
        }
        catch { }

        return await WriteErrorAsync(console, jsonMode, exitCode, "Server.Error", rawBody).ConfigureAwait(false);
    }

    private static async Task<int> WriteErrorAsync(IWrapperConsole console, bool jsonMode, int exitCode, string code, string description)
    {
        if (jsonMode)
        {
            await console.Error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code, description }
            }, HttpEnvelope.JsonOptions)).ConfigureAwait(false);
        }
        else
        {
            await console.Error.WriteLineAsync($"Error [{code}]: {description}").ConfigureAwait(false);
        }

        return exitCode;
    }
}
