"use strict";

// ══ 风场配置：wwwroot/farm.json 存在则覆盖这里的默认值 ═══════════════════
let FARM = {
  farmName: "百恒风电场",
  turbineCount: 20,
  // 采集设备列表（每台装在一台机组上）。哪些机组接了设备、接了什么，以服务端 state.devices 为准；这里只是没连上时的兜底
  devices: [],
  flanges: [
    { name: "塔筒顶段法兰", boltCount: 64 },
    { name: "塔筒中段法兰", boltCount: 80 },
    { name: "塔筒底段法兰", boltCount: 96 },
    { name: "叶根法兰 · 叶片1", boltCount: 54 },
  ],
  // 松动判定：夹紧力低于额定 × warnRatio 预警，低于额定 × alarmRatio 报警。
  // ratedKn 填 0 表示未配置，只报测量异常，不判松动。
  thresholds: { ratedKn: 0, warnRatio: 0.85, alarmRatio: 0.7 },
  // 规划的探头位（通道号）。没接探头的位子页面也占着、标"待接入"，业主看得到计划规模；
  // 服务端只轮询 channels 里真接了的，这份列表只管显示。
  // 这是全局缺省；某个法兰规划不一样（如叶根法兰 12 位）就在 flanges[].plannedChannels 里单独写。
  plannedChannels: [],
};

const METRICS = ["声时差 ns", "回波声时 ns", "温度 ℃", "伸长量 um"];
// 伸长量 = 声时差 × umPerNs。系数由服务端随 state.cal 下发（FxDevice.Ucf.UmPerNs 唯一一份），这里只是没连上时的兜底
let umPerNs = 0.807;
const TREND_MS = 5 * 60 * 1000;
// 测点超过这么久没来新帧就按离线显示，别拿最后一帧当"实时"骗人
const STALE_MS = 60 * 1000;
const RANGE_MS = { "5m": TREND_MS, "1h": 3600 * 1000, "24h": 86400 * 1000, "7d": 7 * 86400 * 1000, "30d": 30 * 86400 * 1000 };
let trendRange = "5m";
let alarmRange = "24h";
let reportRange = "24h";
let histPoints = [];
let archiveEvents = [];
let reportStats = null;
const COLORS = ["#2fd0c8", "#f0b429", "#8f7bf0", "#35c463", "#e57373", "#4f9cf0", "#d179c8", "#a4c94b"];

// k = 0 表示未标定：不算、不显示任何力值。服务端没有 .ucf 时就是这个状态；这里不放任何默认斜率
let cal = { k: 0, b: 0 };   // 服务端的全局兜底标定（程序目录 .ucf）；各法兰各有自己的，见 flangeCal()
// 基线口径与阈值：由服务端 state 下发，页面不自己从 farm.json 猜（两边算出来的必须是同一个数）。
//   anchor 在役锚定：帧里的 kn 是"按锚定时 = 额定"估算的预紧力（没额定值就为空），dkn 是较锚定的变化量
//   free   自由置零：kn 就是绝对预紧力
let plan = { baseline: "anchor", baselineText: "在役锚定", rated: 0, warnRatio: 0.85, alarmRatio: 0.7 };
const anchored = () => plan.baseline === "anchor";
// ── 下面这组是"当前看的这台机组"的数据。一个大屏接很多台设备，每台机组各有一份（见 devices），
//    看哪台就用 bindTurbine() 把这组变量切到哪台；所有按机组算的函数都读这组变量 ──
let channels = {};   // ch -> {params, refWave, last, dts[], hist[]}
// 服务端的探头在位识别：ch -> {snr, presence: probe/none/unknown, zeroed, configured, planned, flange, changedAt}
let sigs = {};
let polled = [];
let scanning = false;   // 巡检中（轮宽集，测点刷新变慢）
let selected = 0;
let connected = false;
let maintenance = null;   // {until, note} | null：这台设备在维护模式（大屏让出设备给现场桌面端）
let flangePlan = [];      // 服务端解析好的这台设备各法兰接线/标定：[{index,name,onDevice,channels,planned,rated,cal}]
let devEndpoint = "";

// ── 多设备：t（机组号）-> 这台设备的全部状态 ──
const devices = {};
const emptyDevice = t => ({ t, channels: {}, sigs: {}, connected: false, flangePlan: [], polled: [], scanning: false, maintenance: null, endpoint: "", log: "" });
const hasDevice = t => !!devices[t];
const deviceTurbines = () => Object.keys(devices).map(Number).sort((a, b) => a - b);
const defaultTurbine = () => deviceTurbines()[0] || (FARM.devices[0] && FARM.devices[0].turbine) || 1;
let boundTurbine = 0;
function bindTurbine(t) {
  const d = devices[t] || emptyDevice(t);
  boundTurbine = t;
  channels = d.channels; sigs = d.sigs; connected = d.connected; flangePlan = d.flangePlan;
  polled = d.polled; scanning = d.scanning; maintenance = d.maintenance; devEndpoint = d.endpoint;
}
// 临时切到另一台机组算个东西（总览页要算每台的状态、角标要数全场），算完切回来
function withTurbine(t, fn) {
  const prev = boundTurbine;
  bindTurbine(t);
  try { return fn(); } finally { bindTurbine(prev); }
}

const $ = id => document.getElementById(id);
const fmt = (v, d) => v == null ? "--" : Number(v).toFixed(d);
const fmtSigned = (v, d) => v == null ? "--" : (Number(v) > 0 ? "+" : "") + Number(v).toFixed(d);
const chList = () => Object.keys(channels).map(Number).sort((a, b) => a - b);
const uniqSorted = a => [...new Set((a || []).map(Number))].sort((x, y) => x - y);

// ── 通道归属法兰 ──────────────────────────────────────────────────────────
// 一台 48 路设备可以跨多个法兰（CH1–8 顶段、CH9–16 中段……）。哪路通道属于哪个法兰、用哪份标定、额定多少，
// 以服务端下发的这台设备的 flanges 为准（[{index,name,onDevice,channels,planned,rated,cal}]）；
// state 还没到时按 farm.json 的法兰模板推一份（只有规划位、全部未接入）先把图画出来
function fallbackPlan() {
  return FARM.flanges.map((f, i) => ({
    index: i, name: f.name, onDevice: false, channels: [],
    planned: uniqSorted(Array.isArray(f.plannedChannels) ? f.plannedChannels : FARM.plannedChannels),
    rated: Number((FARM.thresholds || {}).ratedKn) || 0, cal: null,
  }));
}
const flanges = () => flangePlan.length ? flangePlan : fallbackPlan();
const planOf = i => flanges()[i];
const flangeName = i => (FARM.flanges[i] && FARM.flanges[i].name) || (planOf(i) && planOf(i).name) || `法兰 #${i}`;
// 通道属于哪个法兰；-1 = 不在任何接入法兰的配置里（未分配）。一个法兰都没接时（老配置/默认 CH1–4）全归第一个法兰
function chFlange(ch) {
  const fs = flanges().filter(f => f.onDevice);
  for (const f of fs) if ((f.channels || []).map(Number).includes(ch) || (f.planned || []).map(Number).includes(ch)) return f.index;
  return fs.length ? -1 : 0;
}
// 接在这台设备上的法兰（配了在测通道）；一个都没配但设备在上报，就当第一个法兰
function deviceFlanges() {
  const d = flanges().filter(f => f.onDevice).map(f => f.index);
  if (d.length) return d;
  return chList().length || strayList().length ? [0] : [];
}
// 三维图镜头聚焦、默认打开的法兰：这台设备第一个接了线的法兰
const focusFlange = () => deviceFlanges()[0] ?? 0;
// 某法兰生效的标定/额定：服务端已经把"法兰自己的 .ucf → 程序目录兜底"算好了，这里不再兜底；state 没到时一律未标定
const flangeCal = i => { const f = flangePlan[i]; return f && f.cal && Number(f.cal.k) > 0 ? f.cal : null; };
const flangeRated = i => { const f = planOf(i); return f ? Number(f.rated) || 0 : Number(plan.rated) || 0; };
const calibratedCh = ch => { const i = chFlange(ch); return i >= 0 ? !!flangeCal(i) : cal.k > 0; };
const ratedOf = ch => { const i = chFlange(ch); return i >= 0 ? flangeRated(i) : Number(plan.rated) || 0; };
const anyCalibrated = () => deviceFlanges().some(i => flangeCal(i)) || (deviceFlanges().length === 0 && cal.k > 0);
const isConfigured = ch => (sigs[ch] && sigs[ch].configured) || flanges().some(f => (f.channels || []).map(Number).includes(ch));

// 各法兰的通道集合
const flangeChannels = i => chList().filter(ch => chFlange(ch) === i);            // 在测（设备上有基线、在上报）
const flangePlanned = i => { const f = planOf(i); return f ? uniqSorted(f.planned) : []; };   // 规划探头位（含未接的）
const strayList = () => Object.values(sigs).filter(g => g.presence === "probe" && !channels[g.ch]).map(g => Number(g.ch));
const flangeStrays = i => strayList().filter(ch => chFlange(ch) === i);
// 一个法兰要画的位子：规划位 ∪ 实际在上报的 ∪ 冒出探头信号的。统计/报警只看 chList（真有数据的）
const flangeSlots = i => uniqSorted([...flangePlanned(i), ...flangeChannels(i), ...flangeStrays(i)]);
// 有探头信号却不在任何法兰配置里的通道：不知道装在哪，只能在提示条里点名
const unassignedStrays = () => strayList().filter(ch => chFlange(ch) < 0);
// 全站槽位：接在设备上的各法兰 ∪ 未分配的野通道
const slotList = () => uniqSorted([...deviceFlanges().flatMap(flangeSlots), ...unassignedStrays()]);
// 没接入的法兰/机组也按规划显示这些位子，状态一律"待接入"
const plannedList = fdef => { const i = FARM.flanges.indexOf(fdef); return i >= 0 ? flangePlanned(i) : uniqSorted(FARM.plannedChannels); };
const boundFlangeDef = () => FARM.flanges[focusFlange()] || FARM.flanges[0];
const chColor = ch => COLORS[(chList().indexOf(ch) + COLORS.length) % COLORS.length];
const pad2 = x => String(x).padStart(2, "0");
const tid = n => "F" + pad2(n);

function stddev(a) {
  if (a.length < 2) return null;
  const m = a.reduce((s, x) => s + x, 0) / a.length;
  return Math.sqrt(a.reduce((s, x) => s + (x - m) * (x - m), 0) / a.length);
}

/// 单颗螺栓的健康结论：ok / warn（预紧力偏低）/ loose（松动报警）/ fault（检测异常）/ offline /
/// pending（规划了探头位但设备上还没这个通道）
function boltHealth(ch) {
  const c = channels[ch];
  const g = sigs[ch];
  if (!c) {
    // 设备上这一路没有置零基线：冒出探头信号 = 插错位/没置零；否则配了的算离线，没配的是规划位"待接入"
    if (g && g.presence === "probe") return "stray";
    return isConfigured(ch) ? "offline" : "pending";
  }
  const m = c.last;
  if (!m) return "offline";
  if (c.at && Date.now() - c.at > STALE_MS) return "offline";
  // 状态是服务端每帧判好的（Health.Judge：在位、失配、标定、负力值、阈值全在那一份口径里），
  // 历史采样和事件记的就是它，页面照抄——业主看到的和报表里写的才是同一个结论
  if (m.health) return m.health;

  // 老服务端的帧没有 health：按旧逻辑兜底
  if (g && g.presence === "none") return "noprobe";
  if (m.mismatch) return "shift";
  if (!m.normal || m.deltaTof == null) return "fault";
  if (!calibratedCh(ch)) return "uncal";
  const rated = ratedOf(ch);
  const f = liveForce(ch);
  if (rated > 0 && f != null) {
    if (f < rated * plan.alarmRatio) return "loose";
    if (f < rated * plan.warnRatio) return "warn";
  }

  return "ok";
}

const HEALTH_TEXT = {
  ok: "正常", warn: "预紧力偏低", loose: "松动报警", fault: "检测异常", offline: "离线", pending: "待接入",
  noprobe: "探头无信号", stray: "有信号·未配置", shift: "基线失配", uncal: "未标定", badbase: "基线可疑",
  rezero: "基线重置", unknown: "未知",
};
const UNCAL_TEXT = "未加载标定文件（.ucf），不计算预紧力";
const BADBASE_TEXT = "力值为负：基线或额定值有误，需现场核对并重新置零";
const ALARM_HEALTH = new Set(["loose", "fault", "noprobe", "shift", "badbase"]);
// 卡片/槽位上要带颜色样式的状态
const STYLED_HEALTH = ["loose", "warn", "pending", "noprobe", "stray", "shift", "badbase"];
// 牌子上写文字而不是数字的状态
const TEXTUAL_HEALTH = ["pending", "stray", "noprobe", "shift", "uncal", "badbase"];
// 信号质量分级（厂家指南要求测量值与信号质量分开管理）：真探头 30 dB 以上，16 dB 以下算弱
const WEAK_SNR_DB = 16;
const isWeakSignal = ch => sigs[ch] && sigs[ch].presence === "probe" && sigs[ch].snr != null && sigs[ch].snr < WEAK_SNR_DB;

// 一个测点的力值能不能显示：有声时差、状态正常、且没有基线失配
// 预紧力 kN：服务端算好放在帧里（自由置零 = 绝对值；锚定 = 按锚定时=额定估算，没额定值为空）
function liveForce(ch) {
  const m = channels[ch] && channels[ch].last;
  if (!m || m.mismatch || m.kn == null) return null;
  return Number(m.kn);
}
// 较置零基线的变化量 kN（k·Δt）：两种口径下都有，锚定口径下这是业主最该看的数
function liveDelta(ch) {
  const m = channels[ch] && channels[ch].last;
  if (!m || m.mismatch || m.dkn == null) return null;
  return Number(m.dkn);
}
// 卡片主数字：有预紧力就是预紧力；锚定口径没额定值时退到带符号的变化量，并标明是变化量
function mainKn(ch) {
  const f = liveForce(ch);
  if (f != null) return { v: f, delta: false };
  const d = liveDelta(ch);
  if (anchored() && d != null) return { v: d, delta: true };
  return null;
}
const mainKnHtml = (mk, digits) => mk == null
  ? `--<small>kN</small>`
  : (mk.delta ? fmtSigned(mk.v, digits) : mk.v.toFixed(digits)) + `<small>${mk.delta ? "kN 较锚定" : "kN"}</small>`;
// 锚定口径的补充说明："较锚定 −3.2 kN（−2.8%）"
function deltaTxt(ch) {
  const d = liveDelta(ch);
  if (!anchored() || d == null) return "";
  const r = ratedOf(ch);
  return `较锚定 ${fmtSigned(d, 1)} kN` + (r > 0 ? `（${fmtSigned(d / r * 100, 1)}%）` : "");
}
// 接线核对：这一路的在位判定是不是刚刚翻转（现场脱耦/插上探头，几秒内页面上亮出来是几号）
const FLIP_HIGHLIGHT_MS = 20000;
function recentFlip(ch) {
  const g = sigs[ch];
  if (!g || !g.changedAt) return null;
  const age = Date.now() - Date.parse(g.changedAt);
  return age >= 0 && age < FLIP_HIGHLIGHT_MS ? { secs: Math.round(age / 1000), to: g.presence } : null;
}
// 预紧力物理上不会是负的。超出噪声的负值只有一个解释：基线不对（置零后拧过、探头挪过、或压根没在自由状态置零）
const negativeHint = f => f != null && f < 0 ? " · 力值为负，基线可能有误，建议核对置零" : "";

/// 一个法兰当前状态：offline / online / alarm（有松动、检测异常或探头丢失）
function flangeState(i) {
  const chs = flangeChannels(i);
  if (!connected || chs.length === 0) return "offline";
  const hs = chs.map(boltHealth);
  // 所有测点都超时没帧才算离线；页面刚打开还没收到第一帧的那一两秒不算
  if (hs.every(h => h === "offline") && chs.some(ch => channels[ch].at)) return "offline";
  return hs.some(h => ALARM_HEALTH.has(h)) ? "alarm" : "online";
}
/// 绑定机组当前状态：接在设备上的各法兰取最坏的那个
function bindState() {
  if (!connected) return "offline";
  const sts = deviceFlanges().map(flangeState);
  return sts.includes("alarm") ? "alarm" : sts.includes("online") ? "online" : "offline";
}

// ── 探头在位提示：根据服务端的在位识别给一句人话 ──────────────────────────
function probeNotice() {
  const slots = slotList();
  const chs = a => a.map(c => "CH" + c).join("、");
  const parts = [];
  let level = "info";
  // 接线核对：刚翻转的通道排最前面——现场脱耦一颗，几秒内这里就写出是几号、在哪个法兰
  const flips = slots.map(ch => ({ ch, f: recentFlip(ch) })).filter(x => x.f);
  if (flips.length) {
    parts.push("接线核对：" + flips.map(({ ch, f }) => {
      const fi = chFlange(ch);
      return `CH${ch}${fi >= 0 ? `（${flangeName(fi)}）` : ""} ${f.secs} 秒前${f.to === "probe" ? "出现探头信号" : "探头信号消失"}`;
    }).join("，"));
  }

  const lost = slots.filter(ch => boltHealth(ch) === "noprobe");
  // 只有"未配置的通道冒出探头"才算插错位；配置了的通道有探头但没读到基线是另一回事，会自动补读
  const stray = slots.filter(ch => boltHealth(ch) === "stray" && !(sigs[ch] && sigs[ch].configured));
  const noBase = slots.filter(ch => boltHealth(ch) === "stray" && sigs[ch] && sigs[ch].configured);
  const shifted = slots.filter(ch => boltHealth(ch) === "shift");
  // 未配置通道冒出探头，分三种：不在任何法兰里 → 不知道装在哪；设备上已有基线 → 更像装好了没写进配置；没基线 → 更像插错位
  const unassigned = stray.filter(ch => chFlange(ch) < 0);
  const strayZeroed = stray.filter(ch => chFlange(ch) >= 0 && sigs[ch] && sigs[ch].zeroed);
  const strayRaw = stray.filter(ch => chFlange(ch) >= 0 && !(sigs[ch] && sigs[ch].zeroed));
  if (lost.length) parts.push(`${chs(lost)} 探头信号消失（拔出、接错或耦合失效）`);
  if (unassigned.length) parts.push(`${chs(unassigned)} 有探头信号，但不在任何法兰的接线表里 —— 请在 farm.json 的对应法兰写上 channels/channelBolts`);
  if (strayZeroed.length) parts.push(`${chs(strayZeroed)} 有探头信号且设备上已有基线 —— 若是新装探头，加进该法兰的 channels 即可监测${lost.length ? "；若是从别处挪来的，请核对接线" : ""}`);
  if (strayRaw.length) parts.push(`${chs(strayRaw)} 有探头信号但未配置、未置零 —— 可能插错位置${lost.length ? `（${chs(lost)} 的探头？）` : ""}`);
  if (noBase.length) parts.push(`${chs(noBase)} 有探头信号但本次没读到基线，稍后自动补读`);
  // 基线失配：探头还插在原通道，但声时对不上置零基线（贴到别的螺栓上/重新耦合过）
  if (shifted.length) parts.push(`${chs(shifted)} 声时偏离置零基线超过 2 µs —— 探头位置或耦合已变，需重新置零`);
  if (lost.length || shifted.length) level = "bad";
  else if (stray.length) level = "warn";
  if (parts.length === 0) return scanning ? { level: "info", text: "巡检中：正在轮询规划位看有没有探头，测点刷新会慢几十秒" } : null;
  return { level, text: parts.join("；") };
}

