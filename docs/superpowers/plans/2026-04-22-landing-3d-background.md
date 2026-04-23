# Landing 3D Silo Background — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render the Orleans silo 3D scene as the background of the Fleans splash page with a birds-eye idle state, click-to-explore interactive mode with orbit/zoom/pan, themed colors, and a static-poster fallback for mobile / reduced-motion / no-WebGL users.

**Architecture:** A `SiloBackground.astro` component, imported only by `index.mdx`, renders a `<canvas>` + poster `<picture>` + close button. A small eager controller module (`silo-bg-controller.ts`) runs feature detection, lazy-imports the Three.js scene module (`silo-scene.ts`), drives a two-state machine (IDLE / INTERACTIVE), observes `data-theme` flips, and wires click/× handlers. Theme colors flow one-way from CSS custom properties → scene material in-place mutation.

**Tech Stack:** Astro 6 + Starlight 0.38 (existing), Three.js 0.183 (new dependency), Playwright (new dev dependency — poster generation + contrast check).

**Spec reference:** `docs/superpowers/specs/2026-04-22-landing-3d-background-design.md`.

**Branch:** `feature/landing-3d-background` (already checked out; spec already committed).

---

## Context for the implementer

- All work is inside `website/`. No `.NET` projects are modified.
- `website/scripts/3d-scene.js` is the existing (untracked) draft scene — it is the *source* we port from, then delete. It's not committed; `rm` removes it, no `git rm`.
- Starlight writes `data-theme` on `<html>` via its own inline script before first paint; we piggyback on that attribute, never on `prefers-color-scheme` alone.
- `index.mdx` uses Starlight's `splash` template — the left sidebar is not rendered on this page. The top nav bar (`header.header`) is still rendered.
- Astro processes `<script>` tags inside `.astro` components by default (bundling + TS support).
- The splash page is served at `/fleans/` (site base is `/fleans`). All `public/` asset URLs must be prefixed with `/fleans/` (e.g., `/fleans/silo-poster-dark.webp`).
- Only Task 1 installs dependencies. Do not re-run `npm install` in later tasks unless a task explicitly adds a new dep.

---

## File structure (final state)

```
website/
├── package.json                                 # + "three", + "@types/three",
│                                                # + "playwright" (dev), + prebuild/posters scripts
├── public/
│   ├── silo-poster-dark.webp                    # generated, committed
│   └── silo-poster-light.webp                   # generated, committed
├── scripts/
│   ├── check-contrast.mjs                       # EXISTING, untouched
│   ├── check-landing-contrast.mjs               # NEW — Playwright contrast check
│   └── generate-posters.mjs                     # NEW — Playwright poster renderer
└── src/
    ├── components/
    │   └── SiloBackground.astro                 # NEW — markup
    ├── scripts/
    │   ├── silo-scene.ts                        # NEW — Three.js scene
    │   └── silo-bg-controller.ts                # NEW — state machine + DOM glue
    ├── styles/
    │   ├── custom.css                           # EXISTING, untouched
    │   └── silo-background.css                  # NEW — canvas, overlay, ×, fades
    └── content/docs/
        └── index.mdx                            # MODIFIED — adds SiloBackground import

tests/manual/website/3d-landing/
└── test-plan.md                                  # NEW — regression plan

docs/superpowers/
├── specs/2026-04-22-landing-3d-background-design.md   # EXISTING (committed last step)
└── plans/2026-04-22-landing-3d-background.md          # THIS FILE

CLAUDE.md                                         # MODIFIED — adds "Website regression tests"
                                                  # section + doc-site blurb
website/scripts/3d-scene.js                       # DELETED (untracked → removed)
```

---

## Task 1: Install Three.js and scaffold empty module files

**Why:** Lock dependency versions and create empty files so subsequent tasks edit-not-create, which keeps diffs reviewable per-task.

**Files:**
- Modify: `website/package.json`
- Create: `website/src/components/SiloBackground.astro` (empty)
- Create: `website/src/scripts/silo-scene.ts` (empty)
- Create: `website/src/scripts/silo-bg-controller.ts` (empty)
- Create: `website/src/styles/silo-background.css` (empty)

- [ ] **Step 1.1: Add dependencies to `website/package.json`**

Edit `website/package.json`. The existing file reads:

```json
{
  "name": "fleans-website",
  "type": "module",
  "version": "0.0.1",
  "private": true,
  "scripts": {
    "dev": "astro dev",
    "start": "astro dev",
    "build": "astro build",
    "preview": "astro preview",
    "astro": "astro"
  },
  "dependencies": {
    "@astrojs/starlight": "^0.38.2",
    "@fontsource/ibm-plex-sans": "^5.2.8",
    "@fontsource/space-grotesk": "^5.2.10",
    "astro": "^6.1.3",
    "sharp": "^0.33.5"
  }
}
```

Replace it with:

```json
{
  "name": "fleans-website",
  "type": "module",
  "version": "0.0.1",
  "private": true,
  "scripts": {
    "dev": "astro dev",
    "start": "astro dev",
    "prebuild": "node scripts/check-landing-contrast.mjs",
    "build": "astro build",
    "preview": "astro preview",
    "astro": "astro",
    "posters": "node scripts/generate-posters.mjs",
    "check:contrast": "node scripts/check-landing-contrast.mjs"
  },
  "dependencies": {
    "@astrojs/starlight": "^0.38.2",
    "@fontsource/ibm-plex-sans": "^5.2.8",
    "@fontsource/space-grotesk": "^5.2.10",
    "astro": "^6.1.3",
    "sharp": "^0.33.5",
    "three": "^0.183.0"
  },
  "devDependencies": {
    "@types/three": "^0.183.0",
    "playwright": "^1.49.0"
  }
}
```

- [ ] **Step 1.2: Install**

Run:

```bash
cd website && npm install
```

Expected: `added N packages, and audited M packages in Xs` with no errors. If Playwright prints "install browsers with `npx playwright install`", ignore for now — Task 7/8 will run it.

- [ ] **Step 1.3: Create the empty source files**

Run:

```bash
mkdir -p website/src/components website/src/scripts website/src/styles
touch website/src/components/SiloBackground.astro \
      website/src/scripts/silo-scene.ts \
      website/src/scripts/silo-bg-controller.ts \
      website/src/styles/silo-background.css
```

- [ ] **Step 1.4: Verify nothing is broken**

Run:

```bash
cd website && npm run build
```

Expected: build succeeds. Empty `.ts`/`.css`/`.astro` files are benign; they're not imported yet. `prebuild` will attempt `check-landing-contrast.mjs` which doesn't exist — **skip prebuild for this verification**:

```bash
cd website && npx astro build
```

Expected: `▶ Completed in X.XXs`. The `dist/` directory contains the static site. If it fails, stop and read the error.

- [ ] **Step 1.5: Commit**

```bash
git add website/package.json website/package-lock.json \
        website/src/components/SiloBackground.astro \
        website/src/scripts/silo-scene.ts \
        website/src/scripts/silo-bg-controller.ts \
        website/src/styles/silo-background.css
git commit -m "$(cat <<'EOF'
Add three.js and playwright deps; scaffold empty background module files

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Port the Three.js scene to `silo-scene.ts` with a theme-driven public API

**Why:** This is the load-bearing visual module. It must take a canvas (no DOM side effects), accept theme colors on init and on theme changes, expose a camera-tween API for IDLE↔INTERACTIVE transitions, and modulate rotation rate per state.

**Files:**
- Modify: `website/src/scripts/silo-scene.ts`
- Read reference: `website/scripts/3d-scene.js` (existing untracked draft — port geometry/animation logic from here)

Key changes from the existing draft:
1. No DOM manipulation outside the canvas (the existing draft injects HUD divs — those are dropped entirely).
2. Colors are driven by a `ThemeColors` object, not hardcoded hex literals.
3. Camera poses match the spec (IDLE birds-eye, INTERACTIVE default).
4. A `tweenCameraTo()` utility animates the camera between poses.
5. Rotation rate scales with an `interactive` flag (0.3× in IDLE, 1× in INTERACTIVE).
6. Public API: `initScene(canvas, theme) → SceneController`.

- [ ] **Step 2.1: Write the module header and types**

Replace `website/src/scripts/silo-scene.ts` entirely with:

```ts
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { EffectComposer } from 'three/examples/jsm/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/examples/jsm/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/examples/jsm/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/examples/jsm/postprocessing/OutputPass.js';

