# SYSTEM DIRECTIVE: CLEAN CLI ARCHITECTURE CONSTRAINTS FOR AI AGENTS (.NET / C#)

> **TARGET AUDIENCE:** Large Language Models (LLMs), Autonomous Coding Agents, and AI Pair Programmers.
>
> **APPLICATION TYPE:** C# / .NET Command-Line Interface (CLI) Applications ("Clean CLI").
>
> **ENFORCEMENT LEVEL:** STRICT / NON-NEGOTIABLE.
>
> **PRIMARY OBJECTIVE:** Prevent `Program.cs` monoliths by enforcing strict Clean Architecture boundaries where `System.CommandLine` acts purely as a thin Presentation/UI adapter over a decoupled `Application` and `Domain` core.

---

## 1. The Core Problem: Why AI Agents Fail at CLI Architecture

When asked to build or extend a CLI tool in C#, AI agents default to anti-patterns that destroy maintainability:

1. **The `Program.cs` Monster:** Cramming `args` parsing, DI setup, SQL/file I/O, business rules, and `Console.WriteLine` calls into a single file or inline lambda.
2. **Console Leakage into Core Layers:** Calling `Console.WriteLine`, `Console.ForegroundColor`, or `Spectre.Console` inside `Application` handlers or `Domain` entities, making business logic impossible to reuse or unit test without scraping terminal output.
3. **Ignoring Exit Codes & Streams:** Returning `void` or always returning `0` on failure, and mixing diagnostic logs with pipeable data on `stdout` instead of separating `stdout` and `stderr`.
4. **Command Naming Collisions:** Confusing the transport-level `System.CommandLine.Command` with the use-case-level `Application` Command DTO.

**You are strictly forbidden from taking these shortcuts.** Every CLI feature you generate must obey the structural boundaries defined in this specification.

---

## 2. Clean CLI Topology & The Dependency Rule

In a **Clean CLI** solution, the console project (`*.Cli`) is merely an outer delivery mechanism—identical in architectural rank to an ASP.NET Core Web API (`*.Api`). Source code dependencies **must only point inward**.

```
+-----------------------------------------------------------------------+
|                    Outer Ring: Frameworks & Drivers                   |
|   [ *.Cli ] (System.CommandLine / Host / UI)    [ *.Infrastructure ]  |
|                |                                         |            |
|                +--------------------+--------------------+            |
|                                     | (Compile-Time Reference)        |
|                                     v                                 |
|                     Middle Ring: Use Cases & Ports                    |
|                            [ *.Application ]                          |
|                                     | (Compile-Time Reference)        |
|                                     v                                 |
|                     Center Ring: Entities & Business Rules            |
|                              [ *.Domain ]                             |
|                        (DEPENDS ON NOTHING)                           |
+-----------------------------------------------------------------------+
```

### 2.1 Project Reference & Package Matrix

| Project / Layer | Allowed Project References | Allowed NuGet Packages | Forbidden Dependencies |
| :--- | :--- | :--- | :--- |
| **`*.Domain`** | **NONE** | **NONE** (Zero third-party packages) | `Application`, `Infrastructure`, `Cli`, `System.CommandLine`, `Spectre.Console`, `System.Console`, EF Core |
| **`*.Application`** | `*.Domain` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `FluentValidation` (optional), `MediatR` (optional) | `Infrastructure`, `Cli`, `System.CommandLine.*`, `Spectre.Console`, `System.Console`, `Microsoft.EntityFrameworkCore.*` |
| **`*.Infrastructure`** | `*.Application` (and `*.Domain` transitively) | `Microsoft.EntityFrameworkCore.*`, File/OS/Git/HTTP SDKs, `Serilog` | `*.Cli`, `System.CommandLine.*`, `Spectre.Console` |
| **`*.Cli`** | `*.Application`, `*.Infrastructure` (Composition Root only) | `System.CommandLine`, `System.CommandLine.Hosting`, `Microsoft.Extensions.Hosting`, `Spectre.Console`, `Serilog` | Direct `DbContext` or `Infrastructure` concrete class usage inside CLI Command Handlers (except dedicated DB migration commands via a Port) |

> **CRITICAL RULE ON `*.Cli` -> `*.Infrastructure`:** The `*.Cli` project references `*.Infrastructure` **solely** so `Program.cs` (the Composition Root) can invoke `services.AddInfrastructure(configuration)`. **No CLI Command or `ICommandHandler` may inject a concrete type from `*.Infrastructure`.**

