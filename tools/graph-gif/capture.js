// Headless capture of the Engram memory graph -> images/memory-graph.gif.
//
// Drives visualization/memory-graph.html in headless Chromium with the curated
// hero-snapshot.json, choreographs the three-beat story the README hero needs,
// samples frames as PNG, and encodes a looping GIF with gifenc (no system ffmpeg).
//
//   Beat 1  Clustering   force simulation settles nodes into clusters
//   Beat 2  Consolidation  staged STM (amber) nodes promote to LTM (blue)
//   Beat 3  Retrieval      a search pulses matching nodes
//
// Run:  node capture.js   (after node prep-snapshot.js)

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { PNG } = require('pngjs');
const { GIFEncoder, quantize, applyPalette } = require('gifenc');

const HTML = 'file://' + path.resolve(__dirname, '../../visualization/memory-graph.html').replace(/\\/g, '/');
const HERO = path.resolve(__dirname, 'hero-snapshot.json');
const OUT = path.resolve(__dirname, '../../images/memory-graph.gif');

const W = 900, H = 600;        // viewport; canvas is W x (H - header)
const FRAME_MS = 80;           // ~12.5 fps target cadence
const DELAY_CS = 10;           // GIF frame delay (cs); ~matches real capture cadence

const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  const heroData = JSON.parse(fs.readFileSync(HERO, 'utf8'));
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 1 });

  await page.goto(HTML, { waitUntil: 'load' });
  // D3 comes from CDN — wait for it and the page's render() entrypoint.
  await page.waitForFunction(() => !!window.d3 && typeof window.render === 'function', null, { timeout: 30000 });

  // Capture-only styling: the live defaults (edges 0.18, dimmed nodes 0.04) are
  // tuned for an interactive dark room and read as floating dots in a static GIF.
  // Brighten edges and soften the search dim so the network stays legible.
  await page.addStyleTag({ content: '.edge{stroke-opacity:.42 !important} .node.search-dim{opacity:.14 !important}' });

  // Inject the curated snapshot through the page's own render path.
  await page.evaluate(data => { window.snapshot = data; window.render(data); }, heroData);
  await sleep(150);

  const canvas = await page.$('#canvas');
  const box = await canvas.boundingBox();
  const clip = { x: Math.round(box.x), y: Math.round(box.y), width: Math.round(box.width), height: Math.round(box.height) };

  const frames = [];
  const grab = async () => { frames.push(await page.screenshot({ clip, type: 'png' })); };
  const capture = async ms => { const t = Date.now(); while (Date.now() - t < ms) { await grab(); await sleep(FRAME_MS); } };

  // ── Beat 1: clustering (force sim settling), then frame it ───────────────
  // NB: the page's zoomFit() early-returns because its module-scoped `snapshot`
  // is null when we inject via render() directly. Fit ourselves to node centers
  // (search-ring radii are invisible but would otherwise inflate the bbox).
  const FIT = () => {
    const { _svg: svg, _zoom: zoom, _simNodes: ns, d3 } = window;
    const c = document.getElementById('canvas'), W = c.clientWidth, H = c.clientHeight;
    const xs = ns.map(n => n.x), ys = ns.map(n => n.y);
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    const pad = 70;
    const scale = Math.min(W / (maxX - minX + pad * 2), H / (maxY - minY + pad * 2), 4);
    const tx = W / 2 - scale * (minX + maxX) / 2, ty = H / 2 - scale * (minY + maxY) / 2;
    svg.transition().duration(500).call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(scale));
  };
  await capture(1800);
  await page.evaluate(FIT);   // fill the canvas with the graph
  await capture(900);

  // ── Beat 2: consolidation — amber STM nodes promote to blue LTM ──────────
  await page.evaluate(() => {
    const LTM = '#00aaff';
    const sel = window._nodeSel.filter(d => d.lifecycleState === 'stm');
    sel.each(function (d) { d.lifecycleState = 'ltm'; });
    sel.selectAll('circle.core').transition().duration(1100).attr('fill', LTM).attr('stroke', LTM);
    sel.selectAll('circle.halo').transition().duration(1100).attr('fill', LTM);
  });
  await capture(1500);

  // ── Beat 3: retrieval — a query lights up relevant memories across the
  // fitted graph (replicate applySearch's highlight WITHOUT its focus-zoom, so
  // multiple matches pulse at once instead of diving into a single node). ────
  await page.evaluate(() => {
    const q = 'architecture';
    const matchSet = new Set();
    (window._simNodes || []).forEach(n => {
      const hay = [n.id, n.text, n.keywords, n.category, n.ns].filter(Boolean).join(' ').toLowerCase();
      if (hay.includes(q)) matchSet.add(n.id);
    });
    window._nodeSel.classed('search-match', n => matchSet.has(n.id))
                   .classed('search-dim',   n => !matchSet.has(n.id));
    const el = document.getElementById('search-count');
    if (el) { el.textContent = matchSet.size + ' / ' + matchSet.size; el.style.color = '#5a8a5a'; }
    const inp = document.getElementById('search-input');
    if (inp) inp.value = q;
  });
  await capture(1900);

  // brief hold on the final composed frame
  await grab();

  await browser.close();

  // ── Encode GIF ───────────────────────────────────────────────────────────
  const first = PNG.sync.read(frames[0]);
  const gif = GIFEncoder();
  for (const buf of frames) {
    const { data, width, height } = PNG.sync.read(buf);
    const palette = quantize(data, 256, { format: 'rgb444' });
    const indexed = applyPalette(data, palette, 'rgb444');
    gif.writeFrame(indexed, width, height, { palette, delay: DELAY_CS });
  }
  gif.finish();
  const bytes = Buffer.from(gif.bytes());
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, bytes);

  console.log(`frames: ${frames.length}  size: ${first.width}x${first.height}  gif: ${(bytes.length / 1e6).toFixed(2)} MB -> ${OUT}`);
})().catch(e => { console.error(e); process.exit(1); });
