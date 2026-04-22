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

  let activeTheme = initialTheme;

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
        if (!m || !(m as unknown as { isMeshStandardMaterial?: boolean }).isMeshStandardMaterial) return;
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

  function dispose(): void {
    renderer.setAnimationLoop(null);
    window.removeEventListener('resize', onResize);
    controls.dispose();
    composer.dispose();

    scene.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if ((mesh as unknown as { isMesh?: boolean }).isMesh) {
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
