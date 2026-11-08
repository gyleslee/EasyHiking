import test from 'node:test';
import assert from 'node:assert/strict';
import { nearestLineFeature, distanceToSegment, trackPickProfile } from '../../src/HikingGuide.App/Map/Web/track-hit-test.js';
const project = c => ({ x: c[0], y: c[1] });
const line = (id, coordinates) => ({ properties: { trackId: id }, geometry: { type: 'LineString', coordinates } });
test('加宽选取范围，编辑14像素而普通8像素', () => {
 const a=line('a',[[0,0],[100,0]]), p={x:50,y:12};
 assert.equal(nearestLineFeature([a],p,project,trackPickProfile.normalPixels),null);
 assert.equal(nearestLineFeature([a],p,project,trackPickProfile.editPixels),a);
});
test('按实际近距离选线，不让高亮轨迹抢选',()=>{
 const a=line('a',[[0,0],[100,0]]),b=line('b',[[0,8],[100,8]]);
 assert.equal(nearestLineFeature([a,b],{x:50,y:7},project,14,'a'),b);
});
test('重叠距离一致时保持当前轨迹',()=>{
 const a=line('a',[[0,0],[100,0]]),b=line('b',[[0,0],[100,0]]);
 assert.equal(nearestLineFeature([b,a],{x:50,y:0},project,14,'a'),a);
});
test('真实不连续的MultiLineString不能拾取中间虚假连线',()=>{
 const f={properties:{trackId:'a'},geometry:{type:'MultiLineString',coordinates:[[[0,0],[10,0]],[[90,0],[100,0]]]}};
 assert.equal(nearestLineFeature([f],{x:50,y:0},project,14),null);
});
test('退化线与非有限坐标安全处理',()=>{
 assert.equal(distanceToSegment({x:3,y:4},{x:0,y:0},{x:0,y:0}),5);
 assert.equal(distanceToSegment({x:NaN,y:4},{x:0,y:0},{x:10,y:0}),Infinity);
});
test('缩放后使用屏幕像素而不是经纬度固定容差',()=>{
 const a=line('a',[[0,0],[1,0]]);
 assert.equal(nearestLineFeature([a],{x:50,y:13},c=>({x:c[0]*100,y:c[1]*100}),14),a);
 assert.equal(nearestLineFeature([a],{x:50,y:15},c=>({x:c[0]*100,y:c[1]*100}),14),null);
});
