import {readFile} from 'node:fs/promises';import test from 'node:test';import assert from 'node:assert/strict';
const source=await readFile(new URL('../../src/HikingGuide.App/Map/Web/map-keyboard.js',import.meta.url),'utf8');const{mapPanKey}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
test('WASD moves in screen directions including uppercase',()=>{for(const[key,offset]of Object.entries({w:[0,-100],a:[-100,0],s:[0,100],d:[100,0]})){assert.deepEqual(mapPanKey({key}),offset);assert.deepEqual(mapPanKey({key:key.toUpperCase()}),offset);}assert.equal(mapPanKey({key:'x'}),null);});
test('typing, input composition and shortcuts never pan the map',()=>{for(const key of ['ctrlKey','metaKey','altKey','isComposing','defaultPrevented'])assert.equal(mapPanKey({key:'w',[key]:true}),null);assert.equal(mapPanKey({key:'w',target:{closest:()=>({})}}),null);});
test('planning right click routes undo before marker and track menus',async()=>{const s=await readFile(new URL('../../src/HikingGuide.App/Map/Web/map.js',import.meta.url),'utf8');const block=s.slice(s.indexOf('map.on("contextmenu"'));assert.ok(block.indexOf('RoutePlanningUndoRequested')<block.indexOf('FavoriteRightClicked'));assert.ok(block.indexOf('suppressContext()')<block.indexOf('RoutePlanningUndoRequested'));});

test('physical WASD works on map even when Chinese IME reports Process',()=>{
  for(const key of ['W','A','S','D'])assert.ok(mapPanKey({key:'Process',code:'Key'+key,isComposing:true}));
  assert.equal(mapPanKey({key:'Process',code:'KeyW',isComposing:true,target:{closest:()=>({})}}),null);
});
