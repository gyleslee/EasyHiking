import { TrackScene, trackArrowImages } from "./track-scene.js";
import { mapPanKey } from "./map-keyboard.js";
import { StartupMapLoad } from "./startup-map-load.js";
import * as maplibregl from "./vendor/maplibre-gl.mjs";
import { listen, post, setTerrainEditingLock } from "./bridge.js";
import { Terrain3DController } from "./terrain3d.js";
import { TerrainNavigation } from "./terrain-navigation.js";
import { ZoomNavigationControl } from "./zoom-navigation.js";
import { OfflineRegionSelection } from "./offline-region.js";
import { OsmRoadLayer, roadDash } from "./osm-roads.js";
import { ContourLabels } from "./contour-labels.js";
import { loadFavoriteSymbols, FavoriteMapInteraction } from "./favorites.js";
import { nearestLineFeature, trackPickProfile } from "./track-hit-test.js";

const emptyCollection = { type: "FeatureCollection", features: [] };
const pageStarted = performance.now();
let projectId = null;
let revision = 0;
let favoritesRevision = 0;
let lastFavorites = emptyCollection;
let favoriteImageReady = null;
let selected = { kind: "none", objectId: null };
let selectedTrackIds = [];
let selectedSplitSectionIds = [];
let lastMouseFrame = 0;
let suppressNextViewPersistence = false;
let lastTracks = emptyCollection;
let lastSplitSections = emptyCollection;
let lastWaypoints = emptyCollection;
let lastGuideNodes = emptyCollection;
let lastGuideSegments = emptyCollection;
let draggingMarker = null;
let dragEndedAt = 0;
let snapPickNodeId = null;
let addMarkerKind = null;
let activeMediaPopup = null;
let trackEdit = null;
let trackEditContextVersion = -1;
let trackEditHandles = emptyCollection;
let trackEditRange = emptyCollection;
let routePlanning = false;
let draggingTrackHandle = null;
const waypointRadius = ["interpolate", ["linear"], ["zoom"], 5, 1, 10, 1.5, 12, 2.5, 14, 3.5, 16, 4, 18, 5, 19, 6];
const guideRadius = ["interpolate", ["linear"], ["zoom"], 5, 1.5, 10, 2, 12, 3, 14, 4, 16, 5, 18, 7, 19, 8];
const waypointSelectedRadius = ["interpolate", ["linear"], ["zoom"], 5, 2, 10, 3, 12, 4, 14, 6, 16, 8, 18, 10, 19, 11];
const guideSelectedRadius = ["interpolate", ["linear"], ["zoom"], 5, 2.5, 10, 3.5, 12, 5, 14, 7, 16, 9, 18, 12, 19, 13];
const markerStrokeWidth = ["interpolate", ["linear"], ["zoom"], 5, 0.75, 10, 1, 14, 1.25, 19, 2];
const selectedStrokeWidth = ["interpolate", ["linear"], ["zoom"], 5, 1, 10, 1.5, 14, 2, 19, 3];

let trackScene;
function loadFavoriteSymbol(style) { return loadFavoriteSymbols(map, style); }
let terrain3D;
let terrainNavigation;
let requestedOfflineTerrain=null;
let requestedTerrainState=null;

let lastScriptError={message:"",time:0};
window.addEventListener("error", event => {
  const message=event.message||"未知错误";
  if(terrain3D?.handleRenderError(message,event.error?.stack))return;
  if(message===lastScriptError.message&&Date.now()-lastScriptError.time<10000)return;
  lastScriptError={message,time:Date.now()};post("MapError",{projectId,revision,message:`JavaScript错误：${message}`});
});
window.addEventListener("unhandledrejection",event=>{if(terrain3D?.handleRenderError(event.reason?.message,event.reason?.stack))return;post("MapError",{projectId,revision,message:`JavaScript异步错误：${event.reason?.message||"未知错误"}`});});

