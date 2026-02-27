# DirForge

<p align="center">
  <img src="img/logo.svg" alt="DirForge logo" width="120">
</p>

<p align="center">
  <a href="https://github.com/Dissimilis/DirForge/actions/workflows/ci.yml"><img src="https://github.com/Dissimilis/DirForge/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/Dissimilis/DirForge/releases/latest"><img src="https://img.shields.io/github/v/release/Dissimilis/DirForge?label=release" alt="Latest Release"></a>
  <a href="https://github.com/Dissimilis/DirForge/pkgs/container/dirforge"><img src="https://img.shields.io/badge/GHCR-dirforge-blue?logo=docker" alt="GHCR"></a>
  <a href="https://buymeacoffee.com/dissimilis"><img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"></a>
</p>

A stateless, read-only web file browser for homelab and NAS power users. No database, no background workers, no disk writes - just point it at a mounted path and go.

<p align="center">
  <img src="img/screenshot.jpg" alt="DirForge directory listing" width="700">
</p>

<p align="center">
  <a href="https://dirforge.zenforge.eu/"><strong>Live Demo</strong></a>
</p>

Browse files in a clean web UI with search, previews, and archive inspection. Download individual files or entire folders as ZIP. Access the same data through a RESTful JSON API, an S3-compatible endpoint for tools like `rclone`, a read-only WebDAV mount for native OS file managers, or an MCP server for AI assistants. Everything runs in a single stateless container with no external dependencies.

## Quick Start

