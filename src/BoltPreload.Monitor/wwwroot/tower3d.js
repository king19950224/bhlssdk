// 机组结构三维场景（Three.js，程序化建模，不依赖模型文件）。
// app.js 通过 window.Tower3D 调用：
//   mount(box, opts)  在 #tower 里建场景；WebGL 不可用返回 false，app.js 退回 SVG 版
//   update(state)     { bound, live, states[], subs[], names[], probes:[{fi, angle, health}] }
//                     probes 可以分布在多个法兰上（fi 是法兰序号）：绑定法兰的真实探头 + 其他法兰规划的影子探头
//   setVisible(bool)  视图切走时停渲染，省 GPU
// 单位按米的量级：塔高 60，风轮直径 52。
import * as THREE from "three";
import { OrbitControls } from "./vendor/three/OrbitControls.js";
import { RoomEnvironment } from "./vendor/three/RoomEnvironment.js";

const H = 60;                       // 塔高
const R_TOP = 1.9, R_BOT = 3.0;     // 塔筒顶/底半径
const FLANGE_AT = [0.8, 0.41, 0.1]; // 三处塔筒法兰在塔高上的位置（顶/中/底段）
const BLADE_LEN = 26;
const HUB_Y = H + 1.7;
const STATUS_COLOR = {
  ok: 0x35c463, online: 0x35c463, warn: 0xf0b429, loose: 0xe5484d, alarm: 0xe5484d,
  fault: 0xd68a3c, offline: 0x5b7089, pending: 0x3a4c63, na: 0x4d637e,
  noprobe: 0xff6a3d, stray: 0xf0b429, shift: 0xc46ee6, uncal: 0xf0b429, badbase: 0xc46ee6,
};

let renderer, scene, camera, controls, clock;
let rotor, host, labelsEl, linesEl, ro;
let visible = false, running = false, live = false;
let flanges = [];     // {i, y, r, ring, glow, halo, anchor(Vector3), label, dot, line}
let probeMeshes = []; // 各法兰上的探头小球（顺序与 state.probes 一致）
let probesKey = "";   // 当前小球的分布（每个球在哪个法兰），变了才重建
let state = { bound: 0, live: false, states: [], subs: [], names: [], probes: [] };
let onPick = null;
let lastBound = null;
let focus = null;      // 镜头聚焦目标 {target, dist}：绑定点变化时平滑推过去，用户一拖动就作废

// 监测点在哪，镜头就框哪：叶根法兰推近轮毂（叶片 1 竖直朝上），塔筒法兰保持整机构图
function focusOn(bound) {
  focus = bound === 3
    ? { target: new THREE.Vector3(0, HUB_Y + 3, 3.2), dist: 78 }
    : { target: new THREE.Vector3(0, 40, 0), dist: 138 };
}

const towerRadiusAt = y => R_TOP + (R_BOT - R_TOP) * (1 - y / H);

// 叶根：圆柱段 + 叶根法兰环的尺寸（叶片局部坐标，y 沿展向）
const ROOT_R = 0.95, ROOT_Y0 = 0.5, ROOT_Y1 = 2.6, ROOT_FLANGE_Y = 2.15, ROOT_FLANGE_R = 1.12;

