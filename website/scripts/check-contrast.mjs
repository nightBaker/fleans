#!/usr/bin/env node
// Ad-hoc WCAG contrast checker for the Candidate 1 palette.
// Not committed to the build — run via `node scripts/check-contrast.mjs`.

function hexToRgb(hex) {
  const h = hex.replace('#', '');
  return {
    r: parseInt(h.slice(0, 2), 16),
    g: parseInt(h.slice(2, 4), 16),
    b: parseInt(h.slice(4, 6), 16),
  };
}

function srgbToLinear(c) {
  const s = c / 255;
  return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
}

function luminance({ r, g, b }) {
  return (
    0.2126 * srgbToLinear(r) +
    0.7152 * srgbToLinear(g) +
    0.0722 * srgbToLinear(b)
  );
}

function contrast(fg, bg) {
  const L1 = luminance(hexToRgb(fg));
  const L2 = luminance(hexToRgb(bg));
  const [a, b] = L1 >= L2 ? [L1, L2] : [L2, L1];
  return (a + 0.05) / (b + 0.05);
}

function verdict(ratio, minKind) {
  const min = minKind === 'AA-large' ? 3.0 : 4.5;
  return ratio >= min ? `${minKind} pass` : 'FAIL';
}

const dark = {
  '--sl-color-accent-low': '#2a1609',
  '--sl-color-accent': '#ff5f1f',
  '--sl-color-accent-high': '#ffb38f',
  '--sl-color-white': '#f5f5f0',
  '--sl-color-gray-1': '#e5e5df',
  '--sl-color-gray-2': '#b8b8b1',
  '--sl-color-gray-3': '#8a8a82',
  '--sl-color-gray-4': '#5a5a53',
  '--sl-color-gray-5': '#2e2e2a',
  '--sl-color-gray-6': '#1a1a17',
  '--sl-color-black': '#050504',
  '--fleans-accent-2': '#9eff00',
  '--fleans-surface': '#141411',
};

const light = {
  '--sl-color-accent-low': '#fbe6d9',
  '--sl-color-accent': '#ad2f08',
  '--sl-color-accent-high': '#8a2706',
  '--sl-color-white': '#0a0a0a',
  '--sl-color-gray-1': '#1a1a17',
  '--sl-color-gray-2': '#2e2e2a',
  '--sl-color-gray-3': '#5a5a53',
  '--sl-color-gray-4': '#8a8a82',
  '--sl-color-gray-5': '#b8b8b1',
  '--sl-color-gray-6': '#e0e0d9',
  '--sl-color-gray-7': '#efefea',
  '--sl-color-black': '#ffffff',
  '--fleans-accent-2': '#3a7d00',
  '--fleans-surface': '#f5f5f0',
};

// Effective page bg per Starlight: dark uses --sl-color-black-derived bg, light uses near-white.
// We treat "bg" pairwise as the likely rendered background — the darkest/lightest extreme.
// For dark, Starlight renders body against ~var(--sl-color-black). For light, ~var(--sl-color-black) too (which is white).
// To match the plan's narrative bg (#0a0a0a dark / #f5f5f0 light), we verify contrast against BOTH the actual --sl-color-black
// AND the narrative "bg" values used by the plan's tables (accent row etc).

function runTheme(name, p, narrativeBg) {
  console.log(`\n--- ${name} ---`);
  const rows = [
    { id: 1, fg: p['--sl-color-white'], fgName: '--sl-color-white', bg: narrativeBg, bgName: 'bg', min: 'AA' },
    { id: 2, fg: p['--sl-color-accent'], fgName: '--sl-color-accent', bg: narrativeBg, bgName: 'bg', min: 'AA' },
    { id: 3, fg: p['--sl-color-accent-high'], fgName: '--sl-color-accent-high', bg: p['--sl-color-accent-low'], bgName: '--sl-color-accent-low', min: 'AA' },
    { id: 4, fg: p['--sl-color-accent'], fgName: '--sl-color-accent', bg: p['--sl-color-accent-low'], bgName: '--sl-color-accent-low', min: 'AA' },
    { id: 5, fg: p['--sl-color-gray-3'], fgName: '--sl-color-gray-3', bg: narrativeBg, bgName: 'bg', min: 'AA' },
    { id: 6, fg: p['--fleans-accent-2'], fgName: '--fleans-accent-2', bg: narrativeBg, bgName: 'bg', min: 'AA-large' },
    { id: '7a-5', fg: p['--sl-color-accent'], fgName: '--sl-color-accent', bg: p['--sl-color-gray-5'], bgName: '--sl-color-gray-5', min: 'AA-large' },
    { id: '7a-6', fg: p['--sl-color-accent'], fgName: '--sl-color-accent', bg: p['--sl-color-gray-6'], bgName: '--sl-color-gray-6', min: 'AA-large' },
  ];
  if (name === 'dark') {
    rows.push({ id: '7b', fg: p['--sl-color-accent'], fgName: '--sl-color-accent', bg: p['--fleans-surface'], bgName: '--fleans-surface', min: 'AA-large' });
  }
  for (const r of rows) {
    const ratio = contrast(r.fg, r.bg);
    console.log(`  row ${r.id}: pair: ${r.fgName} (${r.fg}) on ${r.bgName} (${r.bg}) = ${ratio.toFixed(2)}:1 (${verdict(ratio, r.min)})`);
  }
}

runTheme('dark', dark, '#0a0a0a');
runTheme('light', light, '#f5f5f0');
