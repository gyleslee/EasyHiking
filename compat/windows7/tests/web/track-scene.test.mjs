import {readFile} from 'node:fs/promises';
import assert from 'node:assert/strict';
import test from 'node:test';
const source=await readFile(new URL('../../src/HikingGuide.App/Map/Web/track-scene.js',import.meta.url),'utf8');
const layoutUrl=new URL('../../src/HikingGuide.App/Map/Web/track-arrow-layout.js',import.meta.url).href;
const {visibleTrackChunks,trackArrowTone,trackArrowPattern}=await import('data:text/javascript;base64,'+Buffer.from(source.replace('"./track-arrow-layout.js"',JSON.stringify(layoutUrl))).toString('base64'));
test('viewport halo retains entering chunks and never edits full geometry',()=>{const inside={__displayBounds:[0,0,1,1]},halo={__displayBounds:[1.2,0,1.3,1]},outside={__displayBounds:[10,10,11,11]};const all=[inside,halo,outside];assert.deepEqual(visibleTrackChunks(all,{west:0,east:1,south:0,north:1}),[inside,halo]);assert.equal(all.length,3);});
test('unsafe or wrapping terrain bounds fall back to complete snapshot',()=>{const all=[{}];assert.equal(visibleTrackChunks(all,null),all);assert.equal(visibleTrackChunks(all,{west:170,east:-170,south:-1,north:1}),all);assert.equal(visibleTrackChunks(all,{west:NaN,east:1,south:0,north:1}),all);});
test('guide colour and arrows share track source, with tile simplification disabled',async()=>{const map=await readFile(new URL('../../src/HikingGuide.App/Map/Web/map.js',import.meta.url),'utf8');assert.match(map,/tracks: \{[^\n]*tolerance: 0/);for(const id of ['guide-segments','guide-segments-selected','split-sections-selected','track-direction-arrows'])assert.match(map,new RegExp('id: "'+id+'"[^\n]*source: "tracks"'));assert.doesNotMatch(map,/id: "track-direction-arrows"[^\n]*type: "symbol"/);});

test('adaptive tone uses line colour and the visible guide overlay',()=>{
 assert.equal(trackArrowTone({color:'#b9c548'}),'dark');
 assert.equal(trackArrowTone({color:'#24578f'}),'light');
 assert.equal(trackArrowTone({color:'#000000',guideVisible:true}),'dark');
 assert.equal(trackArrowTone({color:'#FFFFFF'}),'dark');
 assert.equal(trackArrowTone({color:'#111111'}),'light');
});

function evaluate(value,p){if(!Array.isArray(value))return value;const[op,...args]=value;const ev=x=>evaluate(x,p);switch(op){case'literal':return args[0];case'get':return p[args[0]];case'in':return ev(args[1]).includes(ev(args[0]));case'==':return ev(args[0])===ev(args[1]);case'any':return args.some(ev);case'concat':return args.map(ev).join('');case'case':for(let i=0;i<args.length-1;i+=2)if(ev(args[i]))return ev(args[i+1]);return ev(args.at(-1));default:throw Error(op);}}
test('yellow selection switches dark-track markers to black and resets on deselection',()=>{
 const p={trackId:'track',splitSectionId:'section',guideSegmentId:'guide',arrowTone:'light',isActive:false,guideVisible:false};
 assert.equal(evaluate(trackArrowPattern(),p),'track-chevron-light-narrow');
 for(const selection of [{trackIds:['track']},{sectionIds:['section']},{guideSegmentId:'guide'}])assert.equal(evaluate(trackArrowPattern(selection),p),'track-chevron-dark-wide');
 assert.equal(evaluate(trackArrowPattern({trackIds:['other']}),p),'track-chevron-light-narrow');
});
