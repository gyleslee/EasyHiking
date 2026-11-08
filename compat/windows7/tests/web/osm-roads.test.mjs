import test from 'node:test';
import assert from 'node:assert/strict';
import { OsmRoadLayer, roadFilter, roadWidth, roadDash, roadOpacityAt } from '../../src/HikingGuide.App/Map/Web/osm-roads.js';
const style={id:'a',enabled:true,conditions:[{key:'highway',values:'path|track'},{key:'tunnel',values:'~|no'}],minZoom:13,maxZoom:22,width:1,color:'#925D3A',alpha:1,dashArray:'8,5',lineCap:'butt',casingEnabled:true,casingWidth:.5,casingColor:'#fff',casingAlpha:.5,casingOrder:0,order:1,casingCap:'round'};
function setup(){const layers=[{id:'base',type:'raster'},{id:'dem-hillshade',type:'raster'},{id:'tracks-normal',type:'line'}],sources=new Map();
 const map={on(){},getZoom:()=>16,getStyle:()=>({layers}),getLayer:id=>layers.find(l=>l.id===id),getSource:id=>sources.get(id),addSource:(id,s)=>sources.set(id,s),removeSource:id=>{assert.ok(!layers.some(l=>l.source===id));sources.delete(id);},addLayer:(l,before)=>layers.splice(layers.findIndex(x=>x.id===before),0,l),removeLayer:id=>layers.splice(layers.findIndex(x=>x.id===id),1),moveLayer(id,before){const l=this.getLayer(id);this.removeLayer(id);layers.splice(layers.findIndex(x=>x.id===before),0,l);},isSourceLoaded:()=>true,queryRenderedFeatures:()=>[],getPaintProperty:(id,key)=>map.getLayer(id).paint[key]};
 return {map,sources,layers,roads:new OsmRoadLayer(map,()=>{})};}
test('道路匹配同时处理多值、缺失标签及键别名',()=>{
 assert.deepEqual(roadFilter(style.conditions),['all',['any',['==','highway','path'],['==','highway','track']],['any',['!has','tunnel'],['==','tunnel','no']]]);
 assert.equal(roadFilter([{key:'a|b',values:'yes'}])[1][0],'any');
});
test('全局透明度同时作用于线芯和描边，路网在DEM上方轨迹下方',()=>{
 const s=setup();s.roads.configure({enabled:true,signature:'one',opacity:.4,minZoom:10,styles:[style]});
 assert.deepEqual(s.roads.inspect().opacities,[.2,.4]);assert.deepEqual(s.layers.map(l=>l.id),['base','dem-hillshade','osm-road-a-casing','osm-road-a-core','tracks-normal']);
});
test('关闭移除全部路网图层及源，重新开启可以复用原URI',()=>{
 const s=setup(),v={enabled:true,signature:'one',opacity:1,minZoom:10,styles:[style]};s.roads.configure(v);const uri=s.sources.get('osm-road-source').tiles[0];s.roads.configure({...v,enabled:false});assert.equal(s.sources.size,0);assert.equal(s.roads.ids.length,0);s.roads.configure(v);assert.equal(s.sources.get('osm-road-source').tiles[0],uri);
});
test('修改样式保留矢量源实例，修改起显层级更新源',()=>{
 const s=setup(),v={enabled:true,signature:'one',opacity:1,minZoom:10,styles:[style]};s.roads.configure(v);const source=s.sources.get('osm-road-source');s.roads.configure({...v,opacity:.6,styles:[{...style,color:'#123456'}]});assert.equal(s.sources.get('osm-road-source'),source);s.roads.configure({...v,minZoom:11});assert.notEqual(s.sources.get('osm-road-source'),source);
});
test('透明度0或全部规则关闭不请求瓦片',()=>{
 for(const patch of [{opacity:0},{styles:[{...style,enabled:false}]}]){const s=setup();s.roads.configure({enabled:true,signature:'one',opacity:1,minZoom:10,styles:[style],...patch});assert.equal(s.sources.size,0);}
});
test('主题线宽随层级变化，固定像素虚线与all缩放分别换算',()=>{
 assert.equal(roadWidth(2,'none'),2);assert.equal(roadWidth(2)[3],12);assert.equal(roadDash('',2),null);assert.deepEqual(roadDash('8,5',2,'all'),[4,2.5]);assert.equal(roadDash('8,5',2)[0],'step');
});

test('小径从Z14开始且所有道路Z22仍在显示范围，原生瓦片Z15以上复用',()=>{
 const s=setup();s.roads.configure({enabled:true,signature:'range',opacity:1,minZoom:10,styles:[{...style,minZoom:14,maxZoom:22}]});
 for(const l of s.layers.filter(l=>l.id.startsWith('osm-road-'))){assert.equal(l.minzoom,0);assert.equal(l.maxzoom,24);assert.equal(roadOpacityAt(l.paint["line-opacity"],13),0);assert.ok(roadOpacityAt(l.paint["line-opacity"],14)>0);assert.ok(roadOpacityAt(l.paint["line-opacity"],22)>0);assert.equal(roadOpacityAt(l.paint["line-opacity"],23),0);}
 assert.equal(s.sources.get('osm-road-source').maxzoom,15);
});

test('主题有限层级保留，13至22每层只有一个对应小径线芯',()=>{
 const s=setup(),bands=[[13,14,.45],[15,15,.4],[16,16,.35],[17,22,.3]].map(([minZoom,maxZoom,width],i)=>({...style,id:'band'+i,minZoom,maxZoom,width,casingEnabled:false,color:'#000000'}));
 s.roads.configure({enabled:true,signature:'bands',opacity:1,minZoom:10,styles:bands});
 for(let z=13;z<=22;z+=.25){const active=s.layers.filter(l=>l.id.startsWith('osm-road-')&&roadOpacityAt(l.paint['line-opacity'],z)>0);assert.equal(active.length,1,'Z'+z);}
});
test('道路最大层级低于全局起显时不创建无效图层',()=>{
 const s=setup();s.roads.configure({enabled:true,signature:'hidden',opacity:1,minZoom:10,styles:[{...style,minZoom:0,maxZoom:0}]});assert.equal(s.roads.ids.length,0);
});

test('圆头小径Z18以上保持虚实比例，间隔不被持续增粗的圆头吞没',()=>{
 const p=roadDash('4,6',.3,'stroke','round');assert.equal(p.at(-2),18);const ratio=p.at(-1)[1];assert.ok(ratio[1]>1);assert.equal(roadDash('4,6',.3,'stroke','butt').at(-2),22);
});

test('Z15主题几何在Z13远景LOD仍保留，显示层级由当前画面控制',()=>{
 const s=setup();s.roads.configure({enabled:true,signature:'lod',opacity:1,minZoom:10,styles:[{...style,minZoom:15,maxZoom:15,casingEnabled:false}]});
 const l=s.layers.find(l=>l.id.startsWith('osm-road-'));for(const tileZoom of [10,13,14,15,17,22])assert.ok(tileZoom>=l.minzoom&&tileZoom<l.maxzoom);
 assert.equal(roadOpacityAt(l.paint['line-opacity'],14.99),0);assert.equal(roadOpacityAt(l.paint['line-opacity'],15),1);assert.equal(roadOpacityAt(l.paint['line-opacity'],15.99),1);assert.equal(roadOpacityAt(l.paint['line-opacity'],16),0);
});