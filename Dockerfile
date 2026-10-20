# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY src/McpEngramMemory/McpEngramMemory.csproj src/McpEngramMemory/
COPY src/McpEngramMemory.Core/McpEngramMemory.Core.csproj src/McpEngramMemory.Core/
COPY src/McpEngramMemory.Core/build/ src/McpEngramMemory.Core/build/

# Restore dependencies (downloads ONNX model via .targets during restore/build)
RUN dotnet restore src/McpEngramMemory/McpEngramMemory.csproj -p:TargetFrameworks=net8.0

# Copy remaining source code
COPY src/ src/

# Build and publish in Release mode
RUN dotnet publish src/McpEngramMemory/McpEngramMemory.csproj \
    -c Release -f net8.0 -p:TargetFrameworks=net8.0 \
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

# Default storage backend
ENV MEMORY_STORAGE=json
ENV MEMORY_DATA_PATH=/app/data

WORKDIR /app

# Copy published output from build stage
COPY --from=build /app/publish .

# Create data directory for persistent storage
RUN mkdir -p /app/data
VOLUME /app/data

ENTRYPOINT ["dotnet", "McpEngramMemory.dll"]
