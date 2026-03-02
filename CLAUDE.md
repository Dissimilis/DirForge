# CLAUDE.md

## Project

DirForge is a stateless, read-only directory listing web app for homelab/NAS use.

- Stack: ASP.NET Core Razor Pages, .NET 10 (`net10.0`)
- Data model: no database, no persistent application state
- Dependency model: runtime app uses framework built-ins only
- Mission: safely expose files from a configured root with strong defaults

## Philosophy

DirForge is intentionally small and opinionated. It is not trying to be a full file platform.

### 1) Security Before Features

Path traversal prevention, root-constrained resolution, symlink containment, and strict auth handling are mandatory. A feature that weakens these boundaries is not acceptable.

### 2) Read-Only Means Read-Only

DirForge is a file access and distribution surface, not a file mutation surface. Public endpoints should not write, rename, move, or delete user files.

### 3) Stateless and Disposable Runtime

In-memory metrics, logs, and one-time-share session state are intentional. Restarting the process should be safe and expected operational behavior.

### 4) Simplicity Over Cleverness

Prefer direct, readable code over abstractions and micro-optimizations unless there is a measured production problem.

### 5) Observability Is Product Behavior

Operational endpoints, dashboard, and `/metrics` are part of the contract. User-visible behavior changes should be reflected in telemetry in the same change.

## Non-Negotiable Invariants

1. All request paths must remain constrained to `RootPath`.
2. Symlink traversal cannot escape root scope.
3. Hide/deny policy must stay consistent across UI, API, WebDAV, S3, and MCP where relevant.
4. Auth checks must remain strict and timing-safe where applicable.
5. Endpoint surfaces remain read-only unless the project direction explicitly changes.

## Architecture Map

### Startup and Composition

- `src/DirForge/Program.cs`
  - Configuration load/normalization/validation
  - Service graph, middleware order, endpoint mapping

- `src/DirForge/Models/DirForgeOptions.cs`
- `src/DirForge/Services/DirForgeOptionsResolver.cs`
- `src/DirForge/Services/DirForgeOptionsValidator.cs`

### Core File and Policy Layer

- `src/DirForge/Services/DirectoryListingService.cs`
  - Path normalization, canonical containment, policy filtering, sorting/search

- `src/DirForge/Pages/DirectoryRequestGuards.cs`
  - Request-time guardrails around path and share-scope checks

### UI and Handler Layer

- `src/DirForge/Pages/DirectoryListing.cshtml(.cs)`
- `src/DirForge/Pages/DirectoryActionHandlers.cs`
- `src/DirForge/Pages/DirectoryFileActionHandlers.cs`
- `src/DirForge/Pages/ArchiveBrowse.cshtml(.cs)`

### Security and Access Control

- `src/DirForge/Middleware/BasicAuthMiddleware.cs`
- `src/DirForge/Security/*`
- `src/DirForge/Services/ShareLinkService.cs`
- `src/DirForge/Services/OneTimeShareStore.cs`

### Protocol/API Surfaces

- `src/DirForge/Services/JsonApiEndpoints.cs` (`/api`)
- `src/DirForge/Services/WebDavEndpoints.cs` (`/webdav`)
- `src/DirForge/Services/S3Endpoints.cs` (`/s3`)
- `src/DirForge/Services/Mcp/*` (`/mcp`)

### Operational and Metrics Surfaces

- `src/DirForge/Services/OperationalEndpointExtensions.cs`
  - `/health`, `/healthz`, `/readyz`, `/dashboard/stats`, `/metrics`

- `src/DirForge/Services/DashboardMetricsService.cs`
- `src/DirForge/Pages/Dashboard.cshtml(.cs)`

## Build and Run

From repo root:

```bash
dotnet restore src/DirForge.sln
dotnet build src/DirForge.sln -c Release
dotnet run --project src/DirForge/DirForge.csproj
```

Run full tests:

```bash
dotnet test src/DirForge.IntegrationRunner/DirForge.IntegrationRunner.csproj -c Release
```

Run smoke tests only:

```bash
dotnet test src/DirForge.IntegrationRunner/DirForge.IntegrationRunner.csproj -c Release --filter "TestCategory=Smoke"
```

Container workflow:

```bash
docker build -t dirforge:dev .
HOST_PATH=/srv/share docker compose up -d
```

## Contributor Expectations

- Keep changes small and focused.
- Preserve root-constrained and read-only behavior.
- Keep escaping/validation and secure defaults in place.
- Avoid broad refactors without a concrete, measured need.
- Prefer explicit code over premature abstractions.

When changing routes, handlers, auth, config, logging, or limits, update observability in the same change when relevant:

- Dashboard behavior and data shape
- `/metrics` counters/gauges
- Endpoint classification in metrics service

## Review Lens

Every PR should answer:

1. What invariant is being touched?
2. How is path/security policy preserved?
3. Is behavior consistent across all relevant protocol surfaces?
4. What telemetry changed, and why?
5. Which tests prove this behavior and guard regressions?

## Testing Strategy

- Unit coverage for policy logic, auth parsing, path handling, and endpoint edge cases.
- Integration scenarios for end-to-end correctness (browse/download/auth/operational bypass).
- Add regression tests for every security-relevant bug fix.

Primary test project:

- `src/DirForge.IntegrationRunner/`

## Out of Scope by Default

- File writes/mutations through public endpoints
- Heavy architectural rewrites without concrete user impact
- New dependencies or subsystems that do not materially improve safety/correctness