> **Prerequisite:** [Docker](https://docs.docker.com/get-docker/) or [Podman](https://podman.io/docs/installation) (images are published for amd64 and arm64).

### Docker run
```bash
docker run -d \
  --name dirforge \
  -p 8091:8080 \
  -e RootPath=/data \
  -v /srv/share:/data:ro \
  ghcr.io/dissimilis/dirforge:latest
```

### Docker Compose
```bash
cp .env.example .env
# edit HOST_PATH in .env
docker compose up -d
```

### Podman

The same commands work with [Podman](https://podman.io/). Replace `docker` with `podman`:

```bash
podman run -d \
  --name dirforge \
  -p 8091:8080 \
  -e RootPath=/data \
  -v /srv/share:/data:ro \
  ghcr.io/dissimilis/dirforge:latest
```

For Compose, use `podman compose` (Podman 4.1+):

```bash
cp .env.example .env
# edit HOST_PATH in .env
podman compose up -d
```

> **Notes for Podman users:**
> - `podman compose` is a built-in subcommand in Podman 4.1+, not the older third-party `podman-compose` Python package.
> - DirForge uses ports above 1024, so rootless Podman works without extra configuration.
> - On SELinux systems (Fedora, RHEL), add `:z` to volume mounts: `-v /srv/share:/data:ro,z`.

Open `http://localhost:8091`.
With the default config profile in this repository, sharing, dashboard, and metrics are enabled.

## Features

### Browsing
- List and grid view layouts
- Sortable columns (name, size, date)
- Light and dark themes (toggle or set default via `DefaultTheme`)
- File preview modal for text, images, video, audio, and PDF
- Inline archive browser for `.zip`, `.tar`, `.tar.gz`, `.tgz`, and `.gz`
- Image lightbox with navigation
- Recursive search with configurable depth and time budget
- Age badges on files and folders
- Custom site title via `SiteTitle`

### Sharing & Downloads
- Direct file downloads
- Folder download as ZIP archive (with configurable max size)
- Signed share links with expiry
- One-time share links
- QR code generation for share links
- File hash calculation (MD5, SHA-1, SHA-256, SHA-512)

### WebDAV
- Read-only WebDAV endpoint at `/webdav/` (DAV Class 1)
- Supports `OPTIONS`, `PROPFIND`, `GET`, and `HEAD` methods
- Compatible with Windows Explorer, macOS Finder, and other WebDAV clients
- Same security policies as the web UI (hidden paths, denied extensions, auth)

### S3-Compatible API
- Read-only S3 endpoint at `/s3/` for scripted and programmatic access
- Compatible with `aws cli`, `rclone`, MinIO client, and other S3-compatible tools
- AWS Signature V4 authentication (access key + secret key)
- Supports `ListBuckets`, `GetBucketLocation`, `ListObjectsV2`, `GetObject`, `HeadObject`
- HTTP Range requests for partial downloads
- Hidden paths, denied extensions, and auth enforced

### Security & Operations
- HTTP Basic Auth (username + password)
- Bearer token auth via configurable header (for MCP clients, API consumers, automation)
- External auth via reverse proxy headers (e.g. Authelia, Authentik)
- Hide files by dotfile flag or glob patterns
- Deny downloads by file extension
- Fixed-window rate limiting (per-IP and global)
- Health endpoints (`/health`, `/healthz`, `/readyz`)
- In-memory dashboard at `/dashboard` with optional dedicated credentials
- Prometheus metrics at `/metrics`
- RESTful JSON API at `/api/` (browse, search, share, archive)
- MCP server at `/mcp/` (JSON-RPC 2.0, Streamable HTTP transport)
- Integration stats JSON at `/api/stats`
- Distroless chiseled container image

## Configuration

Defaults are defined in `src/DirForge/appsettings.json`. Override any value with an environment variable of the same name. Boolean values must be `true` or `false`. For the full list of options, see [`.env.example`](.env.example).

| Variable | Default | Description |
|---|---|---|
| `RootPath` | `.` | Root directory to browse. |
| `Port` | `8080` | HTTP listen port. |
| `ListenIp` | `0.0.0.0` | IP address the app binds to. |
| `BasicAuthUser` | unset | Basic Auth username. |
| `BasicAuthPass` | unset | Basic Auth password. |
| `BearerToken` | unset | Bearer token for token-based auth (MCP clients, API consumers, automation). |
| `BearerTokenHeaderName` | `Authorization` | Header to read the bearer token from. |
| `EnableSharing` | `true` | Enable HMAC-signed share links. |
| `ShareSecret` | empty | Secret for signing share links. Set a long random value in production; if empty an in-memory secret is generated at startup. |
| `HideDotfiles` | `false` | Hide entries starting with `.`. |
| `DenyDownloadExtensions` | `env,key,pem,...` | Extensions blocked in direct and ZIP downloads. |
| `DefaultTheme` | `dark` | UI theme (`dark` or `light`). |
| `SiteTitle` | `DirForge` | Custom page title/header label. |
| `EnableWebDav` | `true` | Read-only WebDAV at `/webdav/`. |
| `EnableS3Endpoint` | `false` | Read-only S3 API at `/s3/`. |
| `EnableJsonApi` | `true` | RESTful JSON API at `/api/`. |
| `EnableMcpEndpoint` | `true` | MCP server at `/mcp/`. |
| `ListingCacheTtlSeconds` | `2` | Directory listing cache TTL in seconds (1–2592000). |

## Security

**Hardened example profile** - copy into your `.env` and adjust:
```env
BasicAuthUser=admin
BasicAuthPass=change-this
BearerToken=replace-with-long-random-token
ShareSecret=replace-with-long-random-value
ForwardedHeadersKnownProxies=10.0.0.2
DashboardAuthUser=metrics
DashboardAuthPass=change-this-too
```

- Mount only directories you want to expose; prefer read-only mounts (`:ro`).
- Baseline defaults are homelab-oriented, not internet-hardened.
- Set `BasicAuthUser` / `BasicAuthPass` when exposed outside a trusted network.
- Set `BearerToken` for token-based auth (useful for MCP clients, API consumers, and automation that poorly support Basic Auth). Both auth methods can be enabled simultaneously, either one grants access.
- When `BearerTokenHeaderName` is `Authorization` (default), the middleware accepts `Authorization: Bearer <token>` and `Authorization: <token>`. Set a custom header name (e.g. `X-API-Key`) to read the raw token from that header instead.
- Set `ShareSecret` to a long random value in production. If empty, DirForge uses an in-memory secret and share links reset on restart.
- For reverse proxy auth, set `ExternalAuthEnabled=true` and pin `ForwardedHeadersKnownProxies`. Bearer token auth is also bypassed when external auth is enabled.
- Hidden paths and denied extensions are enforced for both direct downloads and ZIP output.
- Dashboard and metrics data are in-memory only and reset on restart.
- If `DashboardAuthUser` / `DashboardAuthPass` are set, `/dashboard` and `/metrics` accept only those credentials.
- `/api/stats` uses the same dashboard auth behavior: if dashboard credentials are configured, they are required.
- Static UI assets are served from `/dirforge-assets/*` (plus `/favicon.ico`) and are intentionally public.

## WebDAV

DirForge includes a read-only WebDAV endpoint at `/webdav/` (DAV Class 1), enabled by default. It supports `OPTIONS`, `PROPFIND`, `GET`, and `HEAD`. All write methods return `405 Method Not Allowed`.

### Client Access

| Client | Connection |
|---|---|
| **macOS Finder** | Finder → Go → Connect to Server → `http://host:port/webdav/` |
| **Windows Explorer** | Map Network Drive → `http://host:port/webdav/` (requires HTTPS or registry tweak, see below) |
| **Linux (GVFS)** | `dav://host:port/webdav/` in Nautilus/Thunar |
| **cadaver / curl** | `cadaver http://host:port/webdav/` |

### Windows HTTP Limitation

Windows Explorer's WebDAV client (Mini-Redirector) refuses Basic Auth over plain HTTP by default. You must either:
- Use HTTPS (via reverse proxy, recommended)
- Set the registry key `HKLM\SYSTEM\CurrentControlSet\Services\WebClient\Parameters\BasicAuthLevel` to `2` and restart the `WebClient` service

### Security

WebDAV requests follow the same auth and security pipeline as the web UI:
- Basic Auth or bearer token credentials are required when configured
- External auth headers are honored when `ExternalAuthEnabled=true`
- Hidden paths (`HideDotfiles`, `HidePathPatterns`) and denied extensions (`DenyDownloadExtensions`) are enforced
- Path traversal protection applies to all WebDAV paths

Set `EnableWebDav=false` to disable the endpoint entirely.

## S3-Compatible API

DirForge includes a read-only S3-compatible endpoint at `/s3/`, disabled by default. It implements the minimal subset of the S3 API needed for listing and downloading files with standard S3 tools.

### Supported Operations

| Operation | Description |
|---|---|
| `ListBuckets` | `GET /s3/` - returns a single virtual bucket |
| `GetBucketLocation` | `GET /s3/{bucket}?location` - returns configured region |
| `ListObjectsV2` | `GET /s3/{bucket}?prefix=...&delimiter=/&max-keys=...` - list objects with pagination |
| `GetObject` | `GET /s3/{bucket}/{key}` - download a file (supports `Range` header) |
| `HeadObject` | `HEAD /s3/{bucket}/{key}` - file metadata without body |

All write operations (`PUT`, `POST`, `DELETE`) return `405 Method Not Allowed`.

### Authentication

S3 requests use **AWS Signature V4** - the same signing protocol as real AWS S3. Your secret key is never sent over the wire; clients sign each request with an HMAC-based signature that the server verifies.

By default, the S3 endpoint reuses your `BasicAuthUser` / `BasicAuthPass` as access key / secret key. Set `S3AccessKeyId` and `S3SecretAccessKey` for dedicated S3 credentials.

Credentials are required - the app will not start with `EnableS3Endpoint=true` and no credentials configured.

### Client Examples

**aws cli:**
```bash
export AWS_ACCESS_KEY_ID=mykey
export AWS_SECRET_ACCESS_KEY=mysecret
aws --endpoint-url http://localhost:8091/s3 s3 ls
aws --endpoint-url http://localhost:8091/s3 s3 ls s3://dirforge/
aws --endpoint-url http://localhost:8091/s3 s3 ls s3://dirforge/subdir/
aws --endpoint-url http://localhost:8091/s3 s3 cp s3://dirforge/file.txt .
```

**rclone:**
```bash
rclone config create myremote s3 \
  provider=Other \
  endpoint=http://localhost:8091/s3 \
  access_key_id=mykey \
  secret_access_key=mysecret \
  region=us-east-1

rclone ls myremote:dirforge
rclone copy myremote:dirforge/file.txt .
```

### Security

S3 requests bypass Basic Auth (they use SigV4 instead) but enforce all the same file-level policies:
- Hidden paths (`HideDotfiles`, `HidePathPatterns`) are not visible
- Denied extensions (`DenyDownloadExtensions`) return `403 Access Denied`
- Path traversal and symlink containment checks apply

Set `EnableS3Endpoint=false` (default) to disable the endpoint entirely.

## Integrations API

For homelab dashboards (Homarr, Homepage, etc.), DirForge exposes:

- `GET /api/stats`

The endpoint returns compact JSON with 10 basic fields:
`generatedAtUtc`, `ready`, `uptimeSeconds`, `totalRequests`, `inFlightRequests`, `requestsPerMinute`, `averageLatencyMs`, `totalDownloadTrafficBytes`, `fileCount`, `zipCount`.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Development
```bash
dotnet restore src/DirForge/DirForge.csproj
dotnet build src/DirForge/DirForge.csproj -c Release --no-restore
docker build -t dirforge:dev .    # or: podman build -t dirforge:dev .
```

## Image Tags

Images are published to `ghcr.io/dissimilis/dirforge`.

| Tag | When pushed | Use for |
|---|---|---|
| `latest` | Every non-pre-release GitHub Release | Stable production use |
| `1.2.0` | Every GitHub Release | Pinned stable version |
| `dev` | Every push to `main` | Latest development build |
| `dev-<sha>` | Every push to `main` | Pinned to a specific commit |

## License
MIT. See `LICENSE`.

## Third-Party Attribution
- File icon vector set in `src/DirForge/wwwroot/file-icon-vectors/` is attributed to [dmhendricks](https://github.com/dmhendricks).
