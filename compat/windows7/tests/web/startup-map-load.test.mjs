import test from 'node:test';
import assert from 'node:assert/strict';
import { StartupMapLoad } from '../../src/HikingGuide.App/Map/Web/startup-map-load.js';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function setup(options = {}) {
  const events = new Map(), errors = [];
  const map = {
    loaded: false, source: {},
    on(name, fn) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(fn); },
    off(name, fn) { events.get(name)?.delete(fn); },
    fire(name, value) { for (const fn of events.get(name) || []) fn(value); },
    getSource() { return this.source; }, isSourceLoaded() { return this.loaded; }
  };
  const startup = new StartupMapLoad(map, error => errors.push(error), { maxWaitMs: 100, paintDelayMs: 5, ...options });
  return { map, startup, errors };
}
function ready(map) { map.loaded = true; map.fire('sourcedata', { sourceId: 'raster-base-source', tile: { state: 'loaded' } }); }

test('首次底图完成绘制前不启动叠加层，加载事件本身不算已绘制', async () => {
  const { map, startup } = setup();
  try {
    const applied = [];
    startup.start(true);
    for (const key of ['overlays', 'derived', 'roads']) startup.run(key, () => applied.push(key));
    map.fire('render'); assert.deepEqual(applied, []);
    ready(map); await sleep(10); assert.deepEqual(applied, []);
    map.fire('render'); assert.deepEqual(applied, []); await sleep(15);
    assert.deepEqual(applied, ['overlays', 'derived', 'roads']);
    assert.equal(startup.inspect().reason, 'base-rendered');
    assert.ok(startup.inspect().releaseMs >= startup.inspect().baseFrameMs);
  } finally { startup.dispose(); }
});

test('仅首次生效，后续拖动缩放及图源切换不重新等待或隐藏图层', async () => {
  const { map, startup } = setup();
  try {
    startup.start(true); ready(map); map.fire('render'); await sleep(15);
    map.loaded = false;
    for (const event of ['movestart', 'move', 'zoomstart', 'moveend', 'zoomend']) map.fire(event);
    startup.start(true);
    let applied = 0; startup.run('derived', () => applied++); startup.run('roads', () => applied++);
    assert.equal(applied, 2); assert.equal(startup.waiting, false);
  } finally { startup.dispose(); }
});

test('启动时只执行最新设置，用户关闭图层不会被旧任务重新开启', async () => {
  const { map, startup } = setup();
  try {
    startup.start(true); const applied = [];
    startup.run('derived', () => applied.push(true)); startup.run('derived', () => applied.push(false));
    ready(map); map.fire('render'); await sleep(15);
    assert.deepEqual(applied, [false]);
  } finally { startup.dispose(); }
});

test('底图不可用超时放行，反复设置不会延长超时', async () => {
  const { startup } = setup({ maxWaitMs: 35 });
  try {
    let applied = 0; startup.start(true); startup.run('roads', () => applied++);
    await sleep(20); startup.start(true); await sleep(25);
    assert.equal(applied, 1); assert.equal(startup.inspect().reason, 'timeout');
  } finally { startup.dispose(); }
});

test('无底图或底图不可见时不等待', () => {
  const { startup } = setup();
  let applied = 0; startup.run('roads', () => applied++); startup.start(false);
  assert.equal(applied, 1); assert.equal(startup.inspect().reason, 'no-base'); startup.dispose();
});

test('首帧提交前切换到未就绪底图不会提前放行', async () => {
  const { map, startup } = setup();
  try {
    startup.start(true); ready(map); map.fire('render'); map.loaded = false;
    await sleep(15); assert.equal(startup.waiting, true);
    ready(map); map.fire('render'); await sleep(15); assert.equal(startup.waiting, false);
  } finally { startup.dispose(); }
});

test('页面销毁取消延迟任务，不回写旧页面', async () => {
  const { map, startup } = setup({ maxWaitMs: 20 });
  let applied = 0; startup.start(true); startup.run('roads', () => applied++);
  ready(map); map.fire('render'); map.fire('remove'); await sleep(30);
  assert.equal(applied, 0); assert.deepEqual(startup.inspect().queued, []);
});

test('只有metadata就绪或失败瓦片时不能把空白误报为底图首帧', async () => {
  const { map, startup } = setup();
  try {
    startup.start(true); map.loaded = true;
    map.fire('sourcedata', { sourceId: 'raster-base-source', sourceDataType: 'content' });
    map.fire('sourcedata', { sourceId: 'raster-base-source', tile: { state: 'errored' } });
    map.fire('render'); await sleep(15); assert.equal(startup.waiting, true);
    ready(map); map.fire('render'); await sleep(15); assert.equal(startup.inspect().reason, 'base-rendered');
  } finally { startup.dispose(); }
});

test('一个派生图层配置失败不阻塞其他图层', () => {
  const { startup, errors } = setup(); let applied = false;
  startup.run('derived', () => { throw new Error('模拟派生图层错误'); });
  startup.run('roads', () => { applied = true; }); startup.start(false);
  assert.equal(errors.length, 1); assert.equal(applied, true); startup.dispose();
});
