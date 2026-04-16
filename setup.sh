#!/usr/bin/env bash
# Engram Memory -- one-click setup (macOS / Linux)
#
# Fresh install:
#   curl -fsSL https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.sh | bash
#
# Already cloned:
#   bash setup.sh
#   bash setup.sh --for gemini,codex --profile standard
#   bash setup.sh --for all

set -euo pipefail

# ── Defaults ──────────────────────────────────────────────────────────────────
INSTALL_DIR=""
PROFILE="minimal"
STORAGE="json"
AGENT_ID=""
FOR_TOOLS="claude"
SKIP_CONFIG=0

# ── Arg parsing ───────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)  INSTALL_DIR="$2";  shift 2 ;;
        --profile)      PROFILE="$2";      shift 2 ;;
        --storage)      STORAGE="$2";      shift 2 ;;
        --agent-id)     AGENT_ID="$2";     shift 2 ;;
        --for)          FOR_TOOLS="$2";    shift 2 ;;
        --skip-config)  SKIP_CONFIG=1;     shift   ;;
        *) printf "Unknown option: %s\n" "$1" >&2; exit 1 ;;
    esac
done

# Expand "all" to the full list
[[ "$FOR_TOOLS" == "all" ]] && FOR_TOOLS="claude,gemini,codex"
# Normalise separators (spaces or commas)
FOR_TOOLS="${FOR_TOOLS//,/ }"

# ── Helpers ───────────────────────────────────────────────────────────────────
step() { printf "\n  \033[36m%s\033[0m\n" "$*"; }
ok()   { printf "  \033[32m[ok]\033[0m %s\n" "$*"; }
skip() { printf "  \033[90m[--]\033[0m %s\n" "$*"; }
warn() { printf "  \033[33m[!] \033[0m %s\n" "$*"; }
die()  { printf "\n  \033[31m[ERROR]\033[0m %s\n\n" "$*" >&2; exit 1; }

printf "\n  \033[1mEngram Memory -- setup\033[0m\n"
printf "  -----------------------------------------\n"
printf "  Configuring: %s\n" "$FOR_TOOLS"

# ── 1. .NET SDK ───────────────────────────────────────────────────────────────
step "Checking .NET SDK..."
command -v dotnet &>/dev/null || die ".NET SDK not found. Install from https://dot.net and re-run."
DOTNET_VER=$(dotnet --version 2>&1 | tr -d '[:space:]')
DOTNET_MAJOR=$(echo "$DOTNET_VER" | cut -d. -f1)
[[ "$DOTNET_MAJOR" -ge 8 ]] || die ".NET SDK $DOTNET_VER found but >= 8.0 required. Install from https://dot.net"
ok ".NET SDK $DOTNET_VER"

# ── 2. Locate or clone ────────────────────────────────────────────────────────
step "Locating repo..."

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

# ── 3. Restore ────────────────────────────────────────────────────────────────
step "Restoring packages...  first run downloads ~5.7 MB ONNX model"
(cd "$REPO_ROOT" && dotnet restore McpEngramMemory.slnx --nologo -v quiet) \
    || die "Restore failed. Run 'dotnet restore' in $REPO_ROOT for details."
ok "Packages restored"

# ── 4. Project path ───────────────────────────────────────────────────────────
PROJECT_PATH="$REPO_ROOT/src/McpEngramMemory"
ok "Project at $PROJECT_PATH"

if [[ "$SKIP_CONFIG" -eq 1 ]]; then
    warn "--skip-config: not modifying any config files."
    printf "  Add this to your tool's MCP server config:\n"
    printf "    command: dotnet\n"
    printf "    args:    run --project %s\n" "$PROJECT_PATH"
    printf "    env:     MEMORY_TOOL_PROFILE=%s\n\n" "$PROFILE"
    exit 0
fi

# ── Helpers: build shared JSON entry ─────────────────────────────────────────
build_entry() {
    local ENV_BLOCK="\"MEMORY_TOOL_PROFILE\": \"$PROFILE\", \"MEMORY_STORAGE\": \"$STORAGE\""
    [[ -n "$AGENT_ID" ]] && ENV_BLOCK="$ENV_BLOCK, \"AGENT_ID\": \"$AGENT_ID\""
    cat <<EOF
{
  "type": "stdio",
  "command": "dotnet",
  "args": ["run", "--project", "$PROJECT_PATH"],
  "env": { $ENV_BLOCK }
}
EOF
}