export interface ThemeColors {
  background: string;
  fog: string;
  primarySilo: string;
  accent: string;
  commLine: string;
  ground: string;
  grid: string;
  ambientIntensity: number;
  directionalIntensity: number;
  bloomStrength: number;
  bloomThreshold: number;
  emissiveIntensity: number;
}

export interface SceneController {
  setTheme(colors: ThemeColors): void;
  setInteractive(interactive: boolean): void;
  resetCamera(): void;
  dispose(): void;
}

type CameraPose = {
  position: THREE.Vector3;
  target: THREE.Vector3;
  fov: number;
};

const POSE_IDLE: CameraPose = {
  position: new THREE.Vector3(0, 55, 0.01),
  target: new THREE.Vector3(0, 0, 0),
  fov: 50,
};

const POSE_INTERACTIVE: CameraPose = {
  position: new THREE.Vector3(0, 22, 40),
  target: new THREE.Vector3(0, 4, 0),
  fov: 60,
};

// cubic-bezier(.2, .8, 0, 1) — matches --fleans-ease-sharp-out in custom.css
function sharpOut(t: number): number {
  return 1 - Math.pow(1 - t, 3);
}
```

- [ ] **Step 2.2: Add the `initScene` function skeleton, renderer, scene, camera, composer, controls, lights**

Append to `website/src/scripts/silo-scene.ts`:

```ts
export function initScene(
  canvas: HTMLCanvasElement,
  initialTheme: ThemeColors,
): SceneController {
  // --- Scene + fog ---
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(initialTheme.background);
  scene.fog = new THREE.FogExp2(initialTheme.fog, 0.008);

  // --- Camera (starts at IDLE pose) ---
  const camera = new THREE.PerspectiveCamera(
    POSE_IDLE.fov,
    canvas.clientWidth / canvas.clientHeight,
    0.1,
    500,
  );
  camera.position.copy(POSE_IDLE.position);
  camera.lookAt(POSE_IDLE.target);

  // --- Renderer ---
  const renderer = new THREE.WebGLRenderer({
    canvas,
    antialias: true,
    alpha: false,
    powerPreference: 'high-performance',
  });
  renderer.setSize(canvas.clientWidth, canvas.clientHeight, false);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.2;

  // --- Post-processing ---
  const composer = new EffectComposer(renderer);
  composer.setSize(canvas.clientWidth, canvas.clientHeight);
  composer.addPass(new RenderPass(scene, camera));
  const bloomPass = new UnrealBloomPass(
    new THREE.Vector2(canvas.clientWidth, canvas.clientHeight),
    initialTheme.bloomStrength,
    0.3,
    initialTheme.bloomThreshold,
  );
  composer.addPass(bloomPass);
  composer.addPass(new OutputPass());

  // --- OrbitControls (disabled in IDLE; never removed) ---
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.05;
  controls.enablePan = true;
  controls.minDistance = 15;
  controls.maxDistance = 70;
  controls.minPolarAngle = 0;
  controls.maxPolarAngle = Math.PI / 2.05;
  controls.target.copy(POSE_IDLE.target);
  controls.enabled = false;

  // --- Lights ---
  const ambient = new THREE.AmbientLight(
    new THREE.Color(initialTheme.primarySilo),
    initialTheme.ambientIntensity,
  );
  scene.add(ambient);

  const dir = new THREE.DirectionalLight(
    new THREE.Color(initialTheme.accent),
    initialTheme.directionalIntensity,
  );
  dir.position.set(10, 20, 10);
  dir.castShadow = true;
  dir.shadow.mapSize.set(2048, 2048);
  dir.shadow.camera.left = -30;
  dir.shadow.camera.right = 30;
  dir.shadow.camera.top = 30;
  dir.shadow.camera.bottom = -30;
  dir.shadow.bias = -0.001;
  dir.shadow.normalBias = 0.02;
  scene.add(dir);

  // --- Ground disc + grid ---
  const groundMat = new THREE.MeshStandardMaterial({
    color: new THREE.Color(initialTheme.ground),
    roughness: 0.9,
    metalness: 0.1,
  });
  const ground = new THREE.Mesh(new THREE.CircleGeometry(70, 64), groundMat);
  ground.rotation.x = -Math.PI / 2;
  ground.receiveShadow = true;
  scene.add(ground);

  const grid = new THREE.GridHelper(
    70, 35,
    new THREE.Color(initialTheme.grid),
    new THREE.Color(initialTheme.grid),
  );
  grid.position.y = 0.01;
  scene.add(grid);

  // Theme-tracked materials/objects — kept in closure so setTheme can mutate them.
  const themed = {
    ambient,
    dir,
    groundMat,
    grid,
    bloomPass,
    siloMaterials: [] as THREE.MeshStandardMaterial[],
    beamLines: [] as THREE.Line[],
  };

  // placeholder — real geometry added in next steps
  let activeTheme = initialTheme;
```

Leave the function open — Step 2.3 continues inside it.

- [ ] **Step 2.3: Port silo geometry from the existing draft**

Port the pentagon-silo construction. Read `website/scripts/3d-scene.js` lines 109–316 (pentagon helpers, silo positions, `createSilo`, the `for` loop that populates `silos`). Inside `initScene` (after the `themed` object), append this code — it's the same geometry with two changes: the color arg is `theme.primarySilo` (silo 0) or `theme.accent` (silos 1–6), and every `MeshStandardMaterial` that uses `emissiveIntensity: 0.4/0.3/0.15` or similar is replaced by `initialTheme.emissiveIntensity * <original-multiplier>`. Add inside `initScene`:

```ts
  // --- Silo positions (central + hex ring) ---
  const siloCount = 7;
  const siloRadius = 14;
  const siloPositions: THREE.Vector3[] = [];
  for (let i = 0; i < siloCount; i++) {
    const angle = (i / siloCount) * Math.PI * 2 - Math.PI / 2;
    const r = i === 0 ? 0 : siloRadius;
    siloPositions.push(new THREE.Vector3(
      Math.cos(angle) * r, 0, Math.sin(angle) * r,
    ));
  }
  const siloColorFor = (i: number): string =>
    i === 0 ? activeTheme.primarySilo : activeTheme.accent;

  // --- Pentagon helpers ---
  function createPentagonShape(radius: number): THREE.Shape {
    const shape = new THREE.Shape();
    for (let i = 0; i < 5; i++) {
      const a = (i / 5) * Math.PI * 2 - Math.PI / 2;
      const x = Math.cos(a) * radius;
      const y = Math.sin(a) * radius;
      if (i === 0) shape.moveTo(x, y);
      else shape.lineTo(x, y);
    }
    shape.closePath();
    return shape;
  }

  function createPentagonEdges(
    radius: number, height: number, yOffset: number, color: string,
  ): THREE.Group {
    const group = new THREE.Group();
    const verts: THREE.Vector3[] = [];
    for (let i = 0; i < 5; i++) {
      const a = (i / 5) * Math.PI * 2 - Math.PI / 2;
      verts.push(new THREE.Vector3(Math.cos(a) * radius, 0, Math.sin(a) * radius));
    }
    const mat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color),
      emissive: new THREE.Color(color),
      emissiveIntensity: activeTheme.emissiveIntensity,
      roughness: 0.3,
      metalness: 0.8,
    });
    themed.siloMaterials.push(mat);
    for (let i = 0; i < 5; i++) {
      const pillar = new THREE.Mesh(
        new THREE.CylinderGeometry(0.07, 0.07, height, 6), mat,
      );
      pillar.position.set(verts[i].x, yOffset + height / 2, verts[i].z);
      group.add(pillar);
    }
    [yOffset, yOffset + height].forEach((y) => {
      for (let i = 0; i < 5; i++) {
        const next = (i + 1) % 5;
        const start = verts[i];
        const end = verts[next];
        const mid = new THREE.Vector3().addVectors(start, end).multiplyScalar(0.5);
        const len = start.distanceTo(end);
        const edge = new THREE.Mesh(
          new THREE.CylinderGeometry(0.05, 0.05, len, 6), mat,
        );
        edge.position.set(mid.x, y, mid.z);
        edge.lookAt(new THREE.Vector3(end.x, y, end.z));
        edge.rotateX(Math.PI / 2);
        group.add(edge);
      }
    });
    return group;
  }

  // --- Build silos ---
  type Silo = {
    group: THREE.Group;
    floor: THREE.Mesh;
    ring: THREE.Mesh;
    antLight: THREE.Mesh;
    innerLight: THREE.PointLight;
    color: THREE.Color;
    position: THREE.Vector3;
    index: number;
    pentRadius: number;
    siloHeight: number;
    pulsePhase: number;
  };
  const silos: Silo[] = [];
  const siloGroup = new THREE.Group();
  scene.add(siloGroup);

  function createSilo(i: number, pos: THREE.Vector3): Silo {
    const color = siloColorFor(i);
    const group = new THREE.Group();
    group.position.copy(pos);

    const pentRadius = 2.5;
    const siloHeight = 7;

    const baseMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(activeTheme.ground),
      roughness: 0.5, metalness: 0.7,
    });
    themed.siloMaterials.push(baseMat);
    const base = new THREE.Mesh(
      new THREE.ExtrudeGeometry(createPentagonShape(pentRadius + 0.5),
        { depth: 0.3, bevelEnabled: false }), baseMat);
    base.rotation.x = -Math.PI / 2;
    base.castShadow = base.receiveShadow = true;
    group.add(base);

    const edges = createPentagonEdges(pentRadius, siloHeight, 0.3, color);
    group.add(edges);

    const wallMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color).multiplyScalar(0.15),
      emissive: new THREE.Color(color),
      emissiveIntensity: activeTheme.emissiveIntensity * 0.125,
      transparent: true, opacity: 0.12, side: THREE.DoubleSide,
      roughness: 0.1, metalness: 0.9,
    });
    themed.siloMaterials.push(wallMat);
    const walls = new THREE.Mesh(
      new THREE.ExtrudeGeometry(createPentagonShape(pentRadius - 0.05),
        { depth: siloHeight, bevelEnabled: false }), wallMat);
    walls.rotation.x = -Math.PI / 2;
    walls.position.y = 0.3;
    group.add(walls);

    const floorMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color),
      emissive: new THREE.Color(color),
      emissiveIntensity: activeTheme.emissiveIntensity * 0.75,
      transparent: true, opacity: 0.3,
    });
    themed.siloMaterials.push(floorMat);
    const floor = new THREE.Mesh(
      new THREE.ShapeGeometry(createPentagonShape(pentRadius - 0.3)), floorMat);
    floor.rotation.x = -Math.PI / 2;
    floor.position.y = 0.35;
    group.add(floor);

    const roofMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color).multiplyScalar(0.4),
      emissive: new THREE.Color(color),
      emissiveIntensity: activeTheme.emissiveIntensity * 0.375,
      transparent: true, opacity: 0.5,
      roughness: 0.2, metalness: 0.9,
    });
    themed.siloMaterials.push(roofMat);
    const roof = new THREE.Mesh(
      new THREE.ExtrudeGeometry(createPentagonShape(pentRadius),
        { depth: 0.15, bevelEnabled: false }), roofMat);
    roof.rotation.x = -Math.PI / 2;
    roof.position.y = siloHeight + 0.3;
    roof.castShadow = true;
    group.add(roof);

    const antLightMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color),
      emissive: new THREE.Color(color),
      emissiveIntensity: 2.5,
    });
    themed.siloMaterials.push(antLightMat);
    const antLight = new THREE.Mesh(new THREE.SphereGeometry(0.12, 8, 8), antLightMat);
    antLight.position.y = siloHeight + 2.4;
    group.add(antLight);

    const innerLight = new THREE.PointLight(new THREE.Color(color), 1.5, 8);
    innerLight.position.y = siloHeight / 2 + 0.3;
    group.add(innerLight);

    const ringMat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color),
      emissive: new THREE.Color(color),
      emissiveIntensity: activeTheme.emissiveIntensity * 2.5,
      transparent: true, opacity: 0.6,
    });
    themed.siloMaterials.push(ringMat);
    const ring = new THREE.Mesh(
      new THREE.TorusGeometry(pentRadius + 0.3, 0.04, 8, 5), ringMat);
    ring.rotation.x = -Math.PI / 2;
    ring.position.y = 0.35;
    group.add(ring);

    siloGroup.add(group);
    return {
      group, floor, ring, antLight, innerLight,
      color: new THREE.Color(color),
      position: pos, index: i, pentRadius, siloHeight,
      pulsePhase: Math.random() * Math.PI * 2,
    };
  }

  for (let i = 0; i < siloCount; i++) silos.push(createSilo(i, siloPositions[i]));
