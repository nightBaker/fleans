// @ts-nocheck
// Direct port of orleans-framework-webgl-silo-c/index.js — full silo cluster
// with failover/rebalance sequence. Type checking is suppressed because this
// is a verbatim port of heavily-dynamic JS; typing it would be a separate
// project. HUD DOM injection from the original is removed (landing page
// renders its own hero text), and the renderer takes a caller-supplied canvas.
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

const POSE_IDLE = {
  position: new THREE.Vector3(0, 55, 0.01),
  target: new THREE.Vector3(0, 0, 0),
  fov: 50,
};
const POSE_INTERACTIVE = {
  position: new THREE.Vector3(0, 22, 40),
  target: new THREE.Vector3(0, 4, 0),
  fov: 60,
};
function sharpOut(t) { return 1 - Math.pow(1 - t, 3); }

export function initScene(canvas: HTMLCanvasElement, initialTheme: ThemeColors): SceneController {

// Scene setup
const scene = new THREE.Scene();
scene.background = new THREE.Color(initialTheme.background);
scene.fog = new THREE.FogExp2(new THREE.Color(initialTheme.fog).getHex(), 0.008);

const camera = new THREE.PerspectiveCamera(POSE_IDLE.fov, canvas.clientWidth / canvas.clientHeight, 0.1, 500);
camera.position.copy(POSE_IDLE.position);
camera.lookAt(POSE_IDLE.target);

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: 'high-performance' });
renderer.setSize(canvas.clientWidth, canvas.clientHeight, false);
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.2;

// Post-processing
const composer = new EffectComposer(renderer);
composer.setSize(canvas.clientWidth, canvas.clientHeight);
composer.addPass(new RenderPass(scene, camera));
const bloomPass = new UnrealBloomPass(new THREE.Vector2(canvas.clientWidth, canvas.clientHeight), initialTheme.bloomStrength, 0.3, initialTheme.bloomThreshold);
composer.addPass(bloomPass);
composer.addPass(new OutputPass());

// Controls
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.dampingFactor = 0.05;
controls.enablePan = true;
controls.maxPolarAngle = Math.PI / 2.05;
controls.minPolarAngle = 0;
controls.minDistance = 15;
controls.maxDistance = 70;
controls.target.copy(POSE_IDLE.target);
controls.enabled = false;

// Lights
const ambientLight = new THREE.AmbientLight(new THREE.Color(initialTheme.primarySilo), initialTheme.ambientIntensity);
ambientLight.name = 'ambientLight';
scene.add(ambientLight);

const dirLight = new THREE.DirectionalLight(new THREE.Color(initialTheme.accent), initialTheme.directionalIntensity);
dirLight.name = 'directionalLight';
dirLight.position.set(10, 20, 10);
dirLight.castShadow = true;
dirLight.shadow.mapSize.set(2048, 2048);
dirLight.shadow.camera.left = -30;
dirLight.shadow.camera.right = 30;
dirLight.shadow.camera.top = 30;
dirLight.shadow.camera.bottom = -30;
dirLight.shadow.bias = -0.001;
dirLight.shadow.normalBias = 0.02;
scene.add(dirLight);

const pointLight1 = new THREE.PointLight(0x5577ff, 2, 50);
pointLight1.name = 'pointLight1';
pointLight1.position.set(-10, 15, -10);
scene.add(pointLight1);

const pointLight2 = new THREE.PointLight(0x9955ff, 1.5, 50);
pointLight2.name = 'pointLight2';
pointLight2.position.set(10, 15, 10);
scene.add(pointLight2);

// Ground
const groundGeometry = new THREE.CircleGeometry(70, 64);
const groundMaterial = new THREE.MeshStandardMaterial({
  color: new THREE.Color(initialTheme.ground),
  roughness: 0.9,
  metalness: 0.1,
});
const ground = new THREE.Mesh(groundGeometry, groundMaterial);
ground.name = 'ground';
ground.rotation.x = -Math.PI / 2;
ground.receiveShadow = true;
scene.add(ground);

let activeTheme = initialTheme;

// --- COLOR CONSTANTS ---
const CLUSTER_COLOR_HEX = 0x4db5a6;
const CRASH_COLOR_HEX = 0xff3030;
const JOIN_COLOR_HEX = 0x4488ff;

// --- SILO CONFIGURATION ---
const activeSiloCount = 6;
const siloRadius = 13;
const siloSlotPositions = [];
for (let i = 0; i < activeSiloCount; i++) {
  const angle = (i / activeSiloCount) * Math.PI * 2 - Math.PI / 2;
  siloSlotPositions.push(new THREE.Vector3(
    Math.cos(angle) * siloRadius,
    0,
    Math.sin(angle) * siloRadius
  ));
}

// --- PENTAGON SHAPE HELPERS ---
function createPentagonShape(radius) {
  const shape = new THREE.Shape();
  for (let i = 0; i < 6; i++) {
    const angle = (i / 6) * Math.PI * 2 - Math.PI / 2;
    const x = Math.cos(angle) * radius;
    const y = Math.sin(angle) * radius;
    if (i === 0) shape.moveTo(x, y);
    else shape.lineTo(x, y);
  }
  shape.closePath();
  return shape;
}

function createPentagonEdges(radius, height, yOffset, edgeMat) {
  const group = new THREE.Group();
  const vertices = [];
  // NOTE: negate sin on Z so pillars land on the exact same world positions
  // as the extruded Shape vertices after rotation.x = -Math.PI/2 (which
  // maps shape-space +Y → world -Z). Without this, the frame and walls/roof
  // are mirrored 180° around Y and corners don't meet.
  for (let i = 0; i < 6; i++) {
    const angle = (i / 6) * Math.PI * 2 - Math.PI / 2;
    vertices.push(new THREE.Vector3(Math.cos(angle) * radius, 0, -Math.sin(angle) * radius));
  }

  for (let i = 0; i < 6; i++) {
    const pillarGeo = new THREE.CylinderGeometry(0.1, 0.1, height, 8);
    const pillar = new THREE.Mesh(pillarGeo, edgeMat);
    pillar.position.set(vertices[i].x, yOffset + height / 2, vertices[i].z);
    pillar.name = `pillar_${i}`;
    group.add(pillar);
  }

  [yOffset, yOffset + height].forEach((y, ri) => {
    for (let i = 0; i < 6; i++) {
      const next = (i + 1) % 6;
      const start = vertices[i];
      const end = vertices[next];
      const mid = new THREE.Vector3().addVectors(start, end).multiplyScalar(0.5);
      const len = start.distanceTo(end);
      const edgeGeo = new THREE.CylinderGeometry(0.07, 0.07, len, 8);
      const edge = new THREE.Mesh(edgeGeo, edgeMat);
      edge.position.set(mid.x, y, mid.z);
      edge.lookAt(new THREE.Vector3(end.x, y, end.z));
      edge.rotateX(Math.PI / 2);
      edge.name = `edge_${ri}_${i}`;
      group.add(edge);
    }
  });

  return group;
}

// --- WORKFLOW DEFINITIONS ---
const workflowTypes = [
  { name: 'PlayerGrain', steps: ['Init', 'Auth', 'Load', 'Ready', 'Active'], color: 0x66aaff },
  { name: 'InventoryGrain', steps: ['Fetch', 'Validate', 'Update', 'Sync'], color: 0x66ffaa },
  { name: 'OrderGrain', steps: ['Create', 'Process', 'Pay', 'Ship', 'Done'], color: 0xffaa66 },
  { name: 'TimerGrain', steps: ['Schedule', 'Wait', 'Trigger', 'Reset'], color: 0xff66aa },
  { name: 'ChatGrain', steps: ['Connect', 'Subscribe', 'Relay', 'Ack'], color: 0xaaff66 },
];

// --- SILO CONSTRUCTION ---
const siloGroup = new THREE.Group();
siloGroup.name = 'siloGroup';
scene.add(siloGroup);

let siloIdSeq = 0;
const siloLabelLetters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';

