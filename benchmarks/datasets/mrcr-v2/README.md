# MRCR v2 (8-needle) dataset

The MRCR v2 benchmark is **not checked into this repo** — the dataset is large and its
license is held by Google/DeepMind. Download it locally before running
`run_mrcr_benchmark`.

## Download

The benchmark expects a single JSONL file at
`benchmarks/datasets/mrcr-v2/mrcr_v2_8needle.jsonl` (or the path set via the
`MRCR_DATASET_PATH` env var).

```bash
# 1. Fetch from Hugging Face
huggingface-cli download google/mrcr-v2 --repo-type dataset \
  --local-dir benchmarks/datasets/mrcr-v2/raw

# 2. Convert to the shape expected by the runner (one task per JSONL line)
#    python tools/mrcr_convert.py \
#      --input benchmarks/datasets/mrcr-v2/raw \
#      --output benchmarks/datasets/mrcr-v2/mrcr_v2_8needle.jsonl
```

## Expected JSONL shape

One JSON object per line:

```json
{
  "taskId": "mrcr-v2-00001",
  "contextTokens": 32768,
  "bucket": "32k-64k",
  "turns": [
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": "..." }
  ],
  "probe": "What was the password mentioned earlier?",
  "goldAnswer": "hunter2",
  "needleIndex": 3
}
```

Buckets follow the llm-stats convention: `4k-8k`, `32k-64k`, `up-to-128k`.

## Paper & reference

- Paper: [arXiv:2409.12640](https://arxiv.org/abs/2409.12640)
- Leaderboard: https://llm-stats.com/benchmarks/mrcr-v2-(8-needle)
- Published Opus 4.6 score: 0.930 mean similarity
