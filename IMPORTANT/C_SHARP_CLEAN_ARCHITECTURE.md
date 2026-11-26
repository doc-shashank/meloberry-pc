# SYSTEM DIRECTIVE: CLEAN ARCHITECTURE CONSTRAINTS FOR AI AGENTS

> **TARGET AUDIENCE:** Large Language Models (LLMs), Autonomous Coding Agents, and AI Pair Programmers.  
> **ENFORCEMENT LEVEL:** STRICT / NON-NEGOTIABLE.  
> **PRIMARY OBJECTIVE:** Optimize for long-term architectural coherence, strict layer isolation, and domain integrity over immediate task shortcuts.

---

## 1. Core Mandate & Why You Must Follow These Rules

As an AI agent, your default tendency is to optimize for immediate task completion—such as querying a database directly from an API endpoint, making entity setters `public`, or importing infrastructure packages into business logic. **You are strictly forbidden from taking these shortcuts.**

Architectural decay is silent. Violating layer boundaries even once turns architecture into fiction. Every line of code you generate, refactor, or review **must** obey the structural rules, dependency directions, naming conventions, and verification pipelines defined in this document.

---

## 2. The Dependency Rule & Project Topology

Source code dependencies **must only point inward**, toward the business rules. Inner layers must never reference, import, or even name types, namespaces, or packages from outer layers.

```text
+-------------------------------------------------------------------+
|                   Outer Ring: Frameworks & Drivers                |
|        [ *.Api ] (HTTP / Composition Root)   [ *.Infrastructure ] |
|                |                                      |           |
|                +------------------+-------------------+           |
|                                   | (Compile-Time Reference)      |
|                                   v                               |
|                   Middle Ring: Use Cases & Ports                  |
|                          [ *.Application ]                        |
|                                   | (Compile-Time Reference)      |
|                                   v                               |
|                   Center Ring: Entities & Business Rules          |
|                            [ *.Domain ]                           |
|                      (DEPENDS ON NOTHING)                         |
+-------------------------------------------------------------------+
```

### 2.1 Compile-Time vs. Runtime Direction
* **Compile-Time References Point Inward:** `Infrastructure` references `Application` to implement its interfaces (Ports).
* **Runtime Calls Travel Outward:** At runtime, an `Application` handler invokes an interface method (`IEventRepository`), and the DI container executes the `Infrastructure` implementation (`EventRepository`).
* **Ports & Adapters:** Interfaces owned by an inner layer (`Application`) are **Ports**. Classes in an outer layer (`Infrastructure` or `Api`) that implement those interfaces are **Adapters**.

### 2.2 Project Reference & Package Matrix