// ── 叶片：沿展向扫一串截面——根部是圆（接叶根法兰），到 1/4 处过渡成最宽的翼型，往叶尖收尖，
//    带扭角和向前的预弯 ──
function bladeGeometry() {
  const N = 26, K = 18;
  const pos = [];
  for (let i = 0; i <= N; i++) {
    const s = i / N;
    const k1 = Math.min(1, s / 0.22);                       // 根部圆 → 翼型的过渡进度
    const chord = s < 0.22 ? 2 * ROOT_R + 0.8 * k1 : 2.7 - 2.35 * Math.pow((s - 0.22) / 0.78, 1.15);
    const tc = s < 0.22 ? 1 - 0.5 * k1 : 0.5 - 0.38 * ((s - 0.22) / 0.78);   // 厚弦比：1（圆）→ 0.12
    const thick = chord * tc;
    const twist = THREE.MathUtils.degToRad(20 * (1 - s) + 1);
    const bend = 1.6 * s * s;
    const y = ROOT_Y1 - 0.2 + s * BLADE_LEN;
    for (let k = 0; k < K; k++) {
      const phi = k / K * Math.PI * 2;
      // 前缘圆、后缘尖：厚度按 cosφ 往后缘收，根部圆截面不收
      const sharpen = 1 - k1 * 0.4 + k1 * 0.4 * Math.cos(phi);
      const u = chord / 2 * Math.cos(phi);
      const w = thick / 2 * Math.sin(phi) * sharpen;
      const x = u * Math.cos(twist) - w * Math.sin(twist);
      const z = u * Math.sin(twist) + w * Math.cos(twist) + bend;
      pos.push(x, y, z);
    }
  }

  const idx = [];
  for (let i = 0; i < N; i++) {
    for (let k = 0; k < K; k++) {
      const a = i * K + k, b = i * K + (k + 1) % K, c = (i + 1) * K + (k + 1) % K, d = (i + 1) * K + k;
      idx.push(a, b, c, a, c, d);
    }
  }

  // 叶尖封口
  const tipCenter = pos.length / 3;
  pos.push(0, ROOT_Y1 - 0.2 + BLADE_LEN + 0.15, 1.6);
  for (let k = 0; k < K; k++) idx.push(N * K + k, tipCenter, N * K + (k + 1) % K);

  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.Float32BufferAttribute(pos, 3));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

