# graph-gif

Headless capture pipeline for the README hero, `images/memory-graph.gif`.

It drives the real visualizer (`visualization/memory-graph.html`) in headless
Chromium and encodes a looping GIF with `gifenc` (pure JS — no system ffmpeg).
The animation tells a three-beat story:

1. **Clustering** — the force simulation settles nodes into clusters
2. **Consolidation** — staged STM (amber) nodes promote to LTM (blue)
3. **Retrieval** — a query lights up matching memories while others dim

## Usage

```sh
npm install                 # playwright, gifenc, pngjs (one-time)
npx playwright install chromium
node prep-snapshot.js       # curate hero-snapshot.json from visualization/snapshot.json
node capture.js             # render + encode -> images/memory-graph.gif
```

## Files

| File | Role |
|------|------|
| `prep-snapshot.js` | Extracts a legible connected subset of the real snapshot and stages a handful of nodes as STM so the consolidation beat is visible. Output: `hero-snapshot.json`. |
| `capture.js` | Loads the visualizer with the curated snapshot, choreographs the three beats while sampling PNG frames, encodes the GIF. |
| `hero-snapshot.json` | Generated curated subset (checked in so the GIF is reproducible without the full 1.5 MB snapshot). |

Regenerate whenever the visualizer layout or color palette changes.