function createSilo(position, colorHex, labelIndex) {
  const id = siloIdSeq++;
  const group = new THREE.Group();
  group.name = `silo_${id}`;
  group.position.copy(position);

  const pentRadius = 2.5;
  const siloHeight = 5.5;
  const color = new THREE.Color(colorHex);

  // Base platform pentagon (not color-driven, but fade-capable)
  const baseShape = createPentagonShape(pentRadius + 0.5);
  const baseGeo = new THREE.ExtrudeGeometry(baseShape, { depth: 0.3, bevelEnabled: false });
  const baseMat = new THREE.MeshStandardMaterial({ color: 0x1a2540, roughness: 0.5, metalness: 0.7, transparent: true, opacity: 1 });
  const base = new THREE.Mesh(baseGeo, baseMat);
  base.name = `silo_base_${id}`;
  base.rotation.x = -Math.PI / 2;
  base.position.y = 0;
  base.castShadow = true;
  base.receiveShadow = true;
  group.add(base);

  // Edge material (per-silo so color can animate independently)
  const edgeMat = new THREE.MeshStandardMaterial({
    color: color.clone(),
    emissive: color.clone(),
    emissiveIntensity: 0.4,
    roughness: 0.3,
    metalness: 0.8,
    transparent: true,
    opacity: 1,
  });
  const edges = createPentagonEdges(pentRadius, siloHeight, 0.3, edgeMat);
  edges.name = `silo_edges_${id}`;
  group.add(edges);

  // Transparent pentagon walls
  const wallShape = createPentagonShape(pentRadius - 0.05);
  const wallGeo = new THREE.ExtrudeGeometry(wallShape, { depth: siloHeight, bevelEnabled: false });
  const wallMat = new THREE.MeshStandardMaterial({
    color: color.clone().multiplyScalar(0.15),
    emissive: color.clone(),
    emissiveIntensity: 0.05,
    transparent: true,
    opacity: 0.12,
    side: THREE.DoubleSide,
    roughness: 0.1,
    metalness: 0.9,
  });
  const walls = new THREE.Mesh(wallGeo, wallMat);
  walls.name = `silo_walls_${id}`;
  walls.rotation.x = -Math.PI / 2;
  walls.position.y = 0.3;
  group.add(walls);

  // Inner glow floor
  const floorShape = createPentagonShape(pentRadius - 0.3);
  const floorGeo = new THREE.ShapeGeometry(floorShape);
  const floorMat = new THREE.MeshStandardMaterial({
    color: color.clone(),
    emissive: color.clone(),
    emissiveIntensity: 0.3,
    transparent: true,
    opacity: 0.3,
  });
  const floor = new THREE.Mesh(floorGeo, floorMat);
  floor.name = `silo_floor_${id}`;
  floor.rotation.x = -Math.PI / 2;
  floor.position.y = 0.35;
  group.add(floor);

  // Top cap (pentagon roof)
  const roofShape = createPentagonShape(pentRadius);
  const roofGeo = new THREE.ExtrudeGeometry(roofShape, { depth: 0.15, bevelEnabled: false });
  const roofMat = new THREE.MeshStandardMaterial({
    color: color.clone().multiplyScalar(0.4),
    emissive: color.clone(),
    emissiveIntensity: 0.15,
    transparent: true,
    opacity: 0.5,
    roughness: 0.2,
    metalness: 0.9,
  });
  const roof = new THREE.Mesh(roofGeo, roofMat);
  roof.name = `silo_roof_${id}`;
  roof.rotation.x = -Math.PI / 2;
  roof.position.y = siloHeight + 0.3;
  roof.castShadow = true;
  group.add(roof);

  // --- ORBITAL BANDS (thin line rings, same style as beams) ---
  const band1Mat = new THREE.LineBasicMaterial({
    color: color.clone(),
    transparent: true,
    opacity: 0.4,
  });
  const band2Mat = new THREE.LineBasicMaterial({
    color: color.clone(),
    transparent: true,
    opacity: 0.4,
  });

  const bandSegments = 96;
  const band1Radius = pentRadius + 1.0;
  const band1Points = [];
  for (let i = 0; i < bandSegments; i++) {
    const a = (i / bandSegments) * Math.PI * 2;
    band1Points.push(new THREE.Vector3(Math.cos(a) * band1Radius, 0, Math.sin(a) * band1Radius));
  }
  const band1Geo = new THREE.BufferGeometry().setFromPoints(band1Points);
  const band1Wrapper = new THREE.Group();
  band1Wrapper.position.y = siloHeight * 0.35;
  group.add(band1Wrapper);
  const band1 = new THREE.LineLoop(band1Geo, band1Mat);
  band1.rotation.x = 0.18;
  band1Wrapper.add(band1);

  const band2Radius = pentRadius + 0.7;
  const band2Points = [];
  for (let i = 0; i < bandSegments; i++) {
    const a = (i / bandSegments) * Math.PI * 2;
    band2Points.push(new THREE.Vector3(Math.cos(a) * band2Radius, 0, Math.sin(a) * band2Radius));
  }
  const band2Geo = new THREE.BufferGeometry().setFromPoints(band2Points);
  const band2Wrapper = new THREE.Group();
  band2Wrapper.position.y = siloHeight * 0.72;
  group.add(band2Wrapper);
  const band2 = new THREE.LineLoop(band2Geo, band2Mat);
  band2.rotation.x = -0.14;
  band2Wrapper.add(band2);

  // --- PILLAR DATA STREAMS (energy pulses traveling up each pillar) ---
  const pillarStreamGeo = new THREE.SphereGeometry(0.09, 10, 10);
  const pillarStreamMats = [];
  const pillarStreams = [];
  for (let i = 0; i < 6; i++) {
    const a = (i / 6) * Math.PI * 2 - Math.PI / 2;
    const px = Math.cos(a) * pentRadius;
    const pz = -Math.sin(a) * pentRadius;
    const smat = new THREE.MeshStandardMaterial({
      color: color.clone(),
      emissive: color.clone(),
      emissiveIntensity: 2.5,
      transparent: true,
      opacity: 0.9,
    });
    const stream = new THREE.Mesh(pillarStreamGeo, smat);
    stream.name = `silo_stream_${id}_${i}`;
    stream.position.set(px, 0, pz);
    stream.userData = { phase: i / 6 };
    pillarStreamMats.push(smat);
    pillarStreams.push(stream);
    group.add(stream);
  }

  // --- BEACON (futuristic antenna replacement) ---
  const beaconGroup = new THREE.Group();
  beaconGroup.name = `silo_beacon_${id}`;
  beaconGroup.position.y = siloHeight + 0.8;
  group.add(beaconGroup);

  // Faceted core orb
  const coreGeo = new THREE.IcosahedronGeometry(0.22, 1);
  const coreMat = new THREE.MeshStandardMaterial({
    color: color.clone(),
    emissive: color.clone(),
    emissiveIntensity: 2.8,
    transparent: true,
    opacity: 1,
    roughness: 0.25,
    metalness: 0.85,
  });
  const core = new THREE.Mesh(coreGeo, coreMat);
  core.name = `silo_beacon_core_${id}`;
  core.position.y = 0.7;
  beaconGroup.add(core);

  // Two orbital rings (shared mat, different rotations)
  const beaconRingGeo = new THREE.TorusGeometry(0.5, 0.02, 8, 48);
  const beaconRingMat = new THREE.MeshStandardMaterial({
    color: color.clone(),
    emissive: color.clone(),
    emissiveIntensity: 1.4,
    transparent: true,
    opacity: 0.75,
  });
  const ringA = new THREE.Mesh(beaconRingGeo, beaconRingMat);
  ringA.name = `silo_beacon_ringA_${id}`;
  ringA.position.y = 0.7;
  beaconGroup.add(ringA);

  const ringB = new THREE.Mesh(beaconRingGeo, beaconRingMat);
  ringB.name = `silo_beacon_ringB_${id}`;
  ringB.position.y = 0.7;
  beaconGroup.add(ringB);

  // Tapered upward energy column
  const beamGeo = new THREE.CylinderGeometry(0.015, 0.08, 2.4, 12, 1, true);
  const beamMat = new THREE.MeshBasicMaterial({
    color: color.clone(),
    transparent: true,
    opacity: 0.45,
    depthWrite: false,
    side: THREE.DoubleSide,
    blending: THREE.AdditiveBlending,
  });
  const beam = new THREE.Mesh(beamGeo, beamMat);
  beam.name = `silo_beacon_beam_${id}`;
  beam.position.y = 2.0;
  beaconGroup.add(beam);

  // Upward-traveling energy pulses (3 offset phases)
  const beaconPulseGeo = new THREE.SphereGeometry(0.07, 10, 10);
  const pulseMats = [];
  const beaconPulses = [];
  for (let i = 0; i < 3; i++) {
    const pm = new THREE.MeshStandardMaterial({
      color: color.clone(),
      emissive: color.clone(),
      emissiveIntensity: 3.2,
      transparent: true,
      opacity: 1,
    });
    const pulse = new THREE.Mesh(beaconPulseGeo, pm);
    pulse.name = `silo_beacon_pulse_${id}_${i}`;
    pulse.userData.phase = i / 3;
    pulseMats.push(pm);
    beaconPulses.push(pulse);
    beaconGroup.add(pulse);
  }

  // Point light inside
  const innerLight = new THREE.PointLight(color.clone(), 1.5, 8);
  innerLight.name = `silo_innerlight_${id}`;
  innerLight.position.y = siloHeight / 2 + 0.3;
  group.add(innerLight);

  // Pulsing pentagon ring at base — 5 cylinders aligned with pillar vertices
  const ringMat = new THREE.MeshStandardMaterial({ color: color.clone(), emissive: color.clone(), emissiveIntensity: 1, transparent: true, opacity: 0.6 });
  const ring = new THREE.Group();
  ring.name = `silo_ring_${id}`;
  ring.position.y = 0.35;
  const ringRadius = pentRadius + 0.3;
  const ringVerts = [];
  for (let i = 0; i < 6; i++) {
    const a = (i / 6) * Math.PI * 2 - Math.PI / 2;
    ringVerts.push(new THREE.Vector3(Math.cos(a) * ringRadius, 0, -Math.sin(a) * ringRadius));
  }
  const ringSegGeos = [];
  for (let i = 0; i < 6; i++) {
    const start = ringVerts[i];
    const end = ringVerts[(i + 1) % 6];
    const mid = start.clone().add(end).multiplyScalar(0.5);
    const len = start.distanceTo(end);
    const segGeo = new THREE.CylinderGeometry(0.04, 0.04, len, 8);
    ringSegGeos.push(segGeo);
    const seg = new THREE.Mesh(segGeo, ringMat);
    seg.position.copy(mid);
    seg.lookAt(new THREE.Vector3(end.x, 0, end.z));
    seg.rotateX(Math.PI / 2);
    ring.add(seg);
  }
  group.add(ring);

  siloGroup.add(group);

  const silo = {
    id,
    label: siloLabelLetters[labelIndex ?? id % siloLabelLetters.length],
    group,
    position: position.clone(),
    siloHeight,
    pentRadius,
    // refs to color-bearing parts
    edgeMat,
    wallMat,
    floorMat,
    roofMat,
    coreMat,
    beaconRingMat,
    beamMat,
    pulseMats,
    ringMat,
    band1Mat,
    band2Mat,
    pillarStreamMats,
    innerLight,
    baseMat,
    // refs for animated parts
    ring,
    floor,
    core,
    ringA,
    ringB,
    beam,
    beaconPulses,
    band1Wrapper,
    band2Wrapper,
    pillarStreams,
    // fade tracking (material + baseOpacity)
    fadeParts: [
      { material: baseMat, base: 1.0 },
      { material: edgeMat, base: 1.0 },
      { material: wallMat, base: 0.12 },
      { material: floorMat, base: 0.3 },
      { material: roofMat, base: 0.5 },
      { material: coreMat, base: 1.0 },
      { material: beaconRingMat, base: 0.75 },
      { material: beamMat, base: 0.45 },
      // ring + pulseMats + pillarStreamMats + band{1,2}Mat handled separately (animated per-frame)
    ],
    // disposal list
    disposables: [baseGeo, wallGeo, floorGeo, roofGeo, coreGeo, beaconRingGeo, beamGeo, beaconPulseGeo, band1Geo, band2Geo, pillarStreamGeo, ...ringSegGeos, baseMat, edgeMat, wallMat, floorMat, roofMat, coreMat, beaconRingMat, beamMat, ...pulseMats, ringMat, band1Mat, band2Mat, ...pillarStreamMats],
    pillarEdgeGeos: [],  // collected below
    // lifecycle state
    status: 'active',           // active | failing | collapsed | rising | joining
    baseColor: new THREE.Color(CLUSTER_COLOR_HEX),
    currentColor: new THREE.Color(colorHex),
    yOffset: 0,
    opacityMul: 1,
    shakeX: 0,
    shakeZ: 0,
    // content
    grains: [],
    workflows: [],
    pulsePhase: Math.random() * Math.PI * 2,
    spawnedAt: performance.now(),
  };

  // Collect edge pillar/edge geos for disposal (they live under edges group)
  edges.traverse((o) => {
    if (o.isMesh && o.geometry) silo.pillarEdgeGeos.push(o.geometry);
  });
  silo.disposables.push(...silo.pillarEdgeGeos);

  return silo;
}

