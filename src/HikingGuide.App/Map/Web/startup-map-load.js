// 只在页面首次加载时让底图先绘制。完成后永久直通，不参与移动/缩放的显隐控制。
export class StartupMapLoad {
  constructor(map, onError = () => {}, { maxWaitMs = 2500, paintDelayMs = 80 } = {}) {
    this.map = map;
    this.onError = onError;
    this.maxWaitMs = maxWaitMs;
    this.paintDelayMs = paintDelayMs;
    this.waiting = true;
    this.queue = new Map();
    this.startedAt = null;
    this.baseFrameMs = null;
    this.releaseMs = null;
    this.reason = null;
    this.onRender = () => this.rendered();
    this.onData = event => {
      // 新增source时metadata已就绪但还没有任何图片，isSourceLoaded也可能返回true。
      // 必须见到实际成功加载的底图瓦片，再等待绘制，不能把空source当作底图首帧。
      if (this.waiting && event.sourceId === "raster-base-source" && event.tile?.state === "loaded") {
        this.contentSource = map.getSource("raster-base-source");
      }
    };
    map.on("render", this.onRender);
    map.on("sourcedata", this.onData);
    map.on("remove", () => this.dispose());
  }
  start(hasBase) {
    if (!this.waiting) return;
    if (!hasBase) { this.finish("no-base"); return; }
    if (this.startedAt !== null) return;
    this.startedAt = performance.now();
    // 网络不可用也不能无限阻塞其余资源；上限不因图源切换或拖动而重新计时。
    this.deadline = setTimeout(() => this.finish("timeout"), this.maxWaitMs);
  }
  run(key, apply) {
    if (!this.waiting) { apply(); return; }
    this.queue.set(key, apply); // 启动期间的设置变更仅执行最新值，不恢复旧的勾选状态。
  }
  baseLoaded() {
    const source = this.map.getSource("raster-base-source");
    return !!source && this.contentSource === source && this.map.isSourceLoaded("raster-base-source");
  }
  rendered() {
    if (!this.waiting || this.startedAt === null) return;
    if (!this.baseLoaded()) { clearTimeout(this.paintTimer); this.paintTimer = null; return; }
    if (this.paintTimer) return;
    const renderedAt = performance.now() - this.startedAt;
    // render之后让浏览器提交画面，避免同一帧立即启动路网样式/派生图层的重活。
    this.paintTimer = setTimeout(() => {
      this.paintTimer = null;
      if (!this.baseLoaded()) return;
      this.baseFrameMs = renderedAt;
      this.finish("base-rendered");
    }, this.paintDelayMs);
  }
  finish(reason) {
    if (!this.waiting) return;
    this.waiting = false;
    this.reason = reason;
    this.releaseMs = this.startedAt === null ? 0 : performance.now() - this.startedAt;
    clearTimeout(this.deadline);
    clearTimeout(this.paintTimer);
    this.map.off("render", this.onRender);
    this.map.off("sourcedata", this.onData);
    const pending = [...this.queue.values()];
    this.queue.clear();
    for (const apply of pending) {
      try { apply(); } catch (error) { this.onError(error); }
    }
  }
  dispose() {
    this.waiting = false;
    clearTimeout(this.deadline);
    clearTimeout(this.paintTimer);
    this.map.off("render", this.onRender);
    this.map.off("sourcedata", this.onData);
    this.queue.clear();
  }
  inspect() {
    return { waiting: this.waiting, reason: this.reason, baseFrameMs: this.baseFrameMs,
      releaseMs: this.releaseMs, queued: [...this.queue.keys()] };
  }
}