| Project / Layer | Allowed Project References | Allowed NuGet Packages | Forbidden Dependencies |
| :--- | :--- | :--- | :--- |
| **`*.Domain`** | **NONE** | **NONE** (Zero third-party packages) | `Application`, `Infrastructure`, `Api`, EF Core, ASP.NET Core, JSON libraries |
| **`*.Application`** | `*.Domain` | `Microsoft.Extensions.DependencyInjection.Abstractions` (and FluentValidation if explicitly requested) | `Infrastructure`, `Api`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.AspNetCore.*`, Database drivers, HTTP clients |
| **`*.Infrastructure`** | `*.Application` (and `*.Domain` transitively) | `Microsoft.EntityFrameworkCore.*`, Database providers, External SDKs (S3, Stripe, SMTP) | `*.Api`, `Microsoft.AspNetCore.Http` (unless isolated in a specific adapter) |
| **`*.Api`** | `*.Application`, `*.Infrastructure` (Composition Root only) | `Microsoft.AspNetCore.*` | Direct `DbContext` or `Infrastructure` type usage inside endpoints/controllers |

> **CRITICAL RULE ON `*.Api` -> `*.Infrastructure`:** The `Api` project references `Infrastructure` **solely** so `Program.cs` (the Composition Root) can call `builder.Services.AddInfrastructure(...)`. **No endpoint, controller, or middleware in `*.Api` may import or inject a concrete class from `*.Infrastructure`.**

---

## 3. Placement Decision Tree: Where Does My Code Go?

Before generating or modifying any file, evaluate these four questions in order and **stop at the first "YES"**:

1. **Would this rule still be true if there were no application, database, or web framework around it?**
   * **YES -> `*.Domain`** (Entities, Value Objects, Domain Errors, Invariant checks).
2. **Does this code sequence the steps of a single operation (load -> tell domain to act -> save -> return DTO)?**
   * **YES -> `*.Application`** (Command/Query records, Handlers, Response DTOs, Port interfaces).
3. **Does this code talk to something outside the process (database, file system, message queue, email, third-party API)?**
   * **YES -> `*.Infrastructure`** (`DbContext`, EF Core configurations, Repositories, external clients).
4. **Does this code exist only because a client speaks HTTP/gRPC/WebSockets, or does it wire up the DI container?**
   * **YES -> `*.Api`** (Minimal API endpoints, HTTP Request DTOs, `ProblemDetails` mapping, `Program.cs`).

If a piece of code seems to belong in two layers, **it is doing two jobs**. Split it (e.g., split request-shape input validation in `Api` from business invariant enforcement in `Domain`).

### 3.1 Component Placement Reference Table

| Artifact / Concern | Target Layer | Rationale |
| :--- | :--- | :--- |
| **Entities & Invariant Rules** (e.g., `Event.Reserve()`) | `Domain` | Guards state; true regardless of tech stack. |
| **Value Objects** (e.g., `Money`, `EmailAddress`) | `Domain` | Self-validating business primitives. |
| **Domain Errors & `Result<T>`** (e.g., `EventErrors.SoldOut`) | `Domain` | The rule that fails knows what went wrong. |
| **Reading Current Time** | `Domain` / `Application` | Inject .NET's built-in `TimeProvider` (never call `DateTime.Now` or `DateTime.UtcNow` directly). |
| **Use Case Handlers** (e.g., `ReserveTicketsHandler`) | `Application` | Orchestrates one business use case. |
| **Repository & Unit of Work Interfaces** (`IEventRepository`) | `Application` | Owned by the use cases that consume them. |
| **Current User Abstraction** (`ICurrentUser`) | `Application` | Use cases need user context; `Api` implements it via `HttpContext`. |
| **`DbContext`, EF Core Mappings & Repositories** | `Infrastructure` | Persistence details and SQL generation. |
| **Email, Payment, & File Storage Clients** | `Infrastructure` | Out-of-process I/O adapters. |
| **HTTP Endpoints & Request DTOs** | `Api` | Transport-specific contracts and routing. |
| **Mapping `ErrorType` to HTTP Status Codes** | `Api` | Only the HTTP edge knows what `404` or `409` means. |
| **DI Wiring (`Program.cs`)** | `Api` | Composition Root where all layers meet. |

---

## 4. Strict Layer-by-Layer Implementation Rules & Templates

### 4.1 Layer 1: `*.Domain` Constraints

1. **Rich Domain Model (No Anemic Entities):**
   * Entities **must** have `private` or `protected` constructors.
   * Every property setter **must** be `private set;` (or `init;` where appropriate). External code must never mutate state directly (e.g., `ev.TicketsSold += 5` is a compile-time error).
   * State mutations **must** happen through intention-revealing domain methods (e.g., `Create(...)`, `Reserve(...)`, `Cancel(...)`) that enforce all invariants before mutating state.
2. **Persistence Ignorance:**
   * Never place EF Core data annotations (`[Table]`, `[Column]`, `[MaxLength]`, `[Key]`) or JSON attributes (`[JsonPropertyName]`) on Domain entities. All DB mapping belongs in `Infrastructure`.
3. **Expected Failures vs. Exceptions (The "Ordinary Tuesday" Rule):**
   * Ask: *"Could a well-behaved user cause this failure on an ordinary Tuesday?"*
   * **YES (Expected Failure):** Business rule violations (e.g., sold out, insufficient funds, item not found, duplicate email). **Return a `Result` or `Result<TValue>` containing a typed `Error`. NEVER throw an exception for expected domain/business failures.**
   * **NO (Bug / Programmer Error / Outage):** Unvalidated invalid arguments (e.g., negative quantity bypassing input validation), null references, or DB outages. **Throw an exception** (`ArgumentOutOfRangeException.ThrowIfNegativeOrZero`, `ArgumentException.ThrowIfNullOrWhiteSpace`).

#### Reference Template: `Result.cs` (Domain)
```csharp
namespace MyProject.Domain;

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

    private Result(TValue? value, Error? error)
        : base(error) => _value = value;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public static implicit operator Result<TValue>(TValue value) =>
        new(value, null);

    public static implicit operator Result<TValue>(Error error) =>
        new(default, error);
}
```

#### Reference Template: Domain Errors & Rich Entity (`Event.cs` & `EventErrors.cs`)
```csharp
namespace MyProject.Domain;

