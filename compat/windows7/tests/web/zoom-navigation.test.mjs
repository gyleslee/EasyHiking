import test from 'node:test';import assert from 'node:assert/strict';
import {zoomLabel,ZoomNavigationControl} from '../../src/HikingGuide.App/Map/Web/zoom-navigation.js';
test('层级显示采用当前地图整数区间，半级不会提前跳到下一层',()=>{assert.equal(zoomLabel(12),'Z12');assert.equal(zoomLabel(15.99),'Z15');assert.equal(zoomLabel(16),'Z16');assert.equal(zoomLabel(NaN),'Z0');});
test('层级在原生按钮左侧且缩放事件更新，移除时清理监听',()=>{
 const document={createElement:()=>({setAttribute(){},append(...items){this.children=items;},remove(){this.removed=true;}})};
 class Native{onAdd(){return {native:true};}onRemove(){this.removed=true;}}
 const map={zoom:12,getZoom(){return this.zoom;},on(name,fn){this.fn=fn;},off(name,fn){assert.equal(this.fn,fn);this.fn=null;}};
 const c=new ZoomNavigationControl(Native,{document}),root=c.onAdd(map);assert.equal(root.children[0].textContent,'Z12');assert.equal(root.children[1].native,true);map.zoom=13.2;map.fn();assert.equal(c.readout.textContent,'Z13');c.onRemove();assert.equal(map.fn,null);assert.ok(c.navigation.removed);
});
