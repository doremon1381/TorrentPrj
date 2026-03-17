# C# Clean Code Standards — TorrentProject

## 1. File Structure

Every `.cs` file follows this order:

```csharp
// 1. Using directives (sorted: System → Microsoft → Third-party → Project)
// 2. Namespace declaration (file-scoped)
// 3. XML summary on the type
// 4. Type declaration
//    4a. #region Constants
//    4b. #region Fields
//    4c. #region Constructor
//    4d. #region Public Methods
//    4e. #region Private Methods
```

## 2. Regions

Use `#region` / `#endregion` blocks in classes with **4+ members**. Skip regions for simple records/interfaces.

```csharp
#region Constants
private const string AppName = "TorrentProject";
#endregion

#region Fields
private readonly ILogger<MyService> _logger;
#endregion

#region Constructor
public MyService(ILogger<MyService> logger) => _logger = logger;
#endregion

#region Public Methods
public async Task DoWorkAsync() { }
#endregion

#region Private Methods
private void Helper() { }
#endregion
```

## 3. XML Documentation

- **Required** on all `public` and `internal` types and members.
- Use `<inheritdoc />` for interface implementations.
- Keep summaries concise — one sentence.

```csharp
/// <summary>
/// Uploads a file to Google Drive using resumable chunked transfer.
/// </summary>
public async Task<string> UploadFileAsync(string path, CancellationToken ct) { }
```

## 4. Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Class / Record | PascalCase | `TorrentService` |
| Interface | `I` + PascalCase | `ITorrentService` |
| Method | PascalCase + verb | `LoadTorrentAsync` |
| Async method | suffix `Async` | `UploadFileAsync` |
| Private field | `_camelCase` | `_logger` |
| Constant | PascalCase | `ApplicationName` |
| Parameter | camelCase | `torrentPath` |
| Property | PascalCase | `TempDownloadPath` |

## 5. Class Design

- **One class per file.** File name = class name.
- **`sealed`** by default. Only unseal when inheritance is intended.
- Prefer **primary constructors** for DI services (C# 12+).
- Use **records** for immutable data (models, events, results).
- Use **init-only properties** for configuration records.

## 6. Method Design

- **Single Responsibility**: Each method does one thing.
- **Max 30 lines** per method. Extract helpers if longer.
- **Guard clauses** at the top — fail fast.
- **No magic numbers** — use named constants or configuration.
- Return early to reduce nesting.

## 7. Error Handling

- Catch **specific exceptions**, never bare `catch`.
- Use `OperationCanceledException` for cancellation handling.
- Log errors with `LogError(ex, "message")` — include the exception.
- Throw `InvalidOperationException` for invalid state.

## 8. Async Patterns

- Always pass `CancellationToken` through the chain.
- Never use `.Result` or `.Wait()` — always `await`.
- Use `ConfigureAwait(false)` in library-style code (not required in top-level app).

## 9. Dependency Injection

- Register services via `IServiceCollection` in `Program.cs`.
- Depend on **interfaces**, not concrete types (except `GoogleAuthService` which has no interface).
- Use `IOptions<T>` for configuration binding.

## 10. Logging

- Use structured logging: `_logger.LogInformation("Downloading {FileName}", name)`.
- **Never** use string interpolation in log messages — use templates.
- Log levels: `Debug` (verbose), `Information` (milestones), `Warning` (recoverable), `Error` (failure).