public static class EventErrors
{
    public static Error NotFound(int eventId) =>
        new("Event.NotFound", $"Event {eventId} was not found.", ErrorType.NotFound);

    public static Error SoldOut(int ticketsLeft, int requested) =>
        new("Event.SoldOut", $"Only {ticketsLeft} tickets left, {requested} requested.", ErrorType.Conflict);
}

public sealed class Event
{
    private Event(string name, int capacity)
    {
        Name = name;
        Capacity = capacity;
    }

    public int Id { get; private set; }
    public string Name { get; private set; }
    public int Capacity { get; private set; }
    public int TicketsSold { get; private set; }

    public int TicketsLeft => Capacity - TicketsSold;

    public static Event Create(string name, int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        return new Event(name, capacity);
    }

    public Result Reserve(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (quantity > TicketsLeft)
            return EventErrors.SoldOut(TicketsLeft, quantity);

        TicketsSold += quantity;

        return Result.Success();
    }
}
```

---

### 4.2 Layer 2: `*.Application` Constraints

1. **One Use Case Per File / Handler:**
   * Group the Command/Query input `record`, the Response `record` (DTO), and the `Handler` class together cleanly.
2. **Handlers Sequence Steps—They Never Evaluate Business Rules:**
   * A Command Handler must only:
     1. Load the entity via a Port (`I*Repository`).
     2. Return `NotFound` if null.
     3. Call the domain method on the entity (`ev.Reserve(...)`).
     4. If `!result.IsSuccess`, return `result.Error!` immediately (do **not** save).
     5. Call `SaveChangesAsync()`.
     6. Return a dedicated Application response record (never return the Domain entity itself).
3. **CQRS by Path (No MediatR Required):**
   * **Commands (Writes):** Always load the tracked Domain entity so its business rules execute, then save changes.
   * **Queries (Reads):** Bypass the Domain entity completely. Project directly into a read-only DTO (`EventAvailability`) via the repository port.
   * Do **not** introduce `MediatR` unless the repository already uses it or the user explicitly asks for a mediator pipeline. Direct handler injection (`ReserveTicketsHandler`) is preferred, explicit, and compiler-verified.
   * Do **not** create `IReserveTicketsHandler` interfaces for handlers; handlers do not vary behind interfaces.
4. **Strict Port Rules (`I*Repository`):**
   * **NO Generic Repositories:** Never create `IRepository<T>` with `GetAll()`, `Add()`, `Update()`, `Delete()`. Write narrow, Aggregate-specific repositories that speak the use case's language.
   * **NO `IQueryable<T>`:** Never return `IQueryable<T>` from a repository method. It leaks EF Core's query provider into `Application`. Always materialize results (`Task<Event?>`, `Task<IReadOnlyList<T>>`) inside `Infrastructure`.
   * **Async & Cancellation:** Every I/O method on a port must accept `CancellationToken cancellationToken = default`.

#### Reference Template: Port, Command Handler, Query Handler, and DI (`*.Application`)
```csharp
using MyProject.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Application;