// ── 维护模式：大屏让出设备给现场桌面端（设备只有一路 TCP），到期自动恢复。按机组，操作的是当前看的这台 ─────
const hhmm = t => { const d = new Date(t); return `${pad2(d.getHours())}:${pad2(d.getMinutes())}`; };
function updateMaintBox() {
  const msg = $("maintMsg");
  if (!msg) return;
  const box = $("maintBox");
  box.classList.toggle("active", !!maintenance);
  if (maintenance) msg.textContent = `维护中，${hhmm(maintenance.until)} 自动恢复`;
  else if (!msg.dataset.err) msg.textContent = "";
}
document.querySelectorAll("#maintBox a[data-min]").forEach(a => {
  a.onclick = async () => {
    const msg = $("maintMsg");
    delete msg.dataset.err;
    try {
      const r = await fetch("/api/maintenance", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ turbine: viewTurbine, minutes: Number(a.dataset.min), note: "现场置零/调参" }) });
      if (r.status === 403) { msg.dataset.err = "1"; msg.textContent = "只能在大屏主机上操作"; return; }
      if (!r.ok) { msg.dataset.err = "1"; msg.textContent = "操作失败"; return; }
      applyDevice(await r.json());
    } catch { msg.dataset.err = "1"; msg.textContent = "操作失败"; }
  };
});

// 当前看的这台机组的提示
function turbineNotice() {
  return maintenance
    ? { level: "info", text: `现场维护中（${maintenance.note}）：大屏已让出设备给桌面端，预计 ${hhmm(maintenance.until)} 自动恢复监测；期间画面不更新` }
    : connected ? probeNotice() : null;
}
function refreshNotice() {
  const el = $("notice");
  if (!el) return;
  let n;
  if (!$("view-farm").hidden || !$("view-alarms").hidden || !$("view-report").hidden) {
    // 全场页面：把每台机组的提示串起来，前面标机组号；级别取最坏的
    const all = deviceTurbines().map(t => ({ t, n: withTurbine(t, turbineNotice) })).filter(x => x.n);
    const rank = { info: 0, warn: 1, bad: 2 };
    n = all.length ? {
      level: all.reduce((w, x) => rank[x.n.level] > rank[w] ? x.n.level : w, "info"),
      text: all.map(x => `${tid(x.t)}：${x.n.text}`).join("　｜　"),
    } : null;
  } else {
    n = turbineNotice();
  }
  el.hidden = !n;
  if (!n) return;
  el.className = "notice " + n.level;
  // 窄屏下提示条会被省略号截断，完整内容放 title 悬停可见
  if (el.textContent !== n.text) { el.textContent = n.text; el.title = n.text; }
}

function minForceOf(chs) {
  let v = null;
  for (const ch of chs) {
    if (boltHealth(ch) === "offline") continue;
    const f = liveForce(ch);
    if (f != null && (v == null || f < v)) v = f;
  }
  return v;
}
const minForce = () => minForceOf(chList());

setInterval(() => {
  const n = new Date();
  $("clock").textContent = `${n.getFullYear()}-${pad2(n.getMonth() + 1)}-${pad2(n.getDate())} ${pad2(n.getHours())}:${pad2(n.getMinutes())}:${pad2(n.getSeconds())}`;
}, 1000);

// ══ 路由 ═══════════════════════════════════════════════════════════════
// 当前查看的机组/法兰和"绑定的监测点"是两回事：没接入的机组和法兰也能点进去看画面，只是没数据。
//   #/turbine/7   看 F07 机组          #/turbine  看绑定机组
//   #/flange/2    看当前机组的第 3 个法兰  #/flange   看绑定法兰（并回到绑定机组）
let viewTurbine = 1, viewFlange = 0;
// 当前看的机组有没有接设备（全局变量已由 bindTurbine 切到这台）
const isBoundTurbine = () => hasDevice(viewTurbine);
// 当前看的法兰是不是接在这台设备上的（配了在测通道的都算，不再只认 bind.flangeIndex 那一个）
const isBoundFlange = () => isBoundTurbine() && deviceFlanges().includes(viewFlange);
const viewFlangeDef = () => FARM.flanges[viewFlange] || FARM.flanges[0];
// 当前查看法兰的探头位与状态：接入的法兰用真实数据（只看属于它的通道），没接入的是它自己的规划位、全部"待接入"
const viewSlots = () => isBoundFlange() ? flangeSlots(viewFlange) : plannedList(viewFlangeDef());
const viewChannels = () => isBoundFlange() ? flangeChannels(viewFlange) : [];
const viewHealth = ch => isBoundFlange() ? boltHealth(ch) : "pending";