function buildScene() {
  scene = new THREE.Scene();
  scene.fog = new THREE.Fog(0x0a1626, 150, 380);

  const steelLight = new THREE.MeshStandardMaterial({ color: 0xd6e0ec, metalness: 0.6, roughness: 0.28 });
  const steelDark = new THREE.MeshStandardMaterial({ color: 0x8ea3ba, metalness: 0.85, roughness: 0.28 });
  const paintWhite = new THREE.MeshStandardMaterial({ color: 0xf3f7fb, metalness: 0.15, roughness: 0.42 });

  // 地面：深色圆盘 + 网格，远处被雾吃掉
  const ground = new THREE.Mesh(new THREE.CircleGeometry(260, 72),
    new THREE.MeshStandardMaterial({ color: 0x0c1a2c, metalness: 0, roughness: 1 }));
  ground.rotation.x = -Math.PI / 2;
  ground.receiveShadow = true;
  scene.add(ground);
  const grid = new THREE.GridHelper(300, 30, 0x24507c, 0x173456);
  grid.position.y = 0.03;
  grid.material.transparent = true;
  grid.material.opacity = 0.55;
  scene.add(grid);

  // 基础：两层圆台
  const base1 = new THREE.Mesh(new THREE.CylinderGeometry(5.2, 5.6, 1.2, 48),
    new THREE.MeshStandardMaterial({ color: 0x3d5c82, metalness: 0.2, roughness: 0.8 }));
  base1.position.y = 0.6;
  base1.receiveShadow = true; base1.castShadow = true;
  scene.add(base1);
  const base2 = new THREE.Mesh(new THREE.CylinderGeometry(4.0, 4.3, 0.8, 48),
    new THREE.MeshStandardMaterial({ color: 0x2a4666, metalness: 0.2, roughness: 0.8 }));
  base2.position.y = 1.6;
  base2.castShadow = true;
  scene.add(base2);

  // 塔筒
  const tower = new THREE.Mesh(new THREE.CylinderGeometry(R_TOP, R_BOT, H, 64, 1), steelLight);
  tower.position.y = H / 2 + 2;
  tower.castShadow = true; tower.receiveShadow = true;
  scene.add(tower);

  // 法兰环 + 状态光环
  flanges = [];
  FLANGE_AT.forEach((f, i) => {
    const y = 2 + H * f;
    const r = towerRadiusAt(H * f);
    const ring = new THREE.Mesh(new THREE.CylinderGeometry(r + 0.55, r + 0.55, 0.75, 72), steelDark);
    ring.position.y = y;
    ring.castShadow = true;
    ring.userData.flange = i;
    scene.add(ring);

    const glow = new THREE.Mesh(new THREE.TorusGeometry(r + 1.0, 0.14, 10, 96),
      new THREE.MeshBasicMaterial({ color: STATUS_COLOR.na, transparent: true, opacity: 0 }));
    glow.rotation.x = Math.PI / 2;
    glow.position.y = y;
    scene.add(glow);
    const halo = new THREE.Mesh(new THREE.TorusGeometry(r + 1.0, 0.7, 10, 96),
      new THREE.MeshBasicMaterial({ color: STATUS_COLOR.na, transparent: true, opacity: 0, depthWrite: false }));
    halo.rotation.x = Math.PI / 2;
    halo.position.y = y;
    scene.add(halo);

    flanges.push({ i, y, r, ring, glow, halo, anchor: new THREE.Vector3(r + 1.2, y, 0) });
  });

  // 机舱（胶囊）+ 散热罩 + 整流罩
  const nacelle = new THREE.Mesh(new THREE.CapsuleGeometry(1.95, 6.4, 8, 32), steelLight);
  nacelle.rotation.x = Math.PI / 2;
  nacelle.position.set(0, HUB_Y, -1.2);
  nacelle.castShadow = true;
  scene.add(nacelle);
  const hatch = new THREE.Mesh(new THREE.CylinderGeometry(0.9, 1.1, 0.5, 24),
    new THREE.MeshStandardMaterial({ color: 0xc9d8e8, metalness: 0.4, roughness: 0.5 }));
  hatch.position.set(0, HUB_Y + 1.95, -3.2);
  scene.add(hatch);

  // 风轮：轮毂 + 三支叶片（各带圆柱叶根段和叶根法兰环），绕 z 轴转
  rotor = new THREE.Group();
  rotor.position.set(0, HUB_Y, 3.2);
  const hub = new THREE.Mesh(new THREE.SphereGeometry(1.7, 32, 24), paintWhite);
  hub.scale.set(1, 1, 1.35);
  hub.castShadow = true;
  rotor.add(hub);
  const bladeGeo = bladeGeometry();
  const rootGeo = new THREE.CylinderGeometry(ROOT_R, ROOT_R, ROOT_Y1 - ROOT_Y0, 40);
  const rootFlangeGeo = new THREE.CylinderGeometry(ROOT_FLANGE_R, ROOT_FLANGE_R, 0.42, 48);
  let blade1Ring = null, blade1Group = null;
  for (let b = 0; b < 3; b++) {
    const g = new THREE.Group();
    g.rotation.z = b * Math.PI * 2 / 3;
    const root = new THREE.Mesh(rootGeo, paintWhite);
    root.position.y = (ROOT_Y0 + ROOT_Y1) / 2;
    root.castShadow = true;
    const ring = new THREE.Mesh(rootFlangeGeo, steelDark);
    ring.position.y = ROOT_FLANGE_Y;
    ring.castShadow = true;
    ring.userData.flange = b === 0 ? 3 : -1;
    const blade = new THREE.Mesh(bladeGeo, paintWhite);
    blade.castShadow = true;
    g.add(root, ring, blade);
    rotor.add(g);
    if (b === 0) { blade1Ring = ring; blade1Group = g; }
  }
  scene.add(rotor);

  // 叶根法兰（叶片 1）：状态光环套在叶根法兰环外，跟叶片一起转；绑定到它时叶轮会停桨把叶片 1 转到正上方
  const rGlow = new THREE.Mesh(new THREE.TorusGeometry(ROOT_FLANGE_R + 0.32, 0.11, 10, 72),
    new THREE.MeshBasicMaterial({ color: STATUS_COLOR.na, transparent: true, opacity: 0 }));
  rGlow.rotation.x = Math.PI / 2;
  rGlow.position.y = ROOT_FLANGE_Y;
  const rHalo = new THREE.Mesh(new THREE.TorusGeometry(ROOT_FLANGE_R + 0.32, 0.5, 10, 72),
    new THREE.MeshBasicMaterial({ color: STATUS_COLOR.na, transparent: true, opacity: 0, depthWrite: false }));
  rHalo.rotation.x = Math.PI / 2;
  rHalo.position.y = ROOT_FLANGE_Y;
  blade1Group.add(rGlow, rHalo);
  flanges.push({
    i: 3, y: HUB_Y, r: ROOT_FLANGE_R, ring: blade1Ring, glow: rGlow, halo: rHalo, group: blade1Group,
    anchorObj: blade1Ring, anchorLocal: new THREE.Vector3(ROOT_FLANGE_R + 0.2, ROOT_FLANGE_Y, 0),
    anchor: new THREE.Vector3(2.6, HUB_Y + 0.4, 3.2),   // 叶轮转着的时候标签牌锚在轮毂，别跟着叶片画圈
  });

  // 光：半球环境 + 主光（带阴影）+ 室内环境贴图给金属反光
  scene.add(new THREE.HemisphereLight(0xbcd3ee, 0x0e1f36, 1.0));
  const sun = new THREE.DirectionalLight(0xffffff, 2.4);
  sun.position.set(70, 110, 60);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  sun.shadow.camera.left = -45; sun.shadow.camera.right = 45;
  sun.shadow.camera.top = 100; sun.shadow.camera.bottom = -10;
  sun.shadow.camera.near = 20; sun.shadow.camera.far = 320;
  sun.shadow.bias = -0.0006;
  scene.add(sun);
  const fill = new THREE.DirectionalLight(0x6f9fd8, 0.7);
  fill.position.set(-80, 40, -60);
  scene.add(fill);
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  scene.environmentIntensity = 0.65;
}

