import test from 'node:test';import assert from 'node:assert/strict';
import {roadPrefetchTiles,RoadPrefetch} from '../../src/HikingGuide.App/Map/Web/road-prefetch.js';
const bounds={getWest:()=>113.26,getEast:()=>113.261,getSouth:()=>24.47,getNorth:()=>24.471};
test('预生成覆盖当前画面之外一圈，双层级且任务有界',()=>{
 const tiles=roadPrefetchTiles(bounds,{lng:113.26,lat:24.47},18);assert.ok(tiles.length>4&&tiles.length<=96);assert.deepEqual([...new Set(tiles.map(t=>t.z))],[14,15]);assert.equal(new Set(tiles.map(t=>`${t.z}/${t.x}/${t.y}`)).size,tiles.length);
 const x=Math.floor((113.26+180)/360*2**15);assert.ok(tiles.some(t=>t.z===15&&t.x<x));assert.ok(tiles.some(t=>t.z===15&&t.x>x));
});
test('日期变更线、极区和过宽视野仍限制在合法瓦片与96项内',()=>{
 const b={getWest:()=>179,getEast:()=>-179,getSouth:()=>-90,getNorth:()=>90};const tiles=roadPrefetchTiles(b,{lng:179.5,lat:85},14);assert.ok(tiles.length<=96);assert.ok(tiles.every(t=>t.x>=0&&t.y>=0&&t.x<2**t.z&&t.y<2**t.z));assert.deepEqual(roadPrefetchTiles(bounds,{lng:113,lat:24},9),[]);
});
test('仅3D预取，成功结果复用，停止及切换设置取消旧任务',async()=>{
 let terrain=null,calls=0;const map={on(){},getTerrain:()=>terrain,getBounds:()=>bounds,getCenter:()=>({lng:113.26,lat:24.47}),getZoom:()=>18};
 const p=new RoadPrefetch(map,async()=>{calls++;return{ok:true,arrayBuffer:async()=>new ArrayBuffer(1)};});p.configure({enabled:true,signature:'a',minZoom:10});await p.load();assert.equal(calls,0);terrain={source:'dem'};await p.load();const count=calls;assert.ok(count>0);await p.load();assert.equal(calls,count);p.configure({enabled:false,signature:'a'});assert.ok(p.abort.signal.aborted);p.stop();
});
