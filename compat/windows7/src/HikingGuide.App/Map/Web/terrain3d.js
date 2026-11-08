// 地平线天蓝、天顶稍浅；关闭地面雾化，避免遮淡地图及等高线。
export const terrainSky = {"horizon-color":"#87CEEB","sky-color":"#B5E2F5","sky-horizon-blend":0.8,
  "fog-color":"#87CEEB","fog-ground-blend":1,"horizon-fog-blend":0,"atmosphere-blend":0};
// Terrain仅定义几何表面；现有raster/line/circle/symbol层由MapLibre原生贴合。
export class Terrain3DController {
  constructor(map, post, setEditingLock, programmatic, workChanged=()=>{}) {
    this.map=map;this.post=post;this.setEditingLock=setEditingLock;this.programmatic=programmatic;this.workChanged=workChanged;
    this.state={enabled:false,terrainRevision:-1};this.effective=false;this.identity=null;
    this.originalMaxPitch=map.getMaxPitch();
    map.on("sourcedata",e=>{if(e.sourceId==="terrain-dem-source"){this.ready();this.scheduleWork();}});
    map.on("idle",()=>this.ready());
    map.on("movestart",()=>{if(this.state.enabled){this.pendingSince=performance.now();this.reportWork(false);}});
    map.on("moveend",()=>this.scheduleWork());
    map.on("remove",()=>{clearInterval(this.healthTimer);clearTimeout(this.workTimer);});
    map.on("zoomend",()=>{if(this.state.enabled&&map.getZoom()<this.state.maxNativeZoom+1)this.fail(`当前缩放范围过大，请放大至Z${this.state.maxNativeZoom+1}或以上再启用3D地形。`);});
    map.on("error",e=>{
      if((e.sourceId||e.error?.sourceId)!=="terrain-dem-source")return;
      if(Number(e.error?.status)===410||/\b410\b/.test(e.error?.message||""))return;
      this.fail("当前视野的3D地形DEM无法完整读取，地图已切回2D；请检查DEM连接、覆盖范围或缺测数据。");
    });
    this.healthTimer=setInterval(()=>{
      if(!this.state.enabled)return;
      this.checkProgress();if(!this.state.enabled)return;
      const center=map.getCenter();
      post("TerrainHealthCheck",{requestId:this.state.terrainRevision,latitude:center.lat,longitude:center.lng});
    },2500);
  }
  camera() {
    try{const c=this.map.getCenter(),zoom=this.map.getZoom(),bearing=this.map.getBearing(),pitch=this.map.getPitch();
      if([c.lng,c.lat,zoom,bearing,pitch].every(Number.isFinite))this.lastCamera={center:[c.lng,c.lat],zoom,bearing,pitch};}catch{}
    return this.lastCamera||{center:[113.3,23.1],zoom:14,bearing:0,pitch:0};
  }
  handleRenderError(message,stack="") {
    if(!this.state.enabled)return false;
    if(!/null|undefined|terrain|framebuffer|webgl/i.test(String(message)))return false;
    if(this.recoveryPending)return true;
    this.recoveryPending=true;this.renderFaults=(this.renderFaults||0)+1;
    const details=String(stack||message).replace(/https?:\/\/(?!hikingguide\.local)[^\s)]+/g,"[外部地址已省略]").replace(/\?[^\s)]*/g,"").slice(0,4000);
    this.post("TerrainRenderDiagnostic",{message:details});
    queueMicrotask(()=>{try{
      // 绘制故障不等于DEM缺测；保持3D开关、视角与图源，重建地形绘制对象。
      const terrain=this.map.getTerrain(),camera=this.camera();
      if(terrain){this.map.setTerrain(null);this.map.setTerrain(terrain);this.programmatic();this.map.jumpTo(camera);}
      this.map.triggerRepaint?.();
    }finally{this.recoveryPending=false;}});return true;
  }
  checkProgress() {
    if(!this.state.enabled||this.map.isMoving())return;
    try{this.ready();if(this.map.getSource("terrain-dem-source")&&this.effective){this.pendingSince=null;return;}}
    catch(error){this.handleRenderError(error.message,error.stack);return;}
    this.pendingSince??=performance.now();
    if(performance.now()-this.pendingSince>45000&&!this.slowWarning){this.slowWarning=true;this.reportWork(true);this.post("TerrainRenderDiagnostic",{message:"3D加载较慢，保留3D视角并继续加载。"});}
  }
  notify(message){this.post("TerrainStateChanged",{requestId:this.state.terrainRevision,message});}
  reportWork(ready){const key=`${this.state.terrainRevision}/${ready}`;if(key===this.workKey)return;this.workKey=key;this.workChanged(ready);this.post("TerrainWorkState",{requestId:this.state.terrainRevision,ready});}
  scheduleWork(){clearTimeout(this.workTimer);this.workTimer=setTimeout(()=>{
    if(!this.state.enabled){this.reportWork(true);return;}
    // 中心地形已可用后，不再让远处/旧视野的source-loaded状态锁住全部路网。
    // 后端仍通过实际DEM生成任务的BeginTerrain租约优先保障地形生成。
    const ready=!this.map.isMoving()&&this.map.getSource("terrain-dem-source")&&this.effective;
    this.reportWork(!!ready);
  },80);}
  showSky(enabled) {
    // 控制器构造时样式尚未加载；首次收到已就绪地图的设置时再读取。
    if(!this.backgroundCaptured){this.originalBackground=this.map.getPaintProperty("background","background-color");this.backgroundCaptured=true;}
    this.map.setSky(enabled ? terrainSky : undefined);
    // 远景尚未绘制的背景与地平线同色，避免灰色带；不补DEM、不改变地形或地图瓦片。
    this.map.setPaintProperty("background","background-color",enabled?terrainSky["horizon-color"]:this.originalBackground);
  }
  update(state) {
    if(!Number.isFinite(state.terrainRevision)||state.terrainRevision<this.state.terrainRevision)return;
    if(state.enabled)this.map.setMaxPitch(85);
    const wasEnabled=this.state.enabled;const oldExaggeration=this.state.exaggeration;
    if(state.enabled&&state.camera){this.programmatic();this.map.jumpTo({center:[state.camera.longitude,state.camera.latitude],zoom:state.camera.zoom,bearing:state.camera.bearing,pitch:state.camera.pitch});}
    const saved=this.camera();this.state=state;
    const identity=state.enabled?JSON.stringify([state.terrainUrlTemplate,state.maxNativeZoom]):null;
    const changedIdentity=identity!==this.identity;
    if(identity!==this.identity) {
      this.map.setTerrain(null);
      if(this.map.getSource("terrain-dem-source"))this.map.removeSource("terrain-dem-source");
      this.identity=identity;this.effective=false;
      if(identity)this.map.addSource("terrain-dem-source",{type:"raster-dem",encoding:"mapbox",tiles:[state.terrainUrlTemplate],tileSize:512,minzoom:state.maxNativeZoom,maxzoom:state.maxNativeZoom});
    }
    this.setEditingLock(!!state.enabled);
    this.showSky(!!state.enabled);
    if(state.enabled&&identity) {
      if(this.map.getZoom()<state.maxNativeZoom+1){this.fail(`当前缩放范围过大，请放大至Z${state.maxNativeZoom+1}或以上再启用3D地形。`);return;}
      if(changedIdentity||!wasEnabled||oldExaggeration!==state.exaggeration)this.map.setTerrain({source:"terrain-dem-source",exaggeration:state.exaggeration});
      if(!wasEnabled&&saved.pitch<state.defaultPitch) {
        this.programmatic();this.map.easeTo({...saved,pitch:state.defaultPitch,duration:350});
      }
      this.pendingSince=performance.now();this.reportWork(false);this.notify(this.effective?"active":"loading");this.ready();this.scheduleWork();
    } else {
      if(this.map.getTerrain())this.map.setTerrain(null);this.effective=false;
      if(wasEnabled){this.programmatic();this.map.jumpTo({...saved,pitch:0});this.map.setMaxPitch(this.originalMaxPitch);}
      this.reportWork(true);this.notify("inactive");
    }
  }
  ready() {
    if(!this.state.enabled||!this.map.getSource("terrain-dem-source")||!this.map.isSourceLoaded("terrain-dem-source"))return;
    const elevation=this.map.queryTerrainElevation(this.map.getCenter());
    if(elevation===null||!Number.isFinite(elevation))return;
    if(!this.effective){this.effective=true;this.notify("active");}
  }
  fail(message) {
    if(!this.state.enabled)return;
    const revision=this.state.terrainRevision;const saved=this.camera();
    this.state={...this.state,enabled:false};this.effective=false;
    clearTimeout(this.workTimer);this.pendingSince=null;this.identity=null;
    // 先移除地形回调，再改天空；异常帧不能继续使用旧地形对象。
    try{this.map.stop?.();this.map.setTerrain(null);}catch{}
    try{if(this.map.getSource("terrain-dem-source"))this.map.removeSource?.("terrain-dem-source");}catch{}
    try{this.showSky(false);}catch{try{this.map.setPaintProperty("background","background-color",this.originalBackground||"#eef1f4");}catch{}}
    this.programmatic();try{this.map.jumpTo({...saved,pitch:0});this.map.setMaxPitch(this.originalMaxPitch);this.map.triggerRepaint?.();}catch{}
    // 图层/控件回调即使失败，也不能打断前面的地形撤除与底图恢复。
    try{this.setEditingLock(false);}catch{}
    try{this.reportWork(true);}catch{}
    this.post("TerrainUnavailable",{requestId:revision,message});
  }
  health(value) {if(value.terrainRevision===this.state.terrainRevision&&!value.connected)this.fail(value.message);}
  resetCamera(northUp,pitch) {
    const camera=this.camera();this.programmatic();this.map.easeTo({...camera,bearing:0,pitch:northUp?0:Math.max(0,Math.min(85,pitch)),duration:250});
  }
  inspect(){return {state:this.state,effective:this.effective,renderFaults:this.renderFaults||0,terrain:this.map.getTerrain(),sky:this.map.getStyle().sky,camera:this.camera(),elevation:this.map.queryTerrainElevation(this.map.getCenter())};}
}
