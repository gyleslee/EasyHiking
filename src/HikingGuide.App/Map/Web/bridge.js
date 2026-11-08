export const protocolVersion = 1;
let terrainEditingLocked=false;
export function setTerrainEditingLock(value){terrainEditingLocked=value;}
const coordinateEdits=new Set(["FavoriteDragStarted","FavoriteDragCompleted","WaypointDragEnded","GuideNodeDragEnded","TrackPointDragEnded","TrackEditLineClicked","TrackSnapLocationPicked","MarkerAddLocationPicked","RoutePlanningPointPicked"]);

export function post(type, values = {}) {
  if (!window.chrome?.webview) return;
  if(terrainEditingLocked&&coordinateEdits.has(type)) {
    window.chrome.webview.postMessage({protocolVersion,type:"TerrainEditBlocked",message:"当前为3D地形模式，该编辑操作需要精确地图坐标，请先切换到2D地图。"});return;
  }
  window.chrome.webview.postMessage({ protocolVersion, type, ...values });
}

export function listen(handler) {
  if (!window.chrome?.webview) return;
  window.chrome.webview.addEventListener("message", event => {
    const command = event.data;
    if (!command || command.protocolVersion !== protocolVersion || typeof command.type !== "string") return;
    handler(command);
  });
}
