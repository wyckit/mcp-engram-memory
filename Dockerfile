# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

# Copy project files first for layer caching
COPY src/McpEngramMemory/McpEngramMemory.csproj src/McpEngramMemory/
COPY src/McpEngramMemory.Core/McpEngramMemory.Core.csproj src/McpEngramMemory.Core/
COPY src/McpEngramMemory.Core/build/ src/McpEngramMemory.Core/build/

# Resolve target Linux RID so the image does not bundle ~100 MB of Windows/macOS native runtimes
RUN ARCH="${TARGETARCH:-$(uname -m)}" && \
    case "$ARCH" in \
        arm64|aarch64) RID="linux-arm64" ;; \
        *) RID="linux-x64" ;; \
    esac && \
    echo "$RID" > /tmp/target-rid && \
    dotnet restore src/McpEngramMemory/McpEngramMemory.csproj -r "$RID" -p:TargetFrameworks=net8.0

# Copy remaining source code
COPY src/ src/

# Build and publish in Release mode for the target Linux RID (framework-dependent)
RUN RID="$(cat /tmp/target-rid)" && \
    dotnet publish src/McpEngramMemory/McpEngramMemory.csproj \
        -c Release -f net8.0 -r "$RID" --self-contained false \
        -p:TargetFrameworks=net8.0 \
        -o /app/publish \
        --no-restore

# Copy benchmark baseline files needed by run_benchmark tool
COPY benchmarks/*.json /app/publish/benchmarks/

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

LABEL maintainer="wyckit"
LABEL org.opencontainers.image.source="https://github.com/wyckit/mcp-engram-memory"
LABEL org.opencontainers.image.description="Cognitive engram memory engine with semantic search, knowledge graphs, clustering, and lifecycle management"
LABEL org.opencontainers.image.licenses="MIT"

# Disable .NET diagnostics for container performance
ENV DOTNET_EnableDiagnostics=0
ENV ORT_DISABLE_TELEMETRY=1

# Default storage backend
ENV MEMORY_STORAGE=json
ENV MEMORY_DATA_PATH=/app/data

WORKDIR /app

# Copy published output from build stage
COPY --from=build /app/publish .

# Create data directory owned by .NET 8's built-in non-root 'app' user
RUN mkdir -p /app/data && chown -R app:app /app/data
VOLUME /app/data

USER app

ENTRYPOINT ["dotnet", "McpEngramMemory.dll"]
