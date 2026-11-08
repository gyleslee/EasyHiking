import * as maplibregl from "./vendor/maplibre-gl.mjs";

window.addEventListener("error", event =>
  window.chrome?.webview?.postMessage({ type: "Error", message: `JavaScript错误：${event.message || "未知错误"}` }));

const map = new maplibregl.Map({
  container: "map", center: [105, 35], zoom: 2.5,
  attributionControl: true,
  style: {
    version: 8,
    sources: {
      osm: { type: "raster", tiles: ["https://tile.openstreetmap.de/{z}/{x}/{y}.png"],
        tileSize: 256, attribution: "© OpenStreetMap contributors", maxzoom: 19 },
      tiles: { type: "geojson", data: { type: "FeatureCollection", features: [] } }
    },
    layers: [
      { id: "osm", type: "raster", source: "osm" },
      { id: "rd5-grid", type: "fill", source: "tiles",
        paint: { "fill-color": ["match", ["get", "state"],
          "selected", "#2676df", "downloading", "#a363df",
          "downloaded", "#28a47a", "failed", "#dc5454",
          "installed", "#26a659", "#e8c654"],
          "fill-opacity": .27 } },
      { id: "rd5-border", type: "line", source: "tiles",
        paint: { "line-color": "#24445c", "line-width": .8 } },
      { id: "rd5-label", type: "symbol", source: "tiles", minzoom: 3.5,
        layout: { "text-field": ["get", "name"], "text-size": 10,
          "text-allow-overlap": false },
        paint: { "text-color": "#15334d", "text-halo-color": "#ffffff",
          "text-halo-width": 1.5 } }
    ]
  }
});
map.addControl(new maplibregl.NavigationControl(), "top-right");
function post(message) { window.chrome?.webview?.postMessage(message); }
let errorReported = false;
map.on("error", event => {
  if (errorReported) return;
  errorReported = true;
  post({ type: "Error", message: event.error?.message || "地图渲染错误" });
});
let available = [];
let selected = new Set();
let installed = new Set();
let downloadStates = new Map();
let boxMode = false;
let dragging = null;
const selectionBox = document.getElementById("tile-selection-box");

function refresh() {
  const features = available.map(item => {
    const west = item.lon, south = item.lat;
    return { type: "Feature",
      properties: { name: item.name, state: downloadStates.get(item.name) ||
        (selected.has(item.name) ? "selected" :
          installed.has(item.name) ? "installed" : "available") },
      geometry: { type: "Polygon", coordinates: [[
        [west, south], [west + 5, south], [west + 5, south + 5],
        [west, south + 5], [west, south]]] } };
  });
  map.getSource("tiles")?.setData({ type: "FeatureCollection", features });
}

map.once("style.load", () => post({ type: "Ready" }));
window.chrome?.webview?.addEventListener("message", event => {
  const value = event.data || {};
  if (value.type === "Data") {
    available = value.available || [];
    installed = new Set(value.installed || []);
    selected = new Set(value.selected || []);
    downloadStates.clear();
    refresh();
  } else if (value.type === "Selection") {
    selected = new Set(value.selected || []);
    refresh();
  } else if (value.type === "BoxMode") {
    boxMode = value.enabled === true;
    map.getCanvas().style.cursor = boxMode ? "crosshair" : "";
    if (boxMode) map.dragPan.disable(); else map.dragPan.enable();
  } else if (value.type === "DownloadState") {
    for (const name of value.names || []) downloadStates.set(name, value.state);
    refresh();
  }
});
map.on("click", event => {
  if (boxMode) return;
  const item = map.queryRenderedFeatures(event.point, { layers: ["rd5-grid"] })[0];
  if (item?.properties?.name) post({ type: "Toggle", name: item.properties.name });
});
map.on("mousedown", event => {
  if (!boxMode || event.originalEvent.button !== 0) return;
  dragging = { x: event.point.x, y: event.point.y,
    lon: event.lngLat.lng, lat: event.lngLat.lat,
    remove: event.originalEvent.ctrlKey };
  selectionBox.style.display = "block";
  event.preventDefault();
});
map.on("mousemove", event => {
  if (!dragging) return;
  selectionBox.style.left = String(Math.min(dragging.x, event.point.x)) + "px";
  selectionBox.style.top = String(Math.min(dragging.y, event.point.y)) + "px";
  selectionBox.style.width = String(Math.abs(dragging.x - event.point.x)) + "px";
  selectionBox.style.height = String(Math.abs(dragging.y - event.point.y)) + "px";
});
map.on("mouseup", event => {
  if (!dragging) return;
  selectionBox.style.display = "none";
  post({ type: "Box", minLon: Math.min(dragging.lon, event.lngLat.lng),
    maxLon: Math.max(dragging.lon, event.lngLat.lng),
    minLat: Math.min(dragging.lat, event.lngLat.lat),
    maxLat: Math.max(dragging.lat, event.lngLat.lat),
    remove: dragging.remove || event.originalEvent.ctrlKey });
  dragging = null;
});

// 仅供本机自动化验收：走同一套地图点击与框选事件，不触发下载。
window.p7WorldMapSmoke = async () => {
  if (!map.getSource("osm") || !map.getLayer("rd5-grid") || available.length < 2)
    return post({ type: "Smoke", ready: false, osm: false, grid: false });
  map.fitBounds([[108, 18], [122, 27]], { duration: 0 });
  await Promise.race([new Promise(resolve => map.once("idle", resolve)),
    new Promise(resolve => setTimeout(resolve, 5000))]);
  const clickLocation = { lng: 112.5, lat: 22.5 };
  map.fire("click", { point: map.project(clickLocation), lngLat: clickLocation });
  boxMode = true;
  map.dragPan.disable();
  const from = { lng: 115.5, lat: 20.5 }, to = { lng: 119.5, lat: 24.5 };
  map.fire("mousedown", { point: map.project(from), lngLat: from,
    originalEvent: { button: 0, ctrlKey: false }, preventDefault() {} });
  map.fire("mouseup", { point: map.project(to), lngLat: to,
    originalEvent: { ctrlKey: false } });
  boxMode = false;
  map.dragPan.enable();
  post({ type: "Smoke", ready: true, osm: true, grid: true,
    available: available.length });
};