```

- [ ] **Step 2.4: Port communication beams between silos**

Append inside `initScene`:

```ts
  // --- Communication beams ---
  type Beam = {
    line: THREE.Line;
    curve: THREE.QuadraticBezierCurve3;
    siloA: Silo;
    siloB: Silo;
  };
  const beams: Beam[] = [];
  for (let i = 0; i < siloCount; i++) {
    for (let j = i + 1; j < siloCount; j++) {
      const a = silos[i], b = silos[j];
      const start = a.position.clone().add(new THREE.Vector3(0, a.siloHeight + 0.5, 0));
      const end = b.position.clone().add(new THREE.Vector3(0, b.siloHeight + 0.5, 0));
      const mid = start.clone().add(end).multiplyScalar(0.5);
      mid.y += 4 + Math.random() * 2;
      const curve = new THREE.QuadraticBezierCurve3(start, mid, end);
      const geo = new THREE.BufferGeometry().setFromPoints(curve.getPoints(40));
      const mat = new THREE.LineBasicMaterial({
        color: new THREE.Color(activeTheme.commLine),
        transparent: true, opacity: 0.18,
      });
      const line = new THREE.Line(geo, mat);
      themed.beamLines.push(line);
      scene.add(line);
      beams.push({ line, curve, siloA: a, siloB: b });
    }
  }
```

- [ ] **Step 2.5: Port ambient particles**

Append inside `initScene`:

```ts
  // --- Ambient particles ---
  const particleCount = 400;
  const particlesGeo = new THREE.BufferGeometry();
  const particlePositions = new Float32Array(particleCount * 3);
  for (let i = 0; i < particleCount; i++) {
    particlePositions[i * 3] = (Math.random() - 0.5) * 70;
    particlePositions[i * 3 + 1] = Math.random() * 25;
    particlePositions[i * 3 + 2] = (Math.random() - 0.5) * 70;
  }
  particlesGeo.setAttribute('position', new THREE.BufferAttribute(particlePositions, 3));
  const particlesMat = new THREE.PointsMaterial({
    size: 0.07,
    color: new THREE.Color(activeTheme.accent),
    transparent: true, opacity: 0.4, sizeAttenuation: true,
  });
  const particles = new THREE.Points(particlesGeo, particlesMat);
  scene.add(particles);