function setProbes(probes) {
  // 从场景摘下来还不够：不 dispose 的话 Three.js 一直握着对应的 WebGL 缓冲，
  // 这页 7×24 挂着、每轮巡检探头集一变就新建一批，显存慢慢涨
  for (const m of probeMeshes) {
    m.parent.remove(m);
    m.geometry.dispose();
    m.material.dispose();
  }
  probeMeshes = [];
  for (const p of probes) {
    const f = flanges.find(x => x.i === p.fi);
    const mesh = new THREE.Mesh(new THREE.SphereGeometry(f.i === 3 ? 0.22 : 0.42, 16, 12),
      new THREE.MeshStandardMaterial({ color: 0x3a4c63, emissive: 0x000000, metalness: 0.2, roughness: 0.4 }));
    if (f.i === 3) {
      // 叶根法兰：探头绕叶根轴排在法兰环面上，挂在叶片 1 的组里跟着转
      const rr = ROOT_FLANGE_R - 0.05;
      mesh.position.set(rr * Math.sin(p.angle), ROOT_FLANGE_Y + 0.28, rr * Math.cos(p.angle));
      f.group.add(mesh);
    } else {
      const rr = f.r + 0.25;
      mesh.position.set(rr * Math.sin(p.angle), f.y + 0.6, rr * Math.cos(p.angle));
      scene.add(mesh);
    }

    mesh.userData.flange = f.i;
    probeMeshes.push(mesh);
  }
}

function applyState() {
  const st = state;
  live = !!st.live;
  controls.autoRotate = true;
  if (st.bound !== lastBound) { focusOn(st.bound); lastBound = st.bound; }
  flanges.forEach(f => {
    const s = st.states[f.i] || "na";
    const col = STATUS_COLOR[s] ?? STATUS_COLOR.na;
    if (f.glow) {
      f.glow.material.color.setHex(col);
      f.halo.material.color.setHex(col);
      const on = s === "online" || s === "alarm" || s === "offline";
      f.glow.material.opacity = on ? 0.95 : 0;
      f.halo.material.opacity = s === "online" ? 0.22 : s === "alarm" ? 0.35 : 0;
    }

    if (f.label) {
      f.label.dataset.st = s;
      f.dot.dataset.st = s;
      f.label.querySelector(".nm").textContent = st.names[f.i] || "";
      f.label.querySelector(".sub").textContent = st.subs[f.i] || "";
    }
  });

  // 场景里没有对应法兰的探头（配置里多出来的法兰）画不了，先丢掉，剩下的和小球一一对应
  const probes = st.probes.filter(p => flanges.some(f => f.i === p.fi));
  const key = probes.map(p => p.fi).join(",");
  if (key !== probesKey) {
    setProbes(probes);
    probesKey = key;
  }

  probes.forEach((p, j) => {
    const m = probeMeshes[j];
    if (!m) return;
    const col = STATUS_COLOR[p.health] ?? STATUS_COLOR.offline;
    const lit = p.health !== "pending" && p.health !== "offline";
    m.material.color.setHex(lit ? col : 0x33445c);
    m.material.emissive.setHex(lit ? col : 0x000000);
    m.material.emissiveIntensity = lit ? 1.6 : 0;
    m.material.transparent = p.health === "pending";
    m.material.opacity = p.health === "pending" ? 0.55 : 1;
    m.userData.health = p.health;
  });
}

