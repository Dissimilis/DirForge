FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG TARGETARCH
ARG VERSION=""
ARG COMMIT_SHA=""

COPY src/DirForge/DirForge.csproj src/DirForge/
RUN dotnet restore src/DirForge/DirForge.csproj

COPY . .
RUN case "$TARGETARCH" in \
      amd64) RID="linux-x64" ;; \
      arm64) RID="linux-arm64" ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && VERSION_FLAG="" \
    && if [ -n "$VERSION" ]; then \
         VERSION_FLAG="-p:Version=$VERSION"; \
       elif [ -n "$COMMIT_SHA" ]; then \
         VERSION_FLAG="-p:InformationalVersion=$(echo "$COMMIT_SHA" | cut -c1-7)"; \
       fi \
    && rm -rf src/DirForge/bin src/DirForge/obj \
    && dotnet publish src/DirForge/DirForge.csproj \
      -c Release \
      -r "$RID" \
      -p:PublishSingleFile=true \
      --self-contained true \
      -p:InvariantGlobalization=true \
      $VERSION_FLAG \
      -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
WORKDIR /app

LABEL org.opencontainers.image.title="DirForge" \
      org.opencontainers.image.description="Simple directory listing application for containerized deployments." \
      org.opencontainers.image.source="https://github.com/Dissimilis/DirForge" \
      org.opencontainers.image.licenses="MIT"

COPY --from=build /app/publish/ ./

ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["./DirForge"]
