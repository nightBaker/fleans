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
 * Post-process SVG string:
 * - Strip bpmn-font marker <text> elements (codepoints U+E800–U+E900)
 * - Strip editor-only nodes (.djs-hit, .djs-outline, .djs-dragger)
 * - Recolor stroke/fill for the target theme
 * - Add viewBox padding
 */
function postProcess(svg, theme) {
  // Strip <text> elements containing bpmn-font codepoints (single char in private use area)
  // These are the small interior type-markers (script icon, user icon, etc.)
  // Match with optional surrounding whitespace
  svg = svg.replace(/<text[^>]*>[\s]*[\uE800-\uE900][\s]*<\/text>/g, '');

  // Strip editor-only overlay elements
  svg = svg.replace(/<[^>]+class="[^"]*djs-hit[^"]*"[^>]*>[\s\S]*?<\/[^>]+>/g, '');
  svg = svg.replace(/<[^>]+class="[^"]*djs-outline[^"]*"[^>]*>[\s\S]*?<\/[^>]+>/g, '');
  svg = svg.replace(/<[^>]+class="[^"]*djs-dragger[^"]*"[^>]*>[\s\S]*?<\/[^>]+>/g, '');
  // Also strip self-closing variants
  svg = svg.replace(/<[^>]+class="[^"]*djs-hit[^"]*"[^>]*\/>/g, '');
  svg = svg.replace(/<[^>]+class="[^"]*djs-outline[^"]*"[^>]*\/>/g, '');

  // Recolor strokes — bpmn-js uses inline style properties, not SVG attributes
  // Default color is rgb(34, 36, 42) for strokes and "white" for fills
  svg = svg.replace(/stroke:\s*rgb\(34,\s*36,\s*42\)/g, `stroke: ${theme.stroke}`);
  svg = svg.replace(/stroke:\s*black/g, `stroke: ${theme.stroke}`);
  svg = svg.replace(/fill:\s*rgb\(34,\s*36,\s*42\)/g, `fill: ${theme.stroke}`);
  // Also handle SVG attribute form as fallback
  svg = svg.replace(/stroke="black"/g, `stroke="${theme.stroke}"`);
  svg = svg.replace(/stroke="#000000"/g, `stroke="${theme.stroke}"`);
  svg = svg.replace(/stroke="#000"/g, `stroke="${theme.stroke}"`);

  // Recolor fills
  svg = svg.replace(/fill:\s*white/g, `fill: ${theme.fill}`);
  svg = svg.replace(/fill="white"/g, `fill="${theme.fill}"`);
  svg = svg.replace(/fill="#ffffff"/g, `fill="${theme.fill}"`);
  svg = svg.replace(/fill="#fff"/g, `fill="${theme.fill}"`);
  svg = svg.replace(/fill="#FFF"/g, `fill="${theme.fill}"`);

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

  // Import BPMN XML and export SVG
  const svgResult = await page.evaluate(async (xml) => {
    const viewer = new BpmnJS({ container: '#canvas' });
    await viewer.importXML(xml);
    const canvas = viewer.get('canvas');
    canvas.zoom('fit-viewport');
    const { svg } = await viewer.saveSVG();
    return svg;
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
