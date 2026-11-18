import {readFile} from 'node:fs/promises';
import test from 'node:test';
import assert from 'node:assert/strict';
const source=await readFile(new URL('../../src/HikingGuide.App/Map/Web/track-arrow-layout.js',import.meta.url),'utf8');
const {prepareTrackArrowRuns,trackArrowLayoutProfile:profile,trackArrowSpacingPixels,trackChevronPolygons}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const zoom=18,world=512*2**zoom;
const coordinate=(x,y)=>[x/world*360,Math.atan(Math.sinh(-y/world*2*Math.PI))*180/Math.PI];
const feature=points=>({type:'Feature',id:'track:0',properties:{trackId:'track',guideSegmentId:'guide',splitSectionId:'section'},geometry:{type:'MultiLineString',coordinates:[points.map(p=>coordinate(...p))]}});
test('straight runs keep complete lines with room around each pattern repeat',()=>{
 const f=feature(Array.from({length:201},(_,i)=>[i,0]));const before=JSON.stringify(f),runs=prepareTrackArrowRuns(f,zoom);
 assert.ok(runs.some(r=>r.properties.arrowsAllowed));assert.equal(JSON.stringify(f),before);
 assert.deepEqual(runs[0].geometry.coordinates[0][0],f.geometry.coordinates[0][0]);
 assert.deepEqual(runs.at(-1).geometry.coordinates[0].at(-1),f.geometry.coordinates[0].at(-1));
 for(let i=1;i<runs.length;i++)assert.deepEqual(runs[i].geometry.coordinates[0][0],runs[i-1].geometry.coordinates[0].at(-1));
 for(const p of f.geometry.coordinates[0])assert.ok(runs.some(r=>r.geometry.coordinates[0].some(q=>Math.abs(q[0]-p[0])<1e-12&&Math.abs(q[1]-p[1])<1e-12)));
 assert.equal(new Set(runs.map(r=>r.id)).size,runs.length);
});
test('right angle keeps track and guide geometry but leaves the bend unmarked',()=>{
 const f=feature([...Array.from({length:301},(_,i)=>[i,0]),...Array.from({length:300},(_,i)=>[300,i+1])]);
 const runs=prepareTrackArrowRuns(f,zoom),corner=coordinate(300,0);
 const nearCorner=runs.filter(r=>r.geometry.coordinates[0].some(p=>Math.hypot(p[0]-corner[0],p[1]-corner[1])<1e-10));
 assert.ok(nearCorner.length>0);assert.ok(nearCorner.every(r=>!r.properties.arrowsAllowed));
 assert.ok(runs.some(r=>r.properties.arrowsAllowed));
 assert.ok(runs.every(r=>r.properties.trackId==='track'&&r.properties.guideSegmentId==='guide'&&r.properties.splitSectionId==='section'));
});
test('short fragments and zero-length tracks never force a partial marker',()=>{
 for(const f of [feature([[0,0],[10,0]]),feature([[0,0],[0,0]])])assert.ok(prepareTrackArrowRuns(f,zoom).every(r=>!r.properties.arrowsAllowed));
 assert.ok(profile.spacingPixels>20);
});

test('high contrast chevrons extend slightly beyond lines and retain sharp separate points',()=>{
 for(const width of [1,1.5,3,5,7,8,11,14.5,20]){const shape=trackChevronPolygons(width);assert.equal(shape.symbols.length,2);const ys=shape.symbols.flatMap(s=>s.outer.map(p=>p[1]));assert.ok(Math.abs(Math.max(...ys)*2-Math.max(10,width+4))<1e-10);assert.ok(Math.min(...ys)<-width/2);for(const s of shape.symbols)for(const p of s.inner)assert.ok(p.every(Number.isFinite)&&Math.abs(p[1])<=shape.height/2);assert.ok(shape.symbols[0].outer[2][0]<shape.symbols[1].outer[0][0]);}
});
test('density is moderate and grows sparser at higher zoom, with extra relief in 3D',()=>{
 const spacing=[10,12,14,16,18,20,22].map(z=>trackArrowSpacingPixels(z));assert.deepEqual(spacing,[80,88,96,108,128,148,168]);
 assert.ok(trackArrowSpacingPixels(22)>3*44);assert.ok(trackArrowSpacingPixels(16,60)>trackArrowSpacingPixels(16));
 for(let z=0;z<=22;z+=.1){const a=trackArrowSpacingPixels(z),b=trackArrowSpacingPixels(z+.1);assert.ok(a>=80&&b>=a&&b-a<=4);}
});

test('tilted 3D tracks retain larger arrow heads',()=>{assert.ok(trackChevronPolygons(5,60).height>trackChevronPolygons(5).height*1.3);});
