import test from 'node:test';
import assert from 'node:assert/strict';
import { TerrainNavigation, orbitCamera, orbitButton } from '../../src/HikingGuide.App/Map/Web/terrain-navigation.js';

class Target {
  constructor() { this.events = {}; this.style = {}; }
  addEventListener(name, fn) { (this.events[name] ||= []).push(fn); }
  removeEventListener(name, fn) { this.events[name] = this.events[name].filter(f => f !== fn); }
  fire(name, values = {}) {
    const e = { button: 2, buttons: 2, clientX: 100, clientY: 100, preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; }, ...values };
    this.events[name]?.forEach(fn => fn(e)); return e;
  }
  focus() {}
}
function setup() {
  const canvas = new Target(), window = new Target(), moves = [];
  const handler = () => ({ enabled: true, isEnabled() { return this.enabled; }, enable() { this.enabled = true; }, disable() { this.enabled = false; } });
  const map = { bearing: 0, pitch: 55, getCanvas: () => canvas, on() {}, off() {}, stop() {}, getBearing() { return this.bearing; }, getPitch() { return this.pitch; },
    jumpTo(o) { Object.assign(this, o); moves.push(o); }, easeTo(o) { Object.assign(this, o); moves.push(o); }, dragRotate: handler(), boxZoom: handler() };
  const nav = new TerrainNavigation(map, { window, document: {} }); nav.now = () => 1000;
  return { canvas, window, map, nav, moves };
}
test('连续多圈旋转没有正北锁定，负角度也规范化', () => {
  for (const dx of [-5000, -1080, -5, 0, 5, 1080, 5000]) {
    const c = orbitCamera({ bearing: 179, pitch: 55 }, dx, 0);
    assert.ok(c.bearing >= -180 && c.bearing < 180); assert.equal(c.pitch, 55);
  }
  assert.equal(orbitCamera({ bearing: 0, pitch: 55 }, 10, 0).bearing, 3.5);
});
test('俯仰保持0到85度，不改变缩放和中心', () => {
  assert.deepEqual(orbitCamera({ bearing: 0, pitch: 55 }, 0, -1000), { bearing: 0, pitch: 0 });
  assert.deepEqual(orbitCamera({ bearing: 0, pitch: 55 }, 0, 1000), { bearing: 0, pitch: 85 });
});
test('右拖或Shift左拖，Ctrl左击与普通左拖不抢占', () => {
  assert.equal(orbitButton({ button: 2 }), true);
  assert.equal(orbitButton({ button: 0, shiftKey: true }), true);
  assert.equal(!!orbitButton({ button: 0 }), false);
  assert.equal(orbitButton({ button: 0, ctrlKey: true, shiftKey: true }), false);
});
test('只在3D启用新手势，回2D还原原处理器状态', () => {
  const { nav, map, canvas } = setup();
  assert.equal(canvas.fire('mousedown').prevented, undefined);
  nav.setEnabled(true); assert.equal(map.dragRotate.enabled, false); assert.equal(map.boxZoom.enabled, false);
  nav.setEnabled(false); assert.equal(map.dragRotate.enabled, true); assert.equal(map.boxZoom.enabled, true);
  map.dragRotate.disable(); nav.setEnabled(true); nav.setEnabled(false); assert.equal(map.dragRotate.enabled, false);
});
test('轻微抖动不旋转，轻点右键仍可打开菜单', () => {
  const { nav, canvas, window, moves } = setup(); nav.setEnabled(true);
  canvas.fire('mousedown'); window.fire('mousemove', { clientX: 102 }); window.fire('mouseup');
  assert.equal(moves.length, 0); assert.equal(canvas.fire('contextmenu').prevented, undefined);
});
test('右拖改变方位，松开不弹菜单、不产生点击，下一次右击可用', () => {
  const { nav, canvas, window, moves } = setup(); nav.setEnabled(true);
  canvas.fire('mousedown'); window.fire('mousemove', { clientX: 200, clientY: 120 }); window.fire('mouseup');
  assert.equal(moves.length, 1); assert.deepEqual(moves[0], { bearing: 35, pitch: 60 });
  assert.equal(canvas.fire('contextmenu').prevented, true); assert.equal(canvas.fire('click').prevented, true);
  canvas.fire('mousedown'); window.fire('mouseup'); assert.equal(canvas.fire('contextmenu').prevented, undefined);
});
test('原生延迟到mouseup的contextmenu也被拦截，轻点仍透传', () => {
  const { nav, canvas, window } = setup(); nav.setEnabled(true);
  assert.equal(canvas.fire('mousedown').prevented, undefined);
  assert.equal(nav.suppressContext(), false);
  window.fire('mousemove', { clientX: 200 }); assert.equal(nav.suppressContext(), true);
  assert.equal(window.fire('mouseup').prevented, undefined); assert.equal(nav.suppressContext(), true);
  canvas.fire('mousedown'); window.fire('mouseup'); assert.equal(nav.suppressContext(), false);
});
test('Shift左拖不落入框选或标注编辑，松开不触发选点', () => {
  const { nav, canvas, window, map } = setup(); nav.setEnabled(true);
  assert.equal(canvas.fire('mousedown', { button: 0, buttons: 1, shiftKey: true }).stopped, true);
  window.fire('mousemove', { button: 0, buttons: 1, clientX: 200 }); window.fire('mouseup', { button: 0, buttons: 0 });
  assert.equal(map.bearing, 35); assert.equal(canvas.fire('click', { button: 0 }).stopped, true);
});
test('鼠标按键丢失或窗口失焦终止手势，回2D时也不残留', () => {
  const { nav, canvas, window } = setup(); nav.setEnabled(true);
  canvas.fire('mousedown'); window.fire('mousemove', { buttons: 0 }); assert.equal(nav.gesture, null);
  canvas.fire('mousedown'); window.fire('blur'); assert.equal(nav.gesture, null);
  canvas.fire('mousedown'); nav.setEnabled(false); assert.equal(nav.gesture, null);
});
test('向北只改方位，保持当前俯仰与中心缩放', () => {
  const { nav, map, moves } = setup(); map.bearing = 123; nav.north();
  assert.equal(map.bearing, 0); assert.equal(map.pitch, 55);
  assert.deepEqual(moves[0], { bearing: 0, duration: 250 });
});
test('N向北与U俯视独立，Ctrl快捷键不被抢占', () => {
  const { nav, canvas, map } = setup(); nav.setEnabled(true); map.bearing = 120;
  assert.equal(canvas.fire('keydown', { key: 'n', ctrlKey: true }).prevented, undefined); assert.equal(map.bearing, 120);
  canvas.fire('keydown', { key: 'N' }); assert.equal(map.bearing, 0); assert.equal(map.pitch, 55);
  map.bearing = 120; canvas.fire('keydown', { key: 'u' }); assert.equal(map.bearing, 120); assert.equal(map.pitch, 0);
});
