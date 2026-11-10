import {readFile} from 'node:fs/promises';
import test from 'node:test';
import assert from 'node:assert/strict';
const source=await readFile(new URL('../../src/HikingGuide.App/Map/Web/track-arrow-layout.js',import.meta.url),'utf8');
const {prepareTrackArrowRuns,trackArrowLayoutProfile:profile}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
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
 const f=feature([...Array.from({length:101},(_,i)=>[i,0]),...Array.from({length:100},(_,i)=>[100,i+1])]);
 const runs=prepareTrackArrowRuns(f,zoom),corner=coordinate(100,0);
 const nearCorner=runs.filter(r=>r.geometry.coordinates[0].some(p=>Math.hypot(p[0]-corner[0],p[1]-corner[1])<1e-10));
 assert.ok(nearCorner.length>0);assert.ok(nearCorner.every(r=>!r.properties.arrowsAllowed));
 assert.ok(runs.some(r=>r.properties.arrowsAllowed));
 assert.ok(runs.every(r=>r.properties.trackId==='track'&&r.properties.guideSegmentId==='guide'&&r.properties.splitSectionId==='section'));
});
test('short fragments and zero-length tracks never force a partial marker',()=>{
 for(const f of [feature([[0,0],[10,0]]),feature([[0,0],[0,0]])])assert.ok(prepareTrackArrowRuns(f,zoom).every(r=>!r.properties.arrowsAllowed));
 assert.ok(profile.spacingPixels>20);
});
