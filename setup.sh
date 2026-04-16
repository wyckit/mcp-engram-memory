#!/usr/bin/env bash
# Engram Memory -- one-click setup for Claude Code (macOS / Linux)
#
# Fresh install -- run from anywhere:
#   curl -fsSL https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.sh | bash
#
# Already cloned:
#   bash setup.sh
#   bash setup.sh --profile standard --storage sqlite

set -euo pipefail

# ── Defaults ──────────────────────────────────────────────────────────────────
INSTALL_DIR=""
PROFILE="minimal"
STORAGE="json"
AGENT_ID=""
SKIP_CONFIG=0

# ── Arg parsing ───────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)  INSTALL_DIR="$2";  shift 2 ;;
        --profile)      PROFILE="$2";      shift 2 ;;
        --storage)      STORAGE="$2";      shift 2 ;;
        --agent-id)     AGENT_ID="$2";     shift 2 ;;
        --skip-config)  SKIP_CONFIG=1;     shift   ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# ── Helpers ───────────────────────────────────────────────────────────────────
step() { printf "\n  \033[36m%s\033[0m\n" "$*"; }
ok()   { printf "  \033[32m[ok]\033[0m %s\n" "$*"; }
warn() { printf "  \033[33m[!] \033[0m %s\n" "$*"; }
die()  { printf "\n  \033[31m[ERROR]\033[0m %s\n\n" "$*" >&2; exit 1; }

printf "\n  \033[1mEngram Memory -- setup\033[0m\n"
printf "  -----------------------------------------\n"

# ── 1. .NET SDK ───────────────────────────────────────────────────────────────
step "Checking .NET SDK..."
command -v dotnet &>/dev/null || die ".NET SDK not found. Install from https://dot.net and re-run."
DOTNET_VER=$(dotnet --version 2>&1 | tr -d '[:space:]')
DOTNET_MAJOR=$(echo "$DOTNET_VER" | cut -d. -f1)
[[ "$DOTNET_MAJOR" -ge 8 ]] || die ".NET SDK $DOTNET_VER found but >= 8.0 required. Install from https://dot.net"
ok ".NET SDK $DOTNET_VER"

# ── 2. Locate or clone ────────────────────────────────────────────────────────
step "Locating repo..."

# BASH_SOURCE[0] works when script is run directly; $0 works when piped to bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || pwd)"
REPO_ROOT=""

if [[ -f "$SCRIPT_DIR/McpEngramMemory.slnx" ]]; then
    REPO_ROOT="$SCRIPT_DIR"
    ok "Using existing repo at $REPO_ROOT"
else
    [[ -z "$INSTALL_DIR" ]] && INSTALL_DIR="$HOME/mcp-engram-memory"

    if [[ -f "$INSTALL_DIR/McpEngramMemory.slnx" ]]; then
        REPO_ROOT="$INSTALL_DIR"
        ok "Using existing clone at $REPO_ROOT"
    else
        step "Cloning into $INSTALL_DIR ..."
        command -v git &>/dev/null || die "git not found. Install git and re-run."
        git clone https://github.com/wyckit/mcp-engram-memory.git "$INSTALL_DIR" --quiet
        REPO_ROOT="$INSTALL_DIR"
        ok "Cloned to $REPO_ROOT"
    fi
fi

# ── 3. Restore (downloads ONNX model) ────────────────────────────────────────
step "Restoring packages...  first run downloads ~5.7 MB ONNX model"
(cd "$REPO_ROOT" && dotnet restore McpEngramMemory.slnx --nologo -v quiet) \
    || die "Restore failed. Run 'dotnet restore' in $REPO_ROOT for details."
ok "Packages restored"

# ── 4. Project path (used in MCP config) ─────────────────────────────────────
PROJECT_PATH="$REPO_ROOT/src/McpEngramMemory"
ok "Project at $PROJECT_PATH"

# ── 5. Patch ~/.claude.json ───────────────────────────────────────────────────
CLAUDE_JSON="$HOME/.claude.json"
TOOL_COUNT=52
[[ "$PROFILE" == "minimal"  ]] && TOOL_COUNT=16
[[ "$PROFILE" == "standard" ]] && TOOL_COUNT=35

if [[ "$SKIP_CONFIG" -eq 0 ]]; then
    step "Patching $CLAUDE_JSON ..."

    # Build the JSON for the env block
    ENV_BLOCK="\"MEMORY_TOOL_PROFILE\": \"$PROFILE\", \"MEMORY_STORAGE\": \"$STORAGE\""
    [[ -n "$AGENT_ID" ]] && ENV_BLOCK="$ENV_BLOCK, \"AGENT_ID\": \"$AGENT_ID\""

    # MCP server entry uses `dotnet run --project` -- no pre-built DLL required,
    # no lock conflicts when the server is already running.
    ENTRY=$(cat <<EOF
{
  "type": "stdio",
  "command": "dotnet",
  "args": ["run", "--project", "$PROJECT_PATH"],
  "env": { $ENV_BLOCK }
}
EOF
)

    if [[ -f "$CLAUDE_JSON" ]]; then
        python3 - "$CLAUDE_JSON" "$ENTRY" <<'PYEOF'
import sys, json

path  = sys.argv[1]
entry = json.loads(sys.argv[2])

with open(path) as f:
    cfg = json.load(f)

cfg.setdefault("mcpServers", {})["engram-memory"] = entry

with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PYEOF
        ok "Updated $CLAUDE_JSON"
    else
        python3 - "$CLAUDE_JSON" "$ENTRY" <<'PYEOF'
import sys, json

path  = sys.argv[1]
entry = json.loads(sys.argv[2])

cfg = {"mcpServers": {"engram-memory": entry}}

with open(path, "w") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PYEOF
        ok "Created $CLAUDE_JSON"
    fi
else
    warn "--skip-config: skipping $CLAUDE_JSON. Add this to your mcpServers block:"
    printf "\n"
    printf '  "engram-memory": {\n'
    printf '    "type": "stdio",\n'
    printf '    "command": "dotnet",\n'
    printf '    "args": ["run", "--project", "%s"],\n' "$PROJECT_PATH"
    printf '    "env": { "MEMORY_TOOL_PROFILE": "%s" }\n' "$PROFILE"
    printf '  }\n\n'
fi

# ── 6. Done ───────────────────────────────────────────────────────────────────
printf "\n  -----------------------------------------\n"
printf "  \033[32mSetup complete!\033[0m\n\n"
printf "  Next steps:\n"
printf "   1. Restart Claude Code (or reload MCP servers)\n"
printf "   2. The 'engram-memory' server will appear in your tools\n"
printf "   3. Copy examples/CLAUDE.md to ~/.claude/CLAUDE.md for full integration\n\n"
printf "  Profile: %s  |  Storage: %s  |  Tools: %d\n" "$PROFILE" "$STORAGE" "$TOOL_COUNT"
printf "  Change profile: set MEMORY_TOOL_PROFILE in %s\n\n" "$CLAUDE_JSON"
