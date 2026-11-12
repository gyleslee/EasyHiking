import test from 'node:test';
import assert from 'node:assert/strict';
import { Terrain3DController } from '../../src/HikingGuide.App/Map/Web/terrain3d.js';
import { GpuContourLayer } from '../../src/HikingGuide.App/Map/Web/gpu-contours.js';

const wait=()=>new Promise(r=>setTimeout(r,110));
function setup(){
  const events={},messages=[];
  const map={moving:false,loaded:false,on(name,fn){(events[name]||=[]).push(fn);},getMaxPitch(){return 60;},getSource(){return {};},isSourceLoaded(){return this.loaded;},isMoving(){return this.moving;}};
  const controller=new Terrain3DController(map,(type,value)=>messages.push({type,...value}),()=>{},()=>{});
  controller.state={enabled:true,terrainRevision:3};controller.effective=true;
  return {map,controller,messages,events,dispose(){clearInterval(controller.healthTimer);clearTimeout(controller.workTimer);}};
}
test('地形没有加载完成时不放行，加载后等待稳定再补充派生图层',async()=>{
  const s=setup();try{s.controller.effective=false;s.controller.reportWork(false);s.controller.scheduleWork();await wait();assert.equal(s.messages.at(-1).ready,false);
    s.map.loaded=true;s.controller.effective=true;s.controller.scheduleWork();assert.equal(s.messages.at(-1).ready,false);await wait();assert.equal(s.messages.at(-1).ready,true);
  }finally{s.dispose();}
});
test('拖动期间让行，停下后中心地形已可用即可恢复派生图层',async()=>{
  const s=setup();try{s.map.loaded=true;s.controller.scheduleWork();await wait();assert.equal(s.messages.at(-1).ready,true);
    s.map.moving=true;s.events.movestart[0]();assert.equal(s.messages.at(-1).ready,false);
    s.map.moving=false;s.map.loaded=false;s.events.moveend[0]();await wait();assert.equal(s.messages.at(-1).ready,true);
    s.map.loaded=true;s.controller.scheduleWork();await wait();assert.equal(s.messages.at(-1).ready,true);
  }finally{s.dispose();}
});
test('回到2D释放等待，状态消息含修订号且重复状态不刷屏',async()=>{
  const s=setup();try{s.controller.reportWork(false);s.controller.reportWork(false);assert.equal(s.messages.length,1);assert.equal(s.messages[0].requestId,3);
    s.controller.state.enabled=false;s.controller.scheduleWork();await wait();assert.equal(s.messages.at(-1).ready,true);
  }finally{s.dispose();}
});
test('GPU移动时不重采样标签且已有贴地数字保留',()=>{
  const map={isMoving:()=>true};const layer=new GpuContourLayer(map);layer.labelCount=5;
  layer.labels();assert.equal(layer.labelCount,5);assert.equal(layer.pending.size,0);
});