function buildLabels(box) {
  labelsEl = document.createElement("div");
  labelsEl.className = "t3d-labels";
  linesEl = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  linesEl.setAttribute("class", "t3d-lines");
  box.appendChild(linesEl);
  box.appendChild(labelsEl);
  const hint = document.createElement("div");
  hint.className = "t3d-hint";
  hint.textContent = "拖动旋转 · 滚轮缩放";
  box.appendChild(hint);
  flanges.forEach(f => {
    const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
    linesEl.appendChild(line);
    const dot = document.createElement("div");
    dot.className = "t3d-dot";
    const label = document.createElement("div");
    label.className = "t3d-label";
    label.dataset.st = "na";
    label.innerHTML = `<div class="nm"></div><div class="sub"></div>`;
    label.onclick = () => { if (onPick) onPick(f.i); };
    labelsEl.appendChild(dot);
    labelsEl.appendChild(label);
    Object.assign(f, { line, dot, label });
  });
}

const v = new THREE.Vector3();
function placeLabels() {
  const w = host.clientWidth, h = host.clientHeight;
  flanges.forEach(f => {
    // 叶根法兰绑定为监测点时叶轮停桨，锚点跟着法兰环；平时锚在轮毂
    if (f.anchorObj && state.bound === f.i) {
      f.anchorObj.localToWorld(v.copy(f.anchorLocal));
    } else {
      v.copy(f.anchor);
    }

    v.project(camera);
    const x = (v.x + 1) / 2 * w, y = (1 - v.y) / 2 * h;
    const behind = v.z > 1;
    f.dot.style.display = f.label.style.display = behind ? "none" : "";
    if (behind) { f.line.setAttribute("x1", 0); f.line.setAttribute("x2", 0); return; }
    f.dot.style.transform = `translate(${x}px, ${y}px)`;
    // 标签牌统一放在右侧一列，引线从锚点拉过去
    const lx = Math.max(x + 34, w * 0.6), ly = y;
    f.label.style.transform = `translate(${lx}px, ${ly}px)`;
    f.line.setAttribute("x1", x); f.line.setAttribute("y1", y);
    f.line.setAttribute("x2", lx); f.line.setAttribute("y2", ly);
    f.line.dataset.st = f.label.dataset.st;
  });
}