// 1. Port (Owned by Application)
public interface IEventRepository
{
    Task<Event?> GetByIdAsync(int eventId, CancellationToken cancellationToken = default);
    Task<EventAvailability?> GetAvailabilityAsync(int eventId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

// 2. Command Use Case (Write Path)
public sealed record ReserveTicketsCommand(int EventId, int Quantity);
public sealed record ReservationResponse(int EventId, int TicketsReserved, int TicketsLeft);

public sealed class ReserveTicketsHandler(IEventRepository events)
{
    public async Task<Result<ReservationResponse>> HandleAsync(
        ReserveTicketsCommand command, CancellationToken cancellationToken = default)
    {
        var ev = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (ev is null)
            return EventErrors.NotFound(command.EventId);

        var reservation = ev.Reserve(command.Quantity);
        if (!reservation.IsSuccess)
            return reservation.Error!;

        await events.SaveChangesAsync(cancellationToken);

        return new ReservationResponse(ev.Id, command.Quantity, ev.TicketsLeft);
    }
}

// 3. Query Use Case (Read Path - CQRS)
public sealed record GetEventAvailabilityQuery(int EventId);
public sealed record EventAvailability(int EventId, string Name, int TicketsLeft);

public sealed class GetEventAvailabilityHandler(IEventRepository events)
{
    public async Task<Result<EventAvailability>> HandleAsync(
        GetEventAvailabilityQuery query, CancellationToken cancellationToken = default)
    {
        var availability = await events.GetAvailabilityAsync(query.EventId, cancellationToken);
        if (availability is null)
            return EventErrors.NotFound(query.EventId);

        return availability;
    }
}

// 4. Layer DI Registration
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ReserveTicketsHandler>();
        services.AddScoped<GetEventAvailabilityHandler>();
        return services;
    }
}
```

---

### 4.3 Layer 3: `*.Infrastructure` Constraints

1. **Fluent EF Core Configuration Only:**
   * Configure all table names, keys, lengths, conversions, and relationships inside `OnModelCreating` or `IEntityTypeConfiguration<T>` classes in `Infrastructure`.
2. **Command vs. Query Optimization in Repositories:**
   * Methods used by Commands (`GetByIdAsync`) **must** return tracked entities so EF Core detects state modifications made by Domain methods.
   * Methods used by Queries (`GetAvailabilityAsync`) **must** use `.AsNoTracking()` and `.Select(...)` to project directly into the `Application` DTO in SQL.
3. **Configuration Isolation:**
   * `AddInfrastructure` should accept required primitives (like `string connectionString`) or bind options cleanly without coupling Domain/Application to configuration providers.

#### Reference Template: `DbContext`, `Repository`, and DI (`*.Infrastructure`)
```csharp
using MyProject.Application;
using MyProject.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Infrastructure;

public sealed class TicketingDbContext(DbContextOptions<TicketingDbContext> options)
    : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Event>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        });
    }
}

public sealed class EventRepository(TicketingDbContext dbContext) : IEventRepository
{
    public Task<Event?> GetByIdAsync(int eventId, CancellationToken cancellationToken = default) =>
        dbContext.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

    public Task<EventAvailability?> GetAvailabilityAsync(int eventId, CancellationToken cancellationToken = default) =>
        dbContext.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new EventAvailability(e.Id, e.Name, e.Capacity - e.TicketsSold))
            .FirstOrDefaultAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TicketingDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IEventRepository, EventRepository>();
        return services;
    }
}
```

---

### 4.4 Layer 4: `*.Api` Constraints

1. **Separate Transport DTOs from Application Commands:**
   * Define HTTP request body records in `*.Api` (e.g., `ReserveTicketsRequest`), separate from `ReserveTicketsCommand` in `*.Application` (since route parameters like `eventId` come from the URL while the body only carries `Quantity`, and transport contracts evolve independently).
2. **Input Validation vs. Domain Validation:**
   * **Input Validation (Edge):** Validate request shape/ranges at the API boundary using DataAnnotations (`[Range(1, 20)]`) with `.NET 10`'s `builder.Services.AddValidation()` (or FluentValidation in Application).
   * **Business Rule Validation (Domain):** Enforced inside the Domain entity.
3. **Centralized `Result` to `ProblemDetails` Mapping:**
   * Endpoints must never manually construct status codes or serialize raw Domain entities. Map failed `Result` objects through a single `ToProblem()` extension method producing RFC 9110 `ProblemDetails`.
4. **Thin Endpoints:**
   * Endpoints only translate HTTP inputs into a Command/Query, await `handler.HandleAsync(...)`, and return `result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem()`.

#### Reference Template: `ResultExtensions.cs` & `EventEndpoints.cs` (`*.Api`)
```csharp
using System.ComponentModel.DataAnnotations;
using MyProject.Application;
using MyProject.Domain;

