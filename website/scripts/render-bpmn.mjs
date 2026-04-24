/**
 * render-bpmn.mjs — Renders a BPMN fixture to themed SVG files for the landing page.
 *
 * Prerequisites: npx playwright install chromium (one-time)
 * Usage: node scripts/render-bpmn.mjs
 * Output: public/hero-workflow-light.svg, public/hero-workflow-dark.svg
 */

import { chromium } from 'playwright';
import { readFileSync, writeFileSync } from 'fs';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURE = resolve(__dirname, '../../tests/manual/04-parallel-gateway/fork-join.bpmn');
const OUT_DIR = resolve(__dirname, '../public');

// Theme palettes
const THEMES = {
  light: {
    stroke: '#1a1a2e',
    fill: '#ffffff',
    accent: '#6366f1',
    file: 'hero-workflow-light.svg',
  },
  dark: {
    stroke: '#e2e8f0',
    fill: '#1e293b',
    accent: '#818cf8',
    file: 'hero-workflow-dark.svg',
  },
};

/**
 * Post-process SVG string (theme recoloring + viewBox padding only).
 * Structural cleanup (removing .djs-hit / .djs-outline / .djs-dragger overlays
 * and bpmn-font interior-type markers) runs earlier, inside `page.evaluate`,
 * via `DOMParser` + `querySelectorAll().remove()` + `XMLSerializer` — see
 * main(). Attempting to do it with regex here previously caused #366:
 * `<[^>]+>` absorbed the `/` of self-closing `<rect .../>` tags, so the
 * non-greedy `[\s\S]*?</[^>]+>` trailer consumed unrelated `</g>` closers
 * and produced 21 unclosed groups per file.
 */