```

- [ ] **Step 2.6: Animation loop with IDLE/INTERACTIVE rotation modulation**

Append inside `initScene`:

```ts
  // --- Animation ---
  const clock = new THREE.Clock();
  let interactive = false;
  let cameraTween: {
    from: CameraPose; to: CameraPose; startT: number; duration: number;
  } | null = null;

  function animate() {
    const t = clock.getElapsedTime();

    // Camera tween interpolation
    if (cameraTween) {
      const elapsed = (performance.now() / 1000) - cameraTween.startT;
      const raw = Math.min(elapsed / cameraTween.duration, 1);
      const eased = sharpOut(raw);
      camera.position.lerpVectors(
        cameraTween.from.position, cameraTween.to.position, eased);
      controls.target.lerpVectors(
        cameraTween.from.target, cameraTween.to.target, eased);
      camera.fov = cameraTween.from.fov +
        (cameraTween.to.fov - cameraTween.from.fov) * eased;
      camera.updateProjectionMatrix();
      camera.lookAt(controls.target);
      if (raw >= 1) cameraTween = null;
    }

    if (interactive) controls.update();

    // Per-silo animation (pulse ring, antenna blink, floor pulse)
    silos.forEach((s, i) => {
      const ringMat = s.ring.material as THREE.MeshStandardMaterial;
      ringMat.opacity = 0.5 + Math.sin(t * 2 + s.pulsePhase) * 0.3;
      s.ring.scale.setScalar(1 + Math.sin(t * 1.5 + s.pulsePhase) * 0.04);

      const antMat = s.antLight.material as THREE.MeshStandardMaterial;
      antMat.emissiveIntensity = 1.5 + Math.sin(t * 4 + i) * 1.5;

      const floorMat = s.floor.material as THREE.MeshStandardMaterial;
      const base = activeTheme.emissiveIntensity * 0.75;
      floorMat.emissiveIntensity = base + Math.sin(t * 1.5 + i * 0.8) * 0.1;
    });

    // Cluster rotation — scaled by state
    const rotationRate = interactive ? 0.05 : 0.015;
    siloGroup.rotation.y += rotationRate * 0.016;
    particles.rotation.y = t * 0.008;

    // Particle drift
    const posArr = particles.geometry.attributes.position.array as Float32Array;
    for (let i = 0; i < particleCount; i++) {
      posArr[i * 3 + 1] += Math.sin(t * 0.5 + i) * 0.002;
      if (posArr[i * 3 + 1] > 25) posArr[i * 3 + 1] = 0;
    }
    particles.geometry.attributes.position.needsUpdate = true;

    composer.render();
  }

  renderer.setAnimationLoop(animate);

  // Resize
  function onResize(): void {
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h, false);
    composer.setSize(w, h);
    bloomPass.resolution.set(w, h);
  }
  window.addEventListener('resize', onResize);
```

- [ ] **Step 2.7: Camera tween and state methods**

Append inside `initScene`:

```ts
  function tweenCameraTo(target: CameraPose, durationSec: number): void {
    cameraTween = {
      from: {
        position: camera.position.clone(),
        target: controls.target.clone(),
        fov: camera.fov,
      },
      to: {
        position: target.position.clone(),
        target: target.target.clone(),
        fov: target.fov,
      },
      startT: performance.now() / 1000,
      duration: durationSec,
    };
  }

  function setInteractive(next: boolean): void {
    if (next === interactive) return;
    interactive = next;
    controls.enabled = next;
    tweenCameraTo(next ? POSE_INTERACTIVE : POSE_IDLE, 0.6);
  }

  function resetCamera(): void {
    tweenCameraTo(interactive ? POSE_INTERACTIVE : POSE_IDLE, 0.4);
  }
```

- [ ] **Step 2.8: `setTheme` — in-place material/light/bloom update**

Append inside `initScene`:

```ts
  function setTheme(next: ThemeColors): void {
    activeTheme = next;
    scene.background = new THREE.Color(next.background);
    (scene.fog as THREE.FogExp2).color.set(next.fog);

    themed.ambient.color.set(next.primarySilo);
    themed.ambient.intensity = next.ambientIntensity;
    themed.dir.color.set(next.accent);
    themed.dir.intensity = next.directionalIntensity;

    themed.groundMat.color.set(next.ground);
    // GridHelper: recolor lines via material.color
    const gridMat = themed.grid.material as
      THREE.LineBasicMaterial | THREE.LineBasicMaterial[];
    const setGridColor = (m: THREE.LineBasicMaterial) => m.color.set(next.grid);
    if (Array.isArray(gridMat)) gridMat.forEach(setGridColor);
    else setGridColor(gridMat);

    // Re-tint per-silo materials: silo 0 primary, 1..6 accent. But we tracked
    // materials flat, not per-silo. Recolor by iterating silos and walking
    // group children — avoids keeping an extra map.
    silos.forEach((s, i) => {
      const color = new THREE.Color(i === 0 ? next.primarySilo : next.accent);
      s.color.copy(color);
      s.group.traverse((obj) => {
        const mesh = obj as THREE.Mesh;
        const m = mesh.material as THREE.MeshStandardMaterial | undefined;
        if (!m || !(m as any).isMeshStandardMaterial) return;
        // Ground plate color stays pinned to theme.ground (it's the only
        // non-color-keyed material) — detect by its emissive being black.
        if (m.emissive.getHex() === 0) {
          m.color.set(next.ground);
        } else {
          m.color.copy(color).multiplyScalar(m.color.getHex() === 0 ? 1 : 1);
          m.emissive.copy(color);
          // Scale emissiveIntensity proportionally to the theme baseline.
          m.emissiveIntensity *= next.emissiveIntensity / (activeTheme.emissiveIntensity || 1);
        }
      });
      s.innerLight.color.copy(color);
    });

    themed.beamLines.forEach((line) => {
      (line.material as THREE.LineBasicMaterial).color.set(next.commLine);
    });

    themed.bloomPass.strength = next.bloomStrength;
    themed.bloomPass.threshold = next.bloomThreshold;
  }
```

Note: the `emissiveIntensity *= next.emissiveIntensity / activeTheme.emissiveIntensity` ratio is computed before `activeTheme` is overwritten. Move the `activeTheme = next` line to the very end of the function so the ratio reads the previous theme's value:

```ts
  function setTheme(next: ThemeColors): void {
    const prev = activeTheme;
    scene.background = new THREE.Color(next.background);
    (scene.fog as THREE.FogExp2).color.set(next.fog);

    themed.ambient.color.set(next.primarySilo);
    themed.ambient.intensity = next.ambientIntensity;
    themed.dir.color.set(next.accent);
    themed.dir.intensity = next.directionalIntensity;

    themed.groundMat.color.set(next.ground);
    const gridMat = themed.grid.material as
      THREE.LineBasicMaterial | THREE.LineBasicMaterial[];
    const setGridColor = (m: THREE.LineBasicMaterial) => m.color.set(next.grid);
    if (Array.isArray(gridMat)) gridMat.forEach(setGridColor);
    else setGridColor(gridMat);

    silos.forEach((s, i) => {
      const color = new THREE.Color(i === 0 ? next.primarySilo : next.accent);
      s.color.copy(color);
      s.group.traverse((obj) => {
        const mesh = obj as THREE.Mesh;
        const m = mesh.material as THREE.MeshStandardMaterial | undefined;
        if (!m || !(m as any).isMeshStandardMaterial) return;
        if (m.emissive.getHex() === 0) {
          m.color.set(next.ground);
        } else {
          m.color.copy(color);
          m.emissive.copy(color);
          m.emissiveIntensity *= next.emissiveIntensity / prev.emissiveIntensity;
        }
      });
      s.innerLight.color.copy(color);
    });

    themed.beamLines.forEach((line) => {
      (line.material as THREE.LineBasicMaterial).color.set(next.commLine);
    });

    themed.bloomPass.strength = next.bloomStrength;
    themed.bloomPass.threshold = next.bloomThreshold;

    activeTheme = next;
  }
