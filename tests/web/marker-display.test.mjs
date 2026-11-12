import test from 'node:test';
import assert from 'node:assert/strict';
import {MarkerDisplay,shortMarkerName,markerMode,markerDisplayProfile} from '../../src/HikingGuide.App/Map/Web/marker-display.js';
test('中文五字截断，无名称和默认占位名称不绘制文字',()=>{assert.equal(shortMarkerName('船底顶摄影点长名称'),'船底顶摄影…');assert.equal(shortMarkerName('船底顶'),'船底顶');assert.equal(shortMarkerName('未命名标注'),'');assert.equal(shortMarkerName(null),'');assert.equal(shortMarkerName('😀😁😂😃😄😅'),'😀😁😂😃😄…');});
test('模式输入有边界并保持组合模式为默认',()=>{for(const n of [0,1,2,3])assert.equal(markerMode(n),n);for(const n of [-1,4,1.2,undefined])assert.equal(markerMode(n),3);});
test('层级门槛、碰撞与仅圆点模式，不改变原始标注集合',()=>{
 const layers=new Map(),data=new Map(),requests=[];
 const map={on(){},getLayer:id=>layers.get(id),addLayer:l=>layers.set(l.id,l),setLayoutProperty:(id,key,value)=>layers.get(id).layout[key]=value,getSource:id=>({setData:v=>data.set(id,v)}),getZoom:()=>16,getCenter:()=>({lng:113,lat:24}),getTerrain:()=>null,getPitch:()=>0,getCanvas:()=>({clientWidth:800,clientHeight:600}),project:()=>({x:400,y:300}),hasImage:()=>false};
 const display=new MarkerDisplay(map,f=>requests.push(f));const original={type:'FeatureCollection',features:[{geometry:{type:'Point',coordinates:[113,24]},properties:{waypointId:'one',namedLabel:'名字超过五个汉字',mediaExternalCount:1}}]};display.set('waypoints',original);
 assert.equal(requests.length,1);display.set('waypoints',original);assert.equal(requests.length,1);
 assert.equal(layers.get('waypoint-display').minzoom,12);assert.equal(layers.get('waypoint-thumbnails').minzoom,14);assert.equal(layers.get('waypoint-thumbnails').layout['icon-allow-overlap'],false);
 assert.equal(original.features[0].properties.shortLabel,undefined);assert.equal(data.get('waypoints').features[0].properties.shortLabel,'名字超过五…');
 for(const mode of [0,1,2,3]){display.setMode(mode);assert.equal(layers.get('waypoint-display').layout.visibility,mode===1||mode===3?'visible':'none');assert.equal(layers.get('waypoint-thumbnails').layout.visibility,mode>=2?'visible':'none');}
 assert.ok(markerDisplayProfile.maxImages<=96);display.reset();assert.equal(display.urls.size,0);
});
