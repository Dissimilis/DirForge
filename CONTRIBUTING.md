# Contributing

Thanks for helping improve DirForge.

## Before You Start
- Keep changes small and focused.
- Prefer secure defaults.
- Keep behavior deterministic and environment-driven.

## Local Setup
```bash
dotnet restore src/DirForge/DirForge.csproj
dotnet build src/DirForge/DirForge.csproj -c Release --no-restore
docker build -t dirforge:dev .    # or: podman build -t dirforge:dev .
```

Optional smoke test:
```bash
docker run --rm -p 8091:8080 -e RootPath=/data -v /srv/share:/data:ro dirforge:dev  # or: podman run ...
```
Then open `http://localhost:8091` and verify a known file is listed and downloadable.

## Pull Requests
- Explain what changed and why.
- Include manual validation steps.
- Include screenshots only for visible UI changes.
- Mention config impacts (`RootPath`, auth, sharing, hide/deny rules).

## Coding Guidelines
- Keep path handling root-constrained and traversal-safe.
- Preserve stateless behavior.
- Do not introduce writes to mounted data paths.
- Keep HTML output escaped.

## Release Flow
- Merge to `main` to publish the `main` image tag.
- Create `vX.Y.Z` tag to publish versioned tags and `latest`.
