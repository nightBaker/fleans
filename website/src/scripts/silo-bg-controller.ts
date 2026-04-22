import type { SceneController, ThemeColors } from './silo-scene';

type Theme = 'dark' | 'light';

function currentTheme(): Theme {
  const t = document.documentElement.dataset.theme;
  return t === 'light' ? 'light' : 'dark';
}

function readThemeColors(): ThemeColors {
  const styles = getComputedStyle(document.documentElement);
  const get = (v: string): string => styles.getPropertyValue(v).trim();
  const isDark = currentTheme() === 'dark';

  return {
    background: get('--fleans-surface'),
    fog: get('--fleans-surface'),
    primarySilo: get('--sl-color-accent'),
    accent: get('--fleans-accent-2'),
    commLine: get('--sl-color-accent-high'),
    ground: get('--sl-color-gray-6'),
    grid: get('--sl-color-gray-5'),
    ambientIntensity: isDark ? 0.6 : 0.9,
    directionalIntensity: isDark ? 1.0 : 0.5,
    bloomStrength: isDark ? 0.9 : 0.15,
    bloomThreshold: isDark ? 0.8 : 0.95,
    emissiveIntensity: isDark ? 0.4 : 0.05,
  };
}

function supportsWebGL2(): boolean {
  try {
    const c = document.createElement('canvas');
    return !!c.getContext('webgl2');
  } catch {
    return false;
  }
}

function shouldUseLiveScene(): boolean {
  if (typeof window === 'undefined') return false;
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return false;
  if (window.matchMedia('(max-width: 767px)').matches) return false;
  if (!supportsWebGL2()) return false;
  return true;
}

const INTERACTIVE_CLASS = 'silo-interactive';

export function mountSiloBackground(): void {
  if (typeof window === 'undefined') return;

  const canvas = document.getElementById('silo-bg-canvas') as HTMLCanvasElement | null;
  const poster = document.getElementById('silo-bg-poster') as HTMLElement | null;
  const closeBtn = document.getElementById('silo-close-btn') as HTMLButtonElement | null;
  if (!canvas || !poster || !closeBtn) return;

  const liveMode = shouldUseLiveScene();

  // --- Fallback path: poster only ---
  if (!liveMode) {
    canvas.hidden = true;
    poster.hidden = false;
    const img = poster.querySelector('img');
    if (!img) return;

    const syncPoster = (): void => {
      const theme = currentTheme();
      const nextSrc = theme === 'light'
        ? img.dataset.themeSrcLight ?? ''
        : img.dataset.themeSrcDark ?? '';
      if (img.src !== new URL(nextSrc, window.location.href).href) {
        img.src = nextSrc;
      }
    };
    syncPoster();

    const themeObserver = new MutationObserver(syncPoster);
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    });

    window.addEventListener('pagehide', () => themeObserver.disconnect(), { once: true });
    return;
  }

  // --- Live scene path: lazy-load Three.js ---
  canvas.hidden = false;
  poster.hidden = true;

  // Size the canvas attribute to its computed size for crisp rendering.
  const sizeCanvas = (): void => {
    const rect = canvas.getBoundingClientRect();
    canvas.width = Math.round(rect.width * Math.min(window.devicePixelRatio, 2));
    canvas.height = Math.round(rect.height * Math.min(window.devicePixelRatio, 2));
  };
  sizeCanvas();
  window.addEventListener('resize', sizeCanvas);

  let controller: SceneController | null = null;
  let destroyed = false;

  const load = async (): Promise<void> => {
    const { initScene } = await import('./silo-scene');
    if (destroyed) return;
    controller = initScene(canvas, readThemeColors());
  };

  // Defer Three.js import to next idle callback so it doesn't block the hero paint.
  const schedule = (typeof window.requestIdleCallback === 'function')
    ? window.requestIdleCallback
    : (cb: () => void): number => window.setTimeout(cb, 200) as unknown as number;
  schedule(() => { void load(); });

  // --- State machine ---
  let interactive = false;
  let savedFocus: HTMLElement | null = null;

  const enterInteractive = (): void => {
    if (interactive) return;
    interactive = true;
    savedFocus = document.activeElement as HTMLElement | null;
    document.body.classList.add(INTERACTIVE_CLASS);
    closeBtn.hidden = false;
    closeBtn.focus();
    controller?.setInteractive(true);
  };

  const exitInteractive = (): void => {
    if (!interactive) return;
    interactive = false;
    document.body.classList.remove(INTERACTIVE_CLASS);
    // Delay hiding the button until after fade completes.
    window.setTimeout(() => { if (!interactive) closeBtn.hidden = true; }, 260);
    controller?.setInteractive(false);
    if (savedFocus && typeof savedFocus.focus === 'function') {
      savedFocus.focus();
    }
    savedFocus = null;
  };

  // --- Click-to-enter routing ---
  const ignoreSelector = '.hero, .card, header.header, .sidebar, a, button, input, select, textarea';
  const onPointerDown = (ev: PointerEvent): void => {
    if (interactive) return;
    const target = ev.target as Element | null;
    if (!target) return;
    if (target.closest(ignoreSelector)) return;
    enterInteractive();
  };
  document.addEventListener('pointerdown', onPointerDown);

  // --- Exit handlers ---
  closeBtn.addEventListener('click', exitInteractive);
  const onKey = (ev: KeyboardEvent): void => {
    if (!interactive) return;
    if (ev.key === 'Escape') {
      ev.preventDefault();
      exitInteractive();
    }
  };
  document.addEventListener('keydown', onKey);

  // --- Theme observer ---
  const themeObserver = new MutationObserver(() => {
    controller?.setTheme(readThemeColors());
  });
  themeObserver.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['data-theme'],
  });

  // --- Lifecycle / teardown ---
  const teardown = (): void => {
    if (destroyed) return;
    destroyed = true;
    themeObserver.disconnect();
    document.removeEventListener('pointerdown', onPointerDown);
    document.removeEventListener('keydown', onKey);
    window.removeEventListener('resize', sizeCanvas);
    controller?.dispose();
    controller = null;
  };
  window.addEventListener('pagehide', teardown, { once: true });
}
