// 屏幕像素命中参数只控制交互，不参与轨迹距离、高程或坡度计算。
export const trackPickProfile = Object.freeze({ normalPixels: 8, editPixels: 14, lineHitWidth: 28, handlePixels: 10 });

export function distanceToSegment(point, a, b) {
  if (![point.x, point.y, a.x, a.y, b.x, b.y].every(Number.isFinite)) return Infinity;
  const dx = b.x - a.x, dy = b.y - a.y;
  const length = dx * dx + dy * dy;
  const t = length > 0 ? Math.max(0, Math.min(1, ((point.x - a.x) * dx + (point.y - a.y) * dy) / length)) : 0;
  return Math.hypot(point.x - a.x - t * dx, point.y - a.y - t * dy);
}

export function nearestLineFeature(features, point, project, tolerance, preferredTrackId = null) {
  let nearest = null, best = Infinity;
  for (const feature of features) {
    const geometry = feature.geometry;
    const parts = geometry?.type === "LineString" ? [geometry.coordinates] :
      geometry?.type === "MultiLineString" ? geometry.coordinates : [];
    let distance = Infinity;
    for (const part of parts) {
      for (let i = 1; i < part.length; i++) {
        distance = Math.min(distance, distanceToSegment(point, project(part[i - 1]), project(part[i])));
      }
    }
    if (distance > tolerance) continue;
    // 重叠线路距离完全相同时优先当前轨迹；不让高亮层盖住真正更近的轨迹。
    if (distance < best - .01 || (Math.abs(distance - best) <= .01 &&
        feature.properties?.trackId === preferredTrackId && nearest?.properties?.trackId !== preferredTrackId)) {
      nearest = feature; best = distance;
    }
  }
  return nearest;
}