---

## 3. Placement Decision Tree: Where Does My CLI Code Go?

Before generating or modifying any file, evaluate these four questions in order and **stop at the first "YES"**:

1. **Would this business rule or invariant still be true if the app were a Web API instead of a CLI tool?**
   * **YES -> `*.Domain`** (Entities, Value Objects, Domain Errors, `Result<T>`).
2. **Does this code orchestrate a use case (load state via port -> execute domain logic -> save state -> return DTO)?**
   * **YES -> `*.Application`** (Use Case Commands/Queries, Use Case Handlers, Port interfaces like `ITodoRepository`).
3. **Does this code talk to an external system (SQLite/PostgreSQL, local filesystem, Git, Docker, REST API, OS environment)?**
   * **YES -> `*.Infrastructure`** (`DbContext`, EF Core repositories, file readers/writers, external clients).
4. **Does this code define CLI verbs, flags (`--title`, `-t`), arguments (`<id>`), terminal tables/colors, `stdout`/`stderr` formatting, or POSIX exit codes?**
   * **YES -> `*.Cli`** (`System.CommandLine` commands, `ICommandHandler` adapters, `Program.cs`, terminal renderers).

### 3.1 Clean CLI Component Placement Reference Table

| Artifact / Concern | Target Layer | Rationale |
| :--- | :--- | :--- |
| **Entities & Invariants** (e.g., `TodoList.AddItem()`) | `Domain` | Pure business logic independent of CLI or DB. |
| **Domain Errors & `Result<T>`** (e.g., `TodoErrors.NotFound`) | `Domain` | Communicates expected failures without throwing exceptions. |
| **Use Case Inputs & Handlers** (`CreateTodoListCommand`, `CreateTodoListHandler`) | `Application` | Application-level orchestration reusable by CLI, Web, or Workers. |
| **Persistence & OS Ports** (`ITodoRepository`, `IFileSystem`, `IDatabaseMigrator`) | `Application` | Use cases declare what they need from the outside world. |
| **EF Core `DbContext`, SQLite, File System Adapters** | `Infrastructure` | Implements `Application` ports using concrete I/O libraries. |
| **CLI Command Definitions** (`CreateTodoListCliCommand : Command`) | `Cli` | Defines CLI grammar: verbs, options (`--title`), aliases (`-t`), and help text. |
| **CLI Command Handler** (`CreateTodoListCliCommand.Handler : ICommandHandler`) | `Cli` | Binds parsed CLI options, calls the `Application` handler, renders output, and returns a POSIX exit code. |
| **Terminal Rendering & Formatting** (`ICliOutputRenderer`, `Spectre.Console`) | `Cli` | Presentation concern; formats DTOs as tables, plain text, or JSON. |
| **Exit Code Mapping** (`Result` -> `0`, `1`, `2`, `3`, `4`) | `Cli` | Only the CLI edge knows about POSIX exit codes and `stderr`. |
| **Host & CommandLine Builder (`Program.cs`)** | `Cli` | Composition Root wiring `System.CommandLine` to `.NET Generic Host`. |

---

## 4. Strict Layer-by-Layer Implementation Rules & Templates

### 4.1 Layer 1: `*.Domain` Constraints

1. **Zero Console Awareness:** Never reference `System.Console`, `System.CommandLine`, or `Environment.Exit` in `Domain`.
2. **Rich Entities with Private Setters:** All state mutations must occur through domain methods (`Create(...)`, `Complete()`, `Rename(...)`) that guard invariants.
3. **Result Pattern for Expected Failures:** When a user passes an ID that does not exist or violates a domain rule, return a `Result` / `Result<TValue>` with a typed `Error`. Throw exceptions only for programmer bugs (e.g., unvalidated null/negative primitives).

#### Reference Template: `Result.cs` & Domain Entity (`*.Domain`)