// --- COLOR APPLICATION ---
const _tmpColor = new THREE.Color();

function setSiloColor(silo, color) {
  const c = color;
  silo.edgeMat.color.copy(c);
  silo.edgeMat.emissive.copy(c);
  silo.wallMat.color.copy(_tmpColor.copy(c).multiplyScalar(0.15));
  silo.wallMat.emissive.copy(c);
  silo.floorMat.color.copy(c);
  silo.floorMat.emissive.copy(c);
  silo.roofMat.color.copy(_tmpColor.copy(c).multiplyScalar(0.4));
  silo.roofMat.emissive.copy(c);
  silo.coreMat.color.copy(c);
  silo.coreMat.emissive.copy(c);
  silo.beaconRingMat.color.copy(c);
  silo.beaconRingMat.emissive.copy(c);
  silo.beamMat.color.copy(c);
  silo.pulseMats.forEach((pm) => {
    pm.color.copy(c);
    pm.emissive.copy(c);
  });
  silo.band1Mat.color.copy(c);
  silo.band2Mat.color.copy(c);
  silo.pillarStreamMats.forEach((sm) => {
    sm.color.copy(c);
    sm.emissive.copy(c);
  });
  silo.innerLight.color.copy(c);
  silo.ringMat.color.copy(c);
  silo.ringMat.emissive.copy(c);
  silo.currentColor.copy(c);
}

function applyFade(silo) {
  const m = silo.opacityMul;
  silo.fadeParts.forEach((fp) => {
    fp.material.opacity = fp.base * m;
  });
  // ring opacity is set by the pulse step; multiply here
  silo.ringMat.opacity *= m;
  // grains also fade
  silo.grains.forEach((g) => {
    g.material.opacity = (g.userData.baseOpacity ?? 1) * m;
  });
}

function disposeSilo(silo) {
  // Remove children and dispose
  silo.group.parent?.remove(silo.group);
  // Dispose grain geos/materials (geometry is shared but owned here)
  silo.grains.forEach((g) => {
    g.material.dispose();
  });
  // Dispose workflow visual assets (they were added to scene)
  silo.workflows.forEach((wf) => disposeWorkflow(wf));
  silo.workflows.length = 0;
  silo.disposables.forEach((d) => d.dispose());
}

