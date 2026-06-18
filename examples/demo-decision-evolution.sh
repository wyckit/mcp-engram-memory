#!/usr/bin/env bash
# =============================================================================
# Engram Memory — "Decision Evolution" Killer Demo Driver (bash)
# =============================================================================
#
# WHAT THIS IS (read this — it does NOT fake execution):
#
#   This script does NOT call the MCP server over the wire itself. The engram
#   server speaks MCP (JSON-RPC over stdio) and is meant to be driven by an MCP
#   client — Claude Code, an IDE assistant, or any agent that has the
#   `engram-memory` server configured. Tool calls are issued BY THAT AGENT.
#
#   So this driver does two practical things:
#     1. Prints a copy-pasteable AGENT PROMPT SEQUENCE — paste each block into a
#        session connected to the engram server, in order, and the agent will
#        make the real tool calls.
#     2. Prints the exact JSON arguments for every tool call, so you can verify
#        what each step sends (and reuse them in a custom MCP client if you have
#        one).
#
#   Every tool name below is a REAL MCP tool in this repo (verified against
#   src/McpEngramMemory/Tools/*.cs). See docs/demo-decision-evolution.md for the
#   full walkthrough and expected result shapes.
#
# PREREQUISITES:
#   - Engram server configured in your MCP client with MEMORY_TOOL_PROFILE=full
#     (the graph / lifecycle / intelligence / synthesis tools used here live in
#     the standard + full profiles; minimal only has remember/recall/stats).
#   - Step 7 (synthesize_memories) also wants a local Ollama or the in-process
#     ONNX synthesis backend; it degrades gracefully if neither is present.
#
# USAGE:
#   ./examples/demo-decision-evolution.sh            # print prompts + JSON
#   ./examples/demo-decision-evolution.sh --json-only  # print only JSON payloads
# =============================================================================

set -euo pipefail

JSON_ONLY=0
NS="demo-decision-evolution"
for arg in "$@"; do
  case "$arg" in
    --json-only) JSON_ONLY=1 ;;
    --ns=*) NS="${arg#--ns=}" ;;
  esac
done

step() {
  local number="$1" title="$2" prompt="$3" json="$4"
  if [ "$JSON_ONLY" -eq 0 ]; then
    printf '\n===========================================================================\n'
    printf 'STEP %s — %s\n' "$number" "$title"
    printf '===========================================================================\n\n'
    printf 'PASTE THIS INTO YOUR AGENT SESSION:\n%s\n\n' "$prompt"
    printf 'Underlying MCP tool call:\n'
  fi
  printf '%s\n' "$json"
}

if [ "$JSON_ONLY" -eq 0 ]; then
  printf "Engram 'Decision Evolution' demo — agent prompt sequence\n"
  printf 'Namespace: %s   Profile required: full\n' "$NS"
  printf 'Paste each step'\''s prompt into an MCP-connected agent session, in order.\n'
fi

# --- Step 1: store early intent (remember) ----------------------------------
step 1 "Store the early project intent (remember)" \
"Use the 'remember' tool to store our original persistence decision.
id: persistence-json-files
ns: $NS
category: decision
text: \"Decision: persist application data as JSON files on disk. Rationale: simplest possible approach, human-readable, zero external dependencies, easy to diff in git. We expect low write volume.\"" \
'{
  "tool": "remember",
  "arguments": {
    "id": "persistence-json-files",
    "ns": "'"$NS"'",
    "text": "Decision: persist application data as JSON files on disk. Rationale: simplest possible approach, human-readable, zero external dependencies, easy to diff in git. We expect low write volume.",
    "category": "decision",
    "lifecycleState": "stm"
  }
}'

# --- Step 2: store contradicting pivot (remember) ---------------------------
step 2 "Store the contradicting design pivot (remember)" \
"Use 'remember' to store the design pivot that reverses the earlier decision.
id: persistence-sqlite-wal
ns: $NS
category: decision
text: \"Decision REVERSED: stop using JSON files for persistence; switch to SQLite with WAL mode. Reason: under parallel load, concurrent writers corrupted the JSON files and caused write contention. SQLite gives us atomic transactions and safe concurrent access. This supersedes the earlier JSON-files decision.\"
Report the 'actions' it returns (expect an auto-link to the related memory)." \
'{
  "tool": "remember",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "ns": "'"$NS"'",
    "text": "Decision REVERSED: stop using JSON files for persistence; switch to SQLite with WAL mode. Reason: under parallel load, concurrent writers corrupted the JSON files and caused write contention. SQLite gives us atomic transactions and safe concurrent access. This supersedes the earlier JSON-files decision.",
    "category": "decision",
    "lifecycleState": "stm"
  }
}'