```csharp
namespace MyCli.Domain;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden
}

public sealed record Error(string Code, string Description, ErrorType Type);

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);
    public static implicit operator Result(Error error) => new(error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue? value, Error? error) : base(error) => _value = value;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public static implicit operator Result<TValue>(TValue value) => new(value, null);
    public static implicit operator Result<TValue>(Error error) => new(default, error);
}

public static class TodoListErrors
{
    public static Error NotFound(int id) =>
        new("TodoList.NotFound", $"Todo list with ID {id} was not found.", ErrorType.NotFound);

    public static Error DuplicateTitle(string title) =>
        new("TodoList.DuplicateTitle", $"A todo list titled '{title}' already exists.", ErrorType.Conflict);
}

public sealed class TodoList
{
    private TodoList(string title) => Title = title;

    public int Id { get; private set; }
    public string Title { get; private set; }

    public static Result<TodoList> Create(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new Error("TodoList.EmptyTitle", "Title cannot be empty.", ErrorType.Validation);

        return new TodoList(title.Trim());
    }
}
```

---

### 4.2 Layer 2: `*.Application` Constraints

1. **No Terminal Output in Handlers:** Application handlers must **never** print to the console or format tables. They must return strongly typed DTOs wrapped in `Result<T>` so the `*.Cli` layer can decide whether to render a human-readable table, JSON (`--json`), or silent output (`--silent`).
2. **Support `--dry-run` Cleanly (When Applicable):** If a command supports `--dry-run`, pass `bool DryRun` on the Application Command DTO or handle preview logic before calling `SaveChangesAsync`.
3. **Explicit Handler Registration (`AddApplication`):** Register all use case handlers (or MediatR pipeline behaviors) inside `*.Application/DependencyInjection.cs`.
4. **No Generic Repositories or `IQueryable<T>`:** Keep ports narrow and task-focused.

#### Reference Template: Port, Use Case Command/Query, and DI (`*.Application`)

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyCli.Domain;

namespace MyCli.Application;