# Patch a JSON file that uses { "mcpServers": { ... } } format (Claude / Gemini)
patch_json() {
    local config_path="$1"
    local label="$2"
    local entry
    entry="$(build_entry)"

    if [[ -f "$config_path" ]]; then
        python3 - "$config_path" "$entry" <<'PYEOF'
import sys, json
path, entry = sys.argv[1], json.loads(sys.argv[2])
with open(path) as f:
    cfg = json.load(f)
cfg.setdefault("mcpServers", {})["engram-memory"] = entry
with open(path, "w") as f:
    json.dump(cfg, f, indent=2); f.write("\n")
PYEOF
        ok "$label: updated $config_path"
    else
        local dir
        dir="$(dirname "$config_path")"
        [[ -d "$dir" ]] || mkdir -p "$dir"
        python3 - "$config_path" "$entry" <<'PYEOF'
import sys, json
path, entry = sys.argv[1], json.loads(sys.argv[2])
cfg = {"mcpServers": {"engram-memory": entry}}
with open(path, "w") as f:
    json.dump(cfg, f, indent=2); f.write("\n")
PYEOF
        ok "$label: created $config_path"
    fi
}

# Patch ~/.codex/config.toml (TOML format)
patch_toml() {
    local config_path="$1"
    local dir
    dir="$(dirname "$config_path")"

    local env_lines="MEMORY_TOOL_PROFILE = \"$PROFILE\"\nMEMORY_STORAGE = \"$STORAGE\""
    [[ -n "$AGENT_ID" ]] && env_lines="$env_lines\nAGENT_ID = \"$AGENT_ID\""

    local new_section
    new_section=$(printf '\n[mcp_servers.engram-memory]\ncommand = "dotnet"\nargs = ["run", "--project", "%s"]\n\n[mcp_servers.engram-memory.env]\n%b\n' \
        "$PROJECT_PATH" "$env_lines")

    if [[ -f "$config_path" ]]; then
        # Remove existing engram-memory sections line-by-line (safe with TOML arrays)
        python3 - "$config_path" <<'PYEOF'
import sys
path = sys.argv[1]
with open(path) as f:
    lines = f.readlines()
in_section = False
result = []
for line in lines:
    t = line.strip()
    if t.startswith('[mcp_servers.engram-memory'):
        in_section = True
    elif t.startswith('[') and not t.startswith('[mcp_servers.engram-memory'):
        in_section = False
    if not in_section:
        result.append(line)
with open(path, "w") as f:
    content = "".join(result).rstrip()
    f.write(content)
PYEOF
        printf '%s' "$new_section" >> "$config_path"
        ok "Codex: updated $config_path"
    else
        [[ -d "$dir" ]] || mkdir -p "$dir"
        printf '%s' "${new_section#$'\n'}" > "$config_path"
        ok "Codex: created $config_path"
    fi
}

# ── 5. Configure each target tool ─────────────────────────────────────────────
step "Patching config files..."

for tool in $FOR_TOOLS; do
    case "$tool" in
        claude) patch_json "$HOME/.claude.json"              "Claude Code" ;;
        gemini) patch_json "$HOME/.gemini/settings.json"     "Gemini CLI"  ;;
        codex)  patch_toml "$HOME/.codex/config.toml"                      ;;
        *)      warn "Unknown tool '$tool' -- skipping. Valid: claude, gemini, codex, all" ;;
    esac
done

# ── 6. Done ───────────────────────────────────────────────────────────────────
TOOL_COUNT=52
[[ "$PROFILE" == "minimal"  ]] && TOOL_COUNT=16
[[ "$PROFILE" == "standard" ]] && TOOL_COUNT=35

printf "\n  -----------------------------------------\n"
printf "  \033[32mSetup complete!\033[0m\n\n"
printf "  Next steps:\n"
printf "   1. Restart configured tools to reload MCP servers\n"
printf "   2. The 'engram-memory' server will appear in your tools\n"
printf "   3. Copy examples/CLAUDE.md  -> ~/.claude/CLAUDE.md  (Claude Code)\n"
printf "      Copy GEMINI.md           -> workspace GEMINI.md  (Gemini CLI)\n"
printf "      Copy examples/AGENTS.md  -> project AGENTS.md    (Codex)\n\n"
printf "  Profile: %s  |  Storage: %s  |  Tools: %d\n\n" "$PROFILE" "$STORAGE" "$TOOL_COUNT"