# --- Step 3: explicit contradicts edge (link_memories) + verify -------------
step 3 "Make the relationship explicit (link_memories)" \
"Use 'link_memories' to record that the SQLite decision CONTRADICTS the JSON one,
then use 'get_neighbors' on persistence-sqlite-wal (relation: contradicts) to verify the edge." \
'{
  "tool": "link_memories",
  "arguments": {
    "sourceId": "persistence-sqlite-wal",
    "targetId": "persistence-json-files",
    "relation": "contradicts",
    "weight": 1.0
  }
}'
step 3 "Verify the edge (get_neighbors)" \
"(same step — verification call)" \
'{
  "tool": "get_neighbors",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "relation": "contradicts",
    "direction": "both"
  }
}'

# --- Step 4: reinforce + promote new; suppress + archive old ----------------
step 4 "Reinforce the current decision (memory_feedback +3.0)" \
"Use 'memory_feedback' to reinforce persistence-sqlite-wal with delta 3.0 (it is the active truth).
NOTE: in normal operation the background decay/consolidation workers do this over time as the
memory is recalled; here we drive the same transition explicitly so the demo runs in minutes." \
'{
  "tool": "memory_feedback",
  "arguments": { "id": "persistence-sqlite-wal", "delta": 3.0, "ns": "'"$NS"'" }
}'
step 4 "Promote the current decision to LTM (promote_memory)" \
"Use 'promote_memory' to move persistence-sqlite-wal to targetState 'ltm'." \
'{
  "tool": "promote_memory",
  "arguments": { "id": "persistence-sqlite-wal", "targetState": "ltm" }
}'
step 4 "Suppress the superseded decision (memory_feedback -3.0)" \
"Use 'memory_feedback' to suppress persistence-json-files with delta -3.0 (no longer active)." \
'{
  "tool": "memory_feedback",
  "arguments": { "id": "persistence-json-files", "delta": -3.0, "ns": "'"$NS"'" }
}'
step 4 "Archive the superseded decision (promote_memory)" \
"Use 'promote_memory' to move persistence-json-files to targetState 'archived'
(archived = recoverable, not deleted — that's how 'why did it change?' stays answerable).
Then call 'cognitive_stats' for ns $NS (expect ltmCount 1, archivedCount 1, edgeCount >= 1),
and 'engram_status' to see the decay/consolidation/auto_link background workers." \
'{
  "tool": "promote_memory",
  "arguments": { "id": "persistence-json-files", "targetState": "archived" }
}'
step 4 "Observe state (cognitive_stats)" \
"(same step — observation call)" \
'{
  "tool": "cognitive_stats",
  "arguments": { "ns": "'"$NS"'" }
}'
step 4 "Observe background workers (engram_status)" \
"(same step — observation call)" \
'{
  "tool": "engram_status",
  "arguments": {}
}'

# --- Step 5: graph-aware / spectral recall ----------------------------------
step 5 "Graph-aware / spectral recall pulls both (recall)" \
"Use 'recall' with query 'How do we persist application data and why?' on ns $NS,
k 5, expandGraph true, spectralMode auto. Report which memories come back and their
lifecycleState — you should get the current SQLite (ltm) decision AND the archived JSON one." \
'{
  "tool": "recall",
  "arguments": {
    "query": "How do we persist application data and why?",
    "ns": "'"$NS"'",
    "k": 5,
    "expandGraph": true,
    "spectralMode": "auto"
  }
}'

# --- Step 6: surface the conflict -------------------------------------------
step 6 "Surface the conflict (find_contradictions)" \
"Use 'find_contradictions' on ns $NS with topic 'data persistence storage format'
and similarityThreshold 0.8. Expect one contradiction with source 'graph_edge'." \
'{
  "tool": "find_contradictions",
  "arguments": {
    "ns": "'"$NS"'",
    "topic": "data persistence storage format",
    "similarityThreshold": 0.8
  }
}'

# --- Step 7: synthesis explains current/prior truth + WHY -------------------
step 7 "Synthesis explains current truth, prior truth, and WHY (synthesize_memories)" \
"Use 'synthesize_memories' on ns $NS with this focus query:
'Why did the data persistence decision change over time? What is the current approach,
what was the previous one, and what caused the switch?'
(Requires local Ollama or in-process ONNX synthesis backend; degrades gracefully otherwise —
Steps 5-6 already give you current truth, prior truth, and the connecting contradicts edge.)" \
'{
  "tool": "synthesize_memories",
  "arguments": {
    "ns": "'"$NS"'",
    "query": "Why did the data persistence decision change over time? What is the current approach, what was the previous one, and what caused the switch?",
    "maxEntries": 50
  }
}'

if [ "$JSON_ONLY" -eq 0 ]; then
  printf '\nDone. Full walkthrough + expected result shapes: docs/demo-decision-evolution.md\n'
fi