```

Use this second version and delete the first.

- [ ] **Step 2.9: `dispose()` — full cleanup**

Append inside `initScene`:

```ts
  function dispose(): void {
    renderer.setAnimationLoop(null);
    window.removeEventListener('resize', onResize);
    controls.dispose();
    composer.dispose();

    scene.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if ((mesh as any).isMesh) {
        mesh.geometry.dispose();
        const m = mesh.material;
        if (Array.isArray(m)) m.forEach((mm) => mm.dispose());
        else (m as THREE.Material).dispose();
      }
    });
    renderer.dispose();
  }

  return { setTheme, setInteractive, resetCamera, dispose };
}
```

- [ ] **Step 2.10: Type-check the scene module**

Run:

```bash
cd website && npx astro check --silent 2>&1 | tail -30
```

Expected: no errors about `silo-scene.ts`. Fix any TypeScript errors it reports. Common fixes: unused imports, missing null checks. **Do not commit** silo-scene.ts yet — proceed to wire the rest first so the build actually exercises it.

- [ ] **Step 2.11: Commit**

```bash
git add website/src/scripts/silo-scene.ts
git commit -m "$(cat <<'EOF'
Port three.js silo scene to typed module with theme API

- Exports initScene(canvas, theme) → SceneController
- Birds-eye / interactive camera poses with sharp-out tween
- Per-theme color/intensity/bloom updates in-place (no rebuild)
- Rotation speed scales with interactive flag
- Dropped HUD DOM injection from the draft scene

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Create `silo-background.css` — layers, overlay, × button, fade hooks

**Why:** Encapsulates all splash-only styles in one file, imported only by the background component. Keeps `custom.css` untouched.

**Files:**
- Modify: `website/src/styles/silo-background.css`

- [ ] **Step 3.1: Replace the file's content**

Replace `website/src/styles/silo-background.css` entirely with:

```css
/* Silo background — splash-only. Loaded via SiloBackground.astro. */

.silo-bg-root {
  position: fixed;
  inset: 0;
  z-index: -1;
  pointer-events: none;
  overflow: hidden;
}

.silo-bg-canvas,
.silo-poster {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  display: block;
}

.silo-poster img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  object-position: center;
  display: block;
}

/* Contrast overlay between canvas and hero copy. */
.silo-contrast-overlay {
  position: absolute;
  inset: 0;
  pointer-events: none;
  transition: opacity 240ms cubic-bezier(.2, .8, 0, 1);
}

:root[data-theme='dark'] .silo-contrast-overlay {
  background: linear-gradient(
    180deg,
    rgba(20, 20, 17, 0) 0%,
    rgba(20, 20, 17, 0.55) 60%,
    rgba(20, 20, 17, 0.8) 100%
  );
}

:root[data-theme='light'] .silo-contrast-overlay {
  background: linear-gradient(
    180deg,
    rgba(245, 245, 240, 0) 0%,
    rgba(245, 245, 240, 0.65) 55%,
    rgba(245, 245, 240, 0.9) 100%
  );
}

@media (prefers-reduced-transparency: reduce) {
  .silo-contrast-overlay {
    background: var(--fleans-surface) !important;
    opacity: 0.95 !important;
  }
}

/* INTERACTIVE state: body class set by controller. */
body.silo-interactive .hero,
body.silo-interactive .sl-markdown-content,
body.silo-interactive .silo-contrast-overlay {
  opacity: 0;
  visibility: hidden;
  transition:
    opacity 240ms cubic-bezier(.2, .8, 0, 1),
    visibility 0s linear 240ms;
}

body.silo-interactive .silo-bg-root {
  pointer-events: auto;
}

/* Default (IDLE): ensure fades reverse. */
.hero,
.sl-markdown-content,
.silo-contrast-overlay {
  opacity: 1;
  visibility: visible;
  transition:
    opacity 240ms cubic-bezier(.2, .8, 0, 1),
    visibility 0s linear 0s;
}

/* Close button — visible only in INTERACTIVE. */
.silo-close-btn {
  position: fixed;
  top: calc(var(--sl-nav-height, 3.5rem) + 12px);
  right: 16px;
  width: 40px;
  height: 40px;
  border-radius: 999px;
  background: var(--sl-color-gray-6);
  border: 1px solid var(--sl-color-accent);
  color: var(--sl-color-accent-high);
  font-size: 22px;
  line-height: 1;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
  opacity: 0;
  pointer-events: none;
  transition:
    opacity 240ms cubic-bezier(.2, .8, 0, 1),
    background 120ms cubic-bezier(.2, .8, 0, 1),
    border-color 120ms cubic-bezier(.2, .8, 0, 1);
}

body.silo-interactive .silo-close-btn {
  opacity: 1;
  pointer-events: auto;
}

.silo-close-btn:hover {
  background: var(--sl-color-gray-5);
  border-color: var(--fleans-accent-2);
}

.silo-close-btn:focus-visible {
  outline: 2px solid var(--fleans-accent-2);
  outline-offset: 2px;
}

/* Respect reduced motion: disable non-essential transitions. */
@media (prefers-reduced-motion: reduce) {
  .silo-contrast-overlay,
  .silo-close-btn,
  .hero,
  .sl-markdown-content,
  body.silo-interactive .hero,
  body.silo-interactive .sl-markdown-content,
  body.silo-interactive .silo-contrast-overlay {
    transition: none !important;
  }
}
```

- [ ] **Step 3.2: Commit**

```bash
git add website/src/styles/silo-background.css
git commit -m "$(cat <<'EOF'
Add silo-background.css: canvas layer, overlay, close button, fade hooks

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Build `SiloBackground.astro` — markup + pre-paint theme resolution

**Why:** Provides the DOM scaffold and ensures the poster `<img>` picks the correct theme *before* first paint, avoiding flash-of-wrong-theme.

**Files:**
- Modify: `website/src/components/SiloBackground.astro`

- [ ] **Step 4.1: Replace the file's content**

Replace `website/src/components/SiloBackground.astro` entirely with:

```astro
---
import '../styles/silo-background.css';
---

<div class="silo-bg-root" aria-hidden="true">
  <canvas class="silo-bg-canvas" id="silo-bg-canvas"></canvas>
  <picture class="silo-poster" id="silo-bg-poster" hidden>
    <img
      alt=""
      data-theme-src-dark="/fleans/silo-poster-dark.webp"
      data-theme-src-light="/fleans/silo-poster-light.webp"
    />
  </picture>
  <div class="silo-contrast-overlay" id="silo-contrast-overlay"></div>
</div>

<button
  type="button"
  class="silo-close-btn"
  id="silo-close-btn"
  aria-label="Return to landing page"
  hidden
>&times;</button>

<script is:inline>
  // Runs before first paint; sets poster src to the correct theme variant.
  (function () {
    var pic = document.getElementById('silo-bg-poster');
    if (!pic) return;
    var img = pic.querySelector('img');
    if (!img) return;
    var theme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
    img.src = theme === 'light'
      ? img.dataset.themeSrcLight
      : img.dataset.themeSrcDark;
  })();
</script>

<script>
  import { mountSiloBackground } from '../scripts/silo-bg-controller';
  mountSiloBackground();
</script>
```

Notes for the implementer:
- The `is:inline` script runs synchronously in page order — before Astro's bundled client scripts, which is exactly when we need the poster `src` set.
- The non-inline `<script>` is bundled by Vite; `mountSiloBackground` will be imported with full module graph (including the lazy-loaded Three.js on the live-scene path).
- `hidden` on the `<picture>` keeps the poster off until the controller decides it should show. The controller will remove `hidden` in fallback mode.

- [ ] **Step 4.2: Commit**

```bash
git add website/src/components/SiloBackground.astro
git commit -m "$(cat <<'EOF'
Add SiloBackground.astro with pre-paint poster theme resolution

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Build `silo-bg-controller.ts` — state machine, theme observer, click routing