namespace MyProject.Api;

public static class ResultExtensions
{
    public static IResult ToProblem(this Result result)
    {
        var error = result.Error
            ?? throw new InvalidOperationException("A successful result is not a problem.");

        var statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        return TypedResults.Problem(
            statusCode: statusCode,
            title: error.Code,
            detail: error.Description);
    }
}

public sealed record ReserveTicketsRequest([property: Range(1, 20)] int Quantity);

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events");

        events.MapGet("/{eventId:int}", async (
            int eventId,
            GetEventAvailabilityHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(new GetEventAvailabilityQuery(eventId), cancellationToken);
            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        });

        events.MapPost("/{eventId:int}/reservations", async (
            int eventId,
            ReserveTicketsRequest request,
            ReserveTicketsHandler handler,
            CancellationToken cancellationToken) =>
        {
            var command = new ReserveTicketsCommand(eventId, request.Quantity);
            var result = await handler.HandleAsync(command, cancellationToken);
            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        });
    }
}
```

---

## 5. Module Isolation & Cross-Cutting Constraints

When operating in a multi-module system (Modular Monolith) or enterprise codebase, enforce these additional structural boundaries:

1. **Strict Module Isolation:**
   * Code in the `Users` module cannot import internal classes, entities, or repositories from the `Billing` or `Orders` modules.
   * Modules may only communicate through explicit, versioned **Contracts** (Integration Events, public DTOs, or shared Kernel primitives).
2. **Zero Circular Dependencies:**
   * If Namespace/Module A depends on Namespace/Module B, then B **must never** depend on A (directly or transitively: $A \to B \to C \to A$ is forbidden). Extract shared abstractions into a lower layer or decouple via Domain/Integration Events.
3. **Mandatory Naming Conventions (Semantic Structure):**
   * Naming is structural metadata used by static validators:
     * Domain Error catalogs must end with `Errors` (e.g., `EventErrors`).
     * Domain Events must end with `DomainEvent` or `Event`.
     * Write inputs must end with `Command`; Read inputs must end with `Query`.
     * Use case orchestrators must end with `Handler`.
     * Data access ports must start with `I` and end with `Repository` (`IEventRepository`), and their implementations must end with `Repository` (`EventRepository`).
4. **Security & Audit Invariants:**
   * User-facing endpoints must pass through authentication/authorization middleware.
   * Sensitive state mutations (e.g., financial transactions, permission changes) must record an audit trail entry within the same transaction boundary.

---

## 6. Anti-Patterns: What You Must NEVER Generate

### ❌ Anti-Pattern 1: Direct Database Access in Endpoints or Application
```csharp
// FORBIDDEN: Injecting DbContext into an API endpoint or Application handler
app.MapPost("/api/events/{id}/reservations", async (int id, TicketingDbContext db) => { ... });
```
* **Why it fails:** Couples HTTP transport, business logic, and EF Core together. Impossible to unit test without a database.

### ❌ Anti-Pattern 2: Anemic Domain Entity + Business Logic in the Handler
```csharp
// FORBIDDEN: Public setters on Entity and capacity check inside the Handler
public class Event { public int Capacity { get; set; } public int TicketsSold { get; set; } }

// Inside Handler:
if (ev.TicketsSold + command.Quantity > ev.Capacity) // WRONG: Handler stole the Entity's job!
    return EventErrors.SoldOut(...);
