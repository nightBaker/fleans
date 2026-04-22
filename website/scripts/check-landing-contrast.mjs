// Verifies hero text passes WCAG AA contrast in both themes on the splash page.
// Runs as `prebuild` hook. Exits non-zero if any text fails.
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = resolve(__dirname, '..');
const PORT = 4328;
const URL = `http://localhost:${PORT}/fleans/`;

function srgbToLinear(c) {
  const n = c / 255;
  return n <= 0.04045 ? n / 12.92 : Math.pow((n + 0.055) / 1.055, 2.4);
}

function luminance([r, g, b]) {
  return 0.2126 * srgbToLinear(r) + 0.7152 * srgbToLinear(g) + 0.0722 * srgbToLinear(b);
}

function contrastRatio(a, b) {
  const la = luminance(a);
  const lb = luminance(b);
  const [lhi, llo] = la > lb ? [la, lb] : [lb, la];
  return (lhi + 0.05) / (llo + 0.05);
}

function parseRGB(rgbStr) {
  const m = rgbStr.match(/rgb[a]?\((\d+),\s*(\d+),\s*(\d+)/);
  if (!m) throw new Error(`Cannot parse rgb: ${rgbStr}`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

async function waitForServer(url, timeoutMs = 30_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const res = await fetch(url);
      if (res.ok) return;
    } catch { /* keep waiting */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`Astro dev server did not start at ${url}`);
}

async function main() {
  const astro = spawn(
    'npx', ['astro', 'dev', '--port', String(PORT)],
    { cwd: PROJECT_ROOT, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  astro.stdout.on('data', () => {}); // quiet
  astro.stderr.on('data', (b) => process.stderr.write(`[astro] ${b}`));

  let failed = false;
  try {
    await waitForServer(URL);
    const browser = await chromium.launch({ headless: true });

    for (const theme of ['dark', 'light']) {
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      const page = await context.newPage();
      await context.addInitScript(
        (t) => { window.localStorage.setItem('starlight-theme', t); },
        theme,
      );
      await page.goto(URL, { waitUntil: 'networkidle' });
      await page.waitForTimeout(2000);

      const results = await page.evaluate(() => {
        const selectors = [
          { sel: '.hero h1', role: 'h1 (large)' },
          { sel: '.hero .tagline', role: 'tagline (normal)' },
        ];
        return selectors.map(({ sel, role }) => {
          const el = document.querySelector(sel);
          if (!el) return { role, sel, missing: true };
          const cs = window.getComputedStyle(el);
          // The contrast overlay pins hero background to --fleans-surface
          // at high alpha, so approximate the composite with that color.
          const bg = window.getComputedStyle(document.documentElement)
            .getPropertyValue('--fleans-surface').trim();
          return { role, sel, fg: cs.color, bg };
        });
      });

      for (const r of results) {
        if (r.missing) {
          console.error(`[contrast] MISSING element ${r.sel} in theme=${theme}`);
          failed = true;
          continue;
        }
        const fgRgb = parseRGB(r.fg);
        const bgRgb = await page.evaluate((hex) => {
          const div = document.createElement('div');
          div.style.color = hex;
          document.body.appendChild(div);
          const c = window.getComputedStyle(div).color;
          div.remove();
          return c;
        }, r.bg);
        const ratio = contrastRatio(fgRgb, parseRGB(bgRgb));
        const min = r.role.includes('large') ? 3.0 : 4.5;
        const ok = ratio >= min;
        console.log(
          `[contrast] theme=${theme} ${r.role} ratio=${ratio.toFixed(2)} ` +
          `min=${min} ${ok ? 'OK' : 'FAIL'}`,
        );
        if (!ok) failed = true;
      }

      await context.close();
    }

    await browser.close();
  } finally {
    astro.kill('SIGTERM');
  }

  if (failed) {
    console.error('[contrast] One or more checks failed.');
    process.exit(1);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
