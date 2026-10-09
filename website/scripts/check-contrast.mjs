#!/usr/bin/env node
// WCAG AA contrast check for the site palette (website/DESIGN.md § Color & Theme).
// Not wired into the build — run via `node scripts/check-contrast.mjs` after any token change.
// Exits non-zero if any pair fails. Keep the hex values in sync with src/styles/custom.css.

function hexToRgb(hex) {
  const h = hex.replace('#', '');
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
}

function srgbToLinear(c) {
  const s = c / 255;
  return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
}

function luminance(hex) {
  const [r, g, b] = hexToRgb(hex).map(srgbToLinear);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(fg, bg) {
  const [a, b] = [luminance(fg), luminance(bg)].sort((x, y) => y - x);
  return (a + 0.05) / (b + 0.05);
}

const themes = {
  dark: {
    bg: '#1b1818',
    surface2: '#221f1f',
    text: '#fafafa',
    textMuted: '#aba9a9',
    textFaint: '#878484',
    accent: '#8aa4ff',
    chip: '#2652e1',
    chipAlt: '#9b3ed6',
  },
  light: {
    bg: '#ffffff',
    surface2: '#f7f6f6',
    text: '#1b1818',
    textMuted: '#5b5757',
    textFaint: '#757171',
    accent: '#2652e1',
    chip: '#2652e1',
    chipAlt: '#8a2fc4',
  },
};

// [fg, bg, minimum] — 4.5 for body-size text, 3.0 for large text / UI chrome.
const pairs = (t) => [
  ['--fl-text', t.text, '--fl-bg', t.bg, 4.5],
  ['--fl-text-muted', t.textMuted, '--fl-bg', t.bg, 4.5],
  ['--fl-text-muted', t.textMuted, '--fl-surface-2', t.surface2, 4.5],
  ['--fl-text-faint', t.textFaint, '--fl-bg', t.bg, 4.5],
  ['--fl-accent', t.accent, '--fl-bg', t.bg, 4.5],
  ['--fl-accent', t.accent, '--fl-surface-2', t.surface2, 4.5],
  ['#fff (chip/button text)', '#ffffff', '--fl-chip-bg', t.chip, 4.5],
  ['#fff (chip text)', '#ffffff', '--fl-chip-alt-bg', t.chipAlt, 4.5],
];

let failed = false;
for (const [name, t] of Object.entries(themes)) {
  console.log(`\n--- ${name} ---`);
  for (const [fgName, fg, bgName, bg, min] of pairs(t)) {
    const ratio = contrast(fg, bg);
    const ok = ratio >= min;
    if (!ok) failed = true;
    console.log(
      `pair: ${fgName} (${fg}) on ${bgName} (${bg}) = ${ratio.toFixed(2)}:1 (${ok ? 'AA pass' : 'FAIL'})`,
    );
  }
}
process.exit(failed ? 1 : 0);