const VIEWS = ["farm", "turbine", "flange", "alarms", "report"];
function route() {
  const h = location.hash || "#/farm";
  const parts = h.replace(/^#\/?/, "").split("/");
  const view = ["flange", "turbine", "alarms", "report"].includes(parts[0]) ? parts[0] : "farm";
  const arg = Number(parts[1]);
  const prevTurbine = viewTurbine;
  if (view === "turbine") {
    viewTurbine = arg >= 1 && arg <= FARM.turbineCount ? arg : defaultTurbine();
    bindTurbine(viewTurbine);
    viewFlange = focusFlange();
  } else if (view === "flange") {
    if (parts[1] === undefined) viewTurbine = defaultTurbine();
    bindTurbine(viewTurbine);
    viewFlange = arg >= 0 && arg < FARM.flanges.length ? arg : focusFlange();
  } else {
    bindTurbine(viewTurbine);
  }
  // 换了机组：测点表、趋势、卡片都是另一台的数据，要重建；选中测点也换
  if (prevTurbine !== viewTurbine) { rebuildTable(); histPoints = []; rezeroMarks = []; selected = 0; if (trendRange !== "5m") loadTrend(trendRange); }
  refreshHeader();

  VIEWS.forEach(v => $("view-" + v).hidden = v !== view);
  $("opsToggle").hidden = view !== "flange" || !isBoundFlange();
  // 没接入的法兰没有波形/参数可看，运维视图只对绑定法兰开放
  if (view === "flange" && !isBoundFlange() && opsMode) { opsMode = false; applyOpsMode(); }
  // 三维机组图切走就停渲染，别在后台白烧 GPU
  if (window.Tower3D && tower3dMode) window.Tower3D.setVisible(view === "turbine");

  // 菜单高亮：机组页归到总览那一组
  const menuKey = view === "turbine" ? "farm" : view;
  ["farm", "flange", "alarms", "report"].forEach(k =>
    $("mi-" + k).classList.toggle("active", k === menuKey));

  const crumb = $("crumb");
  const t = tid(viewTurbine);
  const f = viewFlangeDef().name;
  const na = `<span class="crumb-na">未接入</span>`;
  if (view === "farm") {
    crumb.innerHTML = "风场总览";
  } else if (view === "turbine") {
    crumb.innerHTML = `<a href="#/farm">风场总览</a><span class="sep">›</span>${t} 机组${isBoundTurbine() ? "" : na}`;
  } else if (view === "flange") {
    crumb.innerHTML = `<a href="#/farm">风场总览</a><span class="sep">›</span><a href="#/turbine/${viewTurbine}">${t} 机组</a>`
      + `<span class="sep">›</span>${f}${isBoundFlange() ? "" : na}`;
  } else if (view === "alarms") {
    crumb.innerHTML = "报警中心";
  } else {
    crumb.innerHTML = "统计报告";
  }
  renderView(view);
}
window.addEventListener("hashchange", route);

function renderView(view) {
  if (view === "farm") updateFarm();
  else if (view === "turbine") { updateFlangeList(); updateTowerSvg(); updateSlotGrid(); }
  else if (view === "alarms") { loadEvents(alarmRange); }
  else if (view === "report") { loadReport(reportRange); }
  else { rebuildTable(); refreshAll(); if (trendRange !== "5m") loadTrend(trendRange); }
}

// ══ 视图1：风场总览 ═════════════════════════════════════════════════════
const TURBINE_SVG = `
<svg viewBox="0 0 100 110">
  <defs>
    <linearGradient id="g-twr" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#c4dcf2"/><stop offset="1" stop-color="#4f6f94"/>
    </linearGradient>
    <linearGradient id="g-bld" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#f2f9ff"/><stop offset="1" stop-color="#7fa6cf"/>
    </linearGradient>
  </defs>
  <ellipse cx="50" cy="102" rx="24" ry="3" fill="rgba(6,14,26,.55)"/>
  <path d="M47.7 36 L52.3 36 L51.4 97 L48.6 97 Z" fill="url(#g-twr)"/>
  <rect x="41" y="97" width="18" height="4" rx="1.6" fill="#40607f"/>
  <g class="blades">
    <g fill="url(#g-bld)">
      <path d="M50 30 L46.8 28.4 Q47.6 12 49.2 2.6 Q50.2 2 51.2 2.6 Q52.6 12 53.2 28.4 Z" transform="rotate(0 50 30)"/>
      <path d="M50 30 L46.8 28.4 Q47.6 12 49.2 2.6 Q50.2 2 51.2 2.6 Q52.6 12 53.2 28.4 Z" transform="rotate(120 50 30)"/>
      <path d="M50 30 L46.8 28.4 Q47.6 12 49.2 2.6 Q50.2 2 51.2 2.6 Q52.6 12 53.2 28.4 Z" transform="rotate(240 50 30)"/>
    </g>
    <circle cx="50" cy="30" r="4.6" fill="#e8f3fd"/>
    <circle cx="50" cy="30" r="2" fill="#8fb3d8"/>
  </g>
</svg>`;

// 结构只建一次，数据来了只改文字和类名。每帧重建 DOM 会把叶片旋转动画
// 打回起点（看着像不转），hover 中的卡片被换掉还会闪烁晃动。
let farmBuilt = false;
function buildFarm() {
  $("farmName").textContent = FARM.farmName + "螺栓预紧力在线监测系统";
  const stat = (id, cls, u, k) =>
    `<div class="stat ${cls}"><div><span class="v" id="${id}">--</span><span class="u">${u}</span></div><div class="k">${k}</div></div>`;
  $("statbar").innerHTML =
    stat("st-total", "", "台", "机组总数")
    + stat("st-online", "ok", "台", "在线监测")
    + stat("st-alarm", "bad", "台", "报警机组")
    + stat("st-off", "off", "台", "未接入")
    + stat("st-pts", "", "颗", "监测螺栓")
    + stat("st-minf", "", "kN", "最小预紧力");

  const g = $("matrix");
  g.innerHTML = "";
  for (let i = 1; i <= FARM.turbineCount; i++) {
    const d = document.createElement("div");
    d.className = "wt offline";
    d.id = "wt-" + i;
    d.innerHTML = `<span class="st"><span class="dot" id="wt-dot-${i}"></span></span>
      <div class="icon">${TURBINE_SVG}</div>
      <div class="id">${tid(i)}</div><div class="info" id="wt-info-${i}">${hasDevice(i) ? "设备未连接" : "未接入"}</div>`;
    // 没接入的机组也能点进去看结构图，只是没有数据
    d.onclick = () => { location.hash = "#/turbine/" + i; };
    g.appendChild(d);
  }

  farmBuilt = true;
}

// 总览：每台接了设备的机组各算各的状态；顶部合计
function updateFarm() {
  if (!farmBuilt) buildFarm();
  let online = 0, alarm = 0, pts = 0, slots = 0, mf = null;
  for (let i = 1; i <= FARM.turbineCount; i++) {
    const card = $("wt-" + i);
    if (!card) continue;
    if (!hasDevice(i)) {
      if (card.className !== "wt offline") card.className = "wt offline";
      continue;
    }

    const r = withTurbine(i, () => ({ st: bindState(), pts: chList().length, slots: slotList().length, mf: minForce() }));
    if (r.st !== "offline") online++;
    if (r.st === "alarm") alarm++;
    pts += r.pts; slots += r.slots;
    if (r.mf != null && (mf == null || r.mf < mf)) mf = r.mf;
    // 类名不变就不动 DOM，免得打断 CSS 动画
    const cls = r.st === "alarm" ? "wt alarm" : r.st === "online" ? "wt online" : "wt offline";
    if (card.className !== cls) card.className = cls;
    const dot = $("wt-dot-" + i);
    const dotCls = ("dot " + (r.st === "alarm" ? "err" : r.st === "online" ? "on" : "")).trim();
    if (dot.className !== dotCls) dot.className = dotCls;
    const dv = devices[i];
    const html = r.st === "offline" ? (dv && dv.maintenance ? `<span style="color:#f5c34d">维护中</span>` : "设备未连接")
      : r.st === "alarm" ? `<span style="color:var(--bad)">测点异常</span>`
      : `最小 <b>${r.mf == null ? "--" : r.mf.toFixed(1)}</b> kN · ${r.pts} 测点`;
    const info = $("wt-info-" + i);
    if (info.innerHTML !== html) info.innerHTML = html;
  }

  $("st-total").textContent = FARM.turbineCount;
  $("st-online").textContent = online;
  $("st-alarm").textContent = alarm;
  $("st-off").textContent = FARM.turbineCount - online;
  // 规划了探头位就显示"已接/规划"，让人知道还有位子没接
  $("st-pts").textContent = slots > pts ? `${pts}/${slots}` : (pts || "--");
  $("st-minf").textContent = mf == null ? "--" : mf.toFixed(1);
}

// ══ 视图2：机组详情（当前查看的机组 viewTurbine，不一定是绑定机组） ═══════════
// 当前查看的机组里，法兰 i 是不是接在设备上的（不是绑定机组就全部未接入）
const flangeLiveInView = i => isBoundTurbine() && deviceFlanges().includes(i);
// 机组图上法兰 i 要画的探头位：
//   接入的法兰     真实槽位（规划 ∪ 实接 ∪ 冒出信号的）；没接入的机组在同一个法兰上画同样的规划（全部"待接入"）
//   有规划的法兰   画它自己的 plannedChannels（叶根法兰 12 位），不管哪台机组
//   其余法兰       不画
function towerSlotsFor(i) {
  const f = FARM.flanges[i];
  if (!f) return [];
  if (flangeLiveInView(i)) return flangeSlots(i);
  return plannedList(f);
}
const towerSlotsKey = () => FARM.flanges.map((f, i) => towerSlotsFor(i).join(",")).join("|");

function buildFlangeList() {
  $("towerTitle").textContent = `${tid(viewTurbine)} 机组结构`;
  const list = $("flangeList");
  list.innerHTML = "";
  list.dataset.turbine = viewTurbine;
  FARM.flanges.forEach((f, i) => {
    const d = document.createElement("div");
    d.className = "fl offline";
    d.id = "fl-row-" + i;
    d.innerHTML = `<span class="dot" id="fl-rdot-${i}"></span><span class="nm">${f.name}</span>
      <span class="meta" id="fl-meta-${i}">螺栓 ${f.boltCount} 颗 · 未接入</span>
      <span class="go" id="fl-go-${i}">查看 ›</span>`;
    // 每个法兰都能点进去看螺栓布置，没接入的只是没数据
    d.onclick = () => { location.hash = "#/flange/" + i; };
    list.appendChild(d);
  });
}

function updateFlangeList() {
  if (String($("flangeList").dataset.turbine) !== String(viewTurbine)) buildFlangeList();
  FARM.flanges.forEach((f, i) => {
    const row = $("fl-row-" + i);
    if (!row) return;
    const live = flangeLiveInView(i);
    const st = live ? flangeState(i) : "offline";
    const cls = "fl " + (live && st !== "offline" ? "online" : "offline");
    if (row.className !== cls) row.className = cls;
    const dotCls = ("dot " + (!live ? "" : st === "alarm" ? "err" : st === "online" ? "on" : "")).trim();
    const dot = $("fl-rdot-" + i);
    if (dot.className !== dotCls) dot.className = dotCls;
    if (!live) {
      const planned = towerSlotsFor(i).length;
      $("fl-meta-" + i).innerHTML = `螺栓 ${f.boltCount} 颗 · ${planned ? `规划探头 ${planned} 位 · ` : ""}未接入监测`;
      return;
    }

    const pts = flangeChannels(i).length, slots = flangeSlots(i).length;
    const mf = minForceOf(flangeChannels(i));
    $("fl-meta-" + i).innerHTML = st === "offline"
      ? `螺栓 ${f.boltCount} 颗 · 探头位 ${slots} · 设备未连接`
      : `螺栓 ${f.boltCount} 颗 · 探头 <b>${pts}</b>/${slots} 在线 · 最小预紧力 <b>${mf == null ? "--" : mf.toFixed(1)}</b> kN`
      + (st === "alarm" ? ` · <span style="color:var(--bad)">有测点异常</span>` : "");
  });
}

// 机组结构：正视 SVG。结构只建一次，数据帧来了只改 data-st 和文字，颜色/光晕/旋转全交给 CSS，
// 避免整图重建抖动，也避免脚本里散落一堆颜色值。
function towerFlangeSub(i) {
  if (!flangeLiveInView(i)) {
    const planned = towerSlotsFor(i).length;
    return planned ? `规划探头 ${planned} 位 · 未接入` : "未接入";
  }
  const st = flangeState(i);
  if (st === "offline") return "设备未连接";
  const mf = minForceOf(flangeChannels(i));
  return `探头 ${flangeChannels(i).length}/${flangeSlots(i).length} · 最小 ${mf == null ? "--" : mf.toFixed(1)} kN`
         + (st === "alarm" ? " · 有异常" : "");
}

// 几何（viewBox 430×560）：k 是圆截面压成椭圆的比例（略俯视），法兰环/基础/地面网格都按它画。
// 叶尖 cx+rotorR 要落在右侧标签牌（x≥262）左边，转起来才不会扫到牌子
// 风轮直径取塔高的四分之三、塔筒粗一点，比例才像真机而不像棒棒糖；塔往左放给风轮腾地方，
// 叶尖 cx+rotorR=254 仍在右侧标签牌（x≥270）左边
const TW = { cx: 130, hubY: 140, rotorR: 124, topY: 156, botY: 486, flangeY: [222, 352, 454], k: 0.3 };

function buildTowerSvg() {
  const { cx, hubY, rotorR, topY, botY, flangeY, k } = TW;
  // 塔筒半宽：顶窄底宽的锥筒
  const halfW = y => 17 + (y - topY) / (botY - topY) * 12;
  const chipX = 270, chipW = 152, chipH = 42;
  // 探头点：每个法兰画自己的位子（绑定法兰的真实槽位 / 自带规划的法兰）；状态色由 updateTowerSvg 按是否绑定决定
  const pdot = (i, ch, x, y) => `<circle class="pdot" data-fi="${i}" data-ch="${ch}" cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="2.8"/>`;

  // 右侧标签牌 + 引线（法兰页同款）：名称/状态两行，左侧色条随状态变
  const callout = (i, x1, y, name) => `
    <g class="fl-hot" data-fi="${i}" data-st="na">
      <line class="lead" x1="${x1}" y1="${y}" x2="${chipX}" y2="${y}"/>
      <circle class="dot" cx="${x1}" cy="${y}" r="2.6"/>
      <rect class="chip" x="${chipX}" y="${y - chipH / 2}" width="${chipW}" height="${chipH}" rx="5"/>
      <rect class="bar" x="${chipX}" y="${y - chipH / 2}" width="3" height="${chipH}" rx="1.5"/>
      <text class="nm" x="${chipX + 12}" y="${y - 4}">${name}</text>
      <text class="sub" id="tw-sub-${i}" x="${chipX + 12}" y="${y + 12}">--</text>
    </g>`;

  // 塔筒上的法兰：有厚度的外凸金属环。俯视只看得到前半圈顶面（月牙）和前侧环壁，
  // 后半圈被上面的塔筒挡住；环下沿给塔筒投一道阴影，段与段就分开了
  let rings = "", statusRings = "", callouts = "";
  flangeY.forEach((y, i) => {
    if (!FARM.flanges[i]) return;
    const hw = halfW(y), rx = hw + 8, ry = rx * k, th = 7;
    rings += `
      <path d="M${cx - rx},${y} A${rx},${ry} 0 0 0 ${cx + rx},${y} L${cx + rx},${y + th} A${rx},${ry} 0 0 1 ${cx - rx},${y + th} Z" fill="url(#tw-ring-side)"/>
      <path d="M${cx - rx},${y} A${rx},${ry} 0 0 0 ${cx + rx},${y} L${cx + hw},${y} A${hw},${(hw * k).toFixed(2)} 0 0 1 ${cx - hw},${y} Z" fill="url(#tw-ring-top)"/>
      <path d="M${cx - rx},${y + th} A${rx},${ry} 0 0 0 ${cx + rx},${y + th}" fill="none" stroke="rgba(0,0,0,.5)" stroke-width="1"/>
      <path d="M${cx - hw},${y + th} L${cx + hw},${y + th} L${cx + hw + .6},${y + th + 14} L${cx - hw - .6},${y + th + 14} Z" fill="url(#tw-ring-drop)"/>`;
    const sr = rx + 4, sry = ry + 2;
    let dots = "";
    const slots = towerSlotsFor(i);
    if (slots.length) {
      // 探头位沿前半圈排开（示意，不是精确角度），颜色跟每个探头位的状态
      const rd = (hw + rx) / 2;
      slots.forEach((ch, j) => {
        const a = Math.PI * (0.1 + 0.8 * (slots.length === 1 ? 0.5 : j / (slots.length - 1)));
        dots += pdot(i, ch, cx + rd * Math.cos(a), y + rd * k * Math.sin(a));
      });
    }

    statusRings += `
      <g class="fl-ring" data-fi="${i}" data-st="na">
        <ellipse class="halo" cx="${cx}" cy="${y + th / 2}" rx="${sr}" ry="${sry}" filter="url(#tw-soft)"/>
        <ellipse class="ring" cx="${cx}" cy="${y + th / 2}" rx="${sr}" ry="${sry}"/>
        ${dots}
    </g>`;
    callouts += callout(i, cx + rx + 4, y + th / 2, FARM.flanges[i].name);
  });

  // 叶根法兰：轮毂外圈；引线从机舱后面出来。探头位按螺栓角度绕整圈排在环上（正视图看叶根是个正圆）
  if (FARM.flanges[3]) {
    const rootDef = FARM.flanges[3], rootSlots = towerSlotsFor(3), rr = 22;
    const idx = slotBoltIndex(rootDef, rootSlots);
    let dots = "";
    rootSlots.forEach(ch => {
      const a = (idx.get(ch) || 0) / rootDef.boltCount * Math.PI * 2;
      dots += pdot(3, ch, cx + rr * Math.sin(a), hubY - rr * Math.cos(a));
    });
    statusRings += `
      <g class="fl-ring" data-fi="3" data-st="na">
        <circle class="halo" cx="${cx}" cy="${hubY}" r="${rr}" filter="url(#tw-soft)"/>
        <circle class="ring" cx="${cx}" cy="${hubY}" r="${rr}"/>
        ${dots}
      </g>`;
    callouts += callout(3, cx + 66, hubY, rootDef.name);
  }

  // 叶片：根部窄、四分之一处最宽、往叶尖收成圆尖，前缘弧度大于后缘
  const tip = -rotorR;
  const blade = `M-3.4,-14 C-7.6,-32 -8.2,-58 -3,${tip + 7} Q0,${tip - 1} 2.4,${tip + 7} C4.6,-58 4.4,-32 3.4,-14 Z`;
  const bladeShade = `M-3.4,-14 C-7.6,-32 -8.2,-58 -3,${tip + 7} L-1.3,${tip + 7} C-5.4,-58 -5,-32 -1.5,-14 Z`;
  const bladeAt = deg => `
      <g transform="rotate(${deg})">
        <path d="${blade}" fill="url(#tw-blade)" stroke="rgba(120,150,185,.55)" stroke-width=".7"/>
        <path d="${bladeShade}" fill="rgba(80,110,145,.28)"/>
      </g>`;

  // 地面：透视网格（向画面中央后方的消失点收敛）+ 基础平台
  const hz = botY + 8, vpY = hz - 64, vpX = 215;
  let grid = "";
  for (let j = -8; j <= 8; j++) {
    grid += `<line x1="${vpX}" y1="${vpY}" x2="${vpX + j * 44}" y2="560"/>`;
  }
  for (const d of [2, 6, 12, 20, 31, 46, 66]) {
    grid += `<line x1="0" y1="${hz + d}" x2="430" y2="${hz + d}"/>`;
  }

  const hwB = halfW(botY);
  $("tower").innerHTML = `
  <svg viewBox="0 0 430 560">
    <defs>
      <linearGradient id="tw-body" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stop-color="#4c6382"/><stop offset=".2" stop-color="#9ab1ca"/>
        <stop offset=".4" stop-color="#eef5fb"/><stop offset=".6" stop-color="#c4d6e8"/>
        <stop offset=".84" stop-color="#7c94b1"/><stop offset="1" stop-color="#3f5573"/>
      </linearGradient>
      <linearGradient id="tw-ring-top" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#f2f7fc"/><stop offset="1" stop-color="#b3c6d9"/>
      </linearGradient>
      <linearGradient id="tw-ring-side" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stop-color="#2e4262"/><stop offset=".3" stop-color="#8ea6c2"/>
        <stop offset=".55" stop-color="#6a84a3"/><stop offset="1" stop-color="#22354d"/>
      </linearGradient>
      <linearGradient id="tw-ring-drop" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="rgba(0,0,0,.4)"/><stop offset="1" stop-color="rgba(0,0,0,0)"/>
      </linearGradient>
      <linearGradient id="tw-nac" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#e8f0f8"/><stop offset=".55" stop-color="#aec4da"/><stop offset="1" stop-color="#5e7998"/>
      </linearGradient>
      <linearGradient id="tw-blade" x1="0" y1="1" x2="0" y2="0">
        <stop offset="0" stop-color="#f8fbff"/><stop offset="1" stop-color="#c3d7eb"/>
      </linearGradient>
      <radialGradient id="tw-hub" cx=".38" cy=".36" r=".75">
        <stop offset="0" stop-color="#ffffff"/><stop offset=".55" stop-color="#d9e6f3"/><stop offset="1" stop-color="#8ba6c4"/>
      </radialGradient>
      <linearGradient id="tw-base-side" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stop-color="#1c3050"/><stop offset=".35" stop-color="#456689"/><stop offset="1" stop-color="#182a44"/>
      </linearGradient>
      <radialGradient id="tw-sky" cx=".5" cy=".45" r=".5">
        <stop offset="0" stop-color="rgba(47,130,200,.22)"/><stop offset="1" stop-color="rgba(47,130,200,0)"/>
      </radialGradient>
      <radialGradient id="tw-grid-fade" cx=".5" cy=".2" r=".6">
        <stop offset="0" stop-color="#fff"/><stop offset=".55" stop-color="rgba(255,255,255,.45)"/><stop offset="1" stop-color="#000"/>
      </radialGradient>
      <mask id="tw-grid-mask"><rect x="0" y="${hz}" width="430" height="${560 - hz}" fill="url(#tw-grid-fade)"/></mask>
      <filter id="tw-shadow" x="-60%" y="-10%" width="220%" height="130%">
        <feDropShadow dx="0" dy="5" stdDeviation="5" flood-color="#000" flood-opacity=".45"/>
      </filter>
      <filter id="tw-soft" x="-40%" y="-300%" width="180%" height="700%">
        <feGaussianBlur stdDeviation="2.6"/>
      </filter>
    </defs>

    <ellipse cx="${cx}" cy="${hubY + 20}" rx="210" ry="170" fill="url(#tw-sky)"/>

    <!-- 地面网格 + 基础 -->
    <g class="tw-grid" mask="url(#tw-grid-mask)">${grid}</g>
    <ellipse cx="${cx}" cy="${hz + 4}" rx="124" ry="13" fill="rgba(0,0,0,.35)"/>
    <path d="M${cx - 52},${botY - 2} A52,${(52 * k).toFixed(1)} 0 0 0 ${cx + 52},${botY - 2} L${cx + 52},${botY + 9} A52,${(52 * k).toFixed(1)} 0 0 1 ${cx - 52},${botY + 9} Z" fill="url(#tw-base-side)"/>
    <ellipse cx="${cx}" cy="${botY - 2}" rx="52" ry="${(52 * k).toFixed(1)}" fill="#3d5c82" stroke="#5f80a6" stroke-width=".7"/>

    <!-- 塔筒（锥形圆柱）+ 法兰环 + 机舱 -->
    <g filter="url(#tw-shadow)">
      <path d="M${cx - halfW(topY)},${topY} L${cx + halfW(topY)},${topY} L${cx + hwB},${botY} A${hwB},${(hwB * k).toFixed(1)} 0 0 1 ${cx - hwB},${botY} Z" fill="url(#tw-body)"/>
      ${rings}
      <!-- 机舱：3/4 视角往右后方延伸，顶上一个散热罩 -->
      <rect x="${cx - 16}" y="${hubY - 19}" width="80" height="36" rx="14" fill="url(#tw-nac)" stroke="#4e6685" stroke-width=".6"/>
      <ellipse cx="${cx + 38}" cy="${hubY - 18}" rx="10" ry="3.2" fill="#d3dfec" stroke="#7e97b4" stroke-width=".5"/>
      <path d="M${cx + 8},${hubY + 17} Q${cx + 40},${hubY + 22} ${cx + 62},${hubY + 8}" fill="none" stroke="rgba(0,0,0,.25)" stroke-width="1.2"/>
    </g>

    <!-- 风轮：叶片 + 整流罩。外层 g 只负责平移到轮毂；旋转动画在内层 g 上，
         因为 CSS transform 会覆盖 SVG 的 transform 属性，放同一个 g 上会把平移冲掉 -->
    <g transform="translate(${cx},${hubY})">
      <g class="tw-rotor">
        ${bladeAt(0)}${bladeAt(120)}${bladeAt(240)}
        <circle r="15" fill="url(#tw-hub)" stroke="#8fb0d2" stroke-width=".8"/>
        <circle r="4.8" fill="#a9c2dd"/>
      </g>
    </g>

    ${statusRings}
    ${callouts}
  </svg>`;

  // 每个法兰都可点进螺栓布置图（没接入的也能看，只是没数据）
  $("tower").querySelectorAll(".fl-hot, .fl-ring").forEach(g => {
    g.addEventListener("click", () => { location.hash = "#/flange/" + g.dataset.fi; });
  });
}

// ── 三维版（tower3d.js，Three.js）优先；模块没加载成功或 WebGL 不可用就走上面的 SVG 版 ──
let tower3dMode = null;   // null 未试过 / true 用三维 / false 用 SVG
function tower3dReady() {
  if (tower3dMode === false || !window.Tower3D) return tower3dMode === true;
  if (tower3dMode === null) {
    // 点任一法兰（环或标签牌）进它的螺栓布置图；没接入的也能看
    tower3dMode = window.Tower3D.mount($("tower"), { onPick: i => { location.hash = "#/flange/" + i; } });
    if (tower3dMode) { $("tower").classList.add("is3d"); window.Tower3D.setVisible(true); }
  }
  return tower3dMode;
}
window.addEventListener("tower3d-ready", () => {
  if (!$("view-turbine").hidden) { $("tower").dataset.key = ""; renderView("turbine"); }
});

// 机组图上某个探头位的状态：只有接入法兰上的才有真实状态，其余（没接入的机组、只有规划的法兰）都是"待接入"影子
function towerProbeHealth(fi, ch) {
  if (!flangeLiveInView(fi)) return "pending";
  return flangeState(fi) === "offline" ? "offline" : boltHealth(ch);
}

function updateTower3d() {
  const st = isBoundTurbine() ? bindState() : "offline";
  // 每个法兰的探头小球各按自己法兰的螺栓角度排；镜头仍聚焦在绑定法兰所在的位置
  const probes = [];
  FARM.flanges.forEach((fdef, fi) => {
    const slots = towerSlotsFor(fi);
    if (!slots.length) return;
    const idx = slotBoltIndex(fdef, slots);
    for (const ch of slots) {
      probes.push({ fi, angle: (idx.get(ch) || 0) / fdef.boltCount * Math.PI * 2, health: towerProbeHealth(fi, ch) });
    }
  });
  window.Tower3D.update({
    bound: focusFlange(),
    turbine: viewTurbine,
    live: st !== "offline",
    states: FARM.flanges.map((f, i) => flangeLiveInView(i) ? flangeState(i) : "na"),
    names: FARM.flanges.map(f => f.name),
    subs: FARM.flanges.map((f, i) => towerFlangeSub(i)),
    probes,
  });
}

function updateTowerSvg() {
  if (tower3dReady()) { updateTower3d(); return; }
  const box = $("tower");
  // 任一法兰的探头位数变了（规划或实接）、接入的法兰集变了或换了机组要重建，法兰环上的点数才对
  const key = towerSlotsKey() + "#" + deviceFlanges().join(",") + "|" + viewTurbine;
  if (!box.firstElementChild || box.dataset.key !== key) { buildTowerSvg(); box.dataset.key = key; }
  const st = isBoundTurbine() ? bindState() : "offline";
  box.classList.toggle("live", st !== "offline");
  FARM.flanges.forEach((f, i) => {
    const s = flangeLiveInView(i) ? flangeState(i) : "na";
    box.querySelectorAll(`[data-fi="${i}"]`).forEach(g => { if (g.dataset.st !== s) g.dataset.st = s; });
    const sub = $("tw-sub-" + i);
    if (!sub) return;
    const txt = towerFlangeSub(i);
    if (sub.textContent !== txt) sub.textContent = txt;
  });
  box.querySelectorAll(".pdot").forEach(d => {
    const h = towerProbeHealth(Number(d.dataset.fi), Number(d.dataset.ch));
    if (d.dataset.st !== h) d.dataset.st = h;
  });
}

// 机组页右下：接入法兰上各探头位的小卡（法兰页螺栓状态卡的紧凑版）。多个法兰接在同一台设备上时全列出来，卡上标法兰名
let slotGridKey = "";
function updateSlotGrid() {
  const grid = $("slotGrid");
  if (!grid) return;
  // 没接入的机组也按绑定法兰的规划显示这些位子（全部"待接入"）
  const boundT = isBoundTurbine();
  const liveFlanges = boundT ? deviceFlanges() : [];
  const entries = liveFlanges.length
    ? liveFlanges.flatMap(fi => flangeSlots(fi).map(ch => ({ ch, fi })))
    : plannedList(boundFlangeDef()).map(ch => ({ ch, fi: focusFlange() }));
  const multi = liveFlanges.length > 1;
  const key = entries.map(e => e.fi + ":" + e.ch).join(",") + "|" + viewTurbine;
  if (entries.length === 0) {
    if (slotGridKey !== key) {
      slotGridKey = key;
      grid.innerHTML = `<div class="slot-empty">${tid(viewTurbine)} 机组尚未规划探头位<br><small>在 farm.json 的 flanges[].plannedChannels 里配置</small></div>`;
      $("slotHint").textContent = "未接入";
    }
    return;
  }

  if (key !== slotGridKey) {
    slotGridKey = key;
    grid.innerHTML = "";
    for (const { ch, fi } of entries) {
      const fdef = FARM.flanges[fi] || {};
      const cb = boltsOf(fi);
      const d = document.createElement("div");
      d.className = "slot";
      d.id = "slot-" + ch;
      const no = Number(cb[ch]);
      const flTag = multi ? `<span class="sno">${fdef.name || ""}</span>` : "";
      d.innerHTML = `<div class="srow"><span class="sc">CH${ch}${no ? `<span class="sno">#${no}</span>` : ""}${flTag}</span>
          <span class="bst pending" id="slot-st-${ch}">--</span></div>
        <div class="sv" id="slot-v-${ch}">--<small>kN</small></div>
        <div class="ssub" id="slot-sub-${ch}">--</div>`;
      d.onclick = () => { selected = ch; location.hash = "#/flange/" + fi; };
      grid.appendChild(d);
    }
  }

  const live = boundT ? entries.filter(e => channels[e.ch]).length : 0;
  const hint = (multi ? `${liveFlanges.length} 个法兰 · ` : `${flangeName(entries[0].fi)} · `)
    + (boundT ? `探头 ${live} / ${entries.length} 在线` : `规划探头 ${entries.length} 位 · 尚未接入`);
  if ($("slotHint").textContent !== hint) $("slotHint").textContent = hint;

  for (const { ch } of entries) {
    const h = boundT ? boltHealth(ch) : "pending";
    const card = $("slot-" + ch);
    const cls = "slot" + (STYLED_HEALTH.includes(h) ? " " + h : "") + (recentFlip(ch) ? " flash" : "");
    if (card.className !== cls) card.className = cls;
    const st = $("slot-st-" + ch);
    if (st.className !== "bst " + h) st.className = "bst " + h;
    if (st.textContent !== HEALTH_TEXT[h]) st.textContent = HEALTH_TEXT[h];
    const m = h === "pending" ? null : channels[ch] && channels[ch].last;
    const mk = h === "pending" || h === "offline" ? null : mainKn(ch);
    const liveVal = mk && !mk.delta ? mk.v : null;
    const html = mainKnHtml(mk, 1);
    const el = $("slot-v-" + ch);
    if (el.innerHTML !== html) el.innerHTML = html;

    const rated = ratedOf(ch);
    // 在位识别结果只属于绑定机组的设备；别的机组"待接入"就是待接入，不能借它写"已确认无探头信号"
    const g = boundT ? sigs[ch] : undefined;
    const weak = isWeakSignal(ch) ? " · 信号弱，建议检查耦合" : "";
    const sub = h === "pending" ? (g && g.presence === "none" ? "规划探头位 · 已确认无探头信号" : "规划探头位，尚未接入")
      : h === "uncal" ? UNCAL_TEXT
      : h === "badbase" ? BADBASE_TEXT
      : h === "noprobe" ? "探头信号消失：可能被拔出或接错"
      : h === "stray" ? (g && g.configured ? "有探头信号，但未置零" : "未配置通道出现探头信号 —— 可能插错位置")
      : h === "shift" ? `声时偏离置零基线 ${m && m.deltaTof != null ? (Math.abs(Number(m.deltaTof)) / 1000).toFixed(1) : "--"} µs，需重新置零`
      : h === "offline" ? (m ? "超过 1 分钟没有新数据" : channels[ch] ? "等待数据" : "已接探头，设备未上报")
      : mk == null ? HEALTH_TEXT[h]
      : mk.delta ? `${deltaTxt(ch)} · 未配置额定值，无法估算绝对预紧力`
      : (rated > 0 ? `额定的 ${(liveVal / rated * 100).toFixed(0)}%` : "未配置额定值")
        + (anchored() ? ` · ${deltaTxt(ch)}` : "")
        + (m.temp != null ? ` · ${Number(m.temp).toFixed(1)} ℃` : "") + weak + negativeHint(liveVal);
    const subEl = $("slot-sub-" + ch);
    if (subEl.textContent !== sub) { subEl.textContent = sub; subEl.title = sub; }
  }
}

// ══ 视图3：法兰详情 ═════════════════════════════════════════════════════

// ── 业主视图：螺栓状态卡（结论），运维视图才有波形/参数 ──────────────────
let opsMode = false;
$("opsToggle").onclick = () => {
  opsMode = !opsMode;
  applyOpsMode();
};

function applyOpsMode() {
  $("opsToggle").textContent = opsMode ? "‹ 返回监测视图" : "运维详情 ›";
  $("flangeGrid").className = "fmain" + (opsMode ? "" : " owner");
  $("ownerPanel").hidden = opsMode;
  $("opsDetail").hidden = !opsMode;
  $("opsTable").hidden = !opsMode;
  if (opsMode) { refreshDetail(); drawWave(); }
  drawFlange();
  drawTrend();
}

let boltCardsKey = "";
// 额定值/口径是 state 来的，第一帧 state 到了要重建一次卡片头上的说明
const boltCardsKeyNow = () => `${viewTurbine}|${viewFlange}|${viewSlots().join(",")}|${plan.baseline}|${flangeRated(viewFlange)}|${!!flangeCal(viewFlange)}`;
function buildBoltCards() {
  const g = $("boltCards");
  g.innerHTML = "";
  const chs = viewSlots();
  boltCardsKey = boltCardsKeyNow();
  if (chs.length === 0) {
    // 连规划位都没配：给个说明占位
    const f = viewFlangeDef();
    g.classList.remove("dense", "packed");
    g.innerHTML = `<div class="bolt-empty"><div class="t">${tid(viewTurbine)} · ${f.name}</div>
      <div class="d">共 ${f.boltCount} 颗螺栓 · 尚未规划探头位</div>
      <div class="s">在 farm.json 该法兰的 plannedChannels 里配置探头位后这里显示每个位子的状态</div></div>`;
    $("ratedShow").textContent = "";
    return;
  }

  // 位子多了卡片排得密一点，8 个正好 2×4 铺满；超过 8 个（叶根 12 位）排三列，12 个正好 3×4
  g.classList.toggle("dense", chs.length > 4);
  g.classList.toggle("packed", chs.length > 8);
  for (const ch of chs) {
    const d = document.createElement("div");
    d.className = "bolt";
    d.id = "bolt-" + ch;
    d.innerHTML = `<div class="brow"><span class="bch">CH${ch}</span><span class="bst offline" id="bst-${ch}">--</span></div>
      <div class="bkn" id="bkn-${ch}">--<small>kN</small></div>
      <div class="bbar"><i id="bbar-${ch}" style="width:0%"></i></div>
      <div class="bsub" id="bsub-${ch}">--</div>`;
    d.onclick = () => { selected = ch; refreshAll(); };
    g.appendChild(d);
  }

  // 头上的说明按当前法兰：它自己的标定文件、额定值
  const rated = flangeRated(viewFlange);
  const fc = flangeCal(viewFlange);
  const calTxt = isBoundFlange() ? (fc ? `标定 ${fc.name} ${Number(fc.k).toFixed(4)} kN/ns · ` : "未标定 · ") : "";
  $("ratedShow").textContent = calTxt + (rated > 0
    ? `额定预紧力 ${rated} kN · 低于 ${Math.round(plan.alarmRatio * 100)}% 报警` + (anchored() ? " · 锚定口径：按锚定时 = 额定估算" : "")
    : "未配置额定预紧力（farm.json 该法兰的 ratedKn）" + (anchored() ? " · 锚定口径下只显示较锚定变化量" : ""));
}

function updateBoltCard(ch) {
  const card = $("bolt-" + ch);
  if (!card) return;
  const h = viewHealth(ch);
  // 接线核对：在位判定刚翻转的通道闪几秒，现场脱耦一颗就知道是几号
  const flip = isBoundFlange() ? recentFlip(ch) : null;
  const cls = ("bolt " + (STYLED_HEALTH.includes(h) ? h : "")
    + (ch === selected ? " sel" : "") + (flip ? " flash" : "")).trim();
  if (card.className !== cls) card.className = cls;
  const bst = $("bst-" + ch);
  const bstCls = "bst " + h;
  if (bst.className !== bstCls) bst.className = bstCls;
  // 配置了的通道有探头却没基线，和"未配置通道冒出探头"是两回事，标签分开写
  const stTxt = h === "stray" && sigs[ch] && sigs[ch].configured ? "有信号·无基线" : HEALTH_TEXT[h];
  if (bst.textContent !== stTxt) bst.textContent = stTxt;

  // 没接入的法兰上，channels 里的实时值属于别的法兰，不能拿来显示；基线失配的帧也不能算力值
  const m = h === "pending" ? null : channels[ch] && channels[ch].last;
  const mk = h === "pending" ? null : mainKn(ch);
  const f = mk && !mk.delta ? mk.v : null;
  $("bkn-" + ch).innerHTML = mainKnHtml(mk, 2);
  const rated = ratedOf(ch);
  const tempTxt = m && m.temp != null ? `温度 ${Number(m.temp).toFixed(1)} ℃` : "温度 --";
  const weakTxt = isWeakSignal(ch) ? ` · 信号弱（${sigs[ch].snr} dB），建议检查耦合` : "";
  if (rated > 0 && f != null && h !== "offline" && h !== "badbase") {
    const pct = Math.max(0, f / rated * 100);
    $("bbar-" + ch).style.width = Math.min(100, pct) + "%";
    setSub(ch, `额定 ${rated} kN 的 ${pct.toFixed(0)}%` + (anchored() ? ` · ${deltaTxt(ch)}` : "") + ` · ${tempTxt}${weakTxt}${negativeHint(f)}`);
  } else {
    $("bbar-" + ch).style.width = "0%";
    // 有回波、状态正常却没有声时差 = 会话里没有参考声时（通道没置零或基线没读到），不是信号问题
    const noBase = m && m.normal && m.echoTime != null && m.deltaTof == null;
    // 在位识别结果只属于绑定法兰；没接入的法兰（叶根 12 位等）一律"尚未接入"
    const g = isBoundFlange() ? sigs[ch] : undefined;
    const snrTxt = g && g.snr != null ? `信噪比 ${g.snr} dB` : "";
    const why = h === "fault" ? (noBase ? "有回波但无基线（通道未置零）" : "信号异常，等待恢复")
      : h === "uncal" ? `${UNCAL_TEXT}；声时差 ${m && m.deltaTof != null ? fmt(m.deltaTof, 1) + " ns" : "--"}`
      : h === "badbase" ? `${BADBASE_TEXT}（算得 ${f == null ? "--" : f.toFixed(1)} kN，${deltaTxt(ch) || "声时差 " + fmt(m && m.deltaTof, 1) + " ns"}）`
      : h === "noprobe" ? `探头信号消失（${snrTxt}）：可能被拔出或接到了别的通道`
      : h === "stray" ? (g && g.configured
          ? `有探头信号（${snrTxt}），但该通道未置零，无法测力`
          : g && g.zeroed
            ? `检测到探头信号（${snrTxt}），设备上已有基线 —— 确认已安装就把它加进 channels 配置`
            : `检测到探头信号（${snrTxt}），此通道未配置/未置零 —— 探头可能插错位置`)
      : h === "shift" ? `声时偏离置零基线 ${m && m.deltaTof != null ? (Math.abs(Number(m.deltaTof)) / 1000).toFixed(1) : "--"} µs（预紧变化只会有几十 ns）：探头贴到了别的螺栓或重新耦合过，需重新置零`
      : h === "offline" ? (m ? "超过 1 分钟没有新数据" : channels[ch] ? "等待数据" : "已接探头，设备未上报该通道")
      : h === "pending" ? (g && g.presence === "none" ? "规划探头位 · 已确认无探头信号" : "规划探头位，尚未接入")
      : mk && mk.delta ? `${deltaTxt(ch)} · 未配置额定值，无法估算绝对预紧力`
      : "未配置额定预紧力" + negativeHint(f);
    setSub(ch, ["pending", "noprobe", "stray", "shift", "uncal", "badbase"].includes(h) ? why : why + " · " + tempTxt);
  }
}

// 副文字两行截断，完整内容进 title；内容没变就不碰 DOM
function setSub(ch, text) {
  const el = $("bsub-" + ch);
  if (el && el.textContent !== text) { el.textContent = text; el.title = text; }
}

function updateBoltCards() {
  if (boltCardsKeyNow() !== boltCardsKey) buildBoltCards();
  viewSlots().forEach(updateBoltCard);
}

const mrowEl = $("mrow");
METRICS.forEach(t => {
  const d = document.createElement("div");
  d.className = "m";
  d.innerHTML = `<div class="k">${t}</div><div class="v">--</div>`;
  mrowEl.appendChild(d);
});
const metricVals = [...mrowEl.querySelectorAll(".v")];

// ── 螺栓布置：2.5D 斜俯视法兰 ─────────────────────────────────────────────
// 有厚度的法兰环、往下延伸并渐隐的塔筒、立在环面上的螺栓（垫圈 + 六角螺母棱柱）、监测点螺母顶上的
// 超声探头（带状态灯），按螺栓数和测点集建一次；数据帧来了只改 data-st / data-sel 和数值文字，
// 颜色、光晕、动画全在 CSS 里。矢量图在 4K 大屏上不糊。
const flangeBox = $("flange");
let flangeKey = "";
let flangeBolts = [];   // 监测点：{ch, g 螺栓组, lead 引线, chip 标签牌, kn 数值文本}
// 几何（viewBox 520×270）：k 圆→椭圆的压扁比（视角），ro/ri 法兰外/内径，rb 螺栓分布圆，t 法兰厚度，
// rt 塔筒外径，tubeH 塔筒可见高度，chipW/H 标签牌尺寸。标签牌放四角——椭圆的四角本来就是空的
const FL = { cx: 260, cy: 108, k: 0.42, ro: 168, ri: 110, rb: 139, t: 17, rt: 152, tubeH: 125, chipW: 98, chipH: 44, h: 270 };

// 螺栓能画多大由相邻螺栓的弧长间距决定：8 颗的法兰是大螺母，96 颗的就是一圈小螺母。
// 监测点给一个下限：真实法兰几十上百颗时螺母只有几个像素，带探头的那几颗得让人一眼找到
function boltGeom(n, mon, rb = FL.rb) {
  const gap = 2 * Math.PI * rb / n;
  let w = Math.max(2.4, Math.min(12, gap * 0.34));     // 垫圈半径
  if (mon) w = Math.max(w, 6.5);
  return {
    w, nr: w * 0.78, hN: Math.max(3, w * 0.95),
    pr: mon ? Math.max(w * 0.6, 4.5) : w * 0.6,
    hP: mon ? Math.max(w * 0.6, 5) : Math.max(3, w * 0.6),
  };
}

// 压扁的椭圆闭合路径
function ellPath(cx, cy, r, k) {
  const ry = (r * k).toFixed(2);
  return `M${cx - r},${cy} a${r},${ry} 0 1 0 ${2 * r},0 a${r},${ry} 0 1 0 ${-2 * r},0 Z`;
}

// 半椭圆弧（接在 M 左端点之后）：到右端点，back=true 走远侧（上半），否则走近侧（下半）
function halfArc(cx, cy, r, k, back) {
  return `A${r},${(r * k).toFixed(2)} 0 0 ${back ? 1 : 0} ${cx + r},${cy}`;
}

// 一颗螺栓（画在 translate 到螺栓位置的 g 里）：垫圈 → 螺母三个可见侧面 → 顶面；
// 监测点顶上叠探头圆柱和指示灯，没监测的露出螺杆端头
function boltSvg(g, k, mon) {
  const { w, nr, hN, pr, hP } = g;
  const f = (nr * k * Math.sqrt(3) / 2).toFixed(2);   // 前排两个顶点的 y
  const hx = (nr / 2).toFixed(2);
  const yT = -hN;
  const hex = [0, 1, 2, 3, 4, 5].map(i => {
    const a = i * Math.PI / 3;
    return `${(Math.cos(a) * nr).toFixed(2)},${(Math.sin(a) * nr * k).toFixed(2)}`;
  }).join(" ");
  let s = `
        <ellipse class="washer" rx="${w.toFixed(1)}" ry="${(w * k).toFixed(1)}"/>
        <polygon class="nut-shadow" points="${hex}" transform="translate(0,1.2)"/>
        <polygon class="face-l" points="${-nr},0 ${-hx},${f} ${-hx},${(f - hN).toFixed(2)} ${-nr},${yT}"/>
        <polygon class="face-f" points="${-hx},${f} ${hx},${f} ${hx},${(f - hN).toFixed(2)} ${-hx},${(f - hN).toFixed(2)}"/>
        <polygon class="face-r" points="${hx},${f} ${nr},0 ${nr},${yT} ${hx},${(f - hN).toFixed(2)}"/>
        <polygon class="nut-top" points="${hex}" transform="translate(0,${yT})"/>`;
  if (!mon) {
    return s + `<ellipse class="stud" cy="${yT}" rx="${(nr * .42).toFixed(2)}" ry="${(nr * .42 * k).toFixed(2)}"/>`;
  }

  const pk = (pr * k).toFixed(2);
  s += `
        <ellipse class="probe-shadow" cy="${yT}" rx="${(pr * 1.1).toFixed(2)}" ry="${(pr * 1.1 * k).toFixed(2)}"/>
        <path class="probe-side" d="M${-pr},${yT} A${pr},${pk} 0 0 0 ${pr},${yT} L${pr},${yT - hP} A${pr},${pk} 0 0 0 ${-pr},${yT - hP} Z"/>
        <ellipse class="probe-top" cy="${yT - hP}" rx="${pr}" ry="${pk}"/>
        <circle class="led" cx="${(pr * .45).toFixed(2)}" cy="${(yT - hP - .3).toFixed(2)}" r="${Math.max(1.2, pr * .2).toFixed(2)}"/>`;
  return s;
}

// ── 螺栓编号：和图纸对齐 ──
// 画图用的位置序号 i 从正上方起顺时针 0..n-1；图纸编号由 numbering 决定：
//   topBolt   正上方那颗的图纸编号（叶根法兰图纸：#23 在正上、#46 正右、#69 正下、#92 正左）
//   clockwise 编号是否顺时针递增（默认是）
function boltNoAt(fdef, i) {
  const n = fdef.boltCount;
  const nb = fdef.numbering || {};
  const top = Number(nb.topBolt) || 1;
  const cw = nb.clockwise !== false;
  return ((cw ? top - 1 + i : top - 1 - i) % n + n) % n + 1;
}

function boltIndexOf(fdef, boltNo) {
  const n = fdef.boltCount;
  const nb = fdef.numbering || {};
  const top = Number(nb.topBolt) || 1;
  const cw = nb.clockwise !== false;
  const d = (boltNo - top) % n;
  return ((cw ? d : -d) + n) % n;
}

// 探头位 -> 螺栓位置序号（从正上方起顺时针 0..n-1）：farm.json 里 channelBolts 给了实际安装的
// 螺栓号就按图纸放；没给的从 1 号螺栓起、沿编号方向均匀占位（CH1 落在 #1）。法兰图和三维机组共用。
// 法兰 fi 的接线表（通道 → 螺栓号）：新写法在 devices[] 里、由服务端随这台设备的 flanges 下发；老写法在模板自己身上
function boltsOf(fi) {
  const p = planOf(fi);
  if (p && p.bolts && Object.keys(p.bolts).length) return p.bolts;
  return (FARM.flanges[fi] && FARM.flanges[fi].channelBolts) || {};
}

function slotBoltIndex(fdef, chs) {
  const n = fdef.boltCount;
  const cb = boltsOf(FARM.flanges.indexOf(fdef));
  const dir = (fdef.numbering || {}).clockwise === false ? -1 : 1;
  const base = boltIndexOf(fdef, 1);
  const out = new Map();
  chs.forEach((ch, i) => {
    const no = Number(cb[ch]);
    out.set(ch, no >= 1 && no <= n
      ? boltIndexOf(fdef, no)
      : ((base + dir * Math.round(i * n / Math.max(1, chs.length))) % n + n) % n);
  });
  return out;
}

// 叶根不是钢法兰：厚壁玻纤层合板圆筒，螺柱/衬套预埋在层合板里，面上只看到一圈金属衬套口。
// 是不是叶根由 farm.json 的 kind:"root" 指定，没写的按名字里带"叶根"判断。
const isRootFlange = fdef => fdef.kind === "root" || /叶根/.test(fdef.name || "");

// 叶根面上的一个螺柱位：金属衬套口（外圈亮、内孔深）；监测点上面立探头圆柱和指示灯
function rootBoltSvg(g, k, mon) {
  const { w, pr, hP } = g;
  const ir = w * 0.9, sr = w * 0.42;
  let s = `
        <ellipse class="ins" rx="${ir.toFixed(2)}" ry="${(ir * k).toFixed(2)}"/>
        <ellipse class="ins-hole" rx="${sr.toFixed(2)}" ry="${(sr * k).toFixed(2)}"/>`;
  if (!mon) return s;
  const pk = (pr * k).toFixed(2);
  s += `
        <ellipse class="probe-shadow" rx="${(pr * 1.1).toFixed(2)}" ry="${(pr * 1.1 * k).toFixed(2)}"/>
        <path class="probe-side" d="M${-pr},0 A${pr},${pk} 0 0 0 ${pr},0 L${pr},${-hP} A${pr},${pk} 0 0 0 ${-pr},${-hP} Z"/>
        <ellipse class="probe-top" cy="${-hP}" rx="${pr}" ry="${pk}"/>
        <circle class="led" cx="${(pr * .45).toFixed(2)}" cy="${(-hP - .3).toFixed(2)}" r="${Math.max(1.2, pr * .2).toFixed(2)}"/>`;
  return s;
}

function buildFlangeSvg(fdef, chs) {
  const { cx, k, tubeH, chipW, chipH } = FL;
  const root = isRootFlange(fdef);
  // 标签牌左右各一列。每列超过两块时中间那几块落在环最宽处，环收小一号让开；超过四块（叶根 12 位）再收一号，
  // 画布也按牌子数拉高（牌与牌之间至少留 6px），法兰跟着画布中线下移，保持居中
  const perCol = Math.ceil(chs.length / 2);
  const sc = perCol > 4 ? 0.82 : perCol > 2 ? 0.88 : 1;
  const h = Math.max(FL.h, 16 + perCol * (chipH + 6) - 6);
  const cy = FL.cy + (h - FL.h) / 2;
  const ro = FL.ro * sc, rt = FL.rt * sc;
  // 叶根层合板壁比钢法兰面窄得多，螺柱走壁厚中线
  const ri = root ? ro - 27 : FL.ri * sc;
  const rb = root ? (ro + ri) / 2 : FL.rb * sc;
  // 叶根视图里 t 是切面到变桨轴承的这段叶根筒高度，环外文字要让开它
  const t = root ? 30 : FL.t;
  const n = fdef.boltCount;
  const geo = boltGeom(n, false, rb), geoMon = boltGeom(n, true, rb);
  if (root) {
    // 衬套口不能比壁厚宽；探头保底大小
    geo.w = Math.min(geo.w, 6); geo.pr = geo.w * 0.6;
    geoMon.w = Math.min(Math.max(geoMon.w, 6.5), 8); geoMon.pr = Math.max(geoMon.w * 0.6, 4.5); geoMon.hP = Math.max(geoMon.hP, 5);
  }
  const drawBolt = root ? rootBoltSvg : boltSvg;
  const monIdx = new Map();
  slotBoltIndex(fdef, chs).forEach((idx, ch) => monIdx.set(idx, ch));
  $("flangeTitle").textContent = `${root ? "螺柱布置" : "螺栓布置"} · ${fdef.name}（${n} 颗）`;

  // 叶根下面的变桨轴承比叶根筒大一圈，前半圈的文字要绕到轴承下沿以外
  const rBear = ro + 14, tBear = 26;
  // 环外的点：沿径向在屏幕上外推 off 个像素（不随视角压扁，正上/正下的字才不会贴着环）；
  // 前半圈还要再往下让出法兰厚度（叶根视图是叶根筒 + 轴承的高度），不然字落在前壁上
  const outer = (a, off) => {
    const s = Math.sin(a), f = Math.max(0, s);
    if (!root) return [cx + (ro + off) * Math.cos(a), cy + ro * k * s + off * s + t * f];
    // 两侧和前半圈的字要绕到比轴承还大的半径上，后半圈贴着叶根筒即可
    const w = Math.min(1, Math.max(0, (s + 0.25) / 0.5));
    const rEff = ro + (rBear + 4 - ro) * w;
    return [cx + (rEff + off) * Math.cos(a), cy + rEff * k * s + off * s + (t + tBear + 10) * Math.sqrt(f)];
  };
  const angleAt = i => -Math.PI / 2 + i * 2 * Math.PI / n;

  // 面上的纹理：钢法兰是机加工同心纹（明暗交替），叶根切面是层合板一层层的铺层线
  let marks = "";
  for (let r = ri + (root ? 3 : 6), i = 0; r < ro - 3; r += root ? 3 : 5, i++) {
    marks += `<ellipse cx="${cx}" cy="${cy}" rx="${r}" ry="${(r * k).toFixed(2)}" fill="none" stroke-width="${root ? .8 : 1}"
      stroke="${root ? (i % 2 ? "rgba(120,110,90,.22)" : "rgba(255,255,255,.18)") : (i % 2 ? "rgba(255,255,255,.07)" : "rgba(0,0,0,.12)")}"/>`;
  }

  // 螺栓：先算投影位置，按 y 排序后画（远的先画，近的盖在上面）
  const bolts = [];
  for (let i = 0; i < n; i++) {
    const a = angleAt(i);
    bolts.push({ ch: monIdx.get(i), no: boltNoAt(fdef, i), x: cx + Math.cos(a) * rb, y: cy + Math.sin(a) * rb * k });
  }
  bolts.sort((p, q) => p.y - q.y);

  let boltsSvg = "";
  const mons = [];
  for (const b of bolts) {
    const x = b.x.toFixed(1), y = b.y.toFixed(1);
    if (b.ch == null) {
      boltsSvg += `<g class="bolt-g idle" transform="translate(${x},${y})">${drawBolt(geo, k, false)}</g>`;
      continue;
    }

    const rs = (geoMon.w + 5).toFixed(1), rsel = (geoMon.w + 11).toFixed(1);
    boltsSvg += `
      <g class="bolt-g mon" data-ch="${b.ch}" data-st="offline" data-sel="0" transform="translate(${x},${y})">
        <ellipse class="halo" rx="${rs}" ry="${(rs * k).toFixed(1)}" filter="url(#f3-soft)"/>
        <ellipse class="stat" rx="${rs}" ry="${(rs * k).toFixed(1)}"/>
        <ellipse class="selring" rx="${rsel}" ry="${(rsel * k).toFixed(1)}"/>${drawBolt(geoMon, k, true)}
      </g>`;
    // 引线从探头顶部出发（叶根的探头直接立在衬套上，没有螺母那一段高度）
    mons.push({ ch: b.ch, no: b.no, x: b.x, y: b.y, top: b.y - (root ? 0 : geoMon.hN) - geoMon.hP - geoMon.pr * k });
  }

  // 标签牌：按投影 x 分左右两列，列内按 y 排（远的在上），沿整列高度均匀铺开——
  // 两个一列时正好落在上下角，引线从后排螺栓往上角走、前排往下角走，互不交叉
  mons.sort((p, q) => p.x - q.x);
  const leftN = Math.ceil(mons.length / 2);
  const cols = [mons.slice(0, leftN), mons.slice(leftN)].map(c => c.sort((p, q) => p.y - q.y));
  let leads = "", chips = "";
  const chipRects = [];
  cols.forEach((col, side) => {
    if (col.length === 0) return;
    const x0 = side === 0 ? 8 : 520 - 8 - chipW;
    const yMin = 8, yMax = h - chipH - 8;
    col.forEach((b, i) => {
      const y = col.length === 1 ? yMin : yMin + (yMax - yMin) * i / (col.length - 1);
      const ex = side === 0 ? x0 + chipW : x0;
      chipRects.push({ x: x0, y, w: chipW, h: chipH });
      leads += `
      <g class="lead" data-ch="${b.ch}" data-st="offline">
        <line x1="${b.x.toFixed(1)}" y1="${b.top.toFixed(1)}" x2="${ex}" y2="${(y + chipH / 2).toFixed(1)}"/>
        <circle cx="${b.x.toFixed(1)}" cy="${b.top.toFixed(1)}" r="2"/>
      </g>`;
      chips += `
      <g class="chip" data-ch="${b.ch}" data-st="offline" data-sel="0" transform="translate(${x0},${y.toFixed(1)})">
        <rect width="${chipW}" height="${chipH}" rx="5"/>
        <rect class="bar" x="0" y="0" width="3" height="${chipH}" rx="1.5"/>
        <text class="chip-ch" x="12" y="16">CH${b.ch}<tspan class="chip-no"> · #${b.no}</tspan></text>
        <text class="chip-kn" x="${chipW - 26}" y="36">--</text>
        <text class="chip-u" x="${chipW - 22}" y="36">kN</text>
      </g>`;
    });
  });

  // 环外的文字撞上标签牌就不画（牌子在上层，压住的字只剩半截更难看）
  const hitsChip = (x, y, hw, hh) => chipRects.some(r =>
    x + hw > r.x - 4 && x - hw < r.x + r.w + 4 && y + hh > r.y - 4 && y - hh < r.y + r.h + 4);

  // 编号：螺栓少全标，多了每 5 / 10 颗标一个（#1 总标），跟图纸一样能对号
  const numStep = n <= 16 ? 1 : n <= 40 ? 5 : 10;
  let numbers = "";
  for (let i = 0; i < n; i++) {
    const no = boltNoAt(fdef, i);
    if (no !== 1 && no % numStep !== 0) continue;
    const [x, y] = outer(angleAt(i), 13);
    if (hitsChip(x, y, 7, 5)) continue;
    numbers += `<text class="bno" x="${x.toFixed(1)}" y="${y.toFixed(1)}" dy="3.2">${no}</text>`;
  }

  // 图纸上的基准标记（可选）：短刻度 + 文字，at 是螺栓号，可带小数
  let markSvg = "";
  for (const mk of fdef.marks || []) {
    const at = Number(mk.at);
    if (!(at >= 1 && at <= n + 1)) continue;
    const a = angleAt(boltIndexOf(fdef, Math.floor(at)) + (at - Math.floor(at)));
    const [x1, y1] = outer(a, 2), [x2, y2] = outer(a, 24), [tx, ty] = outer(a, 31);
    const anchor = Math.cos(a) > 0.3 ? "start" : Math.cos(a) < -0.3 ? "end" : "middle";
    markSvg += `
      <g class="mark">
        <line x1="${x1.toFixed(1)}" y1="${y1.toFixed(1)}" x2="${x2.toFixed(1)}" y2="${y2.toFixed(1)}"/>
        <text x="${tx.toFixed(1)}" y="${ty.toFixed(1)}" dy="3.5" text-anchor="${anchor}">${mk.label || ""}</text>
      </g>`;
  }

  const tubeBottom = cy + t + tubeH;
  const rk = r => (r * k).toFixed(2);

  // 结构层：钢法兰 = 塔筒 + 有厚度的法兰环；叶根 = 轮毂 + 变桨轴承 + 白色层合板叶根筒（切面在上）
  let body;
  if (root) {
    const yB = cy + t, yH = yB + tBear, rHub = ro + 30;
    body = `
    <!-- 轮毂：深色铸铁，往下渐隐 -->
    <g mask="url(#f3-tube-mask)">
      <path d="M${cx - rHub},${yH} L${cx - rHub},${(tubeBottom - rHub * k).toFixed(1)} A${rHub},${rk(rHub)} 0 0 0 ${cx + rHub},${(tubeBottom - rHub * k).toFixed(1)} L${cx + rHub},${yH} Z" fill="url(#f3-hubg)"/>
    </g>
    <!-- 变桨轴承：钢制回转环，比叶根大一圈，中间一道内外圈分界缝 -->
    <g filter="url(#f3-shadow)">
      <path d="M${cx - rBear},${yB} ${halfArc(cx, yB, rBear, k, false)} L${cx + rBear},${yH} A${rBear},${rk(rBear)} 0 0 1 ${cx - rBear},${yH} Z" fill="url(#f3-bear)"/>
      <path d="M${cx - rBear},${yB} ${halfArc(cx, yB, rBear, k, false)} L${cx + ro},${yB} A${ro},${rk(ro)} 0 0 1 ${cx - ro},${yB} Z" fill="url(#f3-bear-top)"/>
      <path d="M${cx - rBear},${(yB + tBear * .45).toFixed(1)} ${halfArc(cx, yB + tBear * .45, rBear, k, false)}" fill="none" stroke="rgba(0,0,0,.55)" stroke-width="1.4"/>
      <path d="M${cx - rBear},${yH} ${halfArc(cx, yH, rBear, k, false)}" fill="none" stroke="rgba(0,0,0,.6)" stroke-width="1.2"/>
      <path d="M${cx - rBear},${yB} ${halfArc(cx, yB, rBear, k, false)}" fill="none" stroke="rgba(255,255,255,.28)" stroke-width="1"/>
    </g>
    <!-- 叶根层合板筒：外壁 → 筒内 → 内壁 → 切面（层合板断面） -->
    <g filter="url(#f3-shadow)">
      <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, false)} L${cx + ro},${yB} A${ro},${rk(ro)} 0 0 1 ${cx - ro},${yB} Z" fill="url(#f3-comp-wall)"/>
      <ellipse cx="${cx}" cy="${cy}" rx="${ri}" ry="${rk(ri)}" fill="url(#f3-hole)"/>
      <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, true)} L${cx + ri},${cy + 34} A${ri},${rk(ri)} 0 0 0 ${cx - ri},${cy + 34} Z" fill="url(#f3-comp-in)"/>
      <path d="${ellPath(cx, cy, ro, k)} ${ellPath(cx, cy, ri, k)}" fill-rule="evenodd" fill="url(#f3-comp-face)"/>
      <path d="${ellPath(cx, cy, ro, k)} ${ellPath(cx, cy, ri, k)}" fill-rule="evenodd" fill="url(#f3-sheen)" opacity=".6"/>
    </g>
    ${marks}
    <ellipse cx="${cx}" cy="${cy}" rx="${rb}" ry="${rk(rb)}" fill="none" stroke="rgba(60,50,40,.35)" stroke-dasharray="3 4"/>
    <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, true)}" fill="none" stroke="rgba(255,255,255,.7)" stroke-width="1.2"/>
    <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, false)}" fill="none" stroke="rgba(0,0,0,.28)" stroke-width="1"/>
    <path d="M${cx - ro},${yB} ${halfArc(cx, yB, ro, k, false)}" fill="none" stroke="rgba(0,0,0,.45)" stroke-width="1.2"/>
    <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, false)}" fill="none" stroke="rgba(255,255,255,.4)" stroke-width="1"/>
    <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, true)}" fill="none" stroke="rgba(0,0,0,.45)" stroke-width="1"/>`;
  } else {
    body = `
    <!-- 法兰下面的塔筒：往下渐隐，表示还在延伸 -->
    <g mask="url(#f3-tube-mask)">
      <path d="M${cx - rt},${cy + t} L${cx - rt},${(tubeBottom - rt * k).toFixed(1)} A${rt},${rk(rt)} 0 0 0 ${cx + rt},${(tubeBottom - rt * k).toFixed(1)} L${cx + rt},${cy + t} Z" fill="url(#f3-tube)"/>
      <line x1="${cx - rt}" y1="${cy + t}" x2="${cx - rt}" y2="${tubeBottom}" stroke="rgba(255,255,255,.08)"/>
    </g>
    <!-- 法兰环：外壁 → 孔内 → 孔的背壁 → 顶面（底色 + 侧向受光） -->
    <g filter="url(#f3-shadow)">
      <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, false)} L${cx + ro},${cy + t} A${ro},${rk(ro)} 0 0 1 ${cx - ro},${cy + t} Z" fill="url(#f3-wall)"/>
      <ellipse cx="${cx}" cy="${cy}" rx="${ri}" ry="${rk(ri)}" fill="url(#f3-hole)"/>
      <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, true)} L${cx + ri},${cy + t} A${ri},${rk(ri)} 0 0 0 ${cx - ri},${cy + t} Z" fill="url(#f3-inwall)"/>
      <path d="${ellPath(cx, cy, ro, k)} ${ellPath(cx, cy, ri, k)}" fill-rule="evenodd" fill="url(#f3-top)"/>
      <path d="${ellPath(cx, cy, ro, k)} ${ellPath(cx, cy, ri, k)}" fill-rule="evenodd" fill="url(#f3-sheen)"/>
    </g>
    ${marks}
    <ellipse cx="${cx}" cy="${cy}" rx="${rb}" ry="${rk(rb)}" fill="none" stroke="rgba(16,30,50,.5)" stroke-dasharray="3 4"/>
    <!-- 倒角：远侧上沿受光，近侧上沿和外壁下沿压暗 -->
    <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, true)}" fill="none" stroke="rgba(255,255,255,.55)" stroke-width="1.3"/>
    <path d="M${cx - ro},${cy} ${halfArc(cx, cy, ro, k, false)}" fill="none" stroke="rgba(0,0,0,.35)" stroke-width="1"/>
    <path d="M${cx - ro},${cy + t} ${halfArc(cx, cy + t, ro, k, false)}" fill="none" stroke="rgba(0,0,0,.55)" stroke-width="1.2"/>
    <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, false)}" fill="none" stroke="rgba(255,255,255,.35)" stroke-width="1"/>
    <path d="M${cx - ri},${cy} ${halfArc(cx, cy, ri, k, true)}" fill="none" stroke="rgba(0,0,0,.5)" stroke-width="1"/>`;
  }

  flangeBox.innerHTML = `
  <svg viewBox="0 0 520 ${h}">
    <defs>
      <linearGradient id="f3-comp-face" gradientUnits="userSpaceOnUse" x1="0" y1="${(cy - ro * k).toFixed(1)}" x2="0" y2="${(cy + ro * k).toFixed(1)}">
        <stop offset="0" stop-color="#f1f2ec"/><stop offset=".5" stop-color="#dcded4"/><stop offset="1" stop-color="#b9bcb1"/>
      </linearGradient>
      <linearGradient id="f3-comp-wall" gradientUnits="userSpaceOnUse" x1="${cx - ro}" y1="0" x2="${cx + ro}" y2="0">
        <stop offset="0" stop-color="#8f979d"/><stop offset=".3" stop-color="#f4f5f1"/>
        <stop offset=".55" stop-color="#e1e4df"/><stop offset="1" stop-color="#727a81"/>
      </linearGradient>
      <linearGradient id="f3-comp-in" gradientUnits="userSpaceOnUse" x1="0" y1="${cy - ri * k}" x2="0" y2="${cy + 34}">
        <stop offset="0" stop-color="#a7adb0"/><stop offset=".55" stop-color="#5d646b"/><stop offset="1" stop-color="#101d30"/>
      </linearGradient>
      <linearGradient id="f3-bear" gradientUnits="userSpaceOnUse" x1="${cx - rBear}" y1="0" x2="${cx + rBear}" y2="0">
        <stop offset="0" stop-color="#293b55"/><stop offset=".3" stop-color="#8ea3bd"/>
        <stop offset=".55" stop-color="#66809e"/><stop offset="1" stop-color="#1d2d44"/>
      </linearGradient>
      <linearGradient id="f3-bear-top" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#c9d6e3"/><stop offset="1" stop-color="#8ea3ba"/>
      </linearGradient>
      <linearGradient id="f3-hubg" gradientUnits="userSpaceOnUse" x1="${cx - ro - 30}" y1="0" x2="${cx + ro + 30}" y2="0">
        <stop offset="0" stop-color="#181f29"/><stop offset=".35" stop-color="#3a4552"/>
        <stop offset=".6" stop-color="#2a333e"/><stop offset="1" stop-color="#121821"/>
      </linearGradient>
      <linearGradient id="f3-top" gradientUnits="userSpaceOnUse" x1="0" y1="${(cy - ro * k).toFixed(1)}" x2="0" y2="${(cy + ro * k).toFixed(1)}">
        <stop offset="0" stop-color="#dfe8f2"/><stop offset=".45" stop-color="#b4c5d7"/><stop offset="1" stop-color="#7890ab"/>
      </linearGradient>
      <linearGradient id="f3-sheen" gradientUnits="userSpaceOnUse" x1="${cx - ro}" y1="0" x2="${cx + ro}" y2="0">
        <stop offset="0" stop-color="rgba(0,0,0,.30)"/><stop offset=".22" stop-color="rgba(255,255,255,.20)"/>
        <stop offset=".45" stop-color="rgba(255,255,255,0)"/><stop offset=".72" stop-color="rgba(0,0,0,.06)"/>
        <stop offset="1" stop-color="rgba(0,0,0,.38)"/>
      </linearGradient>
      <linearGradient id="f3-wall" gradientUnits="userSpaceOnUse" x1="${cx - ro}" y1="0" x2="${cx + ro}" y2="0">
        <stop offset="0" stop-color="#33486a"/><stop offset=".3" stop-color="#93aac5"/>
        <stop offset=".55" stop-color="#6e88a7"/><stop offset="1" stop-color="#24374f"/>
      </linearGradient>
      <linearGradient id="f3-inwall" gradientUnits="userSpaceOnUse" x1="${cx - ri}" y1="0" x2="${cx + ri}" y2="0">
        <stop offset="0" stop-color="#182741"/><stop offset=".5" stop-color="#3d5574"/><stop offset="1" stop-color="#14233a"/>
      </linearGradient>
      <linearGradient id="f3-tube" gradientUnits="userSpaceOnUse" x1="${cx - rt}" y1="0" x2="${cx + rt}" y2="0">
        <stop offset="0" stop-color="#22354f"/><stop offset=".28" stop-color="#7791af"/>
        <stop offset=".5" stop-color="#5b7696"/><stop offset="1" stop-color="#1a2b42"/>
      </linearGradient>
      <linearGradient id="f3-fade" gradientUnits="userSpaceOnUse" x1="0" y1="${cy + t}" x2="0" y2="${tubeBottom}">
        <stop offset="0" stop-color="#fff"/><stop offset=".3" stop-color="#fff"/><stop offset="1" stop-color="#000"/>
      </linearGradient>
      <mask id="f3-tube-mask"><rect x="0" y="0" width="520" height="${h}" fill="url(#f3-fade)"/></mask>
      <radialGradient id="f3-hole" gradientUnits="userSpaceOnUse" cx="${cx}" cy="${cy + t}" r="${ri}">
        <stop offset="0" stop-color="#091322"/><stop offset="1" stop-color="#101f34"/>
      </radialGradient>
      <linearGradient id="f3-probe" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stop-color="#1c2c43"/><stop offset=".4" stop-color="#52708f"/>
        <stop offset=".7" stop-color="#37526f"/><stop offset="1" stop-color="#17263b"/>
      </linearGradient>
      <radialGradient id="f3-glow">
        <stop offset=".5" stop-color="rgba(47,130,200,.16)"/><stop offset="1" stop-color="rgba(47,130,200,0)"/>
      </radialGradient>
      <filter id="f3-shadow" x="-20%" y="-30%" width="140%" height="180%">
        <feDropShadow dx="0" dy="7" stdDeviation="8" flood-color="#000" flood-opacity=".5"/>
      </filter>
      <filter id="f3-soft" x="-60%" y="-120%" width="220%" height="340%"><feGaussianBlur stdDeviation="2.5"/></filter>
    </defs>

    <ellipse cx="${cx}" cy="${cy + 30}" rx="250" ry="120" fill="url(#f3-glow)"/>
    ${body}

    ${numbers}
    ${markSvg}
    ${boltsSvg}
    ${leads}
    ${chips}
  </svg>`;

  flangeBolts = [];
  flangeBox.querySelectorAll(".bolt-g.mon").forEach(g => {
    const ch = Number(g.dataset.ch);
    const chip = flangeBox.querySelector(`.chip[data-ch="${ch}"]`);
    const lead = flangeBox.querySelector(`.lead[data-ch="${ch}"]`);
    flangeBolts.push({ ch, g, chip, lead, kn: chip.querySelector(".chip-kn") });
    const pick = () => { selected = ch; refreshAll(); };
    g.onclick = pick;
    chip.onclick = pick;
  });
}

