# Security Policy

## Supported Versions
Security fixes are provided for:
- the current `main` image tag,
- the most recent stable release tag.

## Reporting a Vulnerability
If you find a security issue, please [create a GitHub issue](https://github.com/Dissimilis/DirForge/issues/new). If the vulnerability is sensitive, note that in the issue and we will coordinate privately.

Include:
- affected tag or commit,
- clear reproduction steps,
- expected vs actual behavior,
- impact assessment.

## Response Process
- We will acknowledge reports as quickly as possible.
- We will validate, patch, and publish a fix.
- We will credit reporters when appropriate.

## Security Features

### Authentication
- **Basic Auth** — constant-time credential comparison (`CryptographicOperations.FixedTimeEquals`)
- **Bearer Token** — configurable header name, supports `Bearer <token>` prefix and raw token
- **S3 SigV4** — AWS Signature Version 4 validation with ±15 min clock skew tolerance and constant-time signature comparison
- **External Auth** — reverse-proxy header authentication (when `ExternalAuthEnabled=true`)
- **Share Tokens** — HMAC-SHA256 signed tokens (`Base64Url(payload).Base64Url(signature)`) with one-time nonce support

### Rate Limiting
- **Auth-failure lockout** — 5 failed attempts per IP triggers a 15-minute lockout, shared across Basic Auth and Bearer Token
- **Request rate limiting** — configurable per-IP and global fixed-window rate limiting via ASP.NET Core middleware

### Path Safety
- **Root-constrained resolution** — all file paths resolved relative to the configured root with traversal prevention
- **Symlink containment** — symlink targets validated to remain within the root path
- **Archive traversal prevention** — virtual paths within ZIP/TAR/GZ archives validated against path traversal

### Access Controls
- **Hidden dotfiles** — configurable `HideDotfiles` setting
- **Glob-based path hiding** — `HidePathPatterns` for flexible path exclusion
- **Denied download extensions** — `DenyDownloadExtensions` blocks download of specified file types
- **Cross-endpoint enforcement** — all access controls enforced across Razor Pages, WebDAV, S3, JSON API, and MCP endpoints

### Security Headers
- `X-Content-Type-Options: nosniff` on all responses
- `X-Frame-Options: SAMEORIGIN`
- `Cache-Control: no-store` on authentication responses

### Security Event Logging
Structured `[SECURITY]` log entries for:
- authentication failures and lockouts,
- path traversal attempts,
- share token replay (one-time nonce reuse),
- blocked extension access attempts.
