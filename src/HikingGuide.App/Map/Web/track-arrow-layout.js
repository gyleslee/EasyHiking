// Display-only geometry: keep every line point; suppress direction marks near tight bends.
export const trackArrowLayoutProfile = Object.freeze({
  spacingPixels: 80, turnProbePixels: 6, turnClearancePixels: 18,
  sharpTurnDegrees: 45, worldPixelsAtZoomZero: 512
});

export const trackArrowSymbolProfile=Object.freeze({minimumHeightPixels:10,outsetPixels:2,chevronWidthRatio:.65,bandRatio:.32,gapRatio:.18,borderRatio:.075,maxBorderPixels:.9});
export const trackArrowDensityStops=Object.freeze([[0,80],[10,80],[12,88],[14,96],[16,108],[18,128],[20,148],[22,168]]);
export function trackArrowSpacingPixels(zoom,pitch=0) {
 const z=Number.isFinite(zoom)?Math.max(0,Math.min(22,zoom)):14;
 let spacing=trackArrowDensityStops.at(-1)[1];
 for(let i=1;i<trackArrowDensityStops.length;i++){const [a,x]=trackArrowDensityStops[i-1],[b,y]=trackArrowDensityStops[i];if(z<=b){spacing=x+(y-x)*(z-a)/(b-a);break;}}
 const angle=Math.max(0,Math.min(80,Number.isFinite(pitch)?pitch:0))*Math.PI/180;
 return Math.round(spacing*(1+.45*Math.sin(angle))/4)*4;
}
function insetPolygon(points,amount) {
 const result=[];
 for(let i=0;i<points.length;i++){
  const before=points[(i+points.length-1)%points.length],at=points[i],after=points[(i+1)%points.length];
  const ux=at[0]-before[0],uy=at[1]-before[1],vx=after[0]-at[0],vy=after[1]-at[1];
  const ul=Math.hypot(ux,uy),vl=Math.hypot(vx,vy),a=[at[0]-uy/ul*amount,at[1]+ux/ul*amount],b=[at[0]-vy/vl*amount,at[1]+vx/vl*amount];
  const cross=ux*vy-uy*vx,t=Math.abs(cross)<1e-12?0:((b[0]-a[0])*vy-(b[1]-a[1])*vx)/cross;
  result.push([a[0]+ux*t,a[1]+uy*t]);
 }
 return result;
}
export function trackChevronPolygons(lineWidth,pitch=0) {
 const p=trackArrowSymbolProfile,width=Math.max(1,Math.min(20,Number.isFinite(lineWidth)?lineWidth:3));
 const angle=Math.max(0,Math.min(80,Number.isFinite(pitch)?pitch:0))*Math.PI/180;
 const height=Math.max(p.minimumHeightPixels,width+2*p.outsetPixels)*(1+.45*Math.sin(angle)),h=height/2,w=height*p.chevronWidthRatio,band=height*p.bandRatio,gap=height*p.gapRatio;
 const border=Math.min(p.maxBorderPixels,height*p.borderRatio),symbols=[];
 for(const center of [-(w+gap)/2,(w+gap)/2]){
  const left=center-w/2,right=center+w/2;
  const outer=[[left,-h],[left+band,-h],[right,0],[left+band,h],[left,h],[right-band,0]];
  symbols.push({outer,inner:insetPolygon(outer,border)});
 }
 return {lineWidth:width,height,length:2*w+gap,edgeInsetPixels:(width-height)/2,symbols};
}

function interpolate(points, distances, distance) {
  if (distance <= 0) return points[0];
  if (distance >= distances.at(-1)) return points.at(-1);
  let low = 1, high = points.length - 1;
  while (low < high) { const mid = (low + high) >> 1; if (distances[mid] < distance) low = mid + 1; else high = mid; }
  if (distances[low] === distance) return points[low];
  const a = points[low - 1], b = points[low];
  const fraction = Math.max(0, Math.min(1, (distance - distances[low - 1]) / Math.max(1e-12, distances[low] - distances[low - 1])));
  return a.map((v, i) => v + (b[i] - v) * fraction);
}

export function prepareTrackArrowRuns(feature, zoom, profile = trackArrowLayoutProfile) {
  if(profile===trackArrowLayoutProfile)profile={...profile,spacingPixels:trackArrowSpacingPixels(zoom)};
  const result = [], world = profile.worldPixelsAtZoomZero * 2 ** zoom;
  const cosLimit = 85.05112878;
  for (const [lineIndex, points] of (feature.geometry?.coordinates || []).entries()) {
    if (points.length < 2) continue;
    const screen = points.map(([lng, lat]) => {
      const phi = Math.max(-cosLimit, Math.min(cosLimit, lat)) * Math.PI / 180;
      return [lng / 360 * world, -Math.log(Math.tan(Math.PI / 4 + phi / 2)) / (2 * Math.PI) * world];
    });
    const distances = [0];
    for (let i = 1; i < screen.length; i++) distances.push(distances[i - 1] + Math.hypot(screen[i][0] - screen[i - 1][0], screen[i][1] - screen[i - 1][1]));
    const total = distances.at(-1), blocked = [];
    for (let i = 1; i < screen.length - 1; i++) {
      const d = distances[i], probe = Math.min(profile.turnProbePixels, d, total - d);
      if (probe < 1) continue;
      const a = interpolate(screen, distances, d - probe), b = screen[i], c = interpolate(screen, distances, d + probe);
      const ux = b[0] - a[0], uy = b[1] - a[1], vx = c[0] - b[0], vy = c[1] - b[1];
      const lengths = Math.hypot(ux, uy) * Math.hypot(vx, vy);
      if (lengths < 1e-8) continue;
      const angle = Math.acos(Math.max(-1, Math.min(1, (ux * vx + uy * vy) / lengths))) * 180 / Math.PI;
      if (angle >= profile.sharpTurnDegrees) blocked.push([Math.max(0, d - profile.turnClearancePixels), Math.min(total, d + profile.turnClearancePixels)]);
    }
    const merged = [];
    for (const range of blocked) {
      const previous = merged.at(-1);
      if (previous && range[0] <= previous[1]) previous[1] = Math.max(previous[1], range[1]);
      else merged.push([...range]);
    }
    const intervals = []; let cursor = 0;
    function straight(start, end) {
      // Whole repeat cycles leave space around both ends; no half chevrons at a cut.
      const eligibleEnd = start + Math.floor((end - start) / profile.spacingPixels) * profile.spacingPixels;
      if (eligibleEnd > start) intervals.push([start, eligibleEnd, true]);
      if (eligibleEnd < end) intervals.push([eligibleEnd, end, false]);
    }
    for (const [start, end] of merged) { if (start > cursor) straight(cursor, start); intervals.push([start, end, false]); cursor = end; }
    if (cursor < total) straight(cursor, total);
    if (!intervals.length) intervals.push([0, total, false]);
    let pointIndex = 1;
    for (const [start, end, allowed] of intervals) {
      if (end <= start) continue;
      const coordinates = [interpolate(points, distances, start)];
      while (pointIndex < points.length && distances[pointIndex] <= start) pointIndex++;
      while (pointIndex < points.length && distances[pointIndex] < end) coordinates.push(points[pointIndex++]);
      coordinates.push(interpolate(points, distances, end));
      result.push({ ...feature, id: `${feature.id}:arrow:${lineIndex}:${result.length}`,
        properties: { ...feature.properties, arrowsAllowed: allowed }, geometry: { type: "MultiLineString", coordinates: [coordinates] } });
    }
  }
  return result.length ? result : [{...feature, properties:{...feature.properties, arrowsAllowed:false}}];
}
