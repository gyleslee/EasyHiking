// 独立于轨迹编辑的相机导航。Ctrl+左击仍留给轨迹多选。
export const orbitOptions = Object.freeze({ threshold: 4, rotateSpeed: .35, pitchSpeed: .25, maxPitch: 85 });
export function orbitCamera(start, dx, dy) {
  return { bearing: ((start.bearing + dx * orbitOptions.rotateSpeed + 180) % 360 + 360) % 360 - 180,
    pitch: Math.max(0, Math.min(orbitOptions.maxPitch, start.pitch + dy * orbitOptions.pitchSpeed)) };
}
export function orbitButton(event) {
  return event.button === 2 || (event.button === 0 && event.shiftKey && !event.ctrlKey && !event.altKey);
}

export class TerrainNavigation {
  constructor(map, environment = { document, window }) {
    this.map = map; this.document = environment.document; this.window = environment.window;
    this.canvas = map.getCanvas(); this.enabled = false; this.gesture = null;
    this.suppressContextUntil = 0; this.suppressClickUntil = 0;
    this.now = () => performance.now();
    this.bind(this.canvas, "mousedown", e => this.down(e), true);
    this.bind(this.window, "mousemove", e => this.move(e), true);
    this.bind(this.window, "mouseup", e => this.up(e), true);
    this.bind(this.window, "blur", () => this.cancel());
    this.bind(this.canvas, "contextmenu", e => {
      if (this.now() < this.suppressContextUntil) this.consume(e);
    }, true);
    this.bind(this.canvas, "click", e => {
      if (this.now() < this.suppressClickUntil) this.consume(e);
    }, true);
    this.bind(this.canvas, "keydown", e => {
      if (!this.enabled || e.ctrlKey || e.altKey || e.metaKey) return;
      if (e.key.toLowerCase() === "n") { this.consume(e); this.north(); }
      else if (e.key.toLowerCase() === "u") { this.consume(e); this.map.easeTo({ pitch: 0, duration: 250 }); }
      else if (e.key === "Escape") this.cancel();
    }, true);
    this.rotateListener = () => this.refresh();
    map.on("rotate", this.rotateListener); map.on("pitch", this.rotateListener);
    map.on("remove", () => this.dispose());
    this.listeners ||= [];
  }
  bind(target, name, fn, capture = false) {
    target.addEventListener(name, fn, capture);
    (this.listeners ||= []).push(() => target.removeEventListener(name, fn, capture));
  }
  consume(e) { e.preventDefault(); e.stopImmediatePropagation(); }
  setEnabled(enabled) {
    if (this.enabled === enabled) return;
    this.cancel(); this.enabled = enabled;
    if (enabled) {
      this.previous = { rotate: this.map.dragRotate.isEnabled(), box: this.map.boxZoom.isEnabled() };
      // 禁止原生Ctrl+左拖和Shift框选抢占同一个3D手势。
      this.map.dragRotate.disable(); this.map.boxZoom.disable();
    } else {
      if (this.previous?.rotate) this.map.dragRotate.enable();
      if (this.previous?.box) this.map.boxZoom.enable();
    }
    this.refresh();
  }
  down(e) {
    if (!this.enabled || !orbitButton(e)) return;
    this.suppressContextUntil = 0;
    this.map.stop(); this.canvas.focus({ preventScroll: true });
    this.gesture = { x: e.clientX, y: e.clientY, bearing: this.map.getBearing(), pitch: this.map.getPitch(),
      button: e.button, active: false };
    // 拦在MapLibre/标注mousedown之前，旋转不得拖动标注或生成选点。
    // 右键按下需让地图接收到，以保留其“按下到松开”的菜单时序。
    // 原生旋转已停用，不会与本手势竞争；Shift左键则必须拦截选点／框选。
    if (e.button === 0) this.consume(e);
  }
  move(e) {
    const g = this.gesture; if (!g) return;
    const mask = g.button === 2 ? 2 : 1;
    if (!(e.buttons & mask)) { this.cancel(); return; }
    const dx = e.clientX - g.x, dy = e.clientY - g.y;
    if (!g.active && Math.hypot(dx, dy) < orbitOptions.threshold) return;
    g.active = true; this.canvas.style.cursor = "grabbing";
    this.map.jumpTo(orbitCamera(g, dx, dy)); this.consume(e);
  }
  up(e) {
    const g = this.gesture; if (!g || e.button !== g.button) return;
    if (g.active) {
      this.suppressContextUntil = this.now() + 600;
      this.suppressClickUntil = this.now() + 200;
    } else if (g.button === 0) this.suppressClickUntil = this.now() + 200;
    this.cancel();
    if (g.button === 0) this.consume(e);
    // 未拖动的右键仍让随后contextmenu事件进入既有菜单。
  }
  cancel() { this.gesture = null; this.canvas.style.cursor = ""; }
  suppressContext() { return this.gesture?.active === true || this.now() < this.suppressContextUntil; }
  north() {
    this.cancel(); this.map.stop();
    // 只改方位；与设置窗口原有“正北朝上”（同时俯视）分开。
    this.map.easeTo({ bearing: 0, duration: 250 });
  }
  onAdd() {
    const root = this.document.createElement("div");
    root.className = "maplibregl-ctrl terrain-navigation";
    const button = (text, title, action, className = "") => {
      const b = this.document.createElement("button"); b.type = "button"; b.textContent = text;
      b.title = title; b.setAttribute("aria-label", title); b.className = className;
      b.addEventListener("click", action); return b;
    };
    this.northButton = button("▲ 向北", "一键向北：保留中心、缩放和俯仰；地图获得焦点后也可按 N", () => this.north(), "terrain-north");
    this.readout = this.document.createElement("div"); this.readout.className = "terrain-camera-readout";
    this.orbitControls = this.document.createElement("div"); this.orbitControls.className = "terrain-orbit-controls";
    const step = (bearing, pitch) => { this.map.stop(); this.map.easeTo({ bearing: this.map.getBearing() + bearing,
      pitch: Math.max(0, Math.min(85, this.map.getPitch() + pitch)), duration: 150 }); };
    this.orbitControls.append(button("↶ 左转", "向左旋转15度", () => step(-15, 0)), button("右转 ↷", "向右旋转15度", () => step(15, 0)),
      button("倾斜 +", "增加俯仰10度，更接近侧面观察", () => step(0, 10)), button("俯视 −", "减小俯仰10度，更接近垂直俯视", () => step(0, -10)));
    this.hint = this.document.createElement("div"); this.hint.className = "terrain-orbit-hint";
    this.hint.textContent = "右键 / Shift＋左键\n左右旋转\n上下俯仰";
    this.hint.title = "左键拖动平移，滚轮缩放；右键轻点仍打开菜单。地图获得焦点后：N向北，U俯视，Shift＋方向键调整视角。";
    root.append(this.northButton, this.readout, this.orbitControls, this.hint);
    this.root = root; this.refresh(); return root;
  }
  refresh() {
    if (!this.root) return;
    const bearing = (this.map.getBearing() % 360 + 360) % 360;
    this.northButton.textContent = `${bearing < .05 || bearing > 359.95 ? "▲" : "↟"} 向北`;
    this.readout.textContent = `方位 ${bearing.toFixed(0) === "360" ? "0" : bearing.toFixed(0)}°\n俯仰 ${this.map.getPitch().toFixed(0)}°`;
    this.orbitControls.hidden = !this.enabled; this.hint.hidden = !this.enabled;
  }
  onRemove() { this.root?.remove(); }
  dispose() {
    this.cancel(); this.listeners.forEach(unbind => unbind()); this.listeners = [];
    this.map.off("rotate", this.rotateListener); this.map.off("pitch", this.rotateListener);
  }
}