function postProcess(svg, theme) {
  // Recolor strokes — bpmn-js uses inline style properties, not SVG attributes
  // The actual rendered color depends on bpmn-js version:
  //   - Some versions emit rgb(34, 36, 42)
  //   - Others emit #1a1a2e (rgb(26, 26, 46))
  // Match all known variants.
  svg = svg.replace(/stroke:\s*rgb\(34,\s*36,\s*42\)/g, `stroke: ${theme.stroke}`);
  svg = svg.replace(/stroke:\s*#1a1a2e/gi, `stroke: ${theme.stroke}`);
  svg = svg.replace(/stroke:\s*black/g, `stroke: ${theme.stroke}`);
  svg = svg.replace(/fill:\s*rgb\(34,\s*36,\s*42\)/g, `fill: ${theme.stroke}`);
  svg = svg.replace(/fill:\s*#1a1a2e/gi, `fill: ${theme.stroke}`);
  // Also handle SVG attribute form as fallback
  svg = svg.replace(/stroke="#1a1a2e"/gi, `stroke="${theme.stroke}"`);
  svg = svg.replace(/stroke="black"/g, `stroke="${theme.stroke}"`);
  svg = svg.replace(/stroke="#000000"/g, `stroke="${theme.stroke}"`);
  svg = svg.replace(/stroke="#000"/g, `stroke="${theme.stroke}"`);

  // Recolor fills
  svg = svg.replace(/fill:\s*#ffffff/gi, `fill: ${theme.fill}`);
  svg = svg.replace(/fill:\s*white/g, `fill: ${theme.fill}`);
  svg = svg.replace(/fill="white"/g, `fill="${theme.fill}"`);
  svg = svg.replace(/fill="#ffffff"/gi, `fill="${theme.fill}"`);
  svg = svg.replace(/fill="#fff"/gi, `fill="${theme.fill}"`);

  // Add padding to viewBox
  const vbMatch = svg.match(/viewBox="([^"]+)"/);
  if (vbMatch) {
    const [x, y, w, h] = vbMatch[1].split(/\s+/).map(Number);
    const pad = 20;
    svg = svg.replace(
      `viewBox="${vbMatch[1]}"`,
      `viewBox="${x - pad} ${y - pad} ${w + pad * 2} ${h + pad * 2}"`
    );
  }

  return svg;
}

async function main() {
  const bpmnXml = readFileSync(FIXTURE, 'utf-8');

  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();

  // Build a minimal HTML page that loads bpmn-js Viewer from node_modules
  const viewerJs = readFileSync(
    resolve(__dirname, '../node_modules/bpmn-js/dist/bpmn-viewer.development.js'),
    'utf-8'
  );
  const diagramCss = readFileSync(
    resolve(__dirname, '../node_modules/bpmn-js/dist/assets/diagram-js.css'),
    'utf-8'
  );
  const bpmnCss = readFileSync(
    resolve(__dirname, '../node_modules/bpmn-js/dist/assets/bpmn-js.css'),
    'utf-8'
  );
  const fontCss = readFileSync(
    resolve(__dirname, '../node_modules/bpmn-js/dist/assets/bpmn-font/css/bpmn.css'),
    'utf-8'
  );

  const html = `<!DOCTYPE html>
<html>
<head>
  <style>${diagramCss}</style>
  <style>${bpmnCss}</style>
  <style>${fontCss}</style>
</head>
<body>
  <div id="canvas" style="width:1200px;height:800px;"></div>
  <script>${viewerJs}</script>
</body>
</html>`;

  await page.setContent(html, { waitUntil: 'networkidle' });

  // Pipeline:
  //   1. saveSVG() → serialized string with computed viewBox + the standard
  //      XML prolog / bpmn-js comment / SVG 1.1 DOCTYPE header.
  //   2. DOMParser → real DOM so we can clean structurally.
  //   3. querySelectorAll().remove() → strip editor-only overlays and bpmn-
  //      font interior-type markers.
  //   4. XMLSerializer → back to string, with the prolog/DOCTYPE re-prepended
  //      (DOMParser keeps them as processing-instruction/comment nodes but
  //      XMLSerializer on the <svg> documentElement emits only the root).
  //
  // Why the round-trip? The live `.djs-container > svg` has `width="100%"`
  // and no viewBox — cloning it directly loses the computed drawing bounds
  // that `saveSVG()` derives from canvas.viewbox(). And raw regex cleanup on
  // saveSVG's string output mis-handled self-closing `<rect class="djs-hit"
  // .../>` tags — that is #366 itself.
  const svgResult = await page.evaluate(async (xml) => {
    const viewer = new BpmnJS({ container: '#canvas' });
    await viewer.importXML(xml);
    const canvas = viewer.get('canvas');
    canvas.zoom('fit-viewport');

    const { svg: rawSvg } = await viewer.saveSVG();

    const doc = new DOMParser().parseFromString(rawSvg, 'image/svg+xml');
    const svgEl = doc.documentElement;

    // djs-hit / djs-outline / djs-dragger are editor-only interaction overlays
    // (invisible hit targets, selection outlines). bpmn-js 17+ emits them as
    // self-closing <rect ...>.
    svgEl.querySelectorAll('.djs-hit, .djs-outline, .djs-dragger').forEach(el => el.remove());

    // Interior type-marker glyphs (script/user/service icons) live in the
    // bpmn-font Unicode Private Use Area A (U+E800–U+E900). Strip them since
    // the static docs site doesn't load the font.
    svgEl.querySelectorAll('text').forEach(el => {
      const t = (el.textContent || '').trim();
      if (t.length === 1) {
        const c = t.charCodeAt(0);
        if (c >= 0xE800 && c <= 0xE900) el.remove();
      }
    });

    const body = new XMLSerializer().serializeToString(svgEl);
    const prolog =
      '<?xml version="1.0" encoding="utf-8"?>\n' +
      '<!-- created with bpmn-js / http://bpmn.io -->\n' +
      '<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">\n';
    return prolog + body;
  }, bpmnXml);

  await browser.close();

  // Generate themed variants
  for (const [name, theme] of Object.entries(THEMES)) {
    const processed = postProcess(svgResult, theme);
    const outPath = resolve(OUT_DIR, theme.file);
    writeFileSync(outPath, processed, 'utf-8');
    console.log(`  ${name}: ${outPath} (${(Buffer.byteLength(processed) / 1024).toFixed(1)} KB)`);
  }

  console.log('\nGenerated hero-workflow-{light,dark}.svg — please visually inspect before committing.');
}

main().catch((err) => {
  console.error('render-bpmn failed:', err);
  process.exit(1);
});
