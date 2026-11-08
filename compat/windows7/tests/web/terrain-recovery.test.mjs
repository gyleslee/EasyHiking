import test from 'node:test';
import assert from 'node:assert/strict';
import {Terrain3DController} from '../../src/HikingGuide.App/Map/Web/terrain3d.js';

function fixture(){
 const calls=[],events=[],map={
  getMaxPitch:()=>60,on(){},getCenter:()=>({lng:113.26,lat:24.48}),getZoom:()=>15.45,getBearing:()=>222,getPitch:()=>59,
  getPaintProperty:()=> '#eef1f4',setPaintProperty:(_,__,v)=>calls.push(['background',v]),setSky:v=>calls.push(['sky',v]),
  stop(){},setTerrain:v=>calls.push(['terrain',v]),jumpTo:v=>calls.push(['camera',v]),setMaxPitch:v=>calls.push(['pitch',v]),triggerRepaint:()=>calls.push(['repaint']),
  getTerrain:()=>({source:'terrain-dem-source',exaggeration:1.5}),isMoving:()=>false,getSource:()=>({}),isSourceLoaded:()=>false
 };
 const c=new Terrain3DController(map,(type,v)=>events.push({type,...v}),()=>{},()=>{});
 c.state={enabled:true,terrainRevision:7};c.effective=true;c.showSky(true);calls.length=0;
 return{c,map,calls,events};
}
test('绘制故障只重建一次地形，保留3D及原视角，不自动退回2D',async()=>{
 const {c,calls,events}=fixture();
 try{
  assert.ok(c.handleRenderError("Cannot read properties of null (reading 'y')",'at terrain (https://example.com/private?key=secret)'));
  assert.ok(c.handleRenderError("Cannot read properties of null (reading 'y')"));await Promise.resolve();
  assert.equal(c.state.enabled,true);assert.equal(c.effective,true);assert.equal(c.renderFaults,1);
  assert.equal(calls[0][0],'terrain');assert.deepEqual(calls.find(x=>x[0]==='camera')[1],{center:[113.26,24.48],zoom:15.45,bearing:222,pitch:59});
  assert.equal(calls.find(x=>x[0]==='background'),undefined);assert.equal(events.filter(e=>e.type==='TerrainUnavailable').length,0);
  assert.ok(!JSON.stringify(events).includes('secret'));assert.equal(c.renderFaults,1);
 }finally{clearInterval(c.healthTimer);}
});
test('天空清理失败仍重置视角并请求重新绘制',()=>{
 const {c,map,calls}=fixture();try{map.setSky=()=>{throw new Error('style unavailable');};c.fail('测试');assert.ok(calls.some(x=>x[0]==='repaint'));assert.ok(calls.some(x=>x[0]==='camera'&&x[1].pitch===0));}finally{clearInterval(c.healthTimer);}
});
test('派生图层回调失败不能阻止地形撤除和2D恢复通知',()=>{
 const {c,calls,events}=fixture();try{c.workChanged=()=>{throw new Error('derived layer fault');};c.setEditingLock=()=>{throw new Error('control fault');};c.fail('测试');assert.equal(calls[0][0],'terrain');assert.ok(calls.some(x=>x[0]==='repaint'));assert.ok(events.some(e=>e.type==='TerrainUnavailable'));}finally{clearInterval(c.healthTimer);}
});
test('地形加载慢时仍保留3D，移动过程中不误触发',()=>{
 const {c,map}=fixture();try{c.effective=false;c.pendingSince=performance.now()-46000;map.isMoving=()=>true;c.checkProgress();assert.ok(c.state.enabled);map.isMoving=()=>false;c.checkProgress();assert.equal(c.state.enabled,true);assert.ok(c.slowWarning);}finally{clearInterval(c.healthTimer);}
});

test('中心地形可用后远处瓦片未就绪不会继续锁住派生路网',async()=>{
 const {c,events}=fixture();try{c.scheduleWork();await new Promise(r=>setTimeout(r,100));assert.ok(events.some(e=>e.type==='TerrainWorkState'&&e.ready));}finally{clearInterval(c.healthTimer);clearTimeout(c.workTimer);}
});