// --- WORKFLOW CREATION ---
function createWorkflow(silo, workflowDef, yLevel, orbitOffset) {
  const wf = {
    name: workflowDef.name,
    steps: workflowDef.steps,
    color: new THREE.Color(workflowDef.color),
    nodes: [],
    connections: [],
    activeStep: 0,
    timer: 0,
    interval: 1.2 + Math.random() * 1.0,
    yLevel: yLevel,
    orbitOffset: orbitOffset,
    orbitSpeed: 0.15 + Math.random() * 0.15,
    silo: silo,
    migration: null,
  };

  const stepCount = workflowDef.steps.length;
  const angleSpread = (Math.PI * 2) / Math.max(stepCount, 1);

  // BPMN-style task boxes (start = thin sphere, end = sphere with extra halo, middle = box)
  for (let s = 0; s < stepCount; s++) {
    const isStart = s === 0;
    const isEnd = s === stepCount - 1;
    let nodeGeo;
    if (isStart) {
      nodeGeo = new THREE.SphereGeometry(0.13, 12, 12);
    } else if (isEnd) {
      nodeGeo = new THREE.SphereGeometry(0.17, 12, 12);
    } else {
      // Task box — rectangular BPMN activity
      nodeGeo = new THREE.BoxGeometry(0.38, 0.24, 0.14);
    }
    const nodeMat = new THREE.MeshStandardMaterial({
      color: workflowDef.color,
      emissive: workflowDef.color,
      emissiveIntensity: 0.3,
      transparent: true,
      opacity: 0.85,
      roughness: 0.3,
      metalness: 0.5,
    });
    const nodeMesh = new THREE.Mesh(nodeGeo, nodeMat);
    nodeMesh.name = `wf_node_${silo.id}_${wf.name}_${s}`;
    scene.add(nodeMesh);

    // Outer halo ring: thick double ring for end event, single ring otherwise
    const ringOuterRadius = (isStart ? 0.22 : isEnd ? 0.30 : 0.28);
    const ringGeo = new THREE.TorusGeometry(ringOuterRadius, 0.02, 8, 24);
    const ringMat = new THREE.MeshStandardMaterial({
      color: workflowDef.color,
      emissive: workflowDef.color,
      emissiveIntensity: 0.2,
      transparent: true,
      opacity: 0.4,
    });
    const ringMesh = new THREE.Mesh(ringGeo, ringMat);
    ringMesh.name = `wf_ring_${silo.id}_${wf.name}_${s}`;
    scene.add(ringMesh);

    // End-event second ring (BPMN end = thick border = double circle)
    let endRing = null;
    let endRingGeo = null;
    if (isEnd) {
      endRingGeo = new THREE.TorusGeometry(0.24, 0.018, 8, 24);
      const endRingMat = new THREE.MeshStandardMaterial({
        color: workflowDef.color,
        emissive: workflowDef.color,
        emissiveIntensity: 0.2,
        transparent: true,
        opacity: 0.4,
      });
      endRing = new THREE.Mesh(endRingGeo, endRingMat);
      endRing.name = `wf_endring_${silo.id}_${wf.name}_${s}`;
      scene.add(endRing);
    }

    wf.nodes.push({ mesh: nodeMesh, ring: ringMesh, endRing, stepIndex: s, angle: angleSpread * s, geo: nodeGeo, ringGeo, endRingGeo, isStart, isEnd });
  }

  // Connection = line + arrowhead cone at destination end
  for (let s = 0; s < stepCount; s++) {
    const next = (s + 1) % stepCount;
    const lineGeo = new THREE.BufferGeometry();
    const positions = new Float32Array(6);
    lineGeo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    const lineMat = new THREE.LineBasicMaterial({
      color: workflowDef.color,
      transparent: true,
      opacity: 0.3,
    });
    const line = new THREE.Line(lineGeo, lineMat);
    line.name = `wf_conn_${silo.id}_${wf.name}_${s}`;
    scene.add(line);

    const coneGeo = new THREE.ConeGeometry(0.06, 0.16, 8);
    const coneMat = new THREE.MeshStandardMaterial({
      color: workflowDef.color,
      emissive: workflowDef.color,
      emissiveIntensity: 0.7,
      transparent: true,
      opacity: 0.55,
    });
    const cone = new THREE.Mesh(coneGeo, coneMat);
    cone.name = `wf_arrow_${silo.id}_${wf.name}_${s}`;
    scene.add(cone);

    wf.connections.push({ line, from: s, to: next, geo: lineGeo, cone, coneGeo, coneMat });
  }

  const runnerGeo = new THREE.SphereGeometry(0.1, 8, 8);
  const runnerMat = new THREE.MeshStandardMaterial({
    color: 0xffffff,
    emissive: workflowDef.color,
    emissiveIntensity: 2.5,
    transparent: true,
    opacity: 1,
  });
  const runner = new THREE.Mesh(runnerGeo, runnerMat);
  runner.name = `wf_runner_${silo.id}_${wf.name}`;
  scene.add(runner);
  wf.runner = runner;
  wf.runnerGeo = runnerGeo;

  const trailCount = 8;
  const trailPositions = [];
  for (let i = 0; i < trailCount; i++) trailPositions.push(new THREE.Vector3());
  const trailGeo = new THREE.BufferGeometry().setFromPoints(trailPositions);
  const trailMat = new THREE.LineBasicMaterial({ color: workflowDef.color, transparent: true, opacity: 0.5 });
  const trail = new THREE.Line(trailGeo, trailMat);
  trail.name = `wf_trail_${silo.id}_${wf.name}`;
  scene.add(trail);
  wf.trail = trail;
  wf.trailGeo = trailGeo;
  wf.trailPositions = trailPositions;

  return wf;
}

function disposeWorkflow(wf) {
  wf.nodes.forEach((n) => {
    scene.remove(n.mesh);
    scene.remove(n.ring);
    n.geo.dispose();
    n.ringGeo.dispose();
    n.mesh.material.dispose();
    n.ring.material.dispose();
    if (n.endRing) {
      scene.remove(n.endRing);
      n.endRingGeo.dispose();
      n.endRing.material.dispose();
    }
  });
  wf.connections.forEach((c) => {
    scene.remove(c.line);
    c.geo.dispose();
    c.line.material.dispose();
    scene.remove(c.cone);
    c.coneGeo.dispose();
    c.coneMat.dispose();
  });
  scene.remove(wf.runner);
  wf.runnerGeo.dispose();
  wf.runner.material.dispose();
  scene.remove(wf.trail);
  wf.trailGeo.dispose();
  wf.trail.material.dispose();
}

// --- GRAIN CREATION ---
const grainGeometry = new THREE.OctahedronGeometry(0.06, 0);
const grainInstanceCount = 25;

function populateGrains(silo) {
  for (let g = 0; g < grainInstanceCount; g++) {
    const grainMat = new THREE.MeshStandardMaterial({
      color: silo.currentColor.clone().lerp(new THREE.Color(0xffffff), Math.random() * 0.3),
      emissive: silo.currentColor.clone(),
      emissiveIntensity: 0.4 + Math.random() * 0.4,
      roughness: 0.3,
      metalness: 0.5,
      transparent: true,
      opacity: 1,
    });
    const grain = new THREE.Mesh(grainGeometry, grainMat);
    grain.name = `grain_${silo.id}_${g}`;
    const angle = Math.random() * Math.PI * 2;
    const radius = 0.3 + Math.random() * (silo.pentRadius * 0.7);
    const height = 0.8 + Math.random() * (silo.siloHeight - 1.0);
    grain.position.set(
      Math.cos(angle) * radius,
      height,
      Math.sin(angle) * radius,
    );
    grain.userData = {
      speed: 0.3 + Math.random() * 1.2,
      phase: Math.random() * Math.PI * 2,
      orbitRadius: radius,
      orbitAngle: angle,
      yBase: height,
      baseOpacity: 1,
    };
    silo.group.add(grain);
    silo.grains.push(grain);
  }
}

function populateWorkflows(silo, workflowCount) {
  const wfCount = workflowCount ?? (2 + (silo.id % 2));
  for (let w = 0; w < wfCount; w++) {
    const wfDef = workflowTypes[(silo.id + w) % workflowTypes.length];
    const yLevel = 1.2 + w * 1.6;
    const wf = createWorkflow(silo, wfDef, yLevel, w * (Math.PI * 2 / wfCount));
    silo.workflows.push(wf);
  }
}

// --- CLUSTER ORCHESTRATOR ---
const cluster = {
  active: [],
  seq: {
    phase: 'idle',
    phaseTimer: 0,
    victim: null,
    replacement: null,
    victimSlotIndex: -1,
  },
  beams: [],
};