function setAttrIfChanged(el, name, value) {
  if (el.getAttribute(name) !== value) el.setAttribute(name, value);
}

function drawFlange() {
  const fdef = viewFlangeDef();
  // 没接入的法兰也画全：螺栓全画、8 个规划探头位也画（影子探头 + "待接入"标签牌）
  const bound = isBoundFlange();
  const slots = viewSlots();
  const key = `${viewTurbine}|${viewFlange}|${fdef.boltCount}|${slots.join(",")}`;
  if (key !== flangeKey) { buildFlangeSvg(fdef, slots); flangeKey = key; }

  for (const b of flangeBolts) {
    const h = viewHealth(b.ch);
    const m = h === "pending" ? null : channels[b.ch] && channels[b.ch].last;
    const sel = b.ch === selected ? "1" : "0";
    setAttrIfChanged(b.g, "data-st", h); setAttrIfChanged(b.g, "data-sel", sel);
    setAttrIfChanged(b.chip, "data-st", h); setAttrIfChanged(b.chip, "data-sel", sel);
    setAttrIfChanged(b.lead, "data-st", h);
    const mk = h === "pending" || h === "offline" ? null : mainKn(b.ch);
    setAttrIfChanged(b.chip, "data-delta", mk && mk.delta ? "1" : "0");
    // 接线核对：刚翻转的通道，牌子和螺栓一起闪
    const flash = bound && recentFlip(b.ch) ? "1" : "0";
    setAttrIfChanged(b.chip, "data-flash", flash); setAttrIfChanged(b.g, "data-flash", flash);
    // 牌子上写的是文字状态（未标定/探头无信号/有信号·未配置…）还是数字：
    // 数字右对齐贴着单位；文字从牌子左侧排起，否则"有信号·未配置"这种长词会从左边顶出牌子
    const textual = TEXTUAL_HEALTH.includes(h);
    if (b.kn.getAttribute("data-textual") !== String(textual)) {
      b.kn.setAttribute("data-textual", String(textual));
      b.kn.setAttribute("x", textual ? 12 : FL.chipW - 26);
      b.kn.style.textAnchor = textual ? "start" : "end";   // text-anchor 被样式表定着，要用 style 覆盖
    }
    const kn = h === "pending" ? "待接入"
      : h === "stray" ? (sigs[b.ch] && sigs[b.ch].configured ? "有信号·无基线" : "有信号·未配置")
      : h === "noprobe" ? "探头无信号"
      : h === "shift" ? "基线失配"
      : h === "uncal" ? "未标定"
      : h === "badbase" ? "基线可疑"
      : mk == null ? "--"
      : mk.delta ? "Δ" + fmtSigned(mk.v, 1)
      : fmt(mk.v, 1);
    if (b.kn.textContent !== kn) b.kn.textContent = kn;
  }

  const stat = bound
    ? `探头 ${viewChannels().length} / ${slots.length} 在线 · ${connected ? "监测中" : "未连接"}`
    : `${tid(viewTurbine)} · 规划探头 ${slots.length} 位 · 尚未接入 · ${fdef.boltCount} 颗螺栓`;
  const statEl = $("flangeStat");
  if (statEl.textContent !== stat) statEl.textContent = stat;
}

