#!/usr/bin/env bash
# Engram Memory source installer (macOS/Linux): Bash and .NET SDK 8+.
set -euo pipefail
INSTALL_DIR=""
PROFILE=""
STORAGE=""
AGENT_ID=""
FOR_TOOLS="claude"
CONFIG_ROOT=""
DATA_PATH=""
LEGACY_DATA=""
SERVER_STOPPED=0
SKIP_CONFIG=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --server-stopped) SERVER_STOPPED=1; shift ;;
        --skip-config) SKIP_CONFIG=1; shift ;;
        --install-dir|--profile|--storage|--agent-id|--for|--config-root|--data-path|--legacy-data)
            [[ $# -ge 2 ]] || { printf 'Missing value for %s\n' "$1" >&2; exit 1; }
            case "$1" in
                --install-dir) INSTALL_DIR="$2" ;;
                --profile) PROFILE="$2" ;;
                --storage) STORAGE="$2" ;;
                --agent-id) AGENT_ID="$2" ;;
                --for) FOR_TOOLS="$2" ;;
                --config-root) CONFIG_ROOT="$2" ;;
                --data-path) DATA_PATH="$2" ;;
                --legacy-data) LEGACY_DATA="$2" ;;
            esac
            shift 2 ;;
        *) printf 'Unknown option: %s\n' "$1" >&2; exit 1 ;;
    esac
done
case "$PROFILE" in ""|minimal|standard|full) ;; *) echo 'Unknown tool profile.' >&2; exit 1 ;; esac
case "$STORAGE" in ""|json|sqlite|sqlserver) ;; *) echo 'Unknown storage backend.' >&2; exit 1 ;; esac
command -v dotnet >/dev/null || { echo 'Install .NET SDK 8 or later and retry.' >&2; exit 1; }
DOTNET_VER=$(dotnet --version)
[[ "${DOTNET_VER%%.*}" -ge 8 ]] || { echo '.NET SDK 8 or later is required.' >&2; exit 1; }
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || pwd)"
if [[ -f "$SCRIPT_DIR/McpEngramMemory.slnx" ]]; then
    REPO_ROOT="$SCRIPT_DIR"
else
    [[ -n "$INSTALL_DIR" ]] || INSTALL_DIR="$HOME/mcp-engram-memory"
    if [[ ! -f "$INSTALL_DIR/McpEngramMemory.slnx" ]]; then
        command -v git >/dev/null || { echo 'Git is required to clone the repository.' >&2; exit 1; }
        git clone https://github.com/wyckit/mcp-engram-memory.git "$INSTALL_DIR"
    fi
    REPO_ROOT="$(cd "$INSTALL_DIR" && pwd)"
fi
OUTPUT="$REPO_ROOT/.engram/server"
(cd "$REPO_ROOT" && dotnet publish src/McpEngramMemory/McpEngramMemory.csproj \
    -c Release -f net8.0 -p:TargetFrameworks=net8.0 -o "$OUTPUT" --nologo)
SERVER="$OUTPUT/McpEngramMemory.dll"
REPLACE_DATA=0
[[ -z "$DATA_PATH" ]] || REPLACE_DATA=1
[[ -n "$DATA_PATH" ]] || DATA_PATH=$(dotnet "$SERVER" data-path --plain)
PREPARE=("$SERVER" prepare-data --repo "$REPO_ROOT" --destination "$DATA_PATH")
[[ -z "$LEGACY_DATA" ]] || PREPARE+=(--source "$LEGACY_DATA")
[[ "$SERVER_STOPPED" -eq 0 ]] || PREPARE+=(--server-stopped)
if ! dotnet "${PREPARE[@]}"; then
    echo 'Existing data needs migration. Stop the old server, select --legacy-data if needed, and rerun with --server-stopped. Client configuration was not changed.' >&2
    exit 1
fi
if [[ "$SKIP_CONFIG" -eq 1 ]]; then
    printf 'Build complete. command: dotnet; args: %s\nSet MEMORY_DATA_PATH=%s and MEMORY_TOOL_PROFILE=%s.\n' "$SERVER" "$DATA_PATH" "$PROFILE"
    exit 0
fi
[[ -n "$CONFIG_ROOT" ]] || CONFIG_ROOT="$HOME"
CONFIGURE=("$SERVER" configure-client --root "$CONFIG_ROOT" --for "$FOR_TOOLS" \
    --server "$SERVER" --data-path "$DATA_PATH")
[[ -z "$PROFILE" ]] || CONFIGURE+=(--profile "$PROFILE")
[[ -z "$STORAGE" ]] || CONFIGURE+=(--storage "$STORAGE")
[[ "$REPLACE_DATA" -eq 0 ]] || CONFIGURE+=(--replace-data-path true)
[[ -z "$AGENT_ID" ]] || CONFIGURE+=(--agent-id "$AGENT_ID")
dotnet "${CONFIGURE[@]}"
TOOL_COUNT=63
[[ "$PROFILE" == "minimal"  ]] && TOOL_COUNT=17
[[ "$PROFILE" == "standard" ]] && TOOL_COUNT=39
echo 'Setup complete. Existing profile, storage and path selections are preserved unless explicitly supplied.'
[[ -z "$PROFILE" ]] || printf 'Profile: %s | Tools: %d\n' "$PROFILE" "$TOOL_COUNT"
printf 'Default durable data: %s\n' "$DATA_PATH"
echo 'Restart the configured clients to load Engram. Existing configuration backups are listed above.'