// Initial 3 silos, all cluster color
for (let i = 0; i < activeSiloCount; i++) {
  const silo = createSilo(siloSlotPositions[i], CLUSTER_COLOR_HEX, i);
  populateGrains(silo);
  populateWorkflows(silo);
  cluster.active.push(silo);
}

// --- BEAMS ---
const beamGroup = new THREE.Group();
beamGroup.name = 'beamGroup';
scene.add(beamGroup);

let beamIdSeq = 0;

function createBeam(siloA, siloB) {
  const start = siloA.position.clone().add(new THREE.Vector3(0, siloA.siloHeight + 0.5, 0));
  const end = siloB.position.clone().add(new THREE.Vector3(0, siloB.siloHeight + 0.5, 0));
  const mid = start.clone().add(end).multiplyScalar(0.5);
  mid.y += 4 + Math.random() * 2;

  const curve = new THREE.QuadraticBezierCurve3(start, mid, end);
  const curvePoints = curve.getPoints(40);

  const lineGeo = new THREE.BufferGeometry().setFromPoints(curvePoints);
  const colorA = siloA.currentColor;
  const colorB = siloB.currentColor;
  const mixedColor = colorA.clone().lerp(colorB, 0.5);

  const lineMat = new THREE.LineBasicMaterial({
    color: mixedColor,
    transparent: true,
    opacity: 0.12,
  });
  const line = new THREE.Line(lineGeo, lineMat);
  const id = beamIdSeq++;
  line.name = `beam_line_${id}`;
  beamGroup.add(line);

  return { id, line, curve, colorA, colorB, mixedColor, siloA, siloB, packets: [], lineGeo, lineMat };
}

function rebuildBeams() {
  // dispose current
  cluster.beams.forEach((beam) => {
    // also dispose packets in transit
    beam.packets.forEach((pkt) => {
      scene.remove(pkt.mesh);
      scene.remove(pkt.trail);
      pkt.mesh.geometry.dispose();
      pkt.mesh.material.dispose();
      pkt.trail.geometry.dispose();
      pkt.trail.material.dispose();
    });
    beamGroup.remove(beam.line);
    beam.lineGeo.dispose();
    beam.lineMat.dispose();
  });
  cluster.beams.length = 0;

  // build new — all pairs among active silos
  const active = cluster.active;
  for (let i = 0; i < active.length; i++) {
    for (let j = i + 1; j < active.length; j++) {
      cluster.beams.push(createBeam(active[i], active[j]));
    }
  }
}
rebuildBeams();

// --- PACKETS ---
const packetGeometry = new THREE.SphereGeometry(0.13, 8, 8);

function spawnPacket(beam) {
  const forward = Math.random() > 0.5;
  const col = forward ? beam.colorA : beam.colorB;
  const packetMat = new THREE.MeshStandardMaterial({
    color: col,
    emissive: col,
    emissiveIntensity: 3,
    transparent: true,
    opacity: 1,
  });
  const packet = new THREE.Mesh(packetGeometry, packetMat);
  packet.name = `packet_${beam.id}_${Date.now()}_${Math.random().toFixed(3)}`;
  scene.add(packet);

  const trailPoints = [];
  for (let i = 0; i < 12; i++) trailPoints.push(new THREE.Vector3());
  const trailGeo = new THREE.BufferGeometry().setFromPoints(trailPoints);
  const trailMat = new THREE.LineBasicMaterial({ color: col, transparent: true, opacity: 0.5 });
  const trail = new THREE.Line(trailGeo, trailMat);
  trail.name = `trail_${beam.id}_${Date.now()}`;
  scene.add(trail);

  beam.packets.push({
    mesh: packet,
    trail,
    trailPositions: trailPoints,
    t: forward ? 0 : 1,
    speed: (0.15 + Math.random() * 0.25) * (forward ? 1 : -1),
    alive: true,
  });
}

// --- PARTICLE FIELD ---
const particleCount = 400;
const particlesGeo = new THREE.BufferGeometry();
const particlePositions = new Float32Array(particleCount * 3);
const particleColors = new Float32Array(particleCount * 3);
for (let i = 0; i < particleCount; i++) {
  particlePositions[i * 3] = (Math.random() - 0.5) * 70;
  particlePositions[i * 3 + 1] = Math.random() * 25;
  particlePositions[i * 3 + 2] = (Math.random() - 0.5) * 70;
  const c = new THREE.Color().setHSL(0.55 + Math.random() * 0.2, 0.8, 0.5 + Math.random() * 0.3);
  particleColors[i * 3] = c.r;
  particleColors[i * 3 + 1] = c.g;
  particleColors[i * 3 + 2] = c.b;
}
particlesGeo.setAttribute('position', new THREE.BufferAttribute(particlePositions, 3));
particlesGeo.setAttribute('color', new THREE.BufferAttribute(particleColors, 3));
const particlesMat = new THREE.PointsMaterial({ size: 0.07, vertexColors: true, transparent: true, opacity: 0.4, sizeAttenuation: true });
const particles = new THREE.Points(particlesGeo, particlesMat);
particles.name = 'ambientParticles';
scene.add(particles);

// HUD removed — the landing page renders its own hero text above the canvas.

// --- SEQUENCE STATE MACHINE ---
const PHASE_DURATIONS = {
  idle: 8.0,
  flee: 2.0,
  crashing: 1.5,
  collapsed: 1.0,
  gap: 1.0,
  rising: 2.0,
  colorShift: 1.5,
  rebalance: 2.5,
};

const MIGRATION_FLEE_DURATION = 1.8;
const MIGRATION_REBALANCE_DURATION = 2.2;

const STATUS_BY_PHASE = {
  idle: 'Running',
  flee: 'Failing',
  crashing: 'Crashing',
  collapsed: 'Collapsed',
  gap: 'Offline',
  rising: 'Joining',
  colorShift: 'Joining',
  rebalance: 'Joining',
};

function easeInOut(x) {
  return x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2;
}

function quadBezier(p0, p1, p2, t, out) {
  const mt = 1 - t;
  out.x = mt * mt * p0.x + 2 * mt * t * p1.x + t * t * p2.x;
  out.y = mt * mt * p0.y + 2 * mt * t * p1.y + t * t * p2.y;
  out.z = mt * mt * p0.z + 2 * mt * t * p1.z + t * t * p2.z;
  return out;
}

function pickSurvivors(victim) {
  return cluster.active.filter((s) => s !== victim && s.status !== 'collapsed');
}

function startMigration(wf, targetSilo, duration) {
  const fromCenter = wf.silo.position.clone();
  fromCenter.y = wf.yLevel + wf.silo.yOffset;
  const toCenter = targetSilo.position.clone();
  toCenter.y = wf.yLevel;
  const arcMid = fromCenter.clone().add(toCenter).multiplyScalar(0.5);
  arcMid.y = Math.max(fromCenter.y, toCenter.y) + 6;

  wf.migration = {
    fromCenter,
    arcMid,
    toCenter,
    target: targetSilo,
    elapsed: 0,
    duration,
  };
}

function enterFlee() {
  const pool = cluster.active.filter((s) => s.status === 'active');
  if (pool.length < 3) return; // abort if something went wrong
  const victim = pool[Math.floor(Math.random() * pool.length)];
  victim.status = 'failing';
  cluster.seq.victim = victim;
  cluster.seq.victimSlotIndex = siloSlotPositions.findIndex((p) => p.distanceTo(victim.position) < 0.01);

  const survivors = pickSurvivors(victim);
  // Snapshot workflows to migrate (don't iterate a list being mutated)
  const fleeingWorkflows = victim.workflows.slice();
  fleeingWorkflows.forEach((wf, i) => {
    const target = survivors[i % survivors.length];
    startMigration(wf, target, MIGRATION_FLEE_DURATION);
  });
}

function enterCrashing() {
  // Spawn a few packet flashes on beams connected to victim as "sparks"
  const victim = cluster.seq.victim;
  if (!victim) return;
  const victimBeams = cluster.beams.filter((b) => b.siloA === victim || b.siloB === victim);
  for (let i = 0; i < 4; i++) {
    const b = victimBeams[i % Math.max(1, victimBeams.length)];
    if (b) spawnPacket(b);
  }
}

function enterCollapsed() {
  // handled by per-frame progress; no-op on entry
}

