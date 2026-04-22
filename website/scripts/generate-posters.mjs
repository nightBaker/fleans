// Renders silo-poster-{dark,light}.webp from the live splash page.
// Usage: npm run posters (runs from /website)
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';
import { statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = resolve(__dirname, '..');
const PORT = 4327;
const URL = `http://localhost:${PORT}/fleans/`;
const OUT_DIR = resolve(PROJECT_ROOT, 'public');
const MAX_BYTES = 180 * 1024;

async function waitForServer(url, timeoutMs = 30_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const res = await fetch(url);
      if (res.ok) return;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`Astro dev server did not start at ${url} within ${timeoutMs}ms`);
}

async function main() {
  const astro = spawn(
    'npx', ['astro', 'dev', '--port', String(PORT)],
    { cwd: PROJECT_ROOT, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  astro.stdout.on('data', (buf) => process.stdout.write(`[astro] ${buf}`));
  astro.stderr.on('data', (buf) => process.stderr.write(`[astro] ${buf}`));

  try {
    await waitForServer(URL);
    const browser = await chromium.launch({ headless: true });

    // Warm-up pass in a throwaway context: first navigation triggers Vite
    // to optimize Three.js (which causes a reload). We want every real
    // screenshot to hit a warm cache.
    {
      const warm = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
      const warmPage = await warm.newPage();
      await warmPage.goto(URL, { waitUntil: 'networkidle' });
      await warmPage.waitForTimeout(8000);
      await warm.close();
    }

    for (const theme of ['dark', 'light']) {
      const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
      const page = await context.newPage();
      // Seed localStorage before page load so Starlight's inline
      // ThemeProvider script picks up the right theme on first paint.
      await context.addInitScript(
        (t) => { window.localStorage.setItem('starlight-theme', t); },
        theme,
      );

      await page.goto(URL, { waitUntil: 'networkidle' });
      // Wait long enough for both (a) Vite HMR to finish and (b) any
      // Starlight hero stagger animations to fully settle.
      await page.waitForTimeout(8000);

      const buffer = await page.screenshot({ type: 'png', fullPage: false });
      const { default: sharp } = await import('sharp');
      const webp = await sharp(buffer).webp({ quality: 80 }).toBuffer();
      const outPath = resolve(OUT_DIR, `silo-poster-${theme}.webp`);
      const { writeFile } = await import('node:fs/promises');
      await writeFile(outPath, webp);
      const size = statSync(outPath).size;
      console.log(`Wrote ${outPath} (${(size / 1024).toFixed(1)} KB)`);
      if (size > MAX_BYTES) {
        throw new Error(`${outPath} exceeds ${MAX_BYTES} bytes (${size})`);
      }

      await context.close();
    }

    await browser.close();
  } finally {
    astro.kill('SIGTERM');
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
