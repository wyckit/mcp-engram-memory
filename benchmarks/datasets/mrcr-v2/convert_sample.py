"""
Download a tiny slice of openai/mrcr 8-needle and convert it to the JSONL shape
expected by MrcrBenchmarkRunner. Keeps only the smallest-context rows so the pilot
finishes in minutes, not hours.

Usage:
    python convert_sample.py --out ./mrcr_v2_8needle.jsonl --rows 3
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import pandas as pd
from huggingface_hub import hf_hub_download


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, help="Output JSONL path")
    ap.add_argument("--rows", type=int, default=3, help="Number of rows to convert")
    ap.add_argument(
        "--strategy",
        choices=["smallest", "stratified"],
        default="smallest",
        help="smallest = shortest N contexts; stratified = evenly spaced across context sizes",
    )
    ap.add_argument(
        "--parquet",
        default="8needle/8needle_0.parquet",
        help="HF file path inside openai/mrcr (default: smallest 8-needle shard)",
    )
    args = ap.parse_args()

    local = hf_hub_download(
        repo_id="openai/mrcr",
        filename=args.parquet,
        repo_type="dataset",
    )
    print(f"[convert] downloaded: {local}", file=sys.stderr)

    df = pd.read_parquet(local)
    print(
        f"[convert] parquet rows={len(df)} cols={list(df.columns)}",
        file=sys.stderr,
    )

    # Select rows based on strategy.
    if args.strategy == "smallest":
        df = df.sort_values("n_chars").head(args.rows).reset_index(drop=True)
    else:
        # Stratified: evenly spaced across context sizes so the pilot covers the
        # full range, not just the short tail.
        df_sorted = df.sort_values("n_chars").reset_index(drop=True)
        if len(df_sorted) < args.rows:
            df = df_sorted
        else:
            step = len(df_sorted) / args.rows
            indices = [int(i * step) for i in range(args.rows)]
            df = df_sorted.iloc[indices].reset_index(drop=True)

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    written = 0

    with out.open("w", encoding="utf-8") as f:
        for i, row in df.iterrows():
            # `prompt` is a JSON string of the multi-turn conversation.
            prompt_json = row["prompt"]
            try:
                turns = json.loads(prompt_json)
            except json.JSONDecodeError as e:
                print(f"[convert] row {i}: skipped, prompt not JSON: {e}", file=sys.stderr)
                continue

            if not isinstance(turns, list) or len(turns) < 2:
                print(f"[convert] row {i}: skipped, turns malformed", file=sys.stderr)
                continue

            # The final user turn is the ask ("give me the N-th message that matched ...")
            # — lift that as the probe, leave the rest as conversation context.
            probe_turn = turns[-1]
            context_turns = turns[:-1]

            task = {
                "taskId": f"mrcr-v2-8needle-{i:04d}",
                "contextTokens": int(row["n_chars"] // 4),
                "bucket": f"{int(row['n_chars'] // 1000)}k-chars",
                "turns": [
                    {
                        "role": str(t.get("role", "user")),
                        "content": str(t.get("content", "")),
                    }
                    for t in context_turns
                ],
                "probe": str(probe_turn.get("content", "")),
                "goldAnswer": str(row["answer"]),
                "needleIndex": int(row.get("desired_msg_index", 0)),
            }
            f.write(json.dumps(task, ensure_ascii=False) + "\n")
            written += 1

    print(f"[convert] wrote {written} probes to {out}", file=sys.stderr)
    return 0 if written else 1


if __name__ == "__main__":
    sys.exit(main())
