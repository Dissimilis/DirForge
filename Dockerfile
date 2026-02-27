FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG TARGETARCH
ARG VERSION=""
ARG COMMIT_SHA=""
ARG BUILD_DATE=""

COPY src/DirForge/DirForge.csproj src/DirForge/
RUN dotnet restore src/DirForge/DirForge.csproj

COPY . .
RUN case "$TARGETARCH" in \
      amd64) RID="linux-musl-x64" ;; \
      arm64) RID="linux-musl-arm64" ;; \
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

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
WORKDIR /app

LABEL org.opencontainers.image.title="DirForge" \
      org.opencontainers.image.description="Simple directory listing application for containerized deployments." \
      org.opencontainers.image.url="https://github.com/Dissimilis/DirForge" \
      org.opencontainers.image.documentation="https://github.com/Dissimilis/DirForge#readme" \
      org.opencontainers.image.source="https://github.com/Dissimilis/DirForge" \
      org.opencontainers.image.vendor="Dissimilis" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT_SHA}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      net.unraid.docker.webui="http://[IP]:[PORT:8080]/" \
      net.unraid.docker.icon="https://raw.githubusercontent.com/Dissimilis/DirForge/main/logo.png" \
      homepage.group="Files" \
      homepage.name="DirForge" \
      homepage.icon="https://raw.githubusercontent.com/Dissimilis/DirForge/main/logo.png" \
      homepage.description="Directory listing app" \
      com.centurylinklabs.watchtower.enable="true" \
      dev.dozzle.name="DirForge" \
      dev.dozzle.description="Directory listing app"

COPY --from=build /app/publish/ ./

ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["./DirForge"]
