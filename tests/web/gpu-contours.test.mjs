import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {parentTile,contourInterval,heightAt,monotoneCubic,surfaceSize} from '../../src/HikingGuide.App/Map/Web/gpu-contours.js';

test('等高距由全场相机档位决定，不由Terrain混合瓦片LOD决定',()=>{
 assert.equal(contourInterval(12.99),0);assert.equal(contourInterval(13),100);assert.equal(contourInterval(14.99),100);assert.equal(contourInterval(15),10);assert.equal(contourInterval(22),10);
});
test('子瓦片准确映射至共同高程母瓦片UV，含world wrap',()=>{
 const p=parentTile({z:16,x:41701,y:29005},11);
 assert.equal(p.z,11);assert.equal(p.x,1303);assert.equal(p.y,906);
 assert.deepEqual(p.origin,[5/32,13/32]);assert.equal(p.scale,1/32);
 const wrapped=parentTile({z:16,x:41701+65536,y:29005},11);assert.deepEqual(wrapped,p);
 const neighbor=parentTile({z:16,x:41702,y:29005},11);assert.equal(p.origin[0]+p.scale,neighbor.origin[0]);
});
test('原始RGB解码保留负高程和真实0米，Alpha只作为有效性',()=>{
 const b=new Uint8Array(surfaceSize*surfaceSize*4);
 for(const [index,h] of [[0,-50.1],[1,0],[2,1200.4]]){const v=Math.round((h+10000)*10),p=index*4;b[p]=v>>16;b[p+1]=v>>8&255;b[p+2]=v&255;b[p+3]=255;assert.ok(Math.abs(heightAt(b,index,0)-h)<1e-8);}
 assert.equal(heightAt(b,3,0),null);
});
test('有约束曲面插值通过原样本、相邻切线一致且不制造新峰谷',()=>{
 const a=[0,10,30,90],b=[10,30,90,100],epsilon=1e-5;
 assert.equal(monotoneCubic(a,0),10);assert.equal(monotoneCubic(a,1),30);
 for(const values of [a,[100,10,30,10],[-50,-10,0,10],[50,50,50,50]])for(let i=0;i<=100;i++){
  const h=monotoneCubic(values,i/100);assert.ok(h>=Math.min(values[1],values[2])-1e-8&&h<=Math.max(values[1],values[2])+1e-8);
 }
 const left=(monotoneCubic(a,1)-monotoneCubic(a,1-epsilon))/epsilon,right=(monotoneCubic(b,epsilon)-monotoneCubic(b,0))/epsilon;
 assert.ok(Math.abs(left-right)<.01);
});
test('完整6.17.0发行资源身份一致，只有已批准的主模块补丁',()=>{
 const base=new URL('../../src/HikingGuide.App/Map/Web/vendor/',import.meta.url),m=JSON.parse(fs.readFileSync(new URL('VERSION.json',base)));
 assert.equal(m.version,'6.17.0');assert.deepEqual(m.files.filter(f=>f.patched).map(f=>f.file),['maplibre-gl.mjs']);
 for(const f of m.files){assert.ok(fs.existsSync(new URL(f.file,base)));if(!f.patched)assert.equal(f.officialSha256,f.deployedSha256);}
});
