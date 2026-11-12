import test from 'node:test';
import assert from 'node:assert/strict';
import {distanceWeights,contourIntervals,labelInterval,labelBudget,selectContourLabels,groundProfile,groundDistance} from '../../src/HikingGuide.App/Map/Web/contour-distance.js';

test('以地面米距分区，船底顶距离当前山坡不足2公里仍为近景',()=>{
 const d=groundDistance(113.2613403,24.4816224,113.269571,24.464423),p=groundProfile(15,24.464423);
 assert.ok(d>1800&&d<2200);assert.deepEqual(distanceWeights(d,60,p),[1,0,0]);
 for(const z of [13,14,15,16,18]){const r=groundProfile(z,24.48);assert.deepEqual(distanceWeights(0,72,r),[1,0,0]);assert.deepEqual(distanceWeights(r.nearEnd*1.2,72,r),[0,1,0]);assert.deepEqual(distanceWeights(r.farEnd*1.1,72,r),[0,0,1]);}
 assert.ok(groundDistance(179.999,0,-179.999,0)<230);
});

test('2D数字受面积和间距限制，避免相同海拔反复标注',()=>{
 const options={flat:true,width:1200,height:600};assert.equal(labelBudget(200,options),14);
 const c=Array.from({length:80},(_,i)=>({x:(i%10)*100,y:Math.floor(i/10)*80,level:1000,band:0}));
 const selected=selectContourLabels(c,options);assert.ok(selected.length<8);
 for(let i=0;i<selected.length;i++)for(let j=i+1;j<selected.length;j++)assert.ok(Math.hypot(selected[i].x-selected[j].x,selected[i].y-selected[j].y)>=450);
});

test('Z13/14为100/100/200，Z15及以后为10/100/200',()=>{
 for(const z of [13,14,14.99])assert.deepEqual(contourIntervals(z),[100,100,200]);
 for(const z of [15,16,18,22])assert.deepEqual(contourIntervals(z),[10,100,200]);
 assert.equal(contourIntervals(12)[0],0);
 assert.deepEqual(contourIntervals(15,300,500),[500,100,200]);assert.deepEqual(contourIntervals(14,300,500),[300,100,200]);
});
test('俯视和2D保持原等高距，倾斜逐渐引入距离分区',()=>{
 assert.deepEqual(distanceWeights(20000,0),[1,0,0]);assert.deepEqual(distanceWeights(20000,15),[1,0,0]);
 assert.ok(distanceWeights(20000,27)[0]>0&&distanceWeights(20000,27)[2]>0);
});
test('标签采用分区匹配的等高距，过渡区提前使用更稀的线',()=>{
 const i=[10,100,200];assert.equal(labelInterval(i,[1,0,0]),10);assert.equal(labelInterval(i,[.5,.5,0]),100);assert.equal(labelInterval(i,[0,.8,.2]),200);
});
test('标签总预算是原候选上限的三分之一，保留各距离分区及不同高度',()=>{
 for(const n of [0,1,3,12,60,80,200])assert.equal(labelBudget(n),Math.ceil(Math.min(80,n)/3));
 const c=Array.from({length:80},(_,i)=>({x:(i%10)*160,y:Math.floor(i/10)*160,level:1000+i*10,band:i%3}));
 const selected=selectContourLabels(c);assert.ok(selected.length<=27);assert.equal(new Set(selected.map(x=>x.band)).size,3);assert.equal(new Set(selected.map(x=>x.level)).size,selected.length);
 const repeated=c.map(x=>({...x,level:1000}));assert.ok(selectContourLabels(repeated).length<=27);
});

test('近景扩大到Z15约3.5公里，缩放后至少2公里',()=>{assert.ok(groundProfile(15,25).nearStart===3500);assert.equal(groundProfile(18,25).nearStart,2000);});
