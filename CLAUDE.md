# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Summary

DirForge is a stateless, read-only directory listing web app for homelab and NAS users. It serves files from a configured root path using ASP.NET Core Razor Pages on .NET 10.0. No database, no background workers, no disk writes to mounted storage. All configuration comes from environment variables or `appsettings.json`, and everything resets on restart.

## Build & Run

```bash
dotnet restore src/DirForge/DirForge.csproj
dotnet build src/DirForge/DirForge.csproj -c Release --no-restore
docker build -t dirforge:dev .
HOST_PATH=/srv/share docker compose up -d
```

Unit tests: `dotnet test src/DirForge.IntegrationRunner --filter TestCategory=Unit`

## Architecture

**Single-project solution** at `src/DirForge/`. No external NuGet dependencies — only ASP.NET Core built-ins.

### Request Pipeline (order matters)

1. Store original RemoteIpAddress
2. ForwardedHeaders middleware
3. `X-Content-Type-Options: nosniff` header injection
4. DashboardMetricsService request timing hooks
5. ASP.NET Core rate limiting (per-IP + global fixed-window)
6. **BasicAuthMiddleware** — bearer token auth, basic auth, share tokens, auth-failure rate limiting
7. Static assets (`/dirforge-assets/{**path}`, `/favicon.ico`) — 1-year cache headers
8. WebDAV (`/webdav/{**path}`) — read-only DAV Class 1
9. S3 (`/s3/{**path}`) — read-only S3 API with SigV4 auth
10. JSON API (`/api/{**path}`) — RESTful JSON API with HATEOAS links
11. MCP (`/mcp`) — JSON-RPC 2.0 Streamable HTTP transport
12. Razor Pages (`/{**requestPath}`)
13. Operational endpoints (`/health`, `/healthz`, `/readyz`, `/metrics`, `/api/stats`)

### Razor Pages

- **DirectoryListing** (`/{**requestPath}`) — composes `DirectoryActionHandlers` (GET/search/sort), `DirectoryFileActionHandlers` (downloads/preview/hash/share), `DirectoryRequestGuards` (path resolution/traversal/scope)
- **ArchiveBrowse** (`/archive/{**requestPath}`) — inline ZIP/TAR/GZ viewer
- **Dashboard** (`/dashboard`) — in-memory metrics, traffic timeseries, logs

### Services

| Service | Responsibility |
|---------|---------------|
| `DirectoryListingService` | File ops, path safety, search, directory sizing, hash generation, listing cache (configurable TTL, default 2s) |
| `ShareLinkService` | HMAC-SHA256 signed share tokens (create, validate, one-time nonces) |
| `ArchiveBrowseService` | ZIP/TAR/GZ parsing, virtual path traversal protection within archives |
| `DashboardMetricsService` | Thread-safe in-memory metrics (requests, status codes, downloads, timeseries) |
| `IconResolver` | Extension → SVG icon mapping (200+ extensions, auto-discovered at startup) |
| `ContentTypeResolver` | MIME type detection by extension |
| `WebDavEndpoints` | Read-only DAV Class 1 at `/webdav/`: OPTIONS, PROPFIND, GET, HEAD |
| `S3Endpoints` | Read-only S3 API at `/s3/`: ListBuckets, ListObjectsV2, GetObject, HeadObject |
| `S3SigV4Auth` | Stateless SigV4 validation (canonical request, signing key derivation, constant-time compare) |
| `JsonApiEndpoints` | REST API at `/api/`: browse, metadata, download, hashes, search, share, archive |
| `McpEndpoints` | MCP at `/mcp`: list_directory, get_file_info, read_file, search |
| `InMemoryLogStore` | Circular 500-entry structured log buffer |

### Static Assets

Served from `wwwroot` at `/dirforge-assets/*` with 1-year cache headers. Frontend uses vanilla ES5 IIFEs (no build step): `dirforge.js` (main bundle), `qrcode-generator.js` (third-party), `style.css`, `dashboard.css`.

### Security Model

- Path handling is root-constrained with symlink containment validation
- Constant-time credential comparison (`CryptographicOperations.FixedTimeEquals`) for all auth methods
- Auth-failure rate limiting (5 attempts / 15 min per IP) across Basic Auth and bearer token failures
- Bearer token auth: configurable header name, supports `Bearer <token>` prefix and raw token; checked before Basic Auth; both can coexist
- Share tokens: `Base64Url(JSON payload).Base64Url(HMAC-SHA256 signature)`
- Hidden paths and denied extensions enforced for direct downloads and ZIP output
- BasicAuth and bearer token auth bypassed when `ExternalAuthEnabled=true`
- S3 uses SigV4 auth (secret never transmitted, ±15 min clock skew tolerance, constant-time compare)

## Key Environment Variables

`RootPath`, `Port`, `ListenIp`, `BasicAuthUser`/`BasicAuthPass`, `BearerToken`/`BearerTokenHeaderName`, `EnableSearch`, `AllowFileDownload`, `AllowFolderDownload`, `OpenArchivesInline`, `EnableSharing`/`ShareSecret`, `HideDotfiles`, `HidePathPatterns`, `DenyDownloadExtensions`, `MaxZipSize`, `DefaultTheme`, `SiteTitle`, `ExternalAuthEnabled`, `EnableWebDav`, `EnableS3Endpoint`/`S3BucketName`/`S3Region`/`S3AccessKeyId`/`S3SecretAccessKey`, `EnableJsonApi`, `EnableMcpEndpoint`/`McpReadFileSizeLimit`, `DashboardEnabled`, `EnableDefaultRateLimiter`, `OperationTimeBudgetMs`, `ListingCacheTtlSeconds`.

Flat keys, PascalCase. Validation at startup via `DirForgeOptionsValidator`; normalization via `PostConfigure` in `Program.cs`.

## Project Philosophy & Contributor Guidance

DirForge is a **simple homelab tool** for 1–5 concurrent users, not an enterprise platform:

- **Stateless and disposable.** No database, no persistent state, no disk writes to mounted storage.
- **Simplicity over performance.** Clear code beats clever optimizations at this scale. Disk I/O and network latency dominate — microsecond-level code overhead is irrelevant.
- **Zero external dependencies.** Only ASP.NET Core built-ins. No NuGet packages.
- **Security is non-negotiable.** Path traversal prevention, symlink containment, and auth checks run on every request. Never weaken them for performance.
- Keep changes small and targeted. Preserve stateless, read-only behavior — no writes to mounted data paths.
- Keep path handling root-constrained and traversal-safe. Keep HTML output escaped. Prefer secure defaults.
- Do not add caches, workers, or abstractions without a measured, user-visible problem to solve.
- Update docs when behavior or configuration changes.
- Commit messages: short imperative form (e.g., "Add share link expiry validation").
- **No alignment whitespace in C# code.** Do not use extra spaces to align `=` signs, comments, or values across lines. The CI enforces `dotnet format` and will reject alignment padding (e.g., `private const long  SampleChunkSize  =` is wrong, `private const long SampleChunkSize =` is correct).