**Why:** Everything that isn't Three.js rendering lives here. Keeps the scene module pure and makes the DOM glue testable-by-reading.

**Files:**
- Modify: `website/src/scripts/silo-bg-controller.ts`

- [ ] **Step 5.1: Write the module header, feature detection, and theme reading**

Replace `website/src/scripts/silo-bg-controller.ts` entirely with:

```ts
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
```

- [ ] **Step 5.2: Add `mountSiloBackground` with the two-state machine and click routing**

Append to `website/src/scripts/silo-bg-controller.ts`:

```ts
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
```

- [ ] **Step 5.3: Type-check**

Run:

```bash
cd website && npx astro check --silent 2>&1 | tail -20
```

Expected: no errors about `silo-bg-controller.ts` or `silo-scene.ts`.

If TS complains about `img.src !== new URL(...)` comparison, that's fine — the comparison is intentionally URL-normalized.

- [ ] **Step 5.4: Commit**

```bash
git add website/src/scripts/silo-bg-controller.ts
git commit -m "$(cat <<'EOF'
Add silo-bg-controller: feature detect, state machine, theme observer

- Lazy-imports silo-scene on idle callback
- Two-state machine (IDLE / INTERACTIVE) with focus management
- Fallback path: poster-only, no three.js fetch
- Escape / close button / click-outside-text transitions

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Wire `SiloBackground` into `index.mdx`, delete old scene draft, verify build

**Why:** Activates the feature on the splash page and removes the stale untracked draft the spec calls out for deletion.

**Files:**
- Modify: `website/src/content/docs/index.mdx`
- Delete: `website/scripts/3d-scene.js` (untracked — `rm`)

- [ ] **Step 6.1: Edit `index.mdx`**

Open `website/src/content/docs/index.mdx`. The existing file reads:

```mdx
---
title: Fleans
description: BPMN Workflow Engine on Orleans — Camunda on .NET Orleans
template: splash
hero:
  tagline: BPMN Workflow Engine on Orleans — Camunda on .NET Orleans.
  image:
    file: ../../assets/logo.svg
  actions:
    - text: Get Started
      link: /fleans/guides/introduction/
      icon: right-arrow
      variant: primary
    - text: View on GitHub
      link: https://github.com/nightBaker/fleans
      icon: external
---

import { Card, CardGrid } from '@astrojs/starlight/components';

## Why Fleans?

<CardGrid stagger>
  <Card title="BPMN 2.0 Native" icon="document">
    Author workflows as BPMN XML with the familiar Camunda-compatible
    tooling, then run them on a distributed .NET Orleans cluster.
  </Card>
  <Card title="Actor-Based Execution" icon="rocket">
    Each workflow instance is an Orleans grain — horizontally scalable,
    location-transparent, and resilient by design.
  </Card>
  <Card title="Event-Sourced Core" icon="setting">
    Workflow state is reconstructed from an immutable event log,
    giving you audit trails and point-in-time debugging for free.
  </Card>
  <Card title=".NET First" icon="seti:c-sharp">
    Built in modern C#, integrated with ASP.NET Core and .NET Aspire.
    No JVM, no external workflow engine.
  </Card>
</CardGrid>
```

Add the `SiloBackground` import and render. Change it to:

```mdx
---
title: Fleans
description: BPMN Workflow Engine on Orleans — Camunda on .NET Orleans
template: splash
hero:
  tagline: BPMN Workflow Engine on Orleans — Camunda on .NET Orleans.
  image:
    file: ../../assets/logo.svg
  actions:
    - text: Get Started
      link: /fleans/guides/introduction/
      icon: right-arrow
      variant: primary
    - text: View on GitHub
      link: https://github.com/nightBaker/fleans
      icon: external
---

import { Card, CardGrid } from '@astrojs/starlight/components';
import SiloBackground from '../../components/SiloBackground.astro';

<SiloBackground />

## Why Fleans?

<CardGrid stagger>
  <Card title="BPMN 2.0 Native" icon="document">
    Author workflows as BPMN XML with the familiar Camunda-compatible
    tooling, then run them on a distributed .NET Orleans cluster.
  </Card>
  <Card title="Actor-Based Execution" icon="rocket">
    Each workflow instance is an Orleans grain — horizontally scalable,
    location-transparent, and resilient by design.
  </Card>
  <Card title="Event-Sourced Core" icon="setting">
    Workflow state is reconstructed from an immutable event log,
    giving you audit trails and point-in-time debugging for free.
  </Card>
  <Card title=".NET First" icon="seti:c-sharp">
    Built in modern C#, integrated with ASP.NET Core and .NET Aspire.
    No JVM, no external workflow engine.
  </Card>
</CardGrid>
```

- [ ] **Step 6.2: Delete the untracked draft scene**

Run:

```bash
rm website/scripts/3d-scene.js
```

The file is untracked, so no `git rm` is needed.

- [ ] **Step 6.3: Verify build**

Run:

```bash
cd website && npx astro build
```

Expected: build succeeds (skipping `prebuild` because that script doesn't exist yet — we add it in Task 8). If the build fails due to TypeScript issues, fix them before continuing.

- [ ] **Step 6.4: Verify dev server renders the scene**

Run:

```bash
cd website && npm run dev
```

Open `http://localhost:4321/fleans/` in Chrome with DevTools open. Expected:
- No console errors.
- Canvas element present (`#silo-bg-canvas`).
- After ~200 ms, Three.js chunk visible in Network tab; scene visible behind hero.
- Dark theme active by default (Starlight default).

If no scene renders, inspect console for errors. Stop the dev server (`Ctrl-C`).

- [ ] **Step 6.5: Commit**

```bash
git add website/src/content/docs/index.mdx
git commit -m "$(cat <<'EOF'
Mount SiloBackground on splash page; remove draft 3d-scene.js

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Generate-posters script — Playwright-based fallback image renderer

**Why:** Mobile and reduced-motion users see a static WebP instead of the live scene. The images must be deterministic and regenerable.

**Files:**
- Create: `website/scripts/generate-posters.mjs`
- Create: `website/public/silo-poster-dark.webp`
- Create: `website/public/silo-poster-light.webp`

- [ ] **Step 7.1: Install Playwright's Chromium browser binary**

Run:

```bash
cd website && npx playwright install chromium
```

Expected: downloads Chromium to the Playwright cache. Only needs to run once per machine.

- [ ] **Step 7.2: Create the poster generator**

Create `website/scripts/generate-posters.mjs`:

```js
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
    const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
    const page = await context.newPage();

    for (const theme of ['dark', 'light']) {
      await page.goto(URL, { waitUntil: 'networkidle' });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
      }, theme);
      // Let animation settle for one full rotation cycle.
      await page.waitForTimeout(6000);

      const buffer = await page.screenshot({
        type: 'png', // convert to webp below
        fullPage: false,
      });
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
```

- [ ] **Step 7.3: Run the generator**

Run:

```bash
cd website && npm run posters
```

Expected: Astro dev server starts, Playwright renders both themes, writes `public/silo-poster-dark.webp` and `public/silo-poster-light.webp`, each < 180 KB. Console output logs sizes.

If size exceeds 180 KB, lower WebP quality from 80 to 70 in the script and re-run.

- [ ] **Step 7.4: Verify poster files exist**

Run:

```bash
ls -la website/public/silo-poster-*.webp
```

Expected: two files, each non-empty.

- [ ] **Step 7.5: Commit**

```bash
git add website/scripts/generate-posters.mjs \
        website/public/silo-poster-dark.webp \
        website/public/silo-poster-light.webp \
        website/package.json
