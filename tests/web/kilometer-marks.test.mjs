import test from 'node:test';
import assert from 'node:assert/strict';
import {KilometerMarks} from '../../src/HikingGuide.App/Map/Web/kilometer-marks.js';
function setup(){
 globalThis.document={createElement:()=>({getContext:()=>new Proxy({getImageData:()=>({data:new Uint8ClampedArray(4)})},{get:(o,k)=>o[k]||(()=>{})})})};
 const sources=new Map(),layers=new Map(),images=new Set(),events={};const map={on:(e,f)=>events[e]=f,getSource:id=>sources.get(id),addSource:(id,s)=>sources.set(id,{...s,setData(d){this.data=d;}}),getLayer:id=>layers.get(id),addLayer:l=>layers.set(l.id,l),addImage:id=>images.add(id),hasImage:id=>images.has(id),removeImage:id=>images.delete(id),getCanvas:()=>({clientWidth:800,clientHeight:600}),project:c=>({x:c[0],y:c[1]})};return{map,sources,layers,images,events};
}
const feature=(label,x=0)=>({type:'Feature',geometry:{type:'Point',coordinates:[x,20]},properties:{label,endpoint:label.includes('.')}});
test('地理锚点不随2D/3D改变，水滴底端贴点，数字正向朝屏幕',()=>{const s=setup(),km=new KilometerMarks(s.map);const data={type:'FeatureCollection',features:[feature('1'),feature('2.5')]};km.set(data);assert.deepEqual(s.sources.get('kilometer-marks').data,data);const l=s.layers.get('kilometer-marks');assert.equal(l.layout['icon-anchor'],'bottom');assert.equal(l.layout['icon-pitch-alignment'],'viewport');assert.equal(l.layout['icon-allow-overlap'],false);});
test('切换项目和隐藏时清除历史纹理',()=>{const s=setup(),km=new KilometerMarks(s.map);km.set({features:[feature('1'),feature('2')]});km.set({features:[feature('0.9')]});assert.deepEqual([...s.images],['km-pin-0.9']);km.clear();assert.equal(s.images.size,0);assert.equal(s.sources.get('kilometer-marks').data.features.length,0);});
test('超长轨迹按视野控制纹理预算，移动后复用完整公里点',()=>{const s=setup(),km=new KilometerMarks(s.map);const data={features:Array.from({length:4000},(_,i)=>feature(String(i+1),i*40))};km.set(data);assert.ok(s.images.size<=512);assert.ok(s.sources.get('kilometer-marks').data.features.length<4000);assert.equal(km.data.features.length,4000);s.events.moveend();assert.equal(km.data.features.length,4000);});
