import test from 'node:test';
import assert from 'node:assert/strict';
import { Terrain3DController, terrainSky } from '../../src/HikingGuide.App/Map/Web/terrain3d.js';

test('样式就绪前构造不读取背景，3D天蓝背景切回2D恢复原色', () => {
  let ready = false, reads = 0, background = '#eef1f4', sky;
  const map = {
    getMaxPitch: () => 60, on() {},
    getPaintProperty() { assert.ok(ready, '样式尚未就绪'); reads++; return background; },
    setPaintProperty(layer, property, value) { assert.equal(layer, 'background'); background = value; },
    setSky(value) { sky = value; }
  };
  const controller = new Terrain3DController(map, () => {}, () => {}, () => {});
  try {
    assert.equal(reads, 0);
    ready = true;
    controller.showSky(true);
    assert.equal(background, '#87CEEB'); assert.deepEqual(sky, terrainSky);
    controller.showSky(true);
    controller.showSky(false);
    assert.equal(background, '#eef1f4'); assert.equal(sky, undefined); assert.equal(reads, 1);
  } finally { clearInterval(controller.healthTimer); }
});