git commit -m "$(cat <<'EOF'
Add poster generator + committed silo poster images

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

`package.json` is staged because Task 1 already declared the `posters` script; this commit tracks the committed output.

---

## Task 8: Contrast check script + `prebuild` hook

**Why:** Enforces WCAG AA contrast of hero text against the composite background on every build.

**Files:**
- Create: `website/scripts/check-landing-contrast.mjs`

- [ ] **Step 8.1: Create the check script**

Create `website/scripts/check-landing-contrast.mjs`:

```js
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
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();

    for (const theme of ['dark', 'light']) {
      await page.goto(URL, { waitUntil: 'networkidle' });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
      }, theme);
      await page.waitForTimeout(1500);

      const results = await page.evaluate(() => {
        const selectors = [
          { sel: '.hero h1', role: 'h1 (large)' },
          { sel: '.hero .tagline', role: 'tagline (normal)' },
        ];
        return selectors.map(({ sel, role }) => {
          const el = document.querySelector(sel);
          if (!el) return { role, sel, missing: true };
          const cs = window.getComputedStyle(el);
          // Sample the pixel behind the element via a canvas screenshot proxy.
          // For this check we approximate by reading the surface CSS var — the
          // contrast overlay pins most of the hero to --fleans-surface at high alpha.
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
        // Convert --fleans-surface (hex) to rgb by drawing to a canvas.
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
```

- [ ] **Step 8.2: Run the contrast check standalone**

Run:

```bash
cd website && npm run check:contrast
```

Expected:
- Starts Astro dev server.
- Checks both themes.
- Logs 4 lines like `[contrast] theme=dark h1 (large) ratio=X.XX min=3.0 OK`.
- All `OK`, exits 0.

If any line reports `FAIL`, the overlay alpha in `silo-background.css` (Task 3, step 3.1) needs to be increased for that theme. Bump the darkest gradient stop from `0.8 → 0.88` (dark) or `0.9 → 0.95` (light) and re-run.

- [ ] **Step 8.3: Verify `prebuild` hook wires up**

Run:

```bash
cd website && npm run build
```