// 运维视图的测点表：只列当前法兰的通道
function rebuildTable() {
  const t = $("chtable");
  t.innerHTML = "";
  for (const ch of viewChannels()) {
    const d = document.createElement("div");
    d.className = "chrow";
    d.dataset.ch = ch;
    d.innerHTML = `<span class="dot"></span><span class="ch">CH${ch}</span>
      <span class="f">--<small> kN</small></span><span class="d">--</span>`;
    d.onclick = () => { selected = ch; refreshAll(); };
    t.appendChild(d);
  }
}

function updateRow(ch) {
  const d = document.querySelector(`.chrow[data-ch="${ch}"]`);
  const c = channels[ch];
  if (!d || !c || !c.last) return;
  const m = c.last;
  const mk = mainKn(ch);
  d.querySelector(".dot").className = "dot " + (m.normal && !m.mismatch ? "on" : "err");
  d.querySelector(".f").innerHTML = mk == null ? "--<small> kN</small>"
    : (mk.delta ? fmtSigned(mk.v, 2) + "<small> kN 较锚定</small>" : fmt(mk.v, 2) + "<small> kN</small>");
  d.querySelector(".d").innerHTML = m.mismatch
    ? `<span class="bad">基线失配 Δt ${fmt(m.deltaTof, 0)} ns</span>`
    : m.normal
      ? `声时差 ${fmt(m.deltaTof, 2)} · ${fmt(m.temp, 1)}℃${isWeakSignal(ch) ? " · 信号弱" : ""}`
      : `<span class="bad">${m.status}</span>`;
}