const map = new maplibregl.Map({
  container: "map",
  center: [113.3, 23.1],
  zoom: 5,
  attributionControl: false,
  renderWorldCopies: true,
  bearingSnap: 0,
  locale: { "NavigationControl.ZoomIn": "放大", "NavigationControl.ZoomOut": "缩小", "NavigationControl.ResetBearing": "向北" },
  style: {
    version: 8,
    sources: {
      favorites: { type: "geojson", data: emptyCollection },
      tracks: { type: "geojson", data: emptyCollection, tolerance: 0, maxzoom: 22, buffer: 128 },
      splitSections: { type: "geojson", data: emptyCollection },
      directionArrows: { type: "geojson", data: emptyCollection },
      waypoints: { type: "geojson", data: emptyCollection },
      guideNodes: { type: "geojson", data: emptyCollection },
      guideSegments: { type: "geojson", data: emptyCollection },
      elevationHover: { type: "geojson", data: emptyCollection },
      trackEditHandles: { type: "geojson", data: emptyCollection },
      trackEditRange: { type: "geojson", data: emptyCollection }
      ,routePreviewLegs: { type: "geojson", data: emptyCollection }
      ,routePreviewPoints: { type: "geojson", data: emptyCollection }
    },
    layers: [
      { id: "background", type: "background", paint: { "background-color": "#eef1f4" } },
      { id: "elevation-hover", type: "circle", source: "elevationHover", paint: { "circle-radius": 8, "circle-color": "#ff7a00", "circle-stroke-color": "#ffffff", "circle-stroke-width": 2 } },
      { id: "tracks-normal", type: "line", source: "tracks", filter: ["==", ["get", "isActive"], false], layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": ["get", "color"], "line-width": 3, "line-opacity": .82 } },
      { id: "tracks-active", type: "line", source: "tracks", filter: ["==", ["get", "isActive"], true], layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": ["get", "color"], "line-width": 4, "line-opacity": .94 } },
      { id: "tracks-selected", type: "line", source: "tracks", filter: ["==", ["get", "trackId"], "__none__"], layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": "#ffcf33", "line-width": 8, "line-opacity": .9 } },
      { id: "tracks-hit", type: "line", source: "tracks", layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": "#ffffff", "line-width": trackPickProfile.lineHitWidth, "line-opacity": .01 } },
      { id: "split-sections-selected", type: "line", source: "tracks", filter: ["==", ["get", "splitSectionId"], "__none__"], layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": "#fff064", "line-width": 9, "line-opacity": .82 } },
      { id: "split-sections-hit", type: "line", source: "tracks", filter: ["==", ["get", "hasSplitSections"], true], layout: { "line-cap": "round", "line-join": "round" }, paint: { "line-color": "#ffffff", "line-width": 13, "line-opacity": .01 } },
      { id: "waypoints", type: "circle", source: "waypoints", paint: { "circle-radius": waypointRadius, "circle-color": "#28a745", "circle-stroke-color": "#ffffff", "circle-stroke-width": markerStrokeWidth } },
      { id: "waypoints-selected", type: "circle", source: "waypoints", filter: ["==", ["get", "waypointId"], "__none__"], paint: { "circle-radius": waypointSelectedRadius, "circle-color": "rgba(255,255,255,0)", "circle-stroke-color": "#ffcf33", "circle-stroke-width": selectedStrokeWidth } },
      { id: "waypoint-labels", type: "symbol", source: "waypoints", minzoom: 12, maxzoom: 15, layout: { "text-field": ["get", "name"], "text-size": 12, "text-offset": [0, 1.15], "text-anchor": "top", "text-allow-overlap": false }, paint: { "text-color": "#20252a", "text-halo-color": "#ffffff", "text-halo-width": 1.5 } }
      ,{ id: "waypoint-media-badges", type: "symbol", source: "waypoints", filter: ["any", [">", ["get", "mediaInternalCount"], 0], [">", ["get", "mediaExternalCount"], 0]], layout: { "text-field": "📷", "text-size": 13, "text-offset": [0.9, -0.8], "text-allow-overlap": true }, paint: { "text-halo-color": "#ffffff", "text-halo-width": 2 } }
      ,{ id: "guide-segments", type: "line", source: "tracks", filter: ["==", ["get", "guideVisible"], true], paint: { "line-color": "#e6a900", "line-width": 5, "line-opacity": .72 } }
      ,{ id: "guide-segments-selected", type: "line", source: "tracks", filter: ["==", ["get", "guideSegmentId"], "__none__"], paint: { "line-color": "#ffed72", "line-width": 9 } }
      ,{ id: "guide-nodes", type: "circle", source: "guideNodes", paint: { "circle-radius": guideRadius, "circle-color": "#ffcf33", "circle-stroke-color": "#20252a", "circle-stroke-width": markerStrokeWidth } }
      ,{ id: "guide-nodes-selected", type: "circle", source: "guideNodes", filter: ["==", ["get", "guideNodeId"], "__none__"], paint: { "circle-radius": guideSelectedRadius, "circle-color": "rgba(255,255,255,0)", "circle-stroke-color": "#ffed72", "circle-stroke-width": selectedStrokeWidth } }
      ,{ id: "guide-node-labels", type: "symbol", source: "guideNodes", minzoom: 10, maxzoom: 15, layout: { "text-field": ["concat", ["to-string", ["get", "order"]], " ", ["get", "name"]], "text-size": 12, "text-offset": [0, 1.4], "text-anchor": "top" }, paint: { "text-color": "#302000", "text-halo-color": "#ffffff", "text-halo-width": 1.5 } }
      ,{ id: "marker-label-z15", type: "symbol", source: "waypoints", minzoom: 15, filter: ["!=", ["get", "namedLabel"], ""], layout: { "text-field": ["get", "namedLabel"], "text-size": 12, "text-offset": [0, 1.4], "text-anchor": "top", "text-allow-overlap": true, "text-ignore-placement": true }, paint: { "text-color": "#20252a", "text-halo-color": "#ffffff", "text-halo-width": 1.5 } }
      ,{ id: "guide-node-label-z15", type: "symbol", source: "guideNodes", minzoom: 15, filter: ["!=", ["get", "namedLabel"], ""], layout: { "text-field": ["get", "namedLabel"], "text-size": 12, "text-offset": [0, 1.5], "text-anchor": "top", "text-allow-overlap": true, "text-ignore-placement": true }, paint: { "text-color": "#302000", "text-halo-color": "#ffffff", "text-halo-width": 1.5 } }
      ,{ id: "guide-node-media-badges", type: "symbol", source: "guideNodes", filter: ["any", [">", ["get", "mediaInternalCount"], 0], [">", ["get", "mediaExternalCount"], 0]], layout: { "text-field": "📷", "text-size": 13, "text-offset": [0.9, -0.8], "text-allow-overlap": true }, paint: { "text-halo-color": "#ffffff", "text-halo-width": 2 } }
      ,{ id: "track-direction-arrows", type: "line", source: "tracks", filter: ["==",["get","arrowsAllowed"],true], layout: { "line-cap": "butt", "line-join": "round" }, paint: { "line-pattern": "track-chevron-dark-wide", "line-width": 4 } }
      ,{ id: "track-edit-handles-hit", type: "circle", source: "trackEditHandles", paint: { "circle-radius": trackPickProfile.handlePixels, "circle-color": "#ffffff", "circle-opacity": .01 } }
      ,{ id: "track-edit-handles", type: "circle", source: "trackEditHandles", paint: { "circle-radius": ["case", ["get", "selected"], 8, 6], "circle-color": ["case", ["get", "selected"], "#ff7b24", "#ffffff"], "circle-stroke-color": "#1b568c", "circle-stroke-width": 2 } }
      ,{ id: "track-edit-range", type: "line", source: "trackEditRange", paint: { "line-color": "#ff6933", "line-width": 7, "line-opacity": .9 } }
      ,{ id: "route-preview-brouter", type: "line", source: "routePreviewLegs",
        filter: ["==", ["get", "mode"], "BRouter"],
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#246bd6", "line-width": 5, "line-opacity": .95 } }
      ,{ id: "route-preview-manual", type: "line", source: "routePreviewLegs",
        filter: ["==", ["get", "mode"], "Manual"],
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#ed722d", "line-width": 5, "line-dasharray": [2, 1.5] } }
      ,{ id: "route-preview-points", type: "circle", source: "routePreviewPoints",
        paint: { "circle-radius": 8, "circle-color": ["match", ["get", "role"],
          "Start", "#27a64c", "End", "#da3b38", "#ffcf33"],
          "circle-stroke-color": "#ffffff", "circle-stroke-width": 2 } }
      ,{ id: "route-preview-labels", type: "symbol", source: "routePreviewPoints",
        layout: { "text-field": ["get", "label"], "text-offset": [0, 1.3],
          "text-size": 12, "text-anchor": "top" },
        paint: { "text-color": "#183450", "text-halo-color": "#ffffff", "text-halo-width": 2 } }
    ]
  }
});

const startupMapLoad = new StartupMapLoad(map, error => post("MapError", { message: error?.message || "加载地图图层失败" }));
window.hikingGuideStartupTest = { inspect: () => startupMapLoad.inspect() };
terrain3D=new Terrain3DController(map,post,enabled=>{setTerrainEditingLock(enabled);terrainNavigation?.setEnabled(enabled);},()=>{suppressNextViewPersistence=true;},ready=>{
  contourLabels.setPaused(!ready);
  if(ready&&requestedOfflineTerrain)setOfflineTerrain(requestedOfflineTerrain);
});
terrainNavigation=new TerrainNavigation(map);
const contourLabels=new ContourLabels(map);
const osmRoads=new OsmRoadLayer(map,post);
function setPlanningCursor(){map.getContainer().classList.toggle("route-planning",routePlanning);}
let stationaryCamera;
function roadBucketCoverage(){
  const manager=map.style.tileManagers['osm-road-source'],zoom=map.getZoom(),rows=[];if(!manager)return rows;
  const styles=(osmRoads.config.styles||[]).filter(s=>s.enabled&&zoom>=Math.max(osmRoads.config.minZoom,s.minZoom)&&zoom<s.maxZoom+1&&s.color.toLowerCase()==='#000000'&&s.dashArray);
  const match=(props,c)=>c.key.split('|').some(k=>c.values.split('|').some(v=>v==='~'?!(k in props):v==='*'?k in props:props[k]===v));
  for(const coord of manager.getVisibleCoordinates()){
    const tile=manager.getTile(coord),layer=tile?.latestFeatureIndex?.loadVTLayers()?.roads;if(!layer)continue;
    const props=Array.from({length:layer.length},(_,i)=>layer.feature(i).properties);
    for(const s of styles){if(!props.some(p=>s.conditions.every(c=>match(p,c))))continue;const id=`osm-road-${s.id}-core`;rows.push({tile:coord.key,z:coord.overscaledZ,style:s.id,min:s.minZoom,max:s.maxZoom,bucket:!!tile.getBucket(map.style._layers[id])});}
  }return rows;
}
window.hikingGuideStationaryTest={
  inspect:()=>({camera:terrain3D.camera(),roads:osmRoads.inspect(),coverage:roadBucketCoverage(),tiles:map.style.tileManagers['osm-road-source']?._inViewTiles.getAllTiles().map(t=>({key:t.tileID.key,state:t.state})),coords:map.style.tileManagers['osm-road-source']?.getVisibleCoordinates().map(c=>c.key),rtt:map.terrain?.tileManager.getRenderableTiles().map(t=>({key:t.tileID.key,roads:t.rttFingerprint['osm-road-source']}))}),
  redraw:()=>{map.terrain.tileManager.releaseAllRTT();map.triggerRepaint();},
  cover:()=>{map.style.tileManagers['osm-road-source'].update(map.painter.transform,map.terrain);map.triggerRepaint();},
  nudge:()=>{stationaryCamera=terrain3D.camera();map.jumpTo({...stationaryCamera,center:[stationaryCamera.center[0]+.001,stationaryCamera.center[1]+.001]});},
  restore:()=>map.jumpTo(stationaryCamera)
};
window.hikingGuideOsmRoadTest={inspect:()=>({...osmRoads.inspect(),atlas:{rows:map.style.lineAtlas.nextRow,height:map.style.lineAtlas.height,patterns:Object.keys(map.style.lineAtlas.dashEntry).length}}),configure:v=>osmRoads.configure(v),sample:type=>osmRoads.sample(type),
  warmDashes:()=>{let missing=0;for(const s of osmRoads.config.styles||[]){const p=roadDash(s.dashArray,s.width,s.scaleMode,s.lineCap);if(!p)continue;for(const d of p[0]==="step"?p.filter(v=>Array.isArray(v)&&v[0]==="literal").map(v=>v[1]):[p])if(!map.style.lineAtlas.getDash(d,s.lineCap==="round"))missing++;}map.triggerRepaint();return{missing,rows:map.style.lineAtlas.nextRow,height:map.style.lineAtlas.height};}};
window.hikingGuide3DTest={inspect:()=>({...terrain3D.inspect(),labelCount:contourLabels.featureCount||0}),camera:(pitch=55,bearing=0)=>{suppressNextViewPersistence=true;map.jumpTo({pitch,bearing});},
  at:(coordinate,zoom=15)=>{suppressNextViewPersistence=true;map.jumpTo({center:coordinate,zoom});},
  surface:(lng,lat)=>{const p=map.project([lng,lat]);const ll=map.unproject(p);return {x:p.x,y:p.y,longitude:ll.lng,latitude:ll.lat};},
  layers:()=>map.getStyle().layers.map(l=>({id:l.id,type:l.type,source:l.source})),
  postEdit:(type)=>post(type,{projectId,revision,latitude:24.42,longitude:113.32}),
  fail:()=>terrain3D.fail("当前3D地形DEM未连接，地图已切回2D。")};
let offlineTerrainBefore=null;
const offlineSelection=new OfflineRegionSelection(map,post,()=>{offlineTerrainBefore={...terrain3D.state};favoriteInteraction.closeHover();terrain3D.update({...terrain3D.state,enabled:false});},()=>{if(offlineTerrainBefore)terrain3D.update(offlineTerrainBefore);offlineTerrainBefore=null;suppressNextViewPersistence=true;});
window.hikingGuideOfflineTest={begin:()=>offlineSelection.begin(),click:(lon,lat)=>map.fire("click",{lngLat:{lng:lon,lat},point:map.project([lon,lat]),originalEvent:{button:0,target:map.getCanvas()}}),add:(x,y)=>offlineSelection.add(x,y),finish:()=>offlineSelection.finish(),cancel:()=>offlineSelection.cancel(),inspect:()=>({active:offlineSelection.active,points:offlineSelection.points})};
const favoriteInteraction = new FavoriteMapInteraction(map, post, () => favoritesRevision,
  () => lastFavorites, data => { lastFavorites = data; });
function lineHit(point, layers = ["tracks-hit"], tolerance = trackEdit ? trackPickProfile.editPixels : trackPickProfile.normalPixels, trackId = null) {
  const box = [[point.x - tolerance, point.y - tolerance], [point.x + tolerance, point.y + tolerance]];
  const features = map.queryRenderedFeatures(box, { layers }).filter(f => !trackId || f.properties?.trackId === trackId);
  const center = map.getCenter().lng;
  return nearestLineFeature(features, point, coordinate => {
    const longitude = coordinate[0] + 360 * Math.round((center - coordinate[0]) / 360);
    return map.project([longitude, coordinate[1]]);
  }, tolerance, trackEdit?.trackId);
}
window.hikingGuideT10Test = {
  inspect: () => ({ projectId, revision, edit: trackEdit, handleIds: trackEditHandles.features.map(f => f.properties.pointId),
    selectedTrackIds, selectedSectionFilter: map.getFilter("split-sections-selected"), selectedTrackFilter: map.getFilter("tracks-selected"),
    camera: { longitude: map.getCenter().lng, latitude: map.getCenter().lat, zoom: map.getZoom(), bearing: map.getBearing(), pitch: map.getPitch() },
    panEnabled: map.dragPan.isEnabled(), dragging: draggingTrackHandle !== null, trackIds: lastTracks.features.map(f => f.properties.trackId),
    waypointIds: lastWaypoints.features.map(f => f.properties.waypointId), guideNodeIds: lastGuideNodes.features.map(f => f.properties.guideNodeId) }),
  point: (id, index = 0) => { const f = lastTracks.features.find(f => f.properties.trackId === id); const c = f?.geometry.coordinates[0]?.[index]; return c ? map.project(c) : null; },
  location: (id, fraction = .4) => { const feature = lastTracks.features.find(f => f.properties.trackId === id) || lastSplitSections.features.find(f => f.properties.splitSectionId === id); const part = feature?.geometry.coordinates[0]; if (!part?.length) return null; const value = (part.length - 1) * fraction, i = Math.floor(value), a = part[i], b = part[Math.min(i + 1, part.length - 1)], t = value - i; return map.project([a[0] + (b[0]-a[0])*t,a[1]+(b[1]-a[1])*t]); },
  handle: id => { const f = trackEditHandles.features.find(f => f.properties.pointId === id); return f ? map.project(f.geometry.coordinates) : null; },
  center: (id, zoom = 16) => { const f = lastTracks.features.find(f => f.properties.trackId === id); const part = f?.geometry.coordinates[0]; if (part?.length) map.jumpTo({ center: part[Math.floor(part.length / 2)], zoom }); }
};
window.hikingGuideFavoriteTest = {
  inspect: () => ({ selectedId: favoriteInteraction.selectedId, popupCount: document.querySelectorAll(".favorite-map-card").length,
    lng: map.getCenter().lng, lat: map.getCenter().lat,
    lastPointer: favoriteInteraction.lastPointer, moving: map.isMoving(), hover: favoriteInteraction.hover,
    cssWidth: map.getCanvas().clientWidth, cssHeight: map.getCanvas().clientHeight,
    hitId: favoriteInteraction.lastPointer ? favoriteInteraction.hit(favoriteInteraction.lastPointer)?.properties?.favoriteId || null : null,
    lastClose: favoriteInteraction.lastMapClose, lastHoverUnblock: favoriteInteraction.lastHoverUnblock, blockedPoint: favoriteInteraction.hoverBlockedPoint, zoom: map.getZoom(), pending: favoriteInteraction.pending, sequence: favoriteInteraction.sequence,
    hoverId: favoriteInteraction.hover?.id || null, dragActive: favoriteInteraction.drag?.active === true,
    panEnabled: map.dragPan.isEnabled(), labels: lastFavorites.features.filter(f => f.properties.labelImage).length,
    labelImages: lastFavorites.features.filter(f => f.properties.labelImage && map.hasImage(f.properties.labelImage)).length,
    goldImage: map.hasImage("favorite-gold-star"), glowLayer: !!map.getLayer("favorites-selected-glow-layer"),
    goldIds: lastFavorites.features.filter(f => f.properties.favoriteId === favoriteInteraction.selectedId).map(f => f.properties.favoriteId),
    coordinates: lastFavorites.features.map(f => ({ id: f.properties.favoriteId, coordinate: f.geometry.coordinates })),
    anchor: map.getLayoutProperty("favorites-label-layer", "icon-anchor"), overlap: map.getLayoutProperty("favorites-label-layer", "icon-allow-overlap"),
    names: lastFavorites.features.map(f => f.properties.name),
    popupText: document.querySelector(".favorite-map-card")?.textContent || "",
    layerVisibility: ["favorites-symbol-layer", "favorites-selected-symbol-layer", "favorites-selected-glow-layer", "favorites-label-layer"].map(l => map.getLayoutProperty(l, "visibility")) }),
  point: id => { const f = lastFavorites.features.find(f => f.properties.favoriteId === id); return f ? map.project(f.geometry.coordinates) : null; },
  center: (id, zoom = 16) => { const f = lastFavorites.features.find(f => f.properties.favoriteId === id); if (f) map.jumpTo({ center: f.geometry.coordinates, zoom }); },
  popupPoint: () => { const r = document.querySelector(".favorite-map-card")?.getBoundingClientRect(); return r ? { x: r.left + Math.min(50, r.width / 2), y: r.top + Math.min(15, r.height / 2) } : null; },
  popupImage: () => document.querySelector(".favorite-map-card img")?.complete === true,
  zoom: () => map.setZoom(map.getZoom() + 0.25),
  pan: () => map.panBy([40, 0], { duration: 50 })
};
map.addControl(new ZoomNavigationControl(maplibregl.NavigationControl), "bottom-right");
map.addControl(terrainNavigation, "bottom-right");

map.on("load", () => {
  document.getElementById("loading").classList.add("hidden");
  post("MapReady", { durationMs: performance.now() - pageStarted });
});

map.on("moveend", () => {
  postTerrainViewport();
  const center = map.getCenter();
  if (startupMapLoad.waiting && requestedTerrainState?.enabled && requestedTerrainState.camera) {
    // 底图预览暂用俯视，不能把这个临时俯仰写回用户的3D视角。
    // 如用户先平移/缩放，恢复3D时沿用新的中心与层级，不跳回旧位置。
    requestedTerrainState = { ...requestedTerrainState, camera: { ...requestedTerrainState.camera,
      longitude: center.lng, latitude: center.lat, zoom: map.getZoom(), bearing: map.getBearing() } };
    return;
  }
  post("ViewChanged", {
    projectId, revision,
    latitude: center.lat, longitude: center.lng,
    zoom: map.getZoom(), bearing: map.getBearing(), pitch: map.getPitch(),
    message: suppressNextViewPersistence ? "programmatic" : null
  });
  suppressNextViewPersistence = false;
});

map.on("mousemove", event => {
  if(offlineSelection.active)return;
  if (draggingTrackHandle) {
    const feature = trackEditHandles.features?.find(item => item.properties?.pointId === draggingTrackHandle.pointId);
    if (feature) {
      feature.geometry.coordinates = [event.lngLat.lng, event.lngLat.lat];
      map.getSource("trackEditHandles")?.setData(trackEditHandles);
    }
    return;
  }
  if (draggingMarker) {
    const { sourceId, property, id } = draggingMarker;
    const collection = sourceId === "waypoints" ? lastWaypoints : lastGuideNodes;
    const feature = collection.features?.find(item => item.properties?.[property] === id);
    if (feature) {
      feature.geometry.coordinates = [event.lngLat.lng, event.lngLat.lat];
      map.getSource(sourceId)?.setData(collection);
    }
    return;
  }
  const now = performance.now();
  if (now - lastMouseFrame < 50) return;
  lastMouseFrame = now;
  if (!routePlanning && !addMarkerKind && !snapPickNodeId)
    map.getCanvas().style.cursor = map.queryRenderedFeatures(event.point, { layers: ["track-edit-handles-hit", "tracks-hit"] }).length ? (trackEdit ? "pointer" : "default") : "";
  post("MouseMoved", { projectId, revision, latitude: event.lngLat.lat, longitude: event.lngLat.lng, zoom: map.getZoom() });
});
map.getCanvas().addEventListener("mouseleave", () => post("MouseLeft", { projectId, revision }));
const mapCanvas=map.getCanvas();
mapCanvas.tabIndex=0;
const editable=target=>!!target?.closest?.('input,textarea,select,[contenteditable]:not([contenteditable="false"]),[role="textbox"]');
map.getContainer().addEventListener("pointerdown",event=>{if(event.target===mapCanvas){mapCanvas.focus({preventScroll:true});post("MapKeyboardFocus",{active:true});}},true);
document.addEventListener("focusin",event=>post("MapKeyboardFocus",{active:map.getContainer().contains(event.target)&&!editable(event.target)}),true);
window.hikingGuidePanKey=key=>{if(editable(document.activeElement)||draggingTrackHandle||draggingMarker)return false;const offset=mapPanKey({code:key,key});if(!offset)return false;map.panBy(offset,{duration:0});return true;};
map.getContainer().addEventListener("keydown", event => {
  const offset=mapPanKey(event);
  if(offset&&!draggingTrackHandle&&!draggingMarker){event.preventDefault();event.stopPropagation();map.panBy(offset,{duration:0});return;}
  if (event.key === "Escape") {
    addMarkerKind = null; snapPickNodeId = null; map.getCanvas().style.cursor = "";
    if (draggingTrackHandle) {
      const feature = trackEditHandles.features?.find(item => item.properties?.pointId === draggingTrackHandle.pointId);
      if (feature) feature.geometry.coordinates = draggingTrackHandle.start;
      map.getSource("trackEditHandles")?.setData(trackEditHandles);
      draggingTrackHandle = null; map.dragPan.enable();
    }
    if (trackEdit) {
      const context = { ...trackEdit };
      trackEditRange = emptyCollection;
      map.getSource("trackEditRange")?.setData(emptyCollection);
      post("TrackEditPreviewCancelled", { projectId, revision, ...context });
    }
  }
}, true);
map.on("click", event => {
  if(offlineSelection.active)return;
  if (event.originalEvent?.target?.closest?.(".favorite-map-popup")) return;
  if (map.getLayer("favorites-symbol-layer")) {
    const favorite = favoriteInteraction.hit(event.point);
    if (favorite) {
      event.originalEvent.favoriteHit = true;
      favoriteInteraction.clicked(favorite);
      return;
    }
  }
  if (routePlanning) {
    post("RoutePlanningPointPicked", { projectId, revision,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng });
    return;
  }
  if (addMarkerKind) {
    post("MarkerAddLocationPicked", { projectId, revision, latitude: event.lngLat.lat,
      longitude: event.lngLat.lng, message: addMarkerKind });
    addMarkerKind = null;
    map.getCanvas().style.cursor = "";
    return;
  }
  const markerHit = map.queryRenderedFeatures(event.point, { layers: ["guide-nodes", "waypoints"] }).length > 0;
  if (markerHit || performance.now() - dragEndedAt < 180) return;
  const track = lineHit(event.point);
  if (!track?.properties?.trackId) return;
  if (event.originalEvent) event.originalEvent.__hgTrackHit = true;
  if (snapPickNodeId) {
    post("TrackSnapLocationPicked", { projectId, revision, guideNodeId: snapPickNodeId,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng });
    snapPickNodeId = null; map.getCanvas().style.cursor = ""; return;
  }
  // Alt 可选择分段所属整轨；Ctrl 负责加选/取消，二者可组合。
  const section = event.originalEvent?.altKey ? null : lineHit(event.point, ["split-sections-hit"], trackEdit ? trackPickProfile.editPixels : trackPickProfile.normalPixels, track.properties.trackId);
  // 普通模式保留攻略分段点选；编辑模式才将同一条线交给原始轨迹点编辑。
  const guide = !trackEdit && !event.originalEvent?.ctrlKey && !event.originalEvent?.altKey && map.queryRenderedFeatures(event.point, { layers: ["guide-segments-selected", "guide-segments"] })
    .find(f => f.properties?.trackId === track.properties.trackId && f.properties?.guideSegmentId);
  if (guide) {
    post("GuideSegmentClicked", { projectId, revision, guideSegmentId: guide.properties.guideSegmentId });
    return;
  }
  if (trackEdit) {
    post("TrackEditLineClicked", { projectId, revision, ...trackEdit, trackId: track.properties.trackId,
      geometryRevision: track.properties.geometryRevision, splitSectionId: section?.properties?.splitSectionId || null,
      ctrlKey: event.originalEvent?.ctrlKey === true,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng, zoom: map.getZoom() });
  } else if (section?.properties?.splitSectionId) {
    post("SplitSectionClicked", { projectId, revision, trackId: track.properties.trackId,
      splitSectionId: section.properties.splitSectionId, geometryRevision: track.properties.geometryRevision,
      ctrlKey: event.originalEvent?.ctrlKey === true });
  } else post("TrackClicked", { projectId, revision, trackId: track.properties.trackId,
    ctrlKey: event.originalEvent?.ctrlKey === true });
});
map.on("contextmenu", event => {
  if(offlineSelection.active)return;
  event.preventDefault();
  if (terrainNavigation.suppressContext()) return;
  if(routePlanning){post("RoutePlanningUndoRequested",{projectId,revision});return;}
  if (event.originalEvent?.target?.closest?.(".favorite-map-popup")) return;
  if (map.getLayer("favorites-symbol-layer")) {
    const favorite = favoriteInteraction.hit(event.point);
    if (favorite) {
      favoriteInteraction.closeHover(); favoriteInteraction.select(favorite.properties.favoriteId);
      post("FavoriteRightClicked", { revision: favoritesRevision, favoriteId: favorite.properties.favoriteId,
        latitude: event.lngLat.lat, longitude: event.lngLat.lng, screenX: event.point.x, screenY: event.point.y });
      return;
    }
  }
  const markerLayers = ["guide-node-media-badges", "guide-node-label-z15", "guide-node-labels", "guide-nodes-selected", "guide-nodes",
    "waypoint-media-badges", "marker-label-z15", "waypoint-labels", "waypoints-selected", "waypoints"];
  const marker = map.queryRenderedFeatures(event.point, { layers: markerLayers })
    .find(feature => feature.properties?.guideNodeId || feature.properties?.waypointId);
  if (marker) {
    post("MarkerRightClicked", { projectId, revision,
      guideNodeId: marker.properties?.guideNodeId || null,
      waypointId: marker.properties?.waypointId || null,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng,
      screenX: event.point.x, screenY: event.point.y });
    return;
  }
  const track = lineHit(event.point);
  const section = track && !event.originalEvent?.altKey && lineHit(event.point, ["split-sections-hit"], trackEdit ? trackPickProfile.editPixels : trackPickProfile.normalPixels, track.properties.trackId);
  if (section?.properties?.splitSectionId) {
    post("SplitSectionContextMenuRequested", { projectId, revision,
      trackId: section.properties.trackId, splitSectionId: section.properties.splitSectionId,
      geometryRevision: section.properties.geometryRevision,
      editSessionId: trackEdit?.editSessionId || null,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng,
      zoom: map.getZoom(),
      screenX: event.point.x, screenY: event.point.y });
    return;
  }
  if (track?.properties?.trackId) {
    post("TrackContextMenuRequested", { projectId, revision, trackId: track.properties.trackId,
      geometryRevision: track.properties.geometryRevision,
      editSessionId: trackEdit?.editSessionId || null,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng,
      zoom: map.getZoom(),
      screenX: event.point.x, screenY: event.point.y });
    return;
  }
  post("MapRightClicked", { projectId, revision, latitude: event.lngLat.lat, longitude: event.lngLat.lng, screenX: event.point.x, screenY: event.point.y });
});

function startMarkerDrag(layer, sourceId, property, resultType) {
  map.on("mousedown", layer, event => {
  if(offlineSelection.active)return;
    if (event.originalEvent?.button !== 0) return;
    if(map.getTerrain()){post("TerrainEditBlocked");return;}
    if (trackEdit) return;
    if (event.originalEvent?.button !== 0) return;
    if (favoriteInteraction.hit(event.point)) return;
    const id = event.features?.[0]?.properties?.[property];
    if (!id) return;
    event.preventDefault();
    draggingMarker = { sourceId, property, id, resultType, start: [event.lngLat.lng, event.lngLat.lat] };
    map.dragPan.disable();
  });
}
startMarkerDrag("waypoints", "waypoints", "waypointId", "WaypointDragEnded");
startMarkerDrag("guide-nodes", "guideNodes", "guideNodeId", "GuideNodeDragEnded");
map.on("mousedown", "track-edit-handles-hit", event => {
  if(offlineSelection.active)return;
  if (event.originalEvent?.button !== 0) return;
  if(map.getTerrain()){post("TerrainEditBlocked");return;}
  if (!trackEdit) return;
  if (favoriteInteraction.hit(event.point)) return;
  if (event.originalEvent?.button !== 0) return;
  const pointId = event.features?.[0]?.properties?.pointId;
  if (!pointId) return;
  event.preventDefault();
  draggingTrackHandle = { pointId, start: [...event.features[0].geometry.coordinates],
    context: { ...trackEdit } };
  map.dragPan.disable();
});
map.on("mouseup", event => {
  if(offlineSelection.active)return;
  if (draggingTrackHandle) {
    const { pointId, start, context } = draggingTrackHandle;
    draggingTrackHandle = null; map.dragPan.enable();
    if (!trackEdit || trackEdit.editSessionId !== context.editSessionId) return;
    if (Math.abs(start[0] - event.lngLat.lng) < 1e-10 &&
        Math.abs(start[1] - event.lngLat.lat) < 1e-10) return;
    dragEndedAt = performance.now();
    post("TrackPointDragEnded", { projectId, revision, ...context, trackPointId: pointId,
      latitude: event.lngLat.lat, longitude: event.lngLat.lng });
    return;
  }
  if (!draggingMarker) return;
  const { property, id, resultType, start } = draggingMarker;
  draggingMarker = null;
  map.dragPan.enable();
  if (Math.abs(start[0] - event.lngLat.lng) < 1e-10 && Math.abs(start[1] - event.lngLat.lat) < 1e-10) return;
  dragEndedAt = performance.now();
  post(resultType, { projectId, revision, [property]: id, latitude: event.lngLat.lat, longitude: event.lngLat.lng });
});

function showMediaPopup(feature, coordinates, id, kind) {
  const props = feature?.properties || {};
  const internalCount = Number(props.mediaInternalCount || 0);
  const externalCount = Number(props.mediaExternalCount || 0);
  if (internalCount + externalCount === 0) return;
  const root = document.createElement("div");
  const title = document.createElement("strong");
  title.textContent = props.name || "未命名标注";
  root.append(title);
  const summary = document.createElement("p");
  summary.textContent = `${internalCount} 张项目图片 · ${externalCount} 张网络图片`;
  root.append(summary);
  if (externalCount > 0) {
    const host = document.createElement("p");
    host.textContent = `网络图片：${props.mediaHost || "未知来源"}（点击后按需下载）`;
    root.append(host);
  }
  const thumbnail = document.createElement("div");
  thumbnail.textContent = internalCount > 0 ? "正在准备图片缩略图……" : "";
  root.append(thumbnail);
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = internalCount > 0 ? "查看图片" : "下载并查看";
  button.addEventListener("click", () => post("MediaOpenRequested", {
    projectId, revision, ...(kind === "guide" ? { guideNodeId: id } : { waypointId: id })
  }));
  root.append(button);
  const popup = new maplibregl.Popup({ closeButton: true }).setLngLat(coordinates).setDOMContent(root).addTo(map);
  activeMediaPopup = { popup, markerId: id, thumbnail };
  popup.on("close", () => { if (activeMediaPopup?.popup === popup) activeMediaPopup = null; });
  post("MediaThumbnailRequested", { projectId, revision,
    ...(kind === "guide" ? { guideNodeId: id } : { waypointId: id }) });
}
for (const layer of ["waypoints", "waypoints-selected", "waypoint-labels", "waypoint-media-badges"]) {
  map.on("click", layer, event => {
  if(offlineSelection.active)return;
    if (routePlanning || event.originalEvent?.favoriteHit) return;
    if (addMarkerKind) return;
    if (performance.now() - dragEndedAt < 180) return;
    const id = event.features?.[0]?.properties?.waypointId;
    if (id) {
      post("WaypointClicked", { projectId, revision, waypointId: id });
      showMediaPopup(event.features[0], event.lngLat, id, "waypoint");
    }
  });
}
for (const layer of ["guide-nodes", "guide-nodes-selected", "guide-node-labels", "guide-node-media-badges"]) {
  map.on("click", layer, event => {
  if(offlineSelection.active)return;
    if (routePlanning || event.originalEvent?.favoriteHit) return;
    if (addMarkerKind) return;
    if (performance.now() - dragEndedAt < 180) return;
    const id = event.features?.[0]?.properties?.guideNodeId;
    if (id) {
      post("GuideNodeClicked", { projectId, revision, guideNodeId: id });
      showMediaPopup(event.features[0], event.lngLat, id, "guide");
    }
  });
}
for (const layer of ["guide-segments", "guide-segments-selected"]) {
  map.on("click", layer, event => {
  if(offlineSelection.active)return;
    if (routePlanning || trackEdit || event.originalEvent?.favoriteHit || event.originalEvent?.__hgTrackHit) return;
    const id = event.features?.[0]?.properties?.guideSegmentId;
    if (id) post("GuideSegmentClicked", { projectId, revision, guideSegmentId: id });
  });
}

map.on("error", event => {
  if((event.sourceId||event.error?.sourceId)==="terrain-dem-source")return;
  const message = event?.error?.message || "地图渲染发生未知错误";
  if(event.sourceId==="osm-road-source"||event.error?.sourceId==="osm-road-source"||String(event.error?.url||"").includes("hikingguide-roads.local")){
    if(Number(event.error?.status)===410||/\b410\b/.test(message))return;
    if(Date.now()-(tileErrorTimes.get("osm-roads")||0)>10000){tileErrorTimes.set("osm-roads",Date.now());post("TileError",{projectId,revision,message:"OSM路网生成失败，请检查BRouter路由数据完整性、版本及缓存目录。可放大地图后重试；原始路由数据未改变。"});}return;
  }
  const terrain = String(event.sourceId || event?.error?.sourceId || "").startsWith("dem-") || String(event?.error?.url || "").includes("hikingguide-terrain.local");
  if (terrain) {
    if (Number(event?.error?.status) === 410 || /\b410\b/.test(message)) return;
    if (Date.now() - (tileErrorTimes.get("terrain") || 0) < 10000) return;
    tileErrorTimes.set("terrain", Date.now());
    post("TileError", {projectId, revision, message:"DEM等高线或阴影生成失败，请检查DEM挂载、单位、文件完整性或缓存目录；原轨迹数据未改变。"});
    return;
  }
  const tileLike = /tile|source|http|network/i.test(message);
  const slot = rasterSlots.find(s => s.source === event.sourceId || s.source === event?.error?.sourceId);
  const key = slot?.slot || "unknown";
  if (tileLike || slot) {
    if (Date.now() - (tileErrorTimes.get(key) || 0) < 10000) return;
    tileErrorTimes.set(key, Date.now());
    const label = slot?.label || "地图图源";
    post("TileError", { projectId, revision, message: `${label}“${slot?.value?.name || "未知图源"}”部分瓦片加载失败，请检查网络或图源设置。` });
  } else post("MapError", { projectId, revision, message: "地图渲染错误，详细敏感信息已省略。" });
});

function accepts(command) {
  if (["Initialize","ClearProject","SetMapSource","SetMapLayerStack","BeginOfflineRegion","CancelOfflineRegion","ClearOfflineRegion","SetOfflineTerrain","SetOsmRoads","SetTerrainState","SetTerrainCamera","TerrainHealthResult","PauseTerrain"].includes(command.type)) return true;
  return command.projectId === projectId && Number(command.revision || 0) >= revision;
}

function setGeoJson(sourceId, geoJson, command) {
  if (!accepts(command)) return;
  const started = performance.now();
  if(sourceId === "tracks") { trackScene ??= new TrackScene(map); trackScene.set(geoJson || emptyCollection); }
  else map.getSource(sourceId)?.setData(geoJson || emptyCollection);
  if (sourceId === "tracks") lastTracks = geoJson || emptyCollection;
  if (sourceId === "waypoints") lastWaypoints = geoJson || emptyCollection;
  if (sourceId === "guideNodes") lastGuideNodes = geoJson || emptyCollection;
  if (sourceId === "guideSegments") lastGuideSegments = geoJson || emptyCollection;
  map.once("idle", () => post("RenderMetrics", {
    projectId, revision,
    message: sourceId,
    durationMs: performance.now() - started
  }));
}

window.hikingGuideTrackSceneTest={
  camera:(zoom,bearing=0,pitch=0,center=null)=>map.jumpTo({zoom,bearing,pitch,...(center?{center}:{})}),
  inspect:()=>({zoom:map.getZoom(),center:map.getCenter(),pitch:map.getPitch(),terrain:!!map.getTerrain(),
    totalChunks:trackScene?.features.length||0,visibleChunks:map.getStyle().sources.tracks?.data?.features?.length||0,
    vertices:(trackScene?.features||[]).reduce((n,f)=>n+f.geometry.coordinates.reduce((a,s)=>a+s.length,0),0),
    sources:["tracks-active","guide-segments","guide-segments-selected","split-sections-selected","track-direction-arrows"].map(id=>map.getLayer(id)?.source),
    arrowType:map.getLayer("track-direction-arrows")?.type,arrowImage:trackArrowImages.every(id=>map.hasImage(id)),arrowPattern:map.getPaintProperty("track-direction-arrows","line-pattern"),arrowTones:[...new Set((trackScene?.features||[]).map(f=>f.properties.arrowTone))],tolerance:map.getStyle().sources.tracks?.tolerance,
    loaded:map.isStyleLoaded()})
};

function setMapSource(payload) {
  setMapLayerStack({ settingsRevision: mapSettingsRevision + 1, base: { ...payload, sourceId: payload.id, templates: payload.tiles, visible: true }, overlay1: null, overlay2: null });
}

const rasterSlots = [
  { slot: "base", label: "底图", source: "raster-base-source", layer: "raster-base-layer" },
  { slot: "overlay1", label: "叠加层1", source: "raster-overlay-1-source", layer: "raster-overlay-1-layer" },
  { slot: "overlay2", label: "叠加层2", source: "raster-overlay-2-source", layer: "raster-overlay-2-layer" }
];
let mapSettingsRevision = -1;
let rasterAttribution = null;
let combinedAttribution = [];
const tileErrorTimes = new Map();
function ensureRasterLayerOrder() {
  const business = map.getStyle().layers.find(l => l.type !== "background" && l.type !== "raster" && !l.id.startsWith("osm-road-") && !l.id.startsWith("dem-"))?.id;
  for (const slot of rasterSlots) if (map.getLayer(slot.layer)) map.moveLayer(slot.layer, business);
  for (const id of ["dem-hillshade", "dem-contours-low", "dem-contours-high"]) if (map.getLayer(id)) map.moveLayer(id, business);
  for (const id of ["guide-segments", "guide-segments-selected"]) if (map.getLayer(id) && map.getLayer("waypoints")) map.moveLayer(id, "waypoints");
  osmRoads.reorder();
  if (map.getLayer("elevation-hover")) map.moveLayer("elevation-hover");
}
let terrainSignature = "";
let lastTerrainViewport = 0;
function postTerrainViewport() {
  const b=map.getBounds();
  post("TerrainViewport",{west:b.getWest(),south:b.getSouth(),east:b.getEast(),north:b.getNorth()});
}
map.on("move",()=>{if(performance.now()-lastTerrainViewport>120){lastTerrainViewport=performance.now();postTerrainViewport();}});
function setOfflineTerrain(value) {
  requestedOfflineTerrain=value;
  // 初次启用或切换山体时不启动派生请求；山体完成后由优先级回调恢复用户原设置。
  if(terrain3D.state.enabled&&!terrain3D.effective)value={...value,contours:false,hillshade:false};
  contourLabels.configure(value);
  postTerrainViewport();
  const signature = JSON.stringify(value);
  if (signature === terrainSignature && map.getSource("dem-contours-low")) return;
  terrainSignature = signature;
  const before = map.getStyle().layers.find(l => l.type !== "background" && l.type !== "raster")?.id;
  const definitions = [
    { id: "dem-hillshade", kind: "hillshade", visible: value.hillshade, min: 13, max: 22, opacity: value.opacity ?? .2 },
    { id: "dem-contours-low", kind: "contours", visible: value.contours, min: 13, max: 14, end: 15, opacity: value.contourOpacity ?? 1 },
    { id: "dem-contours-high", kind: "contours", visible: value.contours, min: 15, max: 22, opacity: value.contourOpacity ?? 1 }
  ];
  for (const d of definitions) {
    const signature=d.kind==="hillshade"?(value.hillshadeSignature||value.signature):value.signature;
    const template=`https://hikingguide-terrain.local/${signature}/${d.kind}/{z}/{x}/{y}.png`;
    if(map.getSource(d.id)?.tiles?.[0]!==template){if(map.getLayer(d.id))map.removeLayer(d.id);if(map.getSource(d.id))map.removeSource(d.id);}
    if(!value.signature)continue;
    if(!map.getSource(d.id)&&d.visible){
      map.addSource(d.id,{type:"raster",tiles:[template],tileSize:256,minzoom:d.min,maxzoom:d.max});
      map.addLayer({id:d.id,type:"raster",source:d.id,minzoom:d.min,...(d.end?{maxzoom:d.end}:{}),paint:{"raster-opacity":d.opacity,"raster-fade-duration":0}},before);
    }
    if(map.getLayer(d.id)){map.setLayoutProperty(d.id,"visibility",d.visible?"visible":"none");map.setPaintProperty(d.id,"raster-opacity",d.opacity);}
  }
  ensureRasterLayerOrder();
}
let continuitySources=null;
window.hikingGuideLayerContinuityTest={
  remember:()=>{continuitySources=new Map(map.getStyle().layers.filter(l=>l.id.startsWith("raster-")||l.id.startsWith("dem-")||l.id.startsWith("osm-road-")).filter(l=>l.source).map(l=>[l.source,map.getSource(l.source)]));},
  inspect:()=>({moving:map.isMoving()||!!terrainNavigation.gesture?.active,zoom:map.getZoom(),terrain:!!map.getTerrain(),effective:terrain3D.effective,
    sameSources:!continuitySources||[...continuitySources].every(([id,source])=>map.getSource(id)===source),
    layers:map.getStyle().layers.filter(l=>l.id.startsWith("raster-overlay-")||l.id.startsWith("dem-")||l.id.startsWith("osm-road-")).map(l=>({id:l.id,visible:l.layout?.visibility!=="none"})),roads:osmRoads.inspect().features})
};
window.hikingGuideTerrainTest = {
  camera: (zoom=15) => map.jumpTo({center:[113.32,24.42],zoom,bearing:0,pitch:0}),
  inspect: () => ({ signature: terrainSignature, labelOpacity:map.getLayer("dem-contour-labels")?map.getPaintProperty("dem-contour-labels","icon-opacity"):null, layers: ["dem-hillshade", "dem-contours-low", "dem-contours-high"].map(id => map.getLayer(id) ? {id, min: map.getLayer(id).minzoom, max: map.getLayer(id).maxzoom, visible:map.getLayoutProperty(id,"visibility")!=="none", opacity:map.getPaintProperty(id,"raster-opacity"), loaded:map.isSourceLoaded(id)} : null) })
};
function setMapLayerStack(payload) {
  const incoming = Number(payload.settingsRevision);
  if (!Number.isFinite(incoming) || incoming < mapSettingsRevision) return;
  mapSettingsRevision = incoming;
  if (startupMapLoad.waiting) {
    const base = payload.base;
    startupMapLoad.start(!!base?.sourceId && base.visible && base.opacity > 0 && !!base.templates?.length);
    if (startupMapLoad.waiting) startupMapLoad.run("overlays", () => setMapLayerStack(payload));
  }
  for (const slot of rasterSlots) {
    if (slot.slot !== "base" && startupMapLoad.waiting) continue;
    const value = payload[slot.slot];
    const signature = value?.sourceId ? JSON.stringify([value.sourceId, value.templates, value.minZoom, value.maxZoom]) : null;
    if (signature !== slot.signature || (signature && !map.getSource(slot.source))) {
      if (map.getLayer(slot.layer)) map.removeLayer(slot.layer);
      if (map.getSource(slot.source)) map.removeSource(slot.source);
      if (signature && value.templates?.length) {
        map.addSource(slot.source, { type: "raster", tiles: value.templates, tileSize: 256, minzoom: value.minZoom ?? 0, maxzoom: value.maxZoom ?? 19 });
        map.addLayer({ id: slot.layer, type: "raster", source: slot.source, layout: { visibility: "none" }, paint: { "raster-opacity": value.opacity } });
      }
      slot.signature = signature;
    }
    slot.value = value;
    if (map.getLayer(slot.layer)) {
      map.setPaintProperty(slot.layer, "raster-opacity", value.opacity);
      map.setLayoutProperty(slot.layer, "visibility", value.visible ? "visible" : "none");
    }
  }
  ensureRasterLayerOrder();
  const seen = new Set();
  combinedAttribution = rasterSlots.filter(s => s.value?.sourceId && s.value.visible).map(s => (s.value.attribution || "").trim()).filter(a => a && !seen.has(a) && seen.add(a));
  if (rasterAttribution) map.removeControl(rasterAttribution);
  const escape = text => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  rasterAttribution = new maplibregl.AttributionControl({ customAttribution: combinedAttribution.map(escape), compact: true });
  map.addControl(rasterAttribution);
}
window.hikingGuideRasterTest = {
  inspect: () => ({ revision: mapSettingsRevision, slots: rasterSlots.map(s => ({ slot: s.slot, sourceId: s.value?.sourceId || null, templates: map.getSource(s.source)?.tiles || [], loaded: map.getSource(s.source) ? map.isSourceLoaded(s.source) : false, opacity: map.getLayer(s.layer) ? map.getPaintProperty(s.layer, "raster-opacity") : null, visible: map.getLayer(s.layer) ? map.getLayoutProperty(s.layer, "visibility") : "none" })), layers: map.getStyle().layers.map(l => ({ id: l.id, type: l.type })), attribution: combinedAttribution, center: map.getCenter(), zoom: map.getZoom(), bearing: map.getBearing(), pitch: map.getPitch(), tracks: lastTracks.features.length, nodes: lastGuideNodes.features.length, waypoints: lastWaypoints.features.length, favorites: lastFavorites.features.length }),
  camera: (zoom = 12) => map.jumpTo({ center: [113.3, 23.1], zoom, bearing: 17, pitch: 20 }),
  sourceInstance: slot => map.getSource(rasterSlots.find(s => s.slot === slot).source),
  failOverlay: () => map.fire("error", {sourceId:"raster-overlay-1-source", error: new Error("simulated tile failure")})
};

function setSelection(payload) {
  selected = payload || { kind: "none", objectId: null };
  const id = selected.objectId || "__none__";
  map.setFilter("tracks-selected", ["in", ["get", "trackId"], ["literal", selectedTrackIds.length ? selectedTrackIds : selected.kind === "track" ? [id] : []]]);
  map.setFilter("waypoints-selected", ["==", ["get", "waypointId"], selected.kind === "waypoint" ? id : "__none__"]);
  map.setFilter("guide-nodes-selected", ["==", ["get", "guideNodeId"], selected.kind === "guidenode" ? id : "__none__"]);
  map.setFilter("guide-segments-selected", ["==", ["get", "guideSegmentId"], selected.kind === "guidesegment" ? id : "__none__"]);
  trackScene?.setArrowSelection({trackIds:selectedTrackIds.length?selectedTrackIds:selected.kind==="track"?[id]:[],sectionIds:selectedSplitSectionIds,guideSegmentId:selected.kind==="guidesegment"?id:null});
}

function clearTrackEdit() {
  trackEdit = null; draggingTrackHandle = null; map.dragPan.enable();
  trackEditHandles = emptyCollection;
  trackEditRange = emptyCollection;
  map.getSource("trackEditHandles")?.setData(emptyCollection);
  map.getSource("trackEditRange")?.setData(emptyCollection);
}

listen(async command => {
  try {
    const payload = command.payload || {};
    switch (command.type) {
      case "SetFavorites": {
        const incoming = Number(command.revision || 0);
        if (incoming < favoritesRevision) break;
        favoritesRevision = incoming;
        if (!favoriteImageReady) favoriteImageReady = loadFavoriteSymbol(payload.style);
        await favoriteImageReady;
        if (incoming !== favoritesRevision) break;
        favoriteInteraction.thresholdX = payload.dragThresholdX || 4;
        favoriteInteraction.thresholdY = payload.dragThresholdY || 4;
        favoriteInteraction.applyData(payload.geoJson || emptyCollection, payload.visible, payload.style, payload.selectedFavoriteId, payload.selectionRevision);
        break;
      }
      case "SetSelectedFavorite": favoriteInteraction.select(payload.favoriteId, payload.selectionRevision); break;
      case "ShowFavoriteHover": favoriteInteraction.showHover(payload, Number(command.revision)); break;
      case "CloseFavoritePopup": favoriteInteraction.closeHover(); break;
      case "CancelFavoriteDrag": favoriteInteraction.cancelDrag(); break;
      case "EndFavoriteMove": favoriteInteraction.endMove(payload); break;
      case "FlyToFavorite":
        if (Number(command.revision || 0) === favoritesRevision) {
          suppressNextViewPersistence = true;
          map.flyTo({ center: [payload.longitude, payload.latitude], zoom: Math.max(map.getZoom(), payload.minimumZoom), duration: 400 });
        }
        break;
      case "RunFavoritesSmoke":
        await favoriteImageReady;
        post("FavoritesSmokeCompleted", { revision: favoritesRevision, message: JSON.stringify({
          layerPresent: !!map.getLayer("favorites-symbol-layer"), imagePresent: map.hasImage("favorite-purple-star"),
          visible: map.getLayoutProperty("favorites-symbol-layer", "visibility") !== "none",
          featureCount: lastFavorites.features.length,
          sourceFavoriteCount: new Set(map.querySourceFeatures("favorites").map(f => f.properties.favoriteId)).size,
          latitude: map.getCenter().lat, longitude: map.getCenter().lng, zoom: map.getZoom() }) });
        break;
      case "Initialize":
        clearTrackEdit();
        projectId = command.projectId;
        revision = Number(command.revision || 0);
        break;
      case "ClearProject":
        routePlanning = false; setPlanningCursor();
        map.getSource("routePreviewLegs")?.setData(emptyCollection);
        map.getSource("routePreviewPoints")?.setData(emptyCollection);
        clearTrackEdit();
        projectId = null; revision = 0;
        selectedTrackIds = []; selectedSplitSectionIds = [];
        activeMediaPopup?.popup.remove(); activeMediaPopup = null;
        map.getSource("tracks")?.setData(emptyCollection);
        trackScene?.set(emptyCollection); lastTracks = emptyCollection; lastWaypoints = emptyCollection; lastGuideNodes = emptyCollection; lastGuideSegments = emptyCollection;
        map.getSource("splitSections")?.setData(emptyCollection);
        lastSplitSections = emptyCollection;
        map.getSource("directionArrows")?.setData(emptyCollection);
        map.getSource("waypoints")?.setData(emptyCollection);
        map.getSource("guideNodes")?.setData(emptyCollection);
        map.getSource("guideSegments")?.setData(emptyCollection);
        map.getSource("elevationHover")?.setData(emptyCollection);
        setSelection({ kind: "none", objectId: null });
        break;
      case "SetMapSource": setMapSource(payload); break;
      case "SetMapLayerStack": setMapLayerStack(payload); break;
      case "SetOfflineTerrain": startupMapLoad.run("derived", () => setOfflineTerrain(payload)); break;
      case "BeginOfflineRegion": offlineSelection.begin(); break;
      case "CancelOfflineRegion": offlineSelection.cancel(); break;
      case "ClearOfflineRegion": offlineSelection.clear(); break;
      case "SetOsmRoads": startupMapLoad.run("roads", () => { osmRoads.configure(payload); ensureRasterLayerOrder(); }); break;
      case "SetTerrainState":
        requestedTerrainState=payload;
        if(startupMapLoad.waiting&&payload.enabled&&payload.camera&&!offlineSelection.active){
          suppressNextViewPersistence=true;
          map.jumpTo({center:[payload.camera.longitude,payload.camera.latitude],zoom:payload.camera.zoom,bearing:payload.camera.bearing,pitch:0});
        }
        startupMapLoad.run("terrain",()=>{if(offlineSelection.active)offlineTerrainBefore=requestedTerrainState;else terrain3D.update(requestedTerrainState);});
        break;
      case "TerrainHealthResult": terrain3D.health(payload); break;
      case "SetTerrainCamera": terrain3D.resetCamera(payload.northUp,payload.pitch);break;
      case "PauseTerrain":
        requestedTerrainState={...(requestedTerrainState||terrain3D.state),enabled:false};
        startupMapLoad.run("terrain",()=>terrain3D.update(requestedTerrainState));break;
      case "SetTracks": setGeoJson("tracks", payload.geoJson, command); break;
      case "SetSplitSections":
        if (accepts(command)) lastSplitSections = payload.geoJson || emptyCollection;
        setGeoJson("splitSections", payload.geoJson, command);
        break;
      case "SetTrackDirectionArrows": setGeoJson("directionArrows", payload.geoJson, command); break;
      case "SetSplitSectionSelection":
        if (accepts(command)) {
          selectedTrackIds = payload.trackIds || [];
          selectedSplitSectionIds = payload.ids || [];
          map.setFilter("split-sections-selected", ["in", ["get", "splitSectionId"], ["literal", payload.ids || []]]);
          setSelection(selected);
        }
        break;
      case "SetWaypoints": setGeoJson("waypoints", payload.geoJson, command); break;
      case "SetGuideNodes": setGeoJson("guideNodes", payload.geoJson, command); break;
      case "SetGuideSegments": setGeoJson("guideSegments", payload.geoJson, command); break;
      case "SetSelection": if (accepts(command)) setSelection(payload); break;
      case "SetElevationHover":
        if (accepts(command)) map.getSource("elevationHover")?.setData(
          Number.isFinite(payload.latitude) && Number.isFinite(payload.longitude)
            ? { type: "FeatureCollection", features: [{ type: "Feature", geometry: { type: "Point", coordinates: [payload.longitude, payload.latitude] }, properties: {} }] }
            : emptyCollection);
        break;
      case "PanMapKey": window.hikingGuidePanKey(payload.code); break;
      case "BeginRoutePlanning":
        if (accepts(command)) { routePlanning = true; setPlanningCursor(); map.getCanvas().style.cursor = "crosshair"; }
        break;
      case "EndRoutePlanning":
        if (accepts(command)) {
          routePlanning = false; setPlanningCursor(); map.getCanvas().style.cursor = "";
          map.getSource("routePreviewLegs")?.setData(emptyCollection);
          map.getSource("routePreviewPoints")?.setData(emptyCollection);
        }
        break;
      case "SetRoutePreview":
        if (accepts(command)) {
          map.getSource("routePreviewLegs")?.setData(payload.legs || emptyCollection);
          map.getSource("routePreviewPoints")?.setData(payload.points || emptyCollection);
        }
        break;
      case "BeginTrackEdit":
        if (accepts(command) && Number(payload.editContextVersion ?? 0) >= trackEditContextVersion) {
          trackEditContextVersion = Number(payload.editContextVersion ?? 0);
          clearTrackEdit();
          trackEdit = { trackId: payload.trackId,
            geometryRevision: payload.geometryRevision, editSessionId: payload.editSessionId };
        }
        break;
      case "EndTrackEdit": if (accepts(command) && Number(payload.editContextVersion ?? 0) >= trackEditContextVersion) { trackEditContextVersion = Number(payload.editContextVersion ?? 0); clearTrackEdit(); } break;
      case "SetTrackEditHandles":
        if (accepts(command) && trackEdit && payload.trackId === trackEdit.trackId &&
            payload.geometryRevision === trackEdit.geometryRevision &&
            payload.editSessionId === trackEdit.editSessionId) {
          trackEditHandles = { type: "FeatureCollection", features: (payload.handles || []).map(item => ({
            type: "Feature", geometry: { type: "Point", coordinates: [item.longitude, item.latitude] },
            properties: { pointId: item.pointId, selected: item.selected === true }
          })) };
          map.getSource("trackEditHandles")?.setData(trackEditHandles);
        }
        break;
      case "PreviewTrackRange":
        if (accepts(command) && trackEdit && payload.trackId === trackEdit.trackId &&
            payload.geometryRevision === trackEdit.geometryRevision &&
            payload.editSessionId === trackEdit.editSessionId) {
          trackEditRange = payload.geoJson || emptyCollection;
          map.getSource("trackEditRange")?.setData(trackEditRange);
        }
        break;
      case "RunP6TrackEditSmoke":
        if (accepts(command) && trackEdit) {
          const context = { ...trackEdit };
          post("TrackEditLineClicked", { projectId, revision, ...context,
            latitude: payload.clickLatitude, longitude: payload.clickLongitude, zoom: map.getZoom() });
          setTimeout(() => {
            if (trackEdit?.editSessionId !== context.editSessionId) return;
            post("TrackPointDragEnded", { projectId, revision, ...context,
              trackPointId: payload.pointId, latitude: payload.newLatitude,
              longitude: payload.newLongitude });
          }, 750);
        }
        break;
      case "RunP61SplitSectionSmoke":
        if (accepts(command)) {
          const feature = lastSplitSections.features?.find(item =>
            item.properties?.splitSectionId === payload.splitSectionId);
          const point = feature?.geometry?.coordinates?.[0]?.[0];
          if (feature && point) {
            post(payload.contextMenu ? "SplitSectionContextMenuRequested" : "SplitSectionClicked",
              { projectId, revision, trackId: feature.properties.trackId,
                splitSectionId: feature.properties.splitSectionId,
                geometryRevision: feature.properties.geometryRevision,
                ctrlKey: payload.ctrlKey === true,
                latitude: point[1], longitude: point[0], screenX: 100, screenY: 100 });
          }
        }
        break;
      case "RunP62TrackClickSmoke":
        if (accepts(command) && payload.trackId)
          post("TrackClicked", { projectId, revision, trackId: payload.trackId });
        break;
      case "RunP62RightClickSmoke":
        if (accepts(command) && trackEdit?.trackId === payload.trackId)
          post("SplitSectionContextMenuRequested", { projectId, revision,
            trackId: payload.trackId, splitSectionId: payload.splitSectionId,
            geometryRevision: trackEdit.geometryRevision, editSessionId: trackEdit.editSessionId,
            latitude: payload.latitude, longitude: payload.longitude, zoom: map.getZoom(),
            screenX: 100, screenY: 100 });
        break;
      case "RunP7PlanningClickSmoke":
        if (accepts(command) && routePlanning)
          post("RoutePlanningPointPicked", { projectId, revision,
            latitude: payload.latitude, longitude: payload.longitude });
        break;
      case "SetMarkerPopupMedia":
        if (accepts(command) && activeMediaPopup?.markerId === payload.markerId &&
            /^https:\/\/hikingguide-media\.local\/media\/[a-f0-9]{32}\.png$/.test(payload.thumbnailUrl || "")) {
          const preview = document.createElement("img");
          preview.src = payload.thumbnailUrl;
          preview.alt = "图片缩略图";
          preview.style.cssText = "max-width:160px;max-height:120px;object-fit:contain";
          preview.addEventListener("load", () => post("MediaThumbnailReady", { projectId, revision, waypointId: payload.markerId }));
          preview.addEventListener("error", () => post("MapError", { projectId, revision, message: "地图图片缩略图资源加载失败" }));
          activeMediaPopup.thumbnail.replaceChildren(preview);
        }
        break;
      case "SetMarkerPopupError":
        if (accepts(command) && activeMediaPopup?.markerId === payload.markerId)
          activeMediaPopup.thumbnail.textContent = payload.message || "图片缩略图不可用";
        break;
      case "FitBounds":
        if (accepts(command)) map.fitBounds([[payload.west, payload.south], [payload.east, payload.north]], { padding: payload.padding ?? 48, maxZoom: 18, duration: 350 });
        break;
      case "SetView":
        if (accepts(command)) {
          const terrainRequested=startupMapLoad.waiting?requestedTerrainState?.enabled:terrain3D.state.enabled;
          if(terrainRequested&&!payload.restoreTerrainCamera&&!payload.terrainUserIntent)break;
          suppressNextViewPersistence = payload.suppressPersistence === true;
          const options = { duration: 250 };
          if (Number.isFinite(payload.longitude) && Number.isFinite(payload.latitude)) options.center = [payload.longitude, payload.latitude];
          if (Number.isFinite(payload.zoom)) options.zoom = payload.zoom;
          if (Number.isFinite(payload.bearing)) options.bearing = payload.bearing;
          if (Number.isFinite(payload.pitch)) options.pitch = terrainRequested&&!payload.restoreTerrainCamera?map.getPitch():payload.pitch;
          if(startupMapLoad.waiting)map.jumpTo(options);else map.easeTo(options);
        }
        break;
      case "FlyToWaypoint":
        if (accepts(command)) map.flyTo({ center: [payload.longitude, payload.latitude], zoom: Math.max(map.getZoom(), payload.minimumZoom ?? 16), duration: 450 });
        break;
      case "RunInteractionSmoke":
        if (accepts(command)) runInteractionSmoke();
        break;
      case "RunP5InteractionSmoke":
        if (accepts(command)) runP5InteractionSmoke();
        break;
      case "RunP51MediaSmoke":
        if (accepts(command)) {
          const feature = (lastWaypoints.features || []).find(item => Number(item.properties?.mediaInternalCount || 0) > 0);
          if (feature) showMediaPopup(feature, feature.geometry.coordinates, feature.properties.waypointId, "waypoint");
        }
        break;
      case "BeginSnapLocationPick":
        if (accepts(command)) {
          addMarkerKind = null;
          snapPickNodeId = payload.guideNodeId || null;
          map.getCanvas().style.cursor = snapPickNodeId ? "crosshair" : "";
        }
        break;
      case "BeginMarkerAdd":
        if (accepts(command)) {
          snapPickNodeId = null;
          addMarkerKind = payload.guideNode === true ? "guide" : "waypoint";
          map.getCanvas().style.cursor = "crosshair";
        }
        break;
      case "RunP5MarkerAddSmoke":
        if (accepts(command) && addMarkerKind) {
          post("MarkerAddLocationPicked", { projectId, revision, latitude: payload.latitude,
            longitude: payload.longitude, message: addMarkerKind });
          addMarkerKind = null;
          map.getCanvas().style.cursor = "";
        }
        break;
      case "RunP5WaypointEditSmoke":
        if (accepts(command)) {
          const feature = lastWaypoints.features?.[0];
          if (feature) post("WaypointDragEnded", { projectId, revision, waypointId: feature.properties.waypointId,
            latitude: feature.geometry.coordinates[1] + 0.00001, longitude: feature.geometry.coordinates[0] });
        }
        break;
      case "RunP5GuideEditSmoke":
        if (accepts(command)) {
          const feature = lastGuideNodes.features?.[0];
          if (feature) post("GuideNodeDragEnded", { projectId, revision, guideNodeId: feature.properties.guideNodeId,
            latitude: feature.geometry.coordinates[1] + 0.00001, longitude: feature.geometry.coordinates[0] });
        }
        break;
    }
  } catch (error) {
    post("MapError", { projectId, revision, message: error?.message || "执行地图命令失败" });
  }
});

function runInteractionSmoke() {
  const trackFeatures = lastTracks.features || [];
  const waypointFeatures = lastWaypoints.features || [];
  const coordinates = [];
  for (const feature of trackFeatures) for (const segment of feature.geometry?.coordinates || []) coordinates.push(...segment);
  if (coordinates.length) {
    let west = Infinity, south = Infinity, east = -Infinity, north = -Infinity;
    for (const [longitude, latitude] of coordinates) {
      west = Math.min(west, longitude); south = Math.min(south, latitude);
      east = Math.max(east, longitude); north = Math.max(north, latitude);
    }
    map.fitBounds([[west, south], [east, north]], { padding: 48, duration: 0 });
  }
  const center = map.getCenter();
  for (let index = 0; index < 20; index++) {
    map.jumpTo({ center: [center.lng + index * 0.00005, center.lat - index * 0.00003], zoom: 13 + (index % 2) });
  }
  const trackId = trackFeatures[0]?.properties?.trackId;
  for (let index = 0; index < 10 && trackId; index++) post("TrackClicked", { projectId, revision, trackId });
  for (let index = 0; index < Math.min(10, waypointFeatures.length); index++) {
    const waypointId = waypointFeatures[index]?.properties?.waypointId;
    if (waypointId) post("WaypointClicked", { projectId, revision, waypointId });
  }
  post("InteractionSmokeCompleted", {
    projectId, revision,
    message: `缩放/平移20次，轨迹选择${trackId ? 10 : 0}次，标注选择${Math.min(10, waypointFeatures.length)}次`
  });
}

function runP5InteractionSmoke() {
  const nodes = lastGuideNodes.features || [];
  const segments = lastGuideSegments.features || [];
  for (const feature of nodes) {
    const guideNodeId = feature.properties?.guideNodeId;
    if (guideNodeId) post("GuideNodeClicked", { projectId, revision, guideNodeId });
  }
  for (const feature of segments) {
    const guideSegmentId = feature.properties?.guideSegmentId;
    if (guideSegmentId) post("GuideSegmentClicked", { projectId, revision, guideSegmentId });
  }
  post("InteractionSmokeCompleted", { projectId, revision, message: `攻略节点选择${nodes.length}次，攻略分段选择${segments.length}次` });
}