function frame() {
  if (!running) return;
  requestAnimationFrame(frame);
  const dt = Math.min(clock.getDelta(), 0.1);
  if (state.bound === 3) {
    // 监测叶根法兰：停桨，叶片 1 缓缓转到正上方（rotation 0）停住，法兰和探头看得清
    const a = THREE.MathUtils.euclideanModulo(rotor.rotation.z, Math.PI * 2);
    const d = a > Math.PI ? Math.PI * 2 - a : -a;
    rotor.rotation.z = Math.abs(d) < 0.002 ? 0 : rotor.rotation.z + d * Math.min(1, dt * 1.5);
  } else if (live) {
    rotor.rotation.z -= dt * 0.42;
  }

  // 镜头聚焦：目标点和距离都按帧插值，到位就停
  if (focus) {
    const k = Math.min(1, dt * 2.2);
    controls.target.lerp(focus.target, k);
    const off = camera.position.clone().sub(controls.target);
    const len = off.length();
    const nl = len + (focus.dist - len) * k;
    camera.position.copy(controls.target).add(off.multiplyScalar(nl / len));
    if (controls.target.distanceTo(focus.target) < 0.05 && Math.abs(nl - focus.dist) < 0.05) focus = null;
  }

  const t = clock.elapsedTime;
  flanges.forEach(f => {
    if (f.halo && state.states[f.i] === "alarm") f.halo.material.opacity = 0.2 + 0.25 * (0.5 + 0.5 * Math.sin(t * 4));
  });
  probeMeshes.forEach(m => {
    if (m.userData.health === "loose" || m.userData.health === "noprobe") {
      m.material.emissiveIntensity = 0.8 + 1.4 * (0.5 + 0.5 * Math.sin(t * 4.5));
    }
  });
  controls.update();
  renderer.render(scene, camera);
  placeLabels();
}

function resize() {
  if (!host) return;
  const w = host.clientWidth, h = host.clientHeight;
  if (!w || !h) return;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  linesEl.setAttribute("viewBox", `0 0 ${w} ${h}`);
  linesEl.setAttribute("width", w);
  linesEl.setAttribute("height", h);
}

function start() {
  if (running || !visible) return;
  running = true;
  clock.getDelta();
  frame();
}

const raycaster = new THREE.Raycaster();
const ndc = new THREE.Vector2();
function onClick(ev) {
  const r = renderer.domElement.getBoundingClientRect();
  ndc.set((ev.clientX - r.left) / r.width * 2 - 1, -((ev.clientY - r.top) / r.height * 2 - 1));
  raycaster.setFromCamera(ndc, camera);
  // 点到任一法兰环或探头就进那个法兰的螺栓布置图（没接入的也能看）
  const hits = raycaster.intersectObjects([...flanges.map(f => f.ring), ...probeMeshes]);
  const hit = hits.find(h => (h.object.userData.flange ?? -1) >= 0);
  if (hit && onPick) onPick(hit.object.userData.flange);
}

window.Tower3D = {
  mount(box, opts = {}) {
    if (renderer) return true;
    try {
      const canvas = document.createElement("canvas");
      const gl = canvas.getContext("webgl2", { antialias: true, alpha: true }) || canvas.getContext("webgl", { antialias: true, alpha: true });
      if (!gl) return false;
      renderer = new THREE.WebGLRenderer({ canvas, context: gl, antialias: true, alpha: true });
    } catch (e) {
      console.warn("Tower3D: WebGL 不可用，退回 SVG", e);
      renderer = null;
      return false;
    }

    onPick = opts.onPick || null;
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setClearColor(0x000000, 0);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 0.95;

    box.innerHTML = "";
    host = document.createElement("div");
    host.className = "t3d";
    host.appendChild(renderer.domElement);
    box.appendChild(host);

    camera = new THREE.PerspectiveCamera(36, 1, 0.5, 800);
    camera.position.set(62, 58, 128);
    controls = new OrbitControls(camera, renderer.domElement);
    controls.target.set(0, 40, 0);
    controls.enableDamping = true;
    controls.dampingFactor = 0.06;
    controls.minDistance = 70;
    controls.maxDistance = 260;
    controls.maxPolarAngle = Math.PI / 2 - 0.04;
    controls.enablePan = false;
    controls.autoRotate = true;
    controls.autoRotateSpeed = 0.35;
    controls.addEventListener("start", () => { focus = null; });
    clock = new THREE.Clock();

    buildScene();
    buildLabels(host);
    renderer.domElement.addEventListener("click", onClick);
    ro = new ResizeObserver(resize);
    ro.observe(host);
    resize();
    return true;
  },

  update(s) {
    if (!renderer) return;
    state = { ...state, ...s };
    applyState();
    if (!running && visible) start();
  },

  setVisible(v) {
    visible = !!v;
    if (!renderer) return;
    if (visible) { resize(); start(); } else { running = false; }
  },
};

window.dispatchEvent(new Event("tower3d-ready"));
