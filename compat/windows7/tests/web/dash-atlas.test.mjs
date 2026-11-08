import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs';
import {roadDash} from '../../src/HikingGuide.App/Map/Web/osm-roads.js';
const vendor=fs.readFileSync(new URL('../../src/HikingGuide.App/Map/Web/vendor/maplibre-gl.mjs',import.meta.url),'utf8');
const start=vendor.indexOf('Xi=class{'),end=vendor.indexOf(';function Zi',start);
const Atlas=new Function('I','let '+vendor.slice(start,end)+';return Xi;')(()=>{});
const theme=JSON.parse(fs.readFileSync(new URL('../../src/HikingGuide.Core/Resources/OsmRoadDefaultStyles.json',import.meta.url),'utf8'));
test('真实引擎虚线图集容纳完整主题各缩放级，扩容后行位置和旧像素不丢失',()=>{
 let deletes=0;const gl=new Proxy({getParameter:()=>4096,deleteTexture:()=>deletes++,createTexture:()=>({})},{get:(o,k)=>o[k]??(()=>{})});
 const atlas=new Atlas(256,512),first=atlas.getDash([4,6],true);atlas.bind({gl,maxTextureSize:4096});const pixels=atlas.data.slice(0,15*256);
 for(const s of theme){const p=roadDash(s.DashArray,s.Width,s.ScaleMode,s.LineCap);if(!p)continue;for(const d of p[0]==='step'?p.filter(v=>Array.isArray(v)&&v[0]==='literal').map(v=>v[1]):[p])assert.ok(atlas.getDash(d,s.LineCap==='round'));}
 assert.ok(atlas.nextRow>512);assert.ok(atlas.height>=atlas.nextRow);assert.ok(atlas.height<=4096);assert.equal(atlas.getDash([4,6],true),first);assert.deepEqual(atlas.data.slice(0,pixels.length),pixels);
 atlas.bind({gl,maxTextureSize:4096});assert.equal(deletes,1);assert.equal(atlas.resized,false);
});