// 选中的测点得是当前法兰上的：切换法兰时把选中项挪到该法兰的第一个通道
function ensureSelectedInView() {
  const vc = viewChannels();
  if (vc.length && !vc.includes(selected)) selected = vc[0];
}

function refreshAll() {
  ensureSelectedInView();
  document.querySelectorAll(".chrow").forEach(d =>
    d.classList.toggle("sel", Number(d.dataset.ch) === selected));
  $("detailTitle").textContent = selected ? `测点详情 · CH${selected}` : "测点详情";
  viewChannels().forEach(ch => updateRow(ch));
  updateBoltCards();
  refreshDetail();
  drawFlange();
  drawWave();
  drawDist();
  drawTrend();
}

function refreshDetail() {
  const c = channels[selected];
  const m = c && c.last;
  const mk = m ? mainKn(selected) : null;
  $("bigForce").innerHTML = mk != null
    ? mainKnHtml(mk, 2)
    : (m && m.mismatch ? "失配" : m && !calibratedCh(selected) ? "未标定" : "--") + "<small>kN</small>";
  const snrTxt = sigs[selected] && sigs[selected].snr != null ? `   信噪比 ${sigs[selected].snr} dB${isWeakSignal(selected) ? "（弱）" : ""}` : "";
  const dTxt = m && !m.mismatch && anchored() && liveForce(selected) != null ? `   ${deltaTxt(selected)}` : "";
  $("waveStatus").textContent = m
    ? `状态：${m.mismatch ? "声时偏离基线 " + fmt(m.deltaTof, 0) + " ns" : m.status}   波动 ±${stddev(c.dts) == null ? "--" : stddev(c.dts).toFixed(2)} ns${snrTxt}${dTxt}`
    : "";
  if (!m) { metricVals.forEach(v => v.textContent = "--"); return; }
  metricVals[0].textContent = fmt(m.deltaTof, 2);
  metricVals[1].textContent = fmt(m.echoTime, 1);
  metricVals[2].textContent = fmt(m.temp, 2);
  metricVals[3].textContent = m.deltaTof == null ? "--" : fmt(m.deltaTof * umPerNs, 2);
}

// ── 波形 ──
const wcv = $("wave"), wctx = wcv.getContext("2d");
function drawWave() {
  const w = wcv.clientWidth, h = wcv.height;
  if (!w) return;
  if (wcv.width !== w) wcv.width = w;
  wctx.clearRect(0, 0, w, h);
  const rc = { x: 44, y: 8, w: w - 58, h: h - 28 };
  wctx.strokeStyle = "#1e3a5c";
  wctx.strokeRect(rc.x, rc.y, rc.w, rc.h);

  const c = channels[selected];
  const live = c && c.last && c.last.wave;
  const ref = $("showref").checked && c && c.refWave;
  if (!live && !ref) {
    wctx.fillStyle = "#44566e"; wctx.textAlign = "center"; wctx.font = "12px sans-serif";
    wctx.fillText(connected ? "等待波形……" : "未连接设备", rc.x + rc.w / 2, rc.y + rc.h / 2);
    return;
  }

  let xMin = Infinity, xMax = -Infinity, peak = 100;
  for (const wv of [live, ref]) {
    if (!wv) continue;
    xMin = Math.min(xMin, wv.start);
    xMax = Math.max(xMax, wv.start + (wv.data.length - 1) * wv.clock);
    for (const v of wv.data) peak = Math.max(peak, Math.abs(v));
  }
  if (xMax <= xMin) return;
  const yMax = (Math.floor(peak / 100) + 1) * 100;
  const px = ns => rc.x + (ns - xMin) / (xMax - xMin) * rc.w;
  const py = v => rc.y + (yMax - v) / (2 * yMax) * rc.h;

  wctx.strokeStyle = "#152a44";
  wctx.beginPath();
  for (let i = 1; i < 8; i++) { const x = rc.x + rc.w * i / 8; wctx.moveTo(x, rc.y); wctx.lineTo(x, rc.y + rc.h); }
  wctx.moveTo(rc.x, py(0)); wctx.lineTo(rc.x + rc.w, py(0));
  wctx.stroke();

  wctx.fillStyle = "#4d637e"; wctx.font = "10px sans-serif"; wctx.textAlign = "center";
  for (let i = 0; i <= 8; i += 2) {
    wctx.fillText((xMin + (xMax - xMin) * i / 8).toFixed(0), rc.x + rc.w * i / 8, rc.y + rc.h + 12);
  }
  wctx.textAlign = "right";
  wctx.fillText(yMax, rc.x - 4, rc.y + 9);
  wctx.fillText(-yMax, rc.x - 4, rc.y + rc.h);

  function path(wv, color, width) {
    wctx.strokeStyle = color; wctx.lineWidth = width;
    wctx.beginPath();
    for (let i = 0; i < wv.data.length; i++) {
      const x = px(wv.start + i * wv.clock), y = py(wv.data[i]);
      i === 0 ? wctx.moveTo(x, y) : wctx.lineTo(x, y);
    }
    wctx.stroke(); wctx.lineWidth = 1;
  }
  if (ref) path(ref, "#3a5578", 1);
  if (live) {
    wctx.shadowColor = "rgba(47, 208, 200, .55)";
    wctx.shadowBlur = 6;
    path(live, "#2fd0c8", 1.5);
    wctx.shadowBlur = 0;
  }

  const mk = c && c.last && c.last.echoTime;
  if (mk != null && mk >= xMin && mk <= xMax) {
    wctx.strokeStyle = "#f0b429"; wctx.setLineDash([4, 3]);
    wctx.beginPath(); wctx.moveTo(px(mk), rc.y); wctx.lineTo(px(mk), rc.y + rc.h); wctx.stroke();
    wctx.setLineDash([]);
  }
}
$("showref").onchange = drawWave;

// ── 法兰轴力分布（柱状图，看一圈螺栓受力均不均匀）──
const dcv = $("dist"), dctx = dcv.getContext("2d");
function drawDist() {
  // 画布高度跟着面板走（底部两个面板撑满剩余高度），像素尺寸同步到布局尺寸才不糊
  const w = dcv.clientWidth, h = dcv.clientHeight || dcv.height;
  if (!w) return;
  if (dcv.width !== w) dcv.width = w;
  if (dcv.height !== h) dcv.height = h;
  dctx.clearRect(0, 0, w, h);
  const rc = { x: 44, y: 12, w: w - 58, h: h - 34 };
  dctx.strokeStyle = "#1e3a5c";
  dctx.strokeRect(rc.x, rc.y, rc.w, rc.h);

  // 看的是没接入的法兰：没有数据可画。接入的只画属于这个法兰的通道
  const chs = viewChannels();
  const vals = chs.map(ch => liveForce(ch));
  if (chs.length === 0 || vals.every(v => v == null)) {
    dctx.fillStyle = "#44566e"; dctx.textAlign = "center"; dctx.font = "12px sans-serif";
    dctx.fillText(!isBoundFlange() ? "本法兰尚未接入监测"
      : !flangeCal(viewFlange) ? UNCAL_TEXT
      : anchored() && flangeRated(viewFlange) === 0 ? "未配置额定值：锚定口径下估不出绝对预紧力，轴力分布不画"
      : "等待数据……", rc.x + rc.w / 2, rc.y + rc.h / 2 + 4);
    return;
  }

  const rated = flangeRated(viewFlange);
  let yMax = Math.max(rated, ...vals.filter(v => v != null));
  yMax = Math.ceil(yMax * 1.25 / 5) * 5 || 10;
  const py = v => rc.y + (1 - v / yMax) * rc.h;

  // 额定线和报警线
  dctx.font = "10px sans-serif";
  if (rated > 0) {
    dctx.strokeStyle = "rgba(53, 196, 99, .55)";
    dctx.setLineDash([5, 4]);
    dctx.beginPath(); dctx.moveTo(rc.x, py(rated)); dctx.lineTo(rc.x + rc.w, py(rated)); dctx.stroke();
    const alarmY = rated * plan.alarmRatio;
    dctx.strokeStyle = "rgba(229, 72, 77, .55)";
    dctx.beginPath(); dctx.moveTo(rc.x, py(alarmY)); dctx.lineTo(rc.x + rc.w, py(alarmY)); dctx.stroke();
    dctx.setLineDash([]);
    dctx.textAlign = "left";
    dctx.fillStyle = "rgba(53, 196, 99, .8)";
    dctx.fillText("额定 " + rated, rc.x + rc.w - 52, py(rated) - 3);
    dctx.fillStyle = "rgba(229, 72, 77, .8)";
    dctx.fillText("报警线", rc.x + rc.w - 40, py(alarmY) - 3);
  }

  dctx.textAlign = "right";
  dctx.fillStyle = "#4d637e";
  dctx.fillText(yMax.toFixed(0), rc.x - 4, rc.y + 9);
  dctx.fillText("0", rc.x - 4, rc.y + rc.h);

  const bw = Math.min(46, rc.w / chs.length * 0.5);
  chs.forEach((ch, i) => {
    const cx = rc.x + rc.w * (i + 0.5) / chs.length;
    const v = vals[i];
    dctx.textAlign = "center";
    if (v != null) {
      const hgt = Math.max(2, rc.h * v / yMax);
      const hh = boltHealth(ch);
      const color = hh === "ok" ? "#2fd0c8" : hh === "warn" ? "#f0b429"
        : hh === "loose" ? "#e5484d" : hh === "fault" ? "#d68a3c" : "#44566e";
      const grad = dctx.createLinearGradient(0, rc.y + rc.h - hgt, 0, rc.y + rc.h);
      grad.addColorStop(0, color);
      grad.addColorStop(1, "rgba(20, 45, 75, .35)");
      dctx.fillStyle = grad;
      dctx.fillRect(cx - bw / 2, rc.y + rc.h - hgt, bw, hgt);
      dctx.fillStyle = "#cfe3f5";
      dctx.font = "bold 12px sans-serif";
      dctx.fillText(v.toFixed(1), cx, rc.y + rc.h - hgt - 5);
    } else {
      dctx.fillStyle = "#44566e";
      dctx.fillText("--", cx, rc.y + rc.h - 8);
    }

    dctx.fillStyle = "#8fa7c2";
    dctx.font = "11px sans-serif";
    dctx.fillText("CH" + ch, cx, rc.y + rc.h + 14);
  });
}