public interface ITodoListRepository
{
    Task<bool> ExistsByTitleAsync(string title, CancellationToken cancellationToken = default);
    Task<TodoList?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TodoListSummaryDto>> ListAllAsync(CancellationToken cancellationToken = default);
    void Add(TodoList list);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreateTodoListCommand(string Title, bool DryRun = false);
public sealed record CreateTodoListResponse(int Id, string Title, bool WasDryRun);

public sealed class CreateTodoListHandler(ITodoListRepository repository)
{
    public async Task<Result<CreateTodoListResponse>> HandleAsync(
        CreateTodoListCommand command,
        CancellationToken cancellationToken = default)
    {
        if (await repository.ExistsByTitleAsync(command.Title, cancellationToken))
            return TodoListErrors.DuplicateTitle(command.Title);

        var createResult = TodoList.Create(command.Title);
        if (!createResult.IsSuccess)
            return createResult.Error!;

        var entity = createResult.Value;

        if (!command.DryRun)
        {
            repository.Add(entity);
            await repository.SaveChangesAsync(cancellationToken);
        }

        return new CreateTodoListResponse(entity.Id, entity.Title, command.DryRun);
    }
}

public sealed record GetTodoListsQuery();
public sealed record TodoListSummaryDto(int Id, string Title);

public sealed class GetTodoListsHandler(ITodoListRepository repository)
{
    public async Task<Result<IReadOnlyList<TodoListSummaryDto>>> HandleAsync(
        GetTodoListsQuery query,
        CancellationToken cancellationToken = default)
    {
        var items = await repository.ListAllAsync(cancellationToken);
        return Result<IReadOnlyList<TodoListSummaryDto>>.Success(items); // Or implicit conversion
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateTodoListHandler>();
        services.AddScoped<GetTodoListsHandler>();
        return services;
    }
}
```

---

### 4.3 Layer 3: `*.Infrastructure` Constraints

1. **Persistence & OS Adapters Only:** Implement `ITodoListRepository`, `IDatabaseMigrator`, file system access, and external API clients here.
2. **No Console UI:** Infrastructure must never write directly to `System.Console`. Use `ILogger<T>` for diagnostics (which `*.Cli` configures to respect `--silent`).

#### Reference Template: Repository & DI (`*.Infrastructure`)

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyCli.Application;
using MyCli.Domain;

namespace MyCli.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TodoList> TodoLists => Set<TodoList>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TodoList>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        });
    }
}

public sealed class TodoListRepository(AppDbContext dbContext) : ITodoListRepository
{
    public Task<bool> ExistsByTitleAsync(string title, CancellationToken cancellationToken = default) =>
        dbContext.TodoLists.AnyAsync(x => x.Title == title, cancellationToken);

    public Task<TodoList?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        dbContext.TodoLists.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TodoListSummaryDto>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.TodoLists
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new TodoListSummaryDto(x.Id, x.Title))
            .ToListAsync(cancellationToken);

    public void Add(TodoList list) => dbContext.TodoLists.Add(list);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=app.db";
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ITodoListRepository, TodoListRepository>();
        return services;
    }
}
```

---

### 4.4 Layer 4: `*.Cli` Constraints (The Clean CLI Presentation Layer)

The `*.Cli` project is the entry point and presentation layer. It must be structured using **`System.CommandLine` + `.NET Generic Host`**.

#### Rule 1: `Program.cs` is Strictly the Composition Root
`Program.cs` must only:
1. Build the `CommandLineBuilder` command tree (`BuildCommandLine()`).
2. Attach the `.NET Generic Host` via `.UseHost(...)` with `.UseEnvironment("CLI")`.
3. Register layer dependencies (`services.AddCli()`, `services.AddApplication()`, `services.AddInfrastructure(...)`).
4. Connect each CLI command to its nested DI handler via `.UseCommandHandler<TCliCommand, TCliCommand.Handler>()`.
5. Call `.UseDefaults().Build()` and `return await parser.InvokeAsync(args);`.

#### Rule 2: Noun-Verb Command Hierarchy & Help-by-Default
* Organize subcommands into logical noun groups (e.g., `todolist create`, `todolist delete <id>`, `todolist list`) or clear top-level operational verbs (`migrate`).
* Configure the `RootCommand` default handler to display help (`-h`) when invoked without arguments:
  ```csharp
  root.Handler = CommandHandler.Create(() => root.Invoke("-h"));
  ```
* Register global options on `RootCommand` via `root.AddGlobalOption(...)`:
  * `--silent`: Disables diagnostic/log output.
  * `--json`: Formats output as machine-readable JSON for shell piping.

#### Rule 3: The Self-Contained CLI Command + Nested `Handler` Pattern
* Every leaf CLI command must inherit from `System.CommandLine.Command` and end with the suffix `CliCommand` (e.g., `CreateTodoListCliCommand`) to avoid naming collisions with `Application` commands (`CreateTodoListCommand`).
* Define all `Argument<T>` and `Option<T>` instances in the `CliCommand` constructor.
* Define `public new sealed class Handler : ICommandHandler` nested inside the `CliCommand` class.
* Use `System.CommandLine` conventional property binding on `Handler` (e.g., `public string Title { get; set; } = string.Empty;`, `public bool DryRun { get; set; }`, `public bool Silent { get; set; }`) alongside constructor-injected `Application` handlers and `IConsole`.

#### Rule 4: Stream Separation (`stdout` vs. `stderr`) & POSIX Exit Codes
* **NEVER use `System.Console.WriteLine` directly.** Always write through the injected `IConsole` (`console.Out.WriteLine(...)` and `console.Error.WriteLine(...)`) so tests can pass a `TestConsole` and capture output deterministically.
* **`stdout` (`console.Out`)** is strictly for primary command results (IDs, tables, JSON payloads) so output can be piped (`mycli todolist list --json | jq .`).
* **`stderr` (`console.Error`)** is strictly for errors, validation failures, and diagnostic warnings.
* **Centralized Exit Code Mapping (`CliResultExtensions.cs`):**
  Map every `Domain.Result` to a standard deterministic POSIX exit code:
  * `0`: Success
  * `1`: Unhandled / General exception
  * `2`: Validation or CLI usage error (`ErrorType.Validation`)
  * `3`: Resource not found (`ErrorType.NotFound`)
  * `4`: Domain conflict / state error (`ErrorType.Conflict`)
  * `5`: Unauthorized / Forbidden (`ErrorType.Unauthorized`, `ErrorType.Forbidden`)

#### Reference Template: `CliResultExtensions.cs` (`*.Cli`)

```csharp
using System.CommandLine;
using System.CommandLine.IO;
using MyCli.Domain;

namespace MyCli.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int ValidationError = 2;
    public const int NotFound = 3;
    public const int Conflict = 4;
    public const int Unauthorized = 5;
}

public static class CliResultExtensions
{
    public static int ToExitCode(this Result result, IConsole console)
    {
        if (result.IsSuccess)
            return ExitCodes.Success;

        var error = result.Error!;
        console.Error.WriteLine($"Error [{error.Code}]: {error.Description}");

        return error.Type switch
        {
            ErrorType.Validation => ExitCodes.ValidationError,
            ErrorType.NotFound => ExitCodes.NotFound,
            ErrorType.Conflict => ExitCodes.Conflict,
            ErrorType.Unauthorized or ErrorType.Forbidden => ExitCodes.Unauthorized,
            _ => ExitCodes.GeneralError
        };
    }
}
```

#### Reference Template: Self-Contained CLI Command (`CreateTodoListCliCommand.cs` in `*.Cli`)

```csharp
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using MyCli.Application;

namespace MyCli.Cli.Commands.TodoLists;

public sealed class CreateTodoListCliCommand : Command
{
    public CreateTodoListCliCommand()
        : base(name: "create", description: "Creates a new todo list")
    {
        var titleOption = new Option<string>(
            aliases: ["--title", "-t"],
            description: "Title of the todo list")
        {
            IsRequired = true
        };

        var dryRunOption = new Option<bool>(
            alias: "--dry-run",
            description: "Displays a summary of what would happen without persisting changes.");

        AddOption(titleOption);
        AddOption(dryRunOption);
    }

    public new sealed class Handler(
        CreateTodoListHandler useCaseHandler,
        IConsole console) : ICommandHandler
    {
        // Bound automatically by System.CommandLine from options/arguments
        public string Title { get; set; } = string.Empty;
        public bool DryRun { get; set; }
        public bool Silent { get; set; }

        public async Task<int> InvokeAsync(InvocationContext context)
        {
            var cancellationToken = context.GetCancellationToken();
            var command = new CreateTodoListCommand(Title, DryRun);

            var result = await useCaseHandler.HandleAsync(command, cancellationToken);
            if (!result.IsSuccess)
                return result.ToExitCode(console);

            if (!Silent)
            {
                var prefix = result.Value.WasDryRun ? "[DRY-RUN] Would create" : "Created";
                console.Out.WriteLine($"{prefix} todo list '{result.Value.Title}' (ID: {result.Value.Id})");
            }

            return ExitCodes.Success;
        }
    }
}
```

#### Reference Template: `Program.cs` Composition Root (`*.Cli`)

```csharp
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Hosting;
using MyCli.Application;
using MyCli.Cli.Commands.TodoLists;
using MyCli.Infrastructure;

var parser = BuildCommandLine()
    .UseHost(_ => Host.CreateDefaultBuilder(args), builder => builder
        .UseEnvironment("CLI")
        .ConfigureServices((hostContext, services) =>
        {
            var configuration = hostContext.Configuration;
            services.AddApplication();
            services.AddInfrastructure(configuration);
        })
        .UseCommandHandler<CreateTodoListCliCommand, CreateTodoListCliCommand.Handler>())
    .UseDefaults()
    .Build();

return await parser.InvokeAsync(args);

static CommandLineBuilder BuildCommandLine()
{
    var root = new RootCommand("Clean CLI Todo Management Tool");

    root.AddGlobalOption(new Option<bool>("--silent", "Disables diagnostics output"));
    root.AddCommand(BuildTodoListCommands());

    // Default to showing help when no subcommand is specified
    root.Handler = CommandHandler.Create(() => root.Invoke("-h"));

    return new CommandLineBuilder(root);

    static Command BuildTodoListCommands()
    {
        var todoList = new Command("todolist", "Todo lists management");
        todoList.AddCommand(new CreateTodoListCliCommand());
        return todoList;
    }
}
```

---

## 5. Anti-Patterns: What You Must NEVER Generate in a Clean CLI

### ❌ Anti-Pattern 1: The Monolithic `Program.cs`
```csharp
// FORBIDDEN: Parsing args, instantiating DbContext, and running business logic in Program.cs
if (args[0] == "create")
{
    using var db = new AppDbContext();
    db.TodoLists.Add(new TodoList { Title = args[1] });
    await db.SaveChangesAsync();
    Console.WriteLine("Created!");
}
```
* **Why it fails:** Untestable, no help generation, no DI, and couples CLI syntax directly to database entities.

### ❌ Anti-Pattern 2: Calling `Console.WriteLine` or `Spectre.Console` in `Application` or `Domain`
```csharp
// FORBIDDEN inside *.Application or *.Domain:
public sealed class CreateTodoListHandler
{
    public async Task HandleAsync(CreateTodoListCommand cmd)
    {
        AnsiConsole.MarkupLine("[green]Created todo list![/]"); // VIOLATION!
    }
}
```
* **Why it fails:** Couples application use cases to a terminal UI. `Application` handlers must return data (`Result<TResponse>`); `*.Cli` handlers render that data.

### ❌ Anti-Pattern 3: Injecting `DbContext` Directly into a `CliCommand.Handler`
```csharp
// FORBIDDEN inside *.Cli:
public new sealed class Handler(AppDbContext db) : ICommandHandler // VIOLATION!
```
* **Why it fails:** Bypasses `Application` use cases and violates the Dependency Rule. CLI handlers may only invoke `Application` handlers/mediators.

### ❌ Anti-Pattern 4: Swallowing Errors and Returning Exit Code `0`
```csharp
// FORBIDDEN: Printing an error to stdout and returning 0
if (!result.IsSuccess)
{
    Console.WriteLine("Failed: " + result.Error.Description);
    return 0; // VIOLATION: Breaks CI/CD scripts and shell pipelines (`&&`)!
}
```
* **Why it fails:** Shell scripts and CI pipelines rely on non-zero exit codes and `stderr` to detect command failure.

---

## 6. Automated Verification: Unit, CLI, & Architecture Tests

### 6.1 Fast Domain & Application Unit Tests (`*.UnitTests`)
* Test `Domain` entities directly with zero mocks.
* Test `Application` handlers using lightweight in-memory Fake repositories (`FakeTodoListRepository : ITodoListRepository`).

### 6.2 In-Memory CLI Invocation Tests (`*.CliTests`)
* Test `*.Cli` commands end-to-end in memory using `System.CommandLine.IO.TestConsole` without spawning external OS processes:
  * Assert that valid arguments return `ExitCodes.Success` (`0`) and write expected text to `testConsole.Out`.
  * Assert that failed domain results return the mapped non-zero exit code (`2`, `3`, `4`) and write error details to `testConsole.Error`.

### 6.3 Executable Architecture Tests (`*.ArchitectureTests`)
Enforce Clean CLI layer boundaries at build time using `NetArchTest.Rules`:

```csharp
using MyCli.Application;
using MyCli.Domain;
using NetArchTest.Rules;
using Xunit;

namespace MyCli.ArchitectureTests;

public class CleanCliDependencyRuleTests
{
    [Fact]
    public void Domain_DependsOnNoOtherLayerOrConsoleLibraries()
    {
        var result = Types.InAssembly(typeof(TodoList).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MyCli.Application",
                "MyCli.Infrastructure",
                "MyCli.Cli",
                "System.CommandLine",
                "Spectre.Console",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Offending Domain types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrCliOrConsoleLibraries()
    {
        var result = Types.InAssembly(typeof(CreateTodoListHandler).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MyCli.Infrastructure",
                "MyCli.Cli",
                "System.CommandLine",
                "Spectre.Console",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Offending Application types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void CliCommands_DoNotDependOnInfrastructureDbContextDirectly()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("MyCli.Cli.Commands")
            .ShouldNot()
            .HaveDependencyOnAny("MyCli.Infrastructure", "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Offending CLI Command types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
```

---

## 7. Pre-Output Self-Verification Checklist for AI Agents

Before returning any generated code or completing a task, run this verification checklist:

* [ ] **Zero Console in Core:** Are `*.Domain` and `*.Application` completely free of `System.Console`, `System.CommandLine`, and `Spectre.Console` references?
* [ ] **Command Separation:** Did you separate the `System.CommandLine` transport command (`*CliCommand` in `*.Cli`) from the use case command (`*Command` in `*.Application`)?
* [ ] **Host & Handler Wiring:** Are all CLI commands wired via `.UseCommandHandler<TCliCommand, TCliCommand.Handler>()` inside `Program.cs`?
* [ ] **Stream Discipline:** Does the `ICommandHandler` write primary output to `IConsole.Out` (`stdout`) and errors to `IConsole.Error` (`stderr`)?
* [ ] **Exit Code Discipline:** Does the `ICommandHandler` return deterministic POSIX exit codes via `.ToExitCode(console)` rather than hardcoding `0`?
* [ ] **Global Options Support:** Do commands respect `--silent` and `CancellationToken` (`context.GetCancellationToken()`)?
* [ ] **Architecture Tests:** Do all `NetArchTest.Rules` checks pass when running `dotnet test`?