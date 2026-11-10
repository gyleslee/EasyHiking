// Display-only geometry: keep every line point; suppress direction marks near tight bends.
export const trackArrowLayoutProfile = Object.freeze({
  spacingPixels: 44, turnProbePixels: 6, turnClearancePixels: 8,
  sharpTurnDegrees: 45, worldPixelsAtZoomZero: 512
});

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