// 曲线的一条线：key "机组:通道"，带标签、颜色、是不是变化量。全场图上不同机组的 CH1 不能混成一条
function seriesEntry(t, ch, multi) {
  const isDelta = withTurbine(t, () => anchored() && ratedOf(Number(ch)) === 0);
  return { t, ch: Number(ch), label: multi ? `${tid(t)} CH${ch}` : `CH${ch}`, color: seriesColor(t, ch), isDelta, pts: [] };
}
// 颜色：单机组按通道在测点表里的位置取（和轴力分布一致）；全场图按机组+通道散开
function seriesColor(t, ch) {
  if (t === viewTurbine && !$("view-flange").hidden) return chColor(Number(ch));
  return COLORS[(t * 3 + Number(ch)) % COLORS.length];
}

// 近 5 分钟实时曲线：当前机组、给定的通道（法兰页 = 当前法兰的通道）
function seriesFromLive(spanMs, chs) {
  const now = Date.now(), out = {};
  for (const ch of chs) {
    const e = seriesEntry(viewTurbine, ch, false);
    e.pts = ((channels[ch] && channels[ch].hist) || []).filter(p => now - p.t <= spanMs);
    out[`${viewTurbine}:${ch}`] = e;
  }
  return out;
}

// 曲线画什么：有预紧力（估算或绝对）画预紧力；锚定口径没额定值时画较锚定变化量。
// 同一个法兰内两者不会混：kn 为空当且仅当"锚定 + 该法兰未配置额定值"，那时它所有通道都是 dkn
const trendValue = p => p.kn != null ? Number(p.kn) : p.dkn != null ? Number(p.dkn) : null;
// 当前法兰的曲线画的是不是变化量（决定图例带不带 Δ 和符号）
const trendIsDelta = () => anchored() && flangeRated(viewFlange) === 0;

// 历史曲线：turbine/chs 给了就只留这台机组这些通道（法兰页），不给就全场（报告页，线上标机组号）
function seriesFromArchive(points, chs, turbine) {
  const out = {};
  const keep = chs ? new Set(chs) : null;
  const multi = turbine == null;
  for (const p of points) {
    const t = p.tb ?? defaultTurbine();
    if (turbine != null && t !== turbine) continue;
    if (keep && !keep.has(Number(p.ch))) continue;
    const v = trendValue(p);
    if (v == null) continue;
    const key = `${t}:${p.ch}`;
    if (!out[key]) out[key] = seriesEntry(t, p.ch, multi);
    out[key].pts.push({ t: p.t, f: v });
  }
  return out;
}

function axisLabel(spanMs, i, n) {
  const ms = spanMs * (n - i) / n;
  if (spanMs <= 60 * 60 * 1000) return `-${Math.round(ms / 60000)}min`;
  if (spanMs <= 24 * 60 * 60 * 1000) return `-${(ms / 3600000).toFixed(0)}h`;
  return `-${Math.round(ms / 86400000)}d`;
}

// marks：基线重置事件 [{t, ch}]——现场重新置零后曲线会跳一截，画条竖虚线标出来，别让人以为是螺栓动了
function drawForceChart(cv, ctx, series, spanMs, emptyText = "这段时间还没有落盘数据……", marks = []) {
  const w = cv.clientWidth, h = cv.clientHeight || cv.height;
  if (!w) return;
  if (cv.width !== w) cv.width = w;
  if (cv.height !== h) cv.height = h;
  ctx.clearRect(0, 0, w, h);
  const rc = { x: 46, y: 10, w: w - 196, h: h - 28 };
  ctx.strokeStyle = "#1e3a5c";
  ctx.strokeRect(rc.x, rc.y, rc.w, rc.h);

  const now = Date.now();
  let fMin = Infinity, fMax = -Infinity, any = false;
  for (const e of Object.values(series)) {
    for (const p of e.pts) {
      if (now - p.t > spanMs) continue;
      fMin = Math.min(fMin, p.f); fMax = Math.max(fMax, p.f); any = true;
    }
  }
  if (!any) {
    ctx.fillStyle = "#44566e"; ctx.textAlign = "center"; ctx.font = "12px sans-serif";
    ctx.fillText(emptyText, rc.x + rc.w / 2, rc.y + rc.h / 2 + 4);
    return;
  }
  const pad = Math.max(0.5, (fMax - fMin) * 0.15);
  fMin -= pad; fMax += pad;
  const px = t => rc.x + (1 - (now - t) / spanMs) * rc.w;
  const py = f => rc.y + (fMax - f) / (fMax - fMin) * rc.h;

  ctx.strokeStyle = "#152a44";
  ctx.beginPath();
  for (let i = 1; i < 5; i++) { const x = rc.x + rc.w * i / 5; ctx.moveTo(x, rc.y); ctx.lineTo(x, rc.y + rc.h); }
  for (let i = 1; i < 3; i++) { const y = rc.y + rc.h * i / 3; ctx.moveTo(rc.x, y); ctx.lineTo(rc.x + rc.w, y); }
  ctx.stroke();

  ctx.fillStyle = "#4d637e"; ctx.font = "10px sans-serif"; ctx.textAlign = "right";
  ctx.fillText(fMax.toFixed(1), rc.x - 4, rc.y + 9);
  ctx.fillText(((fMax + fMin) / 2).toFixed(1), rc.x - 4, rc.y + rc.h / 2 + 3);
  ctx.fillText(fMin.toFixed(1), rc.x - 4, rc.y + rc.h);
  ctx.textAlign = "center";
  for (let i = 0; i <= 5; i++) ctx.fillText(axisLabel(spanMs, i, 5), rc.x + rc.w * i / 5, rc.y + rc.h + 12);

  // 线按机组、通道排序
  const entries = Object.values(series).sort((a, b) => a.t - b.t || a.ch - b.ch);
  for (const e of entries) {
    const pts = e.pts.filter(p => now - p.t <= spanMs);
    if (pts.length < 2) continue;
    ctx.strokeStyle = e.color; ctx.lineWidth = 1.5;
    ctx.beginPath();
    pts.forEach((p, i) => { const x = px(p.t), y = py(p.f); i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y); });
    ctx.stroke();
  }
  ctx.lineWidth = 1;

  // 基线重置标记：只标画在图上的线（按机组+通道对）
  const shown = new Set(entries.map(e => `${e.t}:${e.ch}`));
  const multi = entries.some(e => e.t !== entries[0].t);
  const inSpan = marks.filter(mk => shown.has(`${mk.tb}:${mk.ch}`) && now - mk.t <= spanMs && now - mk.t >= 0);
  if (inSpan.length) {
    ctx.save();
    ctx.strokeStyle = "rgba(240, 180, 41, .8)"; ctx.setLineDash([4, 3]);
    ctx.fillStyle = "rgba(240, 180, 41, .9)"; ctx.font = "10px sans-serif"; ctx.textAlign = "left";
    let lastX = -Infinity;
    for (const mk of inSpan.sort((a, b) => a.t - b.t)) {
      const x = px(mk.t);
      ctx.beginPath(); ctx.moveTo(x, rc.y); ctx.lineTo(x, rc.y + rc.h); ctx.stroke();
      // 挨得太近的只写一个标签
      if (x - lastX > 60) { ctx.fillText(`${multi ? tid(mk.tb) + " " : ""}CH${mk.ch} 重新置零`, x + 3, rc.y + rc.h - 4); lastX = x; }
    }
    ctx.restore();
  }

  // 图例：线多了（全场）就排紧一点
  const step = entries.length > 8 ? Math.max(11, Math.floor((rc.h - 4) / entries.length)) : 19;
  let ly = rc.y + 8;
  ctx.textAlign = "left"; ctx.font = step < 15 ? "10px sans-serif" : "12px sans-serif";
  for (const e of entries) {
    const last = e.pts.length ? e.pts[e.pts.length - 1].f : null;
    ctx.fillStyle = e.color;
    ctx.fillRect(rc.x + rc.w + 16, ly - 8, 14, 3);
    ctx.fillStyle = "#9db8d6";
    // 锚定口径没额定值时画的是较锚定变化量：带符号、标 Δ，别让人当成绝对预紧力
    ctx.fillText(`${e.label}  ${last == null ? "--" : e.isDelta ? "Δ" + fmtSigned(last, 2) : last.toFixed(2)} kN`, rc.x + rc.w + 36, ly - 2);
    ly += step;
    if (ly > rc.y + rc.h) break;
  }
}

const tcv = $("trend"), tctx = tcv.getContext("2d");
function drawTrend() {
  const span = RANGE_MS[trendRange] || TREND_MS;
  // 没接入的法兰没有历史，画空图说明；接入的只画本法兰的通道
  if (!isBoundFlange()) { drawForceChart(tcv, tctx, {}, span, "本法兰尚未接入监测"); return; }
  const chs = viewChannels();
  const series = trendRange === "5m" ? seriesFromLive(span, chs) : seriesFromArchive(histPoints, chs, viewTurbine);
  drawForceChart(tcv, tctx, series, span, flangeCal(viewFlange) ? undefined : UNCAL_TEXT, rezeroMarks);
}

const hcv = $("hist"), hctx = hcv.getContext("2d");
function drawHist() {
  if (!hcv) return;
  const calibratedAny = deviceTurbines().some(t => withTurbine(t, anyCalibrated));
  drawForceChart(hcv, hctx, seriesFromArchive(histPoints), RANGE_MS[reportRange] || RANGE_MS["24h"],
    calibratedAny ? undefined : UNCAL_TEXT + "，这段时间没有力值曲线", rezeroMarks);
}

// 基线重置事件（现场重新置零）：和历史曲线一起拉，画在趋势图上。法兰页只拉当前机组的
let rezeroMarks = [];
async function loadTrend(range) {
  try {
    const q = `range=${range}&turbine=${viewTurbine}`;
    const [r, ev] = await Promise.all([
      fetch(`/api/history?${q}`, { cache: "no-store" }),
      fetch(`/api/events?${q}`, { cache: "no-store" }).catch(() => null),
    ]);
    if (!r.ok) return;
    const j = await r.json();
    histPoints = j.points || [];
    if (ev && ev.ok) {
      const je = await ev.json();
      rezeroMarks = (je.events || []).filter(e => e.to === "rezero").map(e => ({ t: e.t, tb: e.tb ?? defaultTurbine(), ch: e.ch }));
    }
    if (!$("view-flange").hidden) drawTrend();
  } catch { /* 服务刚起来或断了，下轮再拉 */ }
}

async function seedLiveHist() {
  try {
    const r = await fetch("/api/history?range=5m", { cache: "no-store" });
    if (!r.ok) return;
    const j = await r.json();
    for (const p of j.points || []) {
      const v = trendValue(p);
      if (v == null) continue;
      // 按机组回填；只给服务端当前在监测的通道回填近 5 分钟曲线，刚被停掉监测的通道不能凭历史"复活"成测点
      const d = devices[p.tb ?? defaultTurbine()];
      const c = d && d.channels[p.ch];
      if (!c) continue;
      c.hist.push({ t: p.t, f: v });
    }
  } catch { /* 没有历史就从现在开始攒 */ }
}

window.onresize = () => renderView(location.hash.startsWith("#/flange") ? "flange"
  : location.hash.startsWith("#/turbine") ? "turbine"
  : location.hash.startsWith("#/alarms") ? "alarms"
  : location.hash.startsWith("#/report") ? "report" : "farm");
// 帧停了就不会再有 onFrame 触发重画，离线判定要靠这个定时器把当前视图刷一遍
function refreshHealthViews() {
  updateAlarmBadge();
  if (!$("view-farm").hidden) updateFarm();
  else if (!$("view-turbine").hidden) { updateFlangeList(); updateTowerSvg(); updateSlotGrid(); }
  else if (!$("view-flange").hidden) { updateBoltCards(); drawFlange(); drawDist(); }
  else if (!$("view-report").hidden) updateReport();
  else if (!$("view-alarms").hidden) renderAlarms();
}

setInterval(() => {
  refreshHealthViews();
  if (!$("view-flange").hidden) {
    if (trendRange === "5m") drawTrend();
    else loadTrend(trendRange);
  }
  if (!$("view-report").hidden) loadReport(reportRange);
  if (!$("view-alarms").hidden) loadEvents(alarmRange);
  refreshArchive();
}, 15000);

// ══ 视图4：报警中心（事件流来自服务端历史库，页面不再自己记一份） ═══════════

// 要人处理的探头位：松动、检测异常、探头无信号、基线失配，以及冒出来的未配置探头。
// 右上角角标和报警中心顶部的卡片都从这一个口径出，数才对得上
const attentionList = () => slotList().filter(ch => ALARM_HEALTH.has(boltHealth(ch)) || boltHealth(ch) === "stray");

// 全场：每台机组各算一遍再合起来。结果按机组分组，报警中心的卡片和角标都用它
function attentionByTurbine() {
  return deviceTurbines().map(t => ({ t, list: withTurbine(t, () => attentionList().map(ch => ({ ch, h: boltHealth(ch) }))) }));
}
function updateAlarmBadge() {
  const n = attentionByTurbine().reduce((s, x) => s + x.list.length, 0);
  const b = $("alarmBadge");
  b.hidden = n === 0;
  b.textContent = n;
  refreshNotice();
}