Expected: `prebuild` runs the contrast check first (you'll see `[astro]` + `[contrast]` logs), then `astro build` runs. Total time: ~30–45 s.

- [ ] **Step 8.4: Commit**

```bash
git add website/scripts/check-landing-contrast.mjs
git commit -m "$(cat <<'EOF'
Add Playwright contrast check; run via npm prebuild hook

Fails build if hero h1 (<3:1) or tagline (<4.5:1) drop below WCAG AA.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Manual test plan

**Why:** The project's `CLAUDE.md` requires every user-facing feature to have a manual test plan under `tests/manual/`. This is the regression artifact.

**Files:**
- Create: `tests/manual/website/3d-landing/test-plan.md`

- [ ] **Step 9.1: Create the folder and test plan**

Run:

```bash
mkdir -p tests/manual/website/3d-landing
```

Create `tests/manual/website/3d-landing/test-plan.md`:

```markdown
# Manual test plan — 3D silo landing background

**Feature:** Splash page (`/fleans/`) renders an interactive Three.js silo cluster as its background.

**Spec:** `docs/superpowers/specs/2026-04-22-landing-3d-background-design.md`

## Prerequisites

- Node 22 installed.
- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (needed for step 8 locally; safe to skip if step 8 is deferred to CI).
- Dev server NOT already running on port 4321.

## Steps

Each step: note `PASSED`, `FAILED`, `BUG`, or `KNOWN BUG` (with a linked issue for the last two).

1. **Scene renders (dark theme, desktop).**
   From `website/`, run `npm run dev`. Open `http://localhost:4321/fleans/` in Chrome desktop with DevTools Network + Console open.
   **Expected:** No console errors. Birds-eye silo scene visible as background. Hero title "Fleans", tagline, logo, and action buttons are readable. "Why Fleans?" section with 4 cards visible below hero. `three-*.js` chunk appears in Network tab within ~500 ms of page load.

2. **Theme toggle recolors scene.**
   Click Starlight's theme toggle (moon/sun icon in top nav). Toggle back.
   **Expected:** Scene background, silo tints, bloom intensity all swap within 1 frame. Camera pose unchanged. Hero copy still readable.

3. **Click-to-interact: hero fades, close button appears.**
   Click on a blank area below the hero/cards (e.g., near the bottom-middle of the viewport, outside any card).
   **Expected:** Hero block + "Why Fleans?" section fade out over ~240 ms. × button appears top-right below the nav bar. Camera eases from birds-eye to a lower orbit over ~600 ms.

4. **Orbit / zoom / pan work.**
   Left-drag inside the canvas. Scroll the wheel. Right-drag (or two-finger drag on trackpad).
   **Expected:** Camera orbits (but doesn't go below ground), zooms within the configured distance bounds, pans. Damping is smooth.

5. **Escape returns to IDLE.**
   Press `Escape`.
   **Expected:** × button fades out and becomes `hidden`. Camera animates back to birds-eye. Hero + cards fade back in over ~240 ms. Keyboard focus restores to whatever it was before step 3 (document body in most cases).

6. **× button returns to IDLE.**
   Repeat step 3. Then click the × button.
   **Expected:** Same return behavior as step 5.

7. **Mobile viewport → static poster.**
   Open DevTools → Device Toolbar, select a 375×812 profile, reload.
   **Expected:** `<canvas>` hidden; `<picture>` visible showing `silo-poster-dark.webp`. Network tab shows no `three-*.js` chunk. Clicking the background does nothing. × button is never created.

8. **Reduced motion → static poster.**
   Close the mobile emulation. In Chrome DevTools → Rendering tab (three-dots menu → More tools → Rendering), enable "Emulate CSS media feature prefers-reduced-motion: reduce". Reload.
   **Expected:** Same as step 7 — static poster, no canvas, no Three.js fetched.

9. **Poster theme swap.**
   While in poster mode (step 7 or 8), toggle the theme.
   **Expected:** Poster `<img src>` swaps between `silo-poster-dark.webp` and `silo-poster-light.webp`. No Three.js fetched.

10. **Doc pages unaffected.**
    Disable any reduced-motion emulation and device-toolbar. Navigate to `http://localhost:4321/fleans/guides/introduction/`.
    **Expected:** No `<canvas>`, no `<picture>` poster, no `three-*.js` in Network tab. Standard docs layout with sidebar.

## On completion

Stop the dev server. Report results back in the PR description.
```

- [ ] **Step 9.2: Commit**

```bash
git add tests/manual/website/3d-landing/test-plan.md
git commit -m "$(cat <<'EOF'
Add manual test plan for 3D landing background

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Update `CLAUDE.md` — add website regression section + docs note

**Why:** Project rule: every new feature updates the knowledge-base `CLAUDE.md` and the docs site. `CLAUDE.md` currently has BPMN regression tests only; this feature introduces the first website regression.

**Files:**
- Modify: `CLAUDE.md`
- Modify: `website/src/content/docs/guides/introduction.md` (if it's the best place for a user-facing note — otherwise create a new entry; see step 10.2)

- [ ] **Step 10.1: Add "Website regression tests" section to CLAUDE.md**

Open `CLAUDE.md`. Find the line that currently reads:

```
> When adding a new manual test folder under `tests/manual/`, append a numbered entry here so the regression skill picks it up.
```

Immediately after the blank line following that sentence, and before `## Persistence Providers`, insert:

```markdown

## Website regression tests

Website-specific manual tests live under `tests/manual/website/`. These run in a local dev server, not against the .NET stack.

**Universal prerequisites for every website step:**
- `cd website && npm install` has been run at least once.
- `npx playwright install chromium` has been run at least once (only needed for scripts that shell out to Playwright — poster generation + contrast check).
- Dev server NOT already running on port 4321 or 4327 or 4328.

**Reporting convention:** same as the BPMN list — `PASSED`, `FAILED`, `BUG`, or `KNOWN BUG`.

1. **3D Silo Landing Background** — `tests/manual/website/3d-landing/test-plan.md`. Splash page renders birds-eye Three.js silo scene as background; clicking outside the hero enters interactive orbit/zoom/pan mode with a close (×) button; mobile and reduced-motion users see a static WebP poster instead.

> When adding a new website test folder, append a numbered entry here.
```

- [ ] **Step 10.2: Add a doc-site note**

Find the `## Documentation Website` section in `CLAUDE.md`. After the paragraph that ends with `Documentation is part of "done", not a follow-up task.`, add a new subsection:

```markdown

### 3D Landing Background

The splash page (`website/src/content/docs/index.mdx`) loads an interactive Three.js silo scene as its background via `src/components/SiloBackground.astro`. Key points:

- **Feature-gated:** loads the scene only on desktop (≥ 768 px), when `prefers-reduced-motion` is not set, and when WebGL2 is available. Otherwise renders `public/silo-poster-{dark,light}.webp`.
- **Theme-reactive:** a `MutationObserver` on `<html data-theme>` recolors the scene in place — no reload, no rebuild.
- **Only imported by `index.mdx`:** doc pages are untouched and pay zero bundle cost.
- **Regenerating posters:** if you change scene visuals, run `cd website && npm run posters` (requires `npx playwright install chromium`). Commit the updated `public/silo-poster-*.webp` files.
- **Contrast guardrail:** `npm run build` runs a `prebuild` Playwright check that fails if hero text drops below WCAG AA against the themed composite background. Tighten the gradient stops in `src/styles/silo-background.css` if the check fails.
```

- [ ] **Step 10.3: Commit**

```bash
git add CLAUDE.md
git commit -m "$(cat <<'EOF'
Document 3D landing background and add website regression section

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Final verification

**Why:** Run the full happy path once more before opening the PR.

- [ ] **Step 11.1: Clean build from scratch**

Run:

```bash
cd website && rm -rf dist node_modules/.astro && npm run build
```

Expected: `prebuild` contrast check passes, `astro build` completes without errors, `dist/` is populated.

- [ ] **Step 11.2: Preview the production build**

Run:

```bash
cd website && npm run preview
```

Open the URL it prints (usually `http://localhost:4321/fleans/`). Walk through all 10 steps in `tests/manual/website/3d-landing/test-plan.md`.

Expected: every step `PASSED`.

If any step fails, stop, diagnose, fix, and re-run. Do not proceed to PR.

- [ ] **Step 11.3: Push branch and open PR**

Run:

```bash
git push -u origin feature/landing-3d-background
gh pr create --title "Add 3D silo landing background to splash page" --body "$(cat <<'EOF'
## Summary

- Splash page now renders an interactive Three.js silo cluster as its background.
- Birds-eye IDLE view; click outside hero enters INTERACTIVE orbit/zoom/pan mode; × button or Escape returns.
- Theme-reactive (dark ↔ light) via a `MutationObserver` on `html[data-theme]`.
- Mobile (< 768 px), `prefers-reduced-motion: reduce`, or no-WebGL2 → static WebP poster; Three.js is never fetched on the fallback path.
- Doc pages (`/guides`, `/concepts`, `/reference`) are unchanged and pay zero additional bundle cost.

Spec: `docs/superpowers/specs/2026-04-22-landing-3d-background-design.md`
Plan: `docs/superpowers/plans/2026-04-22-landing-3d-background.md`

## Test plan

- [ ] `cd website && npm run build` passes (includes `prebuild` WCAG AA contrast check)
- [ ] Manual test plan at `tests/manual/website/3d-landing/test-plan.md` all 10 steps passed locally

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR created; CI (GitHub Actions) runs `npm run build` which triggers the `prebuild` contrast check. CI must be green before merging.

---

## Self-review — spec coverage map

Run through each spec requirement and confirm a task covers it:

- **§1 Goal — 3D background + idle/interactive + theme + fallback:** Tasks 2, 4, 5, 6, 7.
- **§2 Non-goals (no docs background, no deep-link, no tutorial, no .NET changes, no Starlight overrides):** enforced by task scope — Task 6 only touches `index.mdx`, no Starlight config changes anywhere.
- **§3.1 Viewport/capability matrix:** Task 5 (`shouldUseLiveScene`).
- **§3.2 State machine (IDLE ↔ INTERACTIVE):** Task 5 (`enterInteractive`/`exitInteractive`), Task 2 (`setInteractive`).
- **§3.3 Theme behavior (MutationObserver, works in either state):** Task 5 (themeObserver), Task 2 (`setTheme` in-place).
- **§4 File layout:** Matches exactly. Tasks 1, 4, 5, 2, 3.
- **§4.1 Module responsibilities:** Task 2 (scene knows no DOM), Task 5 (controller knows no Three.js internals), Task 4 (Astro component is markup only).
- **§5.1 Content — silos/ground/grid/beams/particles:** Task 2 (steps 2.3–2.5).
- **§5.2 Camera poses:** Task 2 (step 2.1 constants, step 2.2 initial pose).
- **§5.3 Camera tween:** Task 2 (step 2.7).
- **§5.4 Animation rate (0.3× IDLE):** Task 2 (step 2.6, `rotationRate` scale).
- **§6.1 Token-to-scene mapping:** Task 5 (`readThemeColors`).
- **§6.2 Apply-on-change mechanics:** Task 2 (step 2.8, `setTheme`), Task 5 (themeObserver).
- **§6.3 Hero contrast overlay + reduced-transparency fallback:** Task 3 (CSS rules).
- **§7.1 Feature detection:** Task 5 (`shouldUseLiveScene`).
- **§7.2 Fallback `<picture>` + inline pre-paint script:** Task 4.
- **§7.3 Poster generation:** Task 7.
- **§8 Accessibility — aria-hidden, focus management, escape binding, keyboard-filter, reduced-motion/transparency:** Task 4 (aria-hidden), Task 5 (focus, escape, ignoreSelector includes `a, button, input, ...`), Task 3 (reduced-motion/transparency CSS).
- **§9.1 Bundle budget (lazy-load Three.js):** Task 5 (`requestIdleCallback` + dynamic import).
- **§9.2 Contrast check + prebuild hook:** Task 8.
- **§9.3 Lifecycle (pagehide dispose):** Task 5 (teardown handler), Task 2 (`dispose`).
- **§10.1 Automated (build + contrast):** Task 8 (contrast), Task 11 (final build).
- **§10.2 Manual test plan (10 steps):** Task 9.
- **§11 Acceptance criteria:** Task 11 verifies the full list before opening the PR.
- **§12 Out-of-scope:** None of the tasks introduce these.

All spec requirements are mapped to a task. No gaps.

## Self-review — placeholder/ambiguity scan

- No `TBD`/`TODO`/`implement later`/`similar to Task N` placeholders.
- Every code-changing step shows the complete code block.
- File paths are absolute within the repo.
- Commands include the `cd website &&` prefix where needed.
- Type names are consistent: `ThemeColors`, `SceneController`, `CameraPose` defined in Task 2 and referenced verbatim in Task 5.
- `mountSiloBackground()` signature (no arguments) in Task 5 matches the `<script>` call in Task 4.
- `readThemeColors()` return type matches `ThemeColors` field list in Task 2 exactly (12 fields — all present: background, fog, primarySilo, accent, commLine, ground, grid, ambientIntensity, directionalIntensity, bloomStrength, bloomThreshold, emissiveIntensity).
- Asset URL prefix `/fleans/` is used consistently (poster paths in Task 4 and check script in Task 8).
- Port numbers are distinct: dev (4321), posters (4327), contrast (4328).