ev.TicketsSold += command.Quantity; // WRONG: Unprotected state mutation!
```
* **Why it fails:** The next developer or AI agent writing a different handler can mutate `ev.TicketsSold` without running the capacity check.

### ❌ Anti-Pattern 3: Sneaking Framework Packages into `Application` or `Domain`
```csharp
// FORBIDDEN: Adding Microsoft.EntityFrameworkCore to *.Application
using Microsoft.EntityFrameworkCore;
namespace MyProject.Application;
public sealed class SneakyReportHandler(DbContext dbContext) { ... }
```
* **Why it fails:** Even if project references aren't violated, adding NuGet packages for EF Core or ASP.NET Core into `Application` or `Domain` destroys layer isolation.

### ❌ Anti-Pattern 4: Returning Domain Entities from the API
```csharp
// FORBIDDEN: Serializing the Domain Entity directly to HTTP clients
return Results.Ok(ev);
```
* **Why it fails:** Couples your database schema and internal domain state directly to the public API JSON contract. Always return a dedicated Response DTO record.

---

## 7. Automated Verification: Unit & Architecture Tests

Rules without automated enforcement are merely suggestions. Whenever you add or modify features, you must maintain two test suites:

### 7.1 Fast Unit Tests with In-Memory Fakes (`*.UnitTests`)
* **Domain Tests:** Instantiate the entity via its static factory (`Event.Create(...)`), invoke the behavior (`ev.Reserve(...)`), and assert on the returned `Result` and entity state. Zero mocks, zero databases.
* **Application Handler Tests:** Write a lightweight, in-memory `Fake*Repository` implementing the Port interface instead of pulling in heavy mocking frameworks. Verify both state outcome and whether `SaveChangesAsync` was called (`SaveCount == 1` on success, `SaveCount == 0` on domain failure).

```csharp
internal sealed class FakeEventRepository(Event? ev) : IEventRepository
{
    public int SaveCount { get; private set; }

    public Task<Event?> GetByIdAsync(int eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ev);

    public Task<EventAvailability?> GetAvailabilityAsync(int eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult<EventAvailability?>(null);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
```

### 7.2 Executable Architecture Tests (`*.ArchitectureTests`)
Because the C# compiler only blocks invalid `<ProjectReference>` links and cannot stop someone from installing `Microsoft.EntityFrameworkCore` directly into `Application`, you **must** enforce architectural boundaries using `NetArchTest.Rules` (or `ArchUnitNET`).

```csharp
using MyProject.Application;
using MyProject.Domain;
using NetArchTest.Rules;
using Xunit;

namespace MyProject.ArchitectureTests;

public class DependencyRuleTests
{
    [Fact]
    public void Domain_DependsOnNoOtherLayerOrFramework()
    {
        var result = Types.InAssembly(typeof(Event).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MyProject.Application",
                "MyProject.Infrastructure",
                "MyProject.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Offending types in Domain: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrWebOrEfCore()
    {
        var result = Types.InAssembly(typeof(ReserveTicketsHandler).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "MyProject.Infrastructure",
                "MyProject.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Offending types in Application: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
```

---

## 8. Pre-Output Self-Verification Checklist for AI Agents

Before presenting generated code or committing changes, execute this internal validation pass. If **any** check fails, refactor your code before responding:

- [ ] **Dependency Check:** Does `Domain` have zero `<ProjectReference>` and zero NuGet `<PackageReference>` items?
- [ ] **Leak Check:** Does `Application` contain any `using Microsoft.EntityFrameworkCore;`, `using Microsoft.AspNetCore;`, or references to `Infrastructure`?
- [ ] **Encapsulation Check:** Are all Domain entity constructors `private` and all property setters `private set`?
- [ ] **Rule Ownership Check:** Are all business rules and state mutations inside `Domain` entity methods rather than `Application` handlers or `Api` endpoints?
- [ ] **Error Handling Check:** Do expected business failures return `Result` / `Error` values instead of throwing exceptions?
- [ ] **Port Purity Check:** Do repository interfaces live in `Application`, avoid generic `IRepository<T>`, and never return `IQueryable<T>`?
- [ ] **CQRS Check:** Do queries project directly into DTOs using `.AsNoTracking()` while commands load tracked Domain entities?
- [ ] **API Edge Check:** Do endpoints only inject `Application` handlers (never `DbContext` or `Infrastructure` services) and map errors via `.ToProblem()`?
- [ ] **Verification Check:** Did you run `dotnet build` and `dotnet test` (including `ArchitectureTests`) to confirm zero violations?