function fmtTime(t) {
  const d = new Date(t);
  const p = x => String(x).padStart(2, "0");
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

async function loadEvents(range) {
  try {
    const r = await fetch(`/api/events?range=${range}`, { cache: "no-store" });
    if (r.ok) {
      const j = await r.json();
      archiveEvents = j.events || [];
    }
  } catch { /* 下轮再拉 */ }
  renderAlarms();
}

function renderAlarms() {
  // 全场：各机组的需关注螺栓合起来数
  const attention = attentionByTurbine().flatMap(x => x.list.map(y => y.h));
  const looseNow = attention.filter(h => h === "loose").length;
  const warnNow = deviceTurbines().reduce((s, t) => s + withTurbine(t, () => chList().filter(ch => boltHealth(ch) === "warn").length), 0);
  // 检测异常这张卡把探头无信号、基线失配、未配置却有信号的都算进去：它们都是"测不了/不该信"的螺栓，
  // 和角标的数保持一致（角标 = 松动 + 这一项）
  const abnormalNow = attention.length - looseNow;
  const stat = (cls, v, k) =>
    `<div class="stat ${cls}"><div><span class="v">${v}</span><span class="u">颗</span></div><div class="k">${k}</div></div>`;
  $("alarmStats").innerHTML =
    stat(looseNow ? "bad" : "", looseNow, "当前松动报警")
    + stat(warnNow ? "" : "", warnNow, "当前预紧力偏低")
    + stat(abnormalNow ? "bad" : "", abnormalNow, "当前检测异常")
    + stat("", archiveEvents.length, "区间事件");

  const list = $("evList");
  if (archiveEvents.length === 0) {
    list.innerHTML = `<div class="evempty">这段时间没有状态变化 —— 或历史库还在积累</div>`;
    return;
  }

  // 位置按事件所属机组、通道所属法兰写（法兰归属要在那台机组的接线表里查）；不在任何法兰接线表里的标"未分配"
  const where = (tb, ch) => withTurbine(tb, () => { const i = chFlange(Number(ch)); return i >= 0 ? flangeName(i) : "未分配"; });
  // 基线重置事件（现场重新置零）没有"当时力值"，右侧写参考声时怎么变的
  list.innerHTML = archiveEvents.map(e => `
    <div class="ev${e.to === "rezero" ? " rezero" : ""}">
      <span class="et">${fmtTime(e.t)}</span>
      <span class="ew">${tid(e.tb ?? defaultTurbine())} · ${where(e.tb ?? defaultTurbine(), e.ch)} · CH${e.ch}</span>
      <span class="emsg"><span class="tag ${e.from}">${HEALTH_TEXT[e.from] || e.from}</span>
        <span class="arrow">→</span><span class="tag ${e.to}">${HEALTH_TEXT[e.to] || e.to}</span></span>
      <span class="ekn" title="${e.note || ""}">${e.note ? e.note : e.kn == null ? "--" : "当时 " + e.kn.toFixed(2) + " kN"}</span>
    </div>`).join("");
}

// ══ 视图5：统计报告 ═══════════════════════════════════════════════════════
async function loadReport(range) {
  try {
    const [rep, hist] = await Promise.all([
      fetch(`/api/report?range=${range}`, { cache: "no-store" }).then(r => r.ok ? r.json() : null),
      fetch(`/api/history?range=${range}`, { cache: "no-store" }).then(r => r.ok ? r.json() : null),
    ]);
    if (rep) reportStats = rep;
    if (hist) histPoints = hist.points || [];
  } catch { /* 下轮再拉 */ }
  // 报告页的基线重置标记要全场的
  try {
    const ev = await fetch(`/api/events?range=${range}`, { cache: "no-store" });
    if (ev.ok) rezeroMarks = ((await ev.json()).events || []).filter(e => e.to === "rezero").map(e => ({ t: e.t, tb: e.tb ?? defaultTurbine(), ch: e.ch }));
  } catch { /* 下轮再拉 */ }
  updateReport();
  drawHist();
}

function updateReport() {
  // 全场：每台机组的每个测点一行。每行的状态/力值/额定都要在那台机组的上下文里算
  const rows = deviceTurbines().flatMap(t => withTurbine(t, () => chList().map(ch => {
    const h = boltHealth(ch);
    const m = channels[ch] && channels[ch].last;
    const mk = mainKn(ch);
    const fi = chFlange(ch);
    return { t, ch, h, m, mk, d: liveDelta(ch), rated: ratedOf(ch), where: fi >= 0 ? flangeName(fi) : "未分配" };
  })));
  const calibratedAny = deviceTurbines().some(t => withTurbine(t, anyCalibrated));
  const anyRated = deviceTurbines().some(t => withTurbine(t, () => deviceFlanges().some(i => flangeRated(i) > 0)));
  // 每一种 boltHealth 可能返回的状态都要有桶，否则那颗螺栓就从统计里消失了，几项加起来对不上总数
  const counts = { ok: 0, warn: 0, loose: 0, fault: 0, noprobe: 0, shift: 0, badbase: 0, uncal: 0, offline: 0, other: 0 };
  rows.forEach(r => { counts[r.h in counts ? r.h : "other"]++; });
  const total = rows.length;
  // 没有标定就没有"健康率"这回事——不能把测得到的都算成正常
  const pct = total && calibratedAny ? Math.round(counts.ok / total * 100) : null;
  // 探头无信号、基线可疑也归到"检测异常"里算一颗：都是"测不了/不该信"的螺栓
  const abnormal = counts.fault + counts.noprobe + counts.badbase + counts.other;
  const degraded = counts.warn || abnormal || counts.shift;

  const ring = $("healthRing");
  const color = pct == null ? "#16304f" : counts.loose ? "var(--bad)" : degraded ? "var(--warn)" : "var(--ok)";
  ring.style.background = pct == null
    ? "#16304f"
    : `conic-gradient(${color} 0 ${pct}%, #16304f ${pct}% 100%)`;
  ring.className = "ring" + (counts.loose ? " bad" : degraded ? " warn" : "");
  $("healthPct").textContent = pct == null ? "--" : pct;

  const evN = reportStats && reportStats.eventCount != null ? reportStats.eventCount : "--";
  $("healthCounts").innerHTML = !calibratedAny && total ? `
    <div class="rc"><span class="l">监测螺栓</span><span class="n" style="color:#cfe3f5">${total}</span></div>
    <div class="rc"><span class="l">未标定</span><span class="n" style="color:var(--warn)">${counts.uncal}</span></div>
    <div class="rc"><span class="l">检测异常</span><span class="n" style="color:#d8a15c">${abnormal}</span></div>
    <div class="rc"><span class="l">基线失配</span><span class="n" style="color:#e78cff">${counts.shift}</span></div>
    <div class="rc"><span class="l">离线</span><span class="n" style="color:#5b7089">${counts.offline}</span></div>
    <div class="rc"><span class="l">区间事件</span><span class="n" style="color:#7d92aa">${evN}</span></div>
    <div class="rc" style="grid-column:1/-1;justify-content:center"><span class="l" style="color:var(--warn)">${UNCAL_TEXT}，把 .ucf 放到程序目录后重启</span></div>` : `
    <div class="rc"><span class="l">监测螺栓</span><span class="n" style="color:#cfe3f5">${total || "--"}</span></div>
    <div class="rc"><span class="l">正常</span><span class="n" style="color:var(--ok)">${counts.ok}</span></div>
    <div class="rc"><span class="l">预紧力偏低</span><span class="n" style="color:var(--warn)">${counts.warn}</span></div>
    <div class="rc"><span class="l">松动报警</span><span class="n" style="color:var(--bad)">${counts.loose}</span></div>
    <div class="rc"><span class="l">检测异常</span><span class="n" style="color:#d8a15c">${abnormal}</span></div>
    <div class="rc"><span class="l">基线失配</span><span class="n" style="color:#e78cff">${counts.shift}</span></div>
    <div class="rc"><span class="l">离线</span><span class="n" style="color:#5b7089">${counts.offline}</span></div>
    <div class="rc"><span class="l">区间事件</span><span class="n" style="color:#7d92aa">${evN}</span></div>`;

  // 区间统计按 (机组, 通道) 对上
  const byKey = {};
  if (reportStats && reportStats.channels) {
    for (const row of reportStats.channels) byKey[`${row.tb ?? defaultTurbine()}:${row.ch}`] = row;
  }
  $("reportTime").textContent = "更新于 " + fmtTime(Date.now());
  // 表头写明口径：锚定口径下的"当前"是按锚定时=额定估算的（各法兰额定可能不同，有一个配了就按估算写）
  const thKn = $("thKn");
  const thTxt = anchored() ? (anyRated ? "当前 kN（估算）" : "当前 Δ kN（较锚定）") : "当前 kN";
  if (thKn && thKn.textContent !== thTxt) thKn.textContent = thTxt;
  $("reportRows").innerHTML = rows.length === 0
    ? `<tr><td colspan="10" class="revempty">等待数据……</td></tr>`
    : rows.map(r => {
        const kn = r.mk && !r.mk.delta ? r.mk.v : null;
        // 这一路所属法兰没配额定值：统计列也只能给变化量（带符号）
        const delta = anchored() && r.rated === 0;
        const knTxt = v => v == null ? "--" : delta ? fmtSigned(v, 2) : Number(v).toFixed(2);
        const pctTxt = r.rated > 0 && kn != null ? (kn / r.rated * 100).toFixed(0) + "%" : "--";
        const tempTxt = r.m && r.m.temp != null ? Number(r.m.temp).toFixed(1) : "--";
        const s = byKey[`${r.t}:${r.ch}`] || {};
        const [mn, mx, av] = delta ? [s.dknMin, s.dknMax, s.dknAvg] : [s.knMin, s.knMax, s.knAvg];
        return `<tr><td>CH${r.ch}</td><td>${tid(r.t)} · ${r.where}</td>
          <td class="knv">${r.mk == null ? "--" : r.mk.delta ? fmtSigned(r.mk.v, 2) : r.mk.v.toFixed(2)}</td>
          <td>${r.d == null ? "--" : fmtSigned(r.d, 2)}</td>
          <td>${knTxt(mn)}</td><td>${knTxt(mx)}</td><td>${knTxt(av)}</td>
          <td>${tempTxt}</td><td>${pctTxt}</td>
          <td><span class="bst ${r.h}">${HEALTH_TEXT[r.h]}</span></td></tr>`;
      }).join("");
}

// ══ 状态与数据流 ════════════════════════════════════════════════════════
// 整站状态：农场级口径 + 每台设备一份。开 WebSocket 先收这一份，/api/state 也是它
function applyState(s) {
  // 老服务端（单设备）的 state 没有 devices：包一层当成一台
  const devs = Array.isArray(s.devices) ? s.devices : [{ ...s, turbine: s.turbine ?? 1 }];
  if (Number(s.turbineCount) > 0) FARM.turbineCount = Number(s.turbineCount);
  if (s.farmName) FARM.farmName = s.farmName;
  // 口径与阈值以服务端为准（全场统一）；老服务端没这些字段时保持默认
  const first = devs[0] || {};
  plan = {
    baseline: (s.baseline || first.baseline) === "free" ? "free" : "anchor",
    baselineText: s.baselineText || first.baselineText || ((s.baseline || first.baseline) === "free" ? "自由状态置零" : "在役锚定"),
    rated: Number(first.rated) || 0,
    warnRatio: Number(s.warnRatio || first.warnRatio) || 0.85,
    alarmRatio: Number(s.alarmRatio || first.alarmRatio) || 0.7,
  };
  // 新出现/消失的设备：总览卡片要重建
  const before = deviceTurbines().join(",");
  for (const d of devs) applyDevice(d, true);
  for (const t of deviceTurbines()) if (!devs.some(d => Number(d.turbine) === t)) delete devices[t];
  if (before !== deviceTurbines().join(",")) farmBuilt = false;
  // 第一次拿到设备列表时，默认看第一台接了设备的机组
  if (!hasDevice(viewTurbine) && deviceTurbines().length && !location.hash.match(/#\/(turbine|flange)\/\d/)) viewTurbine = defaultTurbine();
  bindTurbine(viewTurbine);
  afterDeviceUpdate(viewTurbine, true);
}

// 一台设备的状态（服务端只在它变化时发它自己那份）。silent = 由 applyState 统一收尾，这里不刷视图
function applyDevice(s, silent = false) {
  const t = Number(s.turbine) || 1;
  const d = devices[t] || (devices[t] = emptyDevice(t));
  const isView = t === viewTurbine;
  const prevKeys = Object.keys(d.channels).join(",");
  const wasOn = d.connected;
  const prevSlots = isView ? slotList().join(",") : "";

  d.connected = !!s.connected;
  d.endpoint = s.endpoint || d.endpoint;
  d.log = s.log || "";
  d.maintenance = s.maintenance || null;
  d.cal = s.cal || { k: 0, b: 0 };
  if (s.cal && Number(s.cal.umPerNs) > 0) umPerNs = Number(s.cal.umPerNs);
  // 各法兰的接线与标定（服务端解析好的 farm.json）；老服务端没这个字段时留空，按 farm.json 模板兜底
  d.flangePlan = Array.isArray(s.flanges) ? s.flanges.map(f => ({ ...f, channels: uniqSorted(f.channels), planned: uniqSorted(f.planned) })) : [];
  // 在位识别结果：每一路有没有探头信号
  const sg = {};
  for (const g of s.signals || []) sg[g.ch] = g;
  d.sigs = sg;
  d.polled = s.polled || [];
  d.scanning = !!s.scanning;
  const old = d.channels;
  const chs = {};
  for (const c of s.channels || []) {
    chs[c.ch] = { params: c.params, refWave: c.refWave,
                  last: old[c.ch]?.last, at: old[c.ch]?.at,
                  dts: old[c.ch]?.dts || [], hist: old[c.ch]?.hist || [] };
  }
  d.channels = chs;
  d.changed = wasOn !== d.connected || prevKeys !== Object.keys(chs).join(",");
  d.slotsChanged = isView && prevSlots !== withTurbine(t, () => slotList().join(","));
  if (isView) bindTurbine(t);
  if (!silent) afterDeviceUpdate(t, false);
}

// 设备状态更新后的收尾：看的这台变了就重排视图，别的机组只刷总览/角标
function afterDeviceUpdate(t, full) {
  const d = devices[t];
  if (t === viewTurbine) {
    cal = d && d.cal ? { k: Number(d.cal.k) || 0, b: Number(d.cal.b) || 0 } : { k: 0, b: 0 };
    if (!channels[selected]) {
      // 优先选当前法兰上的通道，没有再退到全部里的第一个
      const keys = viewChannels().length ? viewChannels() : chList();
      selected = keys.length ? keys[0] : 0;
    }
    refreshHeader();
    if (full || (d && d.changed)) {
      seedLiveHist().then(() => { if (!$("view-flange").hidden) drawTrend(); });
      refreshArchive();
      route();
    } else if (d && d.slotsChanged) {
      // 冒出/消失了未配置通道的探头信号：卡片和法兰图要增减位子
      refreshHealthViews();
    } else if (!$("view-farm").hidden) {
      updateFarm();
    }
  } else if (!$("view-farm").hidden) {
    updateFarm();
  }
  if (d) { d.changed = false; d.slotsChanged = false; }
  updateAlarmBadge();
}

// 顶栏/底栏：当前看的这台机组的连接状态、标定、维护
function refreshHeader() {
  const d = devices[viewTurbine];
  const n = chList().length;
  const healthy = connected && n > 0;
  const noDev = !d;
  $("stateText").textContent = noDev ? `${tid(viewTurbine)} 未接入设备`
    : maintenance ? `${tid(viewTurbine)} 维护中（${maintenance.note}），预计 ${hhmm(maintenance.until)} 自动恢复监测`
    : healthy ? `${tid(viewTurbine)} 监测中，${n} 个测点`
    : connected ? `${tid(viewTurbine)} 已连接，等待设备上报（自动重试中）`
    : `${tid(viewTurbine)} 未连接（服务自动重连中）` + (d && d.log ? ` · ${d.log.replace(/^\d\d:\d\d:\d\d /, "")}` : "");
  $("stateText").className = maintenance ? "maint" : healthy ? "ok" : "";
  // 右上角小灯：全场有一台在测就算监测中
  const anyOn = deviceTurbines().some(t => withTurbine(t, () => connected && chList().length > 0));
  const anyMaint = deviceTurbines().some(t => devices[t].maintenance);
  $("navState").textContent = anyOn ? "监测中" : anyMaint ? "维护中" : "未连接";
  $("navDot").className = anyOn ? "dot on" : anyMaint ? "dot maint" : "dot";
  updateMaintBox();
  $("epShow").textContent = d ? (d.endpoint || "--") : "--";
  // 底栏：逐法兰写标定和额定。没放 .ucf 的法兰服务端不给斜率（k=0）：页面不算它的力值，只看声时差
  const calParts = deviceFlanges().map(i => {
    const fc = flangeCal(i), r = flangeRated(i);
    return `${flangeName(i)}：${fc ? `${fc.name} ${Number(fc.k).toFixed(4)} kN/ns` : "未标定"}`
      + (anchored() ? (r > 0 ? `，额定 ${r} kN` : "，未配置额定值") : "");
  });
  const c = d && d.cal;
  $("calText").textContent = noDev ? ""
    : calParts.length ? `${plan.baselineText} · ` + calParts.join("；")
    : `标定：${c && Number(c.k) > 0 ? `${c.name}（${c.points} 点） ${Number(c.k).toFixed(4)} ${c.unit}/ns · ${plan.baselineText}` : UNCAL_TEXT}`;
  $("calText").title = $("calText").textContent;
  $("calText").style.color = noDev || anyCalibrated() ? "" : "var(--warn)";
}

function onFrame(f) {
  // 帧带机组号；老服务端没有就算默认那台。不是任何已知设备的帧丢掉
  const t = Number(f.t) || defaultTurbine();
  const d = devices[t];
  if (!d) return;
  const isView = t === viewTurbine;
  // 规划位/巡检位的帧只用于在位识别（刷新信噪比），不建测点、不进趋势
  if (f.monitored === false) {
    if (d.sigs[f.ch]) { d.sigs[f.ch].snr = f.snr; d.sigs[f.ch].at = Date.now(); }
    return;
  }

  let c = d.channels[f.ch];
  if (!c) { c = d.channels[f.ch] = { params: null, refWave: null, last: null, dts: [], hist: [] }; if (isView) rebuildTable(); }
  c.last = f;
  c.at = Date.now();
  // 基线失配的帧不进趋势（会把曲线拉飞），但声时抖动统计照常记
  if (f.deltaTof != null) {
    const dt = Number(f.deltaTof);
    c.dts.push(dt);
    if (c.dts.length > 20) c.dts.shift();
    // 曲线用服务端算好的值（预紧力，或锚定口径没额定值时的变化量），页面不自己乘系数
    const v = f.mismatch ? null : trendValue(f);
    if (v != null) c.hist.push({ t: Date.now(), f: v });
    const cut = Date.now() - TREND_MS - 10000;
    while (c.hist.length && c.hist[0].t < cut) c.hist.shift();
  }

  // 帧太密（每台设备每秒十来帧、很多台）不能每帧都重算全场角标/总览；攒着由下面的节流刷
  scheduleGlobalRefresh();

  // 别的机组的帧：不动当前视图（总览/报警/报告由节流刷新覆盖）
  if (!isView) return;
  if (!selected) selected = f.ch;

  // 只刷当前视图，别的视图切过去时整体重画
  if (!$("view-turbine").hidden) {
    updateFlangeList(); updateTowerSvg(); updateSlotGrid();
  } else if (!$("view-flange").hidden) {
    updateRow(f.ch);
    updateBoltCard(f.ch);
    drawFlange();
    if (f.ch === selected && opsMode) { refreshDetail(); drawWave(); }
    drawDist();
    drawTrend();
  }
}

// 全场级的东西（角标、总览、报警中心、报告表）最多每秒刷一次
let globalRefreshTimer = 0;
function scheduleGlobalRefresh() {
  if (globalRefreshTimer) return;
  globalRefreshTimer = setTimeout(() => {
    globalRefreshTimer = 0;
    updateAlarmBadge();
    if (!$("view-farm").hidden) updateFarm();
    else if (!$("view-alarms").hidden) renderAlarms();
    else if (!$("view-report").hidden) updateReport();
  }, 1000);
}

let ws;
function openWs() {
  ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`);
  ws.onmessage = e => {
    const msg = JSON.parse(e.data);
    if (msg.type === "state") applyState(msg.state);
    else if (msg.type === "device") applyDevice(msg.state);
    else if (msg.type === "frame") onFrame(msg);
    else if (msg.type === "log") { if (devices[msg.t]) devices[msg.t].log = msg.text; if (!msg.t || msg.t === viewTurbine) $("log").textContent = msg.text; }
  };
  ws.onclose = () => setTimeout(openWs, 2000);
}

// ══ 启动：读风场配置（没有就用默认），再开数据流 ═══════════════════════════
// 连接由服务端自动管理（启动即连、断线重连），页面纯展示。
(async () => {
  try {
    const r = await fetch("farm.json", { cache: "no-store" });
    if (r.ok) FARM = { ...FARM, ...(await r.json()) };
  } catch { /* 没有配置文件就用默认 */ }
  // 页面第一台默认看的机组：farm.json 里第一台设备所在的机组（state 到了以后以服务端为准）
  viewTurbine = defaultTurbine();
  viewFlange = 0;
  bindTurbine(viewTurbine);
  $("farmName").textContent = FARM.farmName + "螺栓预紧力在线监测系统";
  bindChips("trendRange", r => { trendRange = r; if (r === "5m") drawTrend(); else loadTrend(r); });
  bindChips("alarmRange", r => { alarmRange = r; loadEvents(r); });
  bindChips("reportRange", r => {
    reportRange = r;
    $("exportCsv").href = `/api/export.csv?range=${r}`;
    loadReport(r);
  });
  route();
  openWs();
  refreshArchive();
  pullState();
  setInterval(pullState, 3000);
})();

async function pullState() {
  try {
    const r = await fetch("/api/state", { cache: "no-store" });
    if (r.ok) applyState(await r.json());
  } catch { /* 服务还没起来 */ }
}

function bindChips(id, onPick) {
  const box = $(id);
  if (!box) return;
  box.querySelectorAll("a").forEach(a => {
    a.addEventListener("click", ev => {
      ev.preventDefault();
      box.querySelectorAll("a").forEach(x => x.classList.toggle("on", x === a));
      onPick(a.dataset.r);
    });
  });
}

async function refreshArchive() {
  try {
    const r = await fetch("/api/archive", { cache: "no-store" });
    if (!r.ok) return;
    const s = await r.json();
    const oldest = s.oldest ? fmtTime(s.oldest) : "--";
    // 丢帧 / 写入失败不能静默：只要不是 0 就亮出来，历史不完整得让人知道
    const loss = (s.dropped ? ` · 丢帧 ${s.dropped}` : "") + (s.writeErrors ? ` · 写入失败 ${s.writeErrors}` : "");
    $("archText").textContent = `历史 ${s.samples} 点 · 事件 ${s.events} · 最早 ${oldest} · ${s.dbMb} MB${loss}`;
    $("archText").style.color = loss ? "var(--bad)" : "";
  } catch { /* 服务未起 */ }
}
