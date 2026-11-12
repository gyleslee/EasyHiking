// 所有Terrain瓦片共享同一地面距离规则，避免视角和瓦片层级造成分区不一致。
export const contourDistanceProfile = Object.freeze({pitchStart:15,pitchEnd:40,labelReduction:3,oldLabelLimit:80});
const smooth=(a,b,x)=>{const t=Math.max(0,Math.min(1,(x-a)/(b-a)));return t*t*(3-2*t);};
export function detailStrength(pitch){return smooth(contourDistanceProfile.pitchStart,contourDistanceProfile.pitchEnd,pitch);}
export function distanceWeights(relativeDepth,pitch,p=groundProfile(15,25)){
 const k=detailStrength(pitch),near=smooth(p.nearStart,p.nearEnd,relativeDepth),far=smooth(p.farStart,p.farEnd,relativeDepth);
 return [1-k*near,k*(near-far),k*far];
}
export function contourIntervals(zoom,low=100,high=10){const near=zoom<13?0:zoom<15?low:high;return [near,100,200];}
export function labelInterval(intervals,weights){return weights[2]>.12?intervals[2]:weights[1]>.12?intervals[1]:intervals[0];}
// 分区以地图定位中心为圆心，使用真实水平地面距离，与屏幕上下位置、相机高程无关。
export function groundProfile(zoom,latitude){
 const near=Math.max(2000,3500*2**(15-zoom)*Math.cos(latitude*Math.PI/180)/Math.cos(25*Math.PI/180));
 return {nearStart:near,nearEnd:near*1.3,farStart:near*3,farEnd:near*4};
}
export function mercatorPoint(longitude,latitude){return [(longitude+180)/360,(1-Math.asinh(Math.tan(latitude*Math.PI/180))/Math.PI)/2];}
export function groundDistance(longitude,latitude,centerLongitude,centerLatitude){
 const a=mercatorPoint(longitude,latitude),b=mercatorPoint(centerLongitude,centerLatitude);let dx=a[0]-b[0];dx-=Math.round(dx);
 return Math.hypot(dx,a[1]-b[1])*40075016.68557849*Math.cos(centerLatitude*Math.PI/180);
}
export function labelBudget(candidates,options={}){const base=candidates>0?Math.ceil(Math.min(contourDistanceProfile.oldLabelLimit,candidates)/contourDistanceProfile.labelReduction):0;return options.flat?Math.min(base,16,Math.max(1,Math.floor((options.width||800)*(options.height||600)/50000))):base;}
export function selectContourLabels(candidates,options={}){
 const budget=labelBudget(candidates.length,options),selected=[],remaining=new Set(candidates),levels=new Set();
 const distance=(a,b)=>(a.x-b.x)**2+(a.y-b.y)**2;
 const add=c=>{selected.push(c);levels.add(c.level);remaining.delete(c);};
 // 先保证画面中有候选的近、中、远景都得到说明，再优先覆盖不同海拔。
 for(const band of [0,1,2]){
  const list=candidates.filter(c=>c.band===band);if(!list.length||selected.length>=budget)continue;
  const center={x:list.reduce((s,c)=>s+c.x,0)/list.length,y:list.reduce((s,c)=>s+c.y,0)/list.length};
  add(list.reduce((a,b)=>distance(a,center)<distance(b,center)?a:b));
 }
 while(selected.length<budget&&remaining.size){
  let best=null,score=-1;
  for(const c of remaining){const gap=selected.length?Math.min(...selected.map(s=>distance(c,s))):Infinity;if(gap<(options.flat?180:120)**2)continue;
   if(options.flat&&selected.some(s=>s.level===c.level&&distance(s,c)<450**2))continue;
   const value=gap*(levels.has(c.level)?.15:1);if(value>score){score=value;best=c;}
  }
  if(!best)break;add(best);
 }
 return selected;
}