function finalizeCollapse() {
  const victim = cluster.seq.victim;
  if (!victim) return;
  const idx = cluster.active.indexOf(victim);
  if (idx !== -1) cluster.active.splice(idx, 1);
  rebuildBeams();
}

function enterGap() {
  // Dispose victim after collapse finalize
  const victim = cluster.seq.victim;
  if (victim) {
    disposeSilo(victim);
  }
}

function enterRising() {
  const slotIdx = cluster.seq.victimSlotIndex >= 0 ? cluster.seq.victimSlotIndex : 0;
  const pos = siloSlotPositions[slotIdx];
  const replacement = createSilo(pos, JOIN_COLOR_HEX, slotIdx);
  replacement.status = 'rising';
  replacement.yOffset = -8;
  replacement.opacityMul = 0;
  populateGrains(replacement);
  // No workflows initially — they'll rebalance in
  cluster.active.push(replacement);
  cluster.seq.replacement = replacement;
  rebuildBeams();
}

function enterColorShift() {
  const replacement = cluster.seq.replacement;
  if (!replacement) return;
  replacement.status = 'joining';
}

function enterRebalance() {
  const replacement = cluster.seq.replacement;
  if (!replacement) return;
  const survivors = cluster.active.filter((s) => s !== replacement && s.status === 'active');
  // Pick 1 workflow from each survivor
  survivors.forEach((s) => {
    if (s.workflows.length === 0) return;
    // Pick a workflow that's not currently migrating
    const candidates = s.workflows.filter((wf) => wf.migration === null);
    if (candidates.length === 0) return;
    const wf = candidates[Math.floor(Math.random() * candidates.length)];
    startMigration(wf, replacement, MIGRATION_REBALANCE_DURATION);
  });
}

function finalizeRebalance() {
  const replacement = cluster.seq.replacement;
  if (replacement) {
    replacement.status = 'active';
    setSiloColor(replacement, replacement.baseColor);
  }
  cluster.seq.victim = null;
  cluster.seq.replacement = null;
  cluster.seq.victimSlotIndex = -1;
}

function advanceSequence(dt) {
  cluster.seq.phaseTimer += dt;
  const dur = PHASE_DURATIONS[cluster.seq.phase];
  if (cluster.seq.phaseTimer < dur) return;

  // Phase transition
  cluster.seq.phaseTimer = 0;
  switch (cluster.seq.phase) {
    case 'idle':
      cluster.seq.phase = 'flee';
      enterFlee();
      break;
    case 'flee':
      cluster.seq.phase = 'crashing';
      enterCrashing();
      break;
    case 'crashing':
      cluster.seq.phase = 'collapsed';
      enterCollapsed();
      break;
    case 'collapsed':
      finalizeCollapse();
      cluster.seq.phase = 'gap';
      enterGap();
      break;
    case 'gap':
      cluster.seq.phase = 'rising';
      enterRising();
      break;
    case 'rising':
      cluster.seq.phase = 'colorShift';
      enterColorShift();
      break;
    case 'colorShift':
      cluster.seq.phase = 'rebalance';
      enterRebalance();
      break;
    case 'rebalance':
      finalizeRebalance();
      cluster.seq.phase = 'idle';
      break;
  }
}

// --- PER-FRAME CRASH/RISE/COLOR VISUAL UPDATES ---
const _tmpColorA = new THREE.Color();
const _crashColor = new THREE.Color(CRASH_COLOR_HEX);
const _joinColor = new THREE.Color(JOIN_COLOR_HEX);
const _clusterColor = new THREE.Color(CLUSTER_COLOR_HEX);
const _arrowDir = new THREE.Vector3();
const _upVec = new THREE.Vector3(0, 1, 0);

function updatePhaseVisuals() {
  const { phase, phaseTimer } = cluster.seq;
  const victim = cluster.seq.victim;
  const replacement = cluster.seq.replacement;

  // Default: active silos remain at cluster color, full opacity, zero offset
  cluster.active.forEach((s) => {
    if (s === victim || s === replacement) return;
    s.opacityMul = 1;
    s.yOffset = 0;
    s.shakeX = 0;
    s.shakeZ = 0;
    if (!s.currentColor.equals(s.baseColor)) {
      setSiloColor(s, s.baseColor);
    }
  });

  if (victim) {
    const progress = Math.min(1, phaseTimer / PHASE_DURATIONS[phase]);
    if (phase === 'flee') {
      // Victim wobbles slightly, starts dimming emissive on ring
      victim.shakeX = (Math.random() - 0.5) * 0.05 * progress;
      victim.shakeZ = (Math.random() - 0.5) * 0.05 * progress;
    } else if (phase === 'crashing') {
      const shake = Math.min(1, progress * 1.5) * 0.35;
      victim.shakeX = (Math.random() - 0.5) * shake;
      victim.shakeZ = (Math.random() - 0.5) * shake;
      _tmpColorA.copy(victim.baseColor).lerp(_crashColor, easeInOut(progress));
      setSiloColor(victim, _tmpColorA);
      victim.opacityMul = 1 - progress * 0.15;
    } else if (phase === 'collapsed') {
      victim.shakeX = (Math.random() - 0.5) * 0.1 * (1 - progress);
      victim.shakeZ = (Math.random() - 0.5) * 0.1 * (1 - progress);
      const e = easeInOut(progress);
      victim.yOffset = -10 * e;
      victim.opacityMul = (1 - e) * 0.85;
      setSiloColor(victim, _crashColor);
    }
  }

  if (replacement) {
    const progress = Math.min(1, phaseTimer / PHASE_DURATIONS[phase]);
    if (phase === 'rising') {
      const e = easeInOut(progress);
      replacement.yOffset = -8 + 8 * e;
      replacement.opacityMul = e;
      setSiloColor(replacement, _joinColor);
    } else if (phase === 'colorShift') {
      replacement.yOffset = 0;
      replacement.opacityMul = 1;
      _tmpColorA.copy(_joinColor).lerp(_clusterColor, easeInOut(progress));
      setSiloColor(replacement, _tmpColorA);
    } else if (phase === 'rebalance') {
      replacement.yOffset = 0;
      replacement.opacityMul = 1;
      setSiloColor(replacement, replacement.baseColor);
    }
  }
}

// --- WORKFLOW MIGRATION ---
const _migVec = new THREE.Vector3();

function updateMigratingWorkflow(wf, t, dt) {
  const mig = wf.migration;
  mig.elapsed += dt;
  const u = Math.min(1, mig.elapsed / mig.duration);
  const eased = easeInOut(u);
  quadBezier(mig.fromCenter, mig.arcMid, mig.toCenter, eased, _migVec);

  const stepCount = wf.nodes.length;
  const baseAngle = t * wf.orbitSpeed + wf.orbitOffset;
  // Tighter formation while flying
  const packedRadius = 0.5 + (1 - u) * 0.3;
  wf.nodes.forEach((node, s) => {
    const a = baseAngle + (s / stepCount) * Math.PI * 2;
    node.mesh.position.set(
      _migVec.x + Math.cos(a) * packedRadius,
      _migVec.y + Math.sin(t * 2 + s) * 0.1,
      _migVec.z + Math.sin(a) * packedRadius,
    );
    node.mesh.lookAt(camera.position);
    node.ring.position.copy(node.mesh.position);
    node.ring.lookAt(camera.position);
    if (node.endRing) {
      node.endRing.position.copy(node.mesh.position);
      node.endRing.lookAt(camera.position);
    }

    const isActive = s === wf.activeStep;
    node.mesh.material.emissiveIntensity = isActive ? 1.5 : 0.3;
    node.mesh.material.opacity = isActive ? 1.0 : 0.7;
    node.mesh.scale.setScalar(isActive ? 1.35 : 1.0);
    node.ring.material.opacity = isActive ? 0.85 : 0.35;
    node.ring.material.emissiveIntensity = isActive ? 1.0 : 0.2;
    if (node.endRing) {
      node.endRing.material.opacity = isActive ? 0.85 : 0.35;
      node.endRing.material.emissiveIntensity = isActive ? 1.0 : 0.2;
    }
  });

  wf.connections.forEach((conn) => {
    const fromPos = wf.nodes[conn.from].mesh.position;
    const toPos = wf.nodes[conn.to].mesh.position;
    const posAttr = conn.line.geometry.attributes.position;
    posAttr.array[0] = fromPos.x;
    posAttr.array[1] = fromPos.y;
    posAttr.array[2] = fromPos.z;
    posAttr.array[3] = toPos.x;
    posAttr.array[4] = toPos.y;
    posAttr.array[5] = toPos.z;
    posAttr.needsUpdate = true;
    conn.line.material.opacity = 0.45;

    conn.cone.position.lerpVectors(fromPos, toPos, 0.78);
    _arrowDir.subVectors(toPos, fromPos).normalize();
    conn.cone.quaternion.setFromUnitVectors(_upVec, _arrowDir);
    conn.coneMat.opacity = 0.6;
  });

  wf.runner.position.copy(_migVec);
  wf.runner.material.emissiveIntensity = 3.0 + Math.sin(t * 8) * 0.8;

  for (let i = wf.trailPositions.length - 1; i > 0; i--) {
    wf.trailPositions[i].copy(wf.trailPositions[i - 1]);
  }
  wf.trailPositions[0].copy(wf.runner.position);
  wf.trail.geometry.setFromPoints(wf.trailPositions);

  if (u >= 1) {
    const fromSilo = wf.silo;
    const toSilo = mig.target;
    const idx = fromSilo.workflows.indexOf(wf);
    if (idx !== -1) fromSilo.workflows.splice(idx, 1);
    toSilo.workflows.push(wf);
    wf.silo = toSilo;
    wf.migration = null;
    wf.timer = 0;
  }
}

// --- ANIMATION ---
const clock = new THREE.Clock();
let totalPackets = 0;
let messageCount = 0;
let spawnTimer = 0;
let totalWorkflowSteps = 0;
let legendLastPhase = null;

function animate() {
  const dt = Math.min(clock.getDelta(), 0.05);
  const t = clock.getElapsedTime();

  controls.update();

  // Sequence drives high-level changes
  advanceSequence(dt);
  updatePhaseVisuals();

  // Animate each tracked silo (including victim + replacement if present)
  const renderSilos = cluster.active.slice();
  if (cluster.seq.victim && !renderSilos.includes(cluster.seq.victim)) {
    renderSilos.push(cluster.seq.victim);
  }

  renderSilos.forEach((silo) => {
    // Apply group transform (yOffset + shake)
    silo.group.position.set(
      silo.position.x + silo.shakeX,
      silo.yOffset,
      silo.position.z + silo.shakeZ,
    );

    // Pulse ring
    const pulse = 0.5 + Math.sin(t * 2 + silo.pulsePhase) * 0.3;
    silo.ringMat.opacity = pulse;
    silo.ring.scale.setScalar(1 + Math.sin(t * 1.5 + silo.pulsePhase) * 0.04);

    // Beacon animation
    const crashing = silo.status === 'failing' || silo.status === 'crashing';
    const crashJitter = crashing ? Math.sin(t * 25) * 1.2 : 0;
    const beaconT = t + silo.id * 0.7;

    // Core: pulse scale + emissive
    silo.core.scale.setScalar(1 + Math.sin(beaconT * 3) * 0.18);
    silo.coreMat.emissiveIntensity = 2.2 + Math.sin(beaconT * 4.5) * 0.9 + crashJitter;

    // Orbital rings: counter-rotating on different axes
    silo.ringA.rotation.x = beaconT * 1.2;
    silo.ringA.rotation.y = beaconT * 0.7;
    silo.ringB.rotation.x = -beaconT * 1.5;
    silo.ringB.rotation.z = beaconT * 0.9;
    silo.beaconRingMat.emissiveIntensity = 1.2 + Math.sin(beaconT * 3) * 0.5;

    // Beam pillar: opacity flicker + subtle scale-Y pulse
    silo.beamMat.opacity = (0.35 + Math.sin(beaconT * 5) * 0.12) * silo.opacityMul;
    silo.beam.scale.y = 1 + Math.sin(beaconT * 2.3) * 0.08;

    // Traveling energy pulses (loop from just above core to top)
    silo.beaconPulses.forEach((pulse, i) => {
      const u = (beaconT * 0.55 + pulse.userData.phase) % 1;
      pulse.position.y = 0.9 + u * 2.4;
      const shrink = 1 - u * 0.6;
      pulse.scale.setScalar(Math.max(0.2, shrink));
      silo.pulseMats[i].opacity = (1 - u) * silo.opacityMul;
      silo.pulseMats[i].emissiveIntensity = 3.0 + Math.sin(beaconT * 6 + i) * 0.8 + crashJitter * 0.5;
    });

    // Floor pulse
    silo.floor.material.emissiveIntensity = 0.2 + Math.sin(t * 1.5 + silo.id * 0.8) * 0.1;

    // Orbital bands rotate in opposite directions around world Y
    silo.band1Wrapper.rotation.y = t * 0.45 + silo.id * 0.6;
    silo.band2Wrapper.rotation.y = -t * 0.32 + silo.id * 0.3;
    // Slow fade in/out on offset phases so one band rises while the other fades
    const bandCycle = t * 0.28 + silo.id * 0.7;
    const band1Pulse = 0.5 + Math.sin(bandCycle) * 0.5;
    const band2Pulse = 0.5 + Math.sin(bandCycle + Math.PI) * 0.5;
    silo.band1Mat.opacity = 0.4 * band1Pulse * silo.opacityMul;
    silo.band2Mat.opacity = 0.4 * band2Pulse * silo.opacityMul;

    // Pillar data streams traveling upward, with mid-height fade peak
    const streamCycle = silo.siloHeight - 0.5;
    silo.pillarStreams.forEach((stream, i) => {
      const u = (t * 0.4 + stream.userData.phase) % 1;
      stream.position.y = 0.3 + u * streamCycle;
      const fade = Math.sin(u * Math.PI);
      stream.scale.setScalar(0.5 + fade * 0.55);
      silo.pillarStreamMats[i].opacity = (0.25 + fade * 0.65) * silo.opacityMul;
      silo.pillarStreamMats[i].emissiveIntensity = 2.0 + fade * 1.5;
    });

    // Animate grains (relative to silo.group)
    silo.grains.forEach((grain) => {
      const ud = grain.userData;
      const newAngle = ud.orbitAngle + t * ud.speed * 0.3;
      grain.position.x = Math.cos(newAngle) * ud.orbitRadius;
      grain.position.z = Math.sin(newAngle) * ud.orbitRadius;
      grain.position.y = ud.yBase + Math.sin(t * ud.speed + ud.phase) * 0.3;
      grain.rotation.x = t * ud.speed;
      grain.rotation.z = t * ud.speed * 0.7;
    });

    // Animate workflows (including migrating ones)
    silo.workflows.forEach((wf) => {
      if (wf.migration) {
        updateMigratingWorkflow(wf, t, dt);
        return;
      }

      const stepCount = wf.steps.length;
      const baseAngle = t * wf.orbitSpeed + wf.orbitOffset;
      const nodeRadius = silo.pentRadius * 0.5;
      const siloCenterX = silo.position.x;
      const siloCenterZ = silo.position.z;
      const yBase = wf.yLevel + silo.yOffset;

      wf.nodes.forEach((node, s) => {
        const a = baseAngle + (s / stepCount) * Math.PI * 2;
        const px = siloCenterX + Math.cos(a) * nodeRadius;
        const pz = siloCenterZ + Math.sin(a) * nodeRadius;
        const py = yBase + Math.sin(t * 0.8 + s) * 0.15;
        node.mesh.position.set(px, py, pz);
        // Billboard task boxes toward camera so the rectangular shape stays readable
        node.mesh.lookAt(camera.position);
        node.ring.position.set(px, py, pz);
        node.ring.lookAt(camera.position);
        if (node.endRing) {
          node.endRing.position.set(px, py, pz);
          node.endRing.lookAt(camera.position);
        }

        const isActive = s === wf.activeStep;
        node.mesh.material.emissiveIntensity = isActive ? 1.5 : 0.3;
        node.mesh.material.opacity = isActive ? 1.0 : 0.7;
        node.mesh.scale.setScalar(isActive ? 1.35 : 1.0);
        node.ring.material.opacity = isActive ? 0.85 : 0.35;
        node.ring.material.emissiveIntensity = isActive ? 1.0 : 0.2;
        if (node.endRing) {
          node.endRing.material.opacity = isActive ? 0.85 : 0.35;
          node.endRing.material.emissiveIntensity = isActive ? 1.0 : 0.2;
        }
      });

      wf.connections.forEach((conn) => {
        const fromPos = wf.nodes[conn.from].mesh.position;
        const toPos = wf.nodes[conn.to].mesh.position;
        const posAttr = conn.line.geometry.attributes.position;
        posAttr.array[0] = fromPos.x;
        posAttr.array[1] = fromPos.y;
        posAttr.array[2] = fromPos.z;
        posAttr.array[3] = toPos.x;
        posAttr.array[4] = toPos.y;
        posAttr.array[5] = toPos.z;
        posAttr.needsUpdate = true;

        const isActiveConn = conn.from === wf.activeStep;
        conn.line.material.opacity = isActiveConn ? 0.85 : 0.25;

        // Arrowhead cone near the "to" end, pointing along the flow
        conn.cone.position.lerpVectors(fromPos, toPos, 0.78);
        _arrowDir.subVectors(toPos, fromPos).normalize();
        conn.cone.quaternion.setFromUnitVectors(_upVec, _arrowDir);
        conn.coneMat.opacity = isActiveConn ? 0.95 : 0.45;
        conn.coneMat.emissiveIntensity = isActiveConn ? 1.2 : 0.5;
      });

      // Freeze runner advancement on failing/crashing/collapsed silos
      const frozen = silo.status === 'failing' || silo.status === 'crashing' || silo.status === 'collapsed';
      if (!frozen) {
        wf.timer += dt;
      }
      const progress = (wf.timer % wf.interval) / wf.interval;
      const currentNode = wf.nodes[wf.activeStep].mesh.position;
      const nextStep = (wf.activeStep + 1) % stepCount;
      const nextNode = wf.nodes[nextStep].mesh.position;

      const ease = progress < 0.5
        ? 4 * progress * progress * progress
        : 1 - Math.pow(-2 * progress + 2, 3) / 2;

      wf.runner.position.lerpVectors(currentNode, nextNode, ease);
      wf.runner.material.emissiveIntensity = 2.0 + Math.sin(t * 8) * 1.0;

      for (let i = wf.trailPositions.length - 1; i > 0; i--) {
        wf.trailPositions[i].copy(wf.trailPositions[i - 1]);
      }
      wf.trailPositions[0].copy(wf.runner.position);
      wf.trail.geometry.setFromPoints(wf.trailPositions);

      if (!frozen && wf.timer >= wf.interval) {
        wf.timer = 0;
        wf.activeStep = nextStep;
        totalWorkflowSteps++;
      }
    });

    // Apply fade AFTER ring pulse so it multiplies correctly
    applyFade(silo);
  });

  // Spawn packets on random active beam
  spawnTimer += dt;
  if (spawnTimer > 0.45 && cluster.beams.length > 0) {
    spawnTimer = 0;
    const beam = cluster.beams[Math.floor(Math.random() * cluster.beams.length)];
    if (beam.packets.length < 3) {
      spawnPacket(beam);
      messageCount++;
    }
  }

  // Animate packets
  totalPackets = 0;
  cluster.beams.forEach((beam) => {
    beam.packets.forEach((pkt) => {
      if (!pkt.alive) return;
      pkt.t += pkt.speed * dt * 0.5;
      if (pkt.t > 1 || pkt.t < 0) {
        pkt.alive = false;
        scene.remove(pkt.mesh);
        scene.remove(pkt.trail);
        pkt.mesh.geometry.dispose();
        pkt.mesh.material.dispose();
        pkt.trail.geometry.dispose();
        pkt.trail.material.dispose();
        return;
      }
      const pos = beam.curve.getPointAt(Math.abs(pkt.t));
      pkt.mesh.position.copy(pos);

      for (let i = pkt.trailPositions.length - 1; i > 0; i--) {
        pkt.trailPositions[i].copy(pkt.trailPositions[i - 1]);
      }
      pkt.trailPositions[0].copy(pos);
      pkt.trail.geometry.setFromPoints(pkt.trailPositions);

      totalPackets++;
    });
    beam.packets = beam.packets.filter(p => p.alive);
  });

  // Ambient particles
  const posArr = particles.geometry.attributes.position.array;
  for (let i = 0; i < particleCount; i++) {
    posArr[i * 3 + 1] += Math.sin(t * 0.5 + i) * 0.002;
    if (posArr[i * 3 + 1] > 25) posArr[i * 3 + 1] = 0;
  }
  particles.geometry.attributes.position.needsUpdate = true;
  particles.rotation.y = t * 0.008;

  // HUD update block removed (handled by page DOM, not the scene).

  // Camera tween applied AFTER animation logic so interactive toggles feel
  // continuous even when the sequence state machine is mid-phase.
  if (cameraTween) {
    const elapsed = (performance.now() / 1000) - cameraTween.startT;
    const raw = Math.min(elapsed / cameraTween.duration, 1);
    const eased = sharpOut(raw);
    camera.position.lerpVectors(cameraTween.from.position, cameraTween.to.position, eased);
    controls.target.lerpVectors(cameraTween.from.target, cameraTween.to.target, eased);
    camera.fov = cameraTween.from.fov + (cameraTween.to.fov - cameraTween.from.fov) * eased;
    camera.updateProjectionMatrix();
    camera.lookAt(controls.target);
    if (raw >= 1) cameraTween = null;
  }
  if (interactiveFlag) controls.update();

  composer.render();
}

renderer.setAnimationLoop(animate);

// Scene-level camera tween state + interactivity toggle
let cameraTween: { from: typeof POSE_IDLE; to: typeof POSE_IDLE; startT: number; duration: number } | null = null;
let interactiveFlag = false;

function tweenCameraTo(target, durationSec) {
  cameraTween = {
    from: { position: camera.position.clone(), target: controls.target.clone(), fov: camera.fov },
    to: { position: target.position.clone(), target: target.target.clone(), fov: target.fov },
    startT: performance.now() / 1000,
    duration: durationSec,
  };
}

function onResize() {
  const w = canvas.clientWidth;
  const h = canvas.clientHeight;
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  renderer.setSize(w, h, false);
  composer.setSize(w, h);
  bloomPass.resolution.set(w, h);
}
window.addEventListener('resize', onResize);

// --- Public controller API ---
const controller: SceneController = {
  setTheme(next: ThemeColors) {
    const prev = activeTheme;
    scene.background = new THREE.Color(next.background);
    (scene.fog as THREE.FogExp2).color.set(next.fog);
    ambientLight.color.set(next.primarySilo);
    ambientLight.intensity = next.ambientIntensity;
    dirLight.color.set(next.accent);
    dirLight.intensity = next.directionalIntensity;
    groundMaterial.color.set(next.ground);
    bloomPass.strength = next.bloomStrength;
    bloomPass.threshold = next.bloomThreshold;
    activeTheme = next;
  },
  setInteractive(next: boolean) {
    if (next === interactiveFlag) return;
    interactiveFlag = next;
    controls.enabled = next;
    tweenCameraTo(next ? POSE_INTERACTIVE : POSE_IDLE, 0.6);
  },
  resetCamera() {
    tweenCameraTo(interactiveFlag ? POSE_INTERACTIVE : POSE_IDLE, 0.4);
  },
  dispose() {
    renderer.setAnimationLoop(null);
    window.removeEventListener('resize', onResize);
    controls.dispose();
    composer.dispose();
    scene.traverse((obj: any) => {
      if (obj.isMesh) {
        obj.geometry?.dispose?.();
        const m = obj.material;
        if (Array.isArray(m)) m.forEach((mm: any) => mm.dispose?.());
        else m?.dispose?.();
      }
    });
    renderer.dispose();
  },
};
return controller;
}  // end initScene
