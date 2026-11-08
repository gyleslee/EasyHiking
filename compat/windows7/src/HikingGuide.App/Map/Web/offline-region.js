const empty={type:"FeatureCollection",features:[]};
export class OfflineRegionSelection {
  constructor(map,post,onBegin=()=>{},onEnd=()=>{}){
    this.map=map;this.post=post;this.onBegin=onBegin;this.onEnd=onEnd;this.active=false;this.points=[];
    map.on("click",e=>{if(this.active&&!e.originalEvent?.target?.closest?.(".offline-region-controls"))this.add(e.lngLat.lng,e.lngLat.lat);});
    map.on("dblclick",e=>{if(this.active){e.preventDefault();this.finish();}});
    map.on("contextmenu",e=>{if(this.active){e.preventDefault();this.undo();}});
    this.key=e=>{if(!this.active)return;if(e.key==="Escape"){e.preventDefault();this.cancel();}else if(e.key==="Enter"){e.preventDefault();this.finish();}else if(e.key==="Backspace"){e.preventDefault();this.undo();}};
    window.addEventListener("keydown",this.key);map.on("remove",()=>window.removeEventListener("keydown",this.key));
  }
  begin(){
    if(this.active)this.cancel();this.previous=this.points;this.points=[];this.active=true;this.map.__hikingOfflineSelecting=true;
    this.camera={center:this.map.getCenter(),zoom:this.map.getZoom(),bearing:this.map.getBearing(),pitch:this.map.getPitch()};
    this.doubleZoom=this.map.doubleClickZoom.isEnabled();this.map.doubleClickZoom.disable();this.onBegin();this.map.jumpTo({pitch:0,bearing:0});this.map.getCanvas().style.cursor="crosshair";
    this.panel=document.createElement("div");this.panel.className="offline-region-controls";const text=document.createElement("div");text.textContent="离线选区：左击加点，拖动平移、滚轮缩放；双击/回车完成，右击撤销，Esc取消。";this.panel.appendChild(text);
    for(const event of ["mousedown","mouseup","click","dblclick"])this.panel.addEventListener(event,e=>e.stopPropagation());
    for(const [name,action]of[["完成选区",()=>this.finish()],["撤销一点",()=>this.undo()],["取消",()=>this.cancel()]]){const button=document.createElement("button");button.textContent=name;button.onclick=e=>{e.stopPropagation();action();};this.panel.appendChild(button);}
    this.info=document.createElement("span");this.panel.appendChild(this.info);this.map.getContainer().appendChild(this.panel);this.draw();
  }
  add(lon,lat){
    if(!this.active)return;lon=((lon+180)%360+360)%360-180;lat=Math.max(-85.05112878,Math.min(85.05112878,lat));
    if(this.points.length>=1000){this.info.textContent="最多1000个点";return;}
    const last=this.points.at(-1);if(last&&Math.abs(last[0]-lon)<1e-9&&Math.abs(last[1]-lat)<1e-9)return;this.points.push([lon,lat]);this.draw();
  }
  undo(){if(this.active){this.points.pop();this.draw();}}
  draw(){
    const map=this.map;if(!map.getSource("offline-region")){
      map.addSource("offline-region",{type:"geojson",data:empty});
      map.addLayer({id:"offline-region-fill",type:"fill",source:"offline-region",filter:["==","$type","Polygon"],paint:{"fill-color":"#198bfa","fill-opacity":.15}});
      map.addLayer({id:"offline-region-line",type:"line",source:"offline-region",paint:{"line-color":"#1683e3","line-width":2,"line-dasharray":[3,2]}});
      map.addLayer({id:"offline-region-points",type:"circle",source:"offline-region",filter:["==","$type","Point"],paint:{"circle-color":"#fff","circle-radius":4,"circle-stroke-width":2,"circle-stroke-color":"#1683e3"}});
    }
    const features=this.points.map(coordinates=>({type:"Feature",properties:{},geometry:{type:"Point",coordinates}}));
    const line=[];for(const p of this.points){let x=p[0];if(line.length)x+=360*Math.round((line.at(-1)[0]-x)/360);line.push([x,p[1]]);}
    if(line.length>=3)features.push({type:"Feature",properties:{},geometry:{type:"Polygon",coordinates:[[...line,line[0]]]}});
    else if(line.length>=2)features.push({type:"Feature",properties:{},geometry:{type:"LineString",coordinates:line}});
    map.getSource("offline-region").setData({type:"FeatureCollection",features});if(this.info)this.info.textContent=` 已选 ${this.points.length} 点`;
  }
  finish(){if(!this.active)return;if(this.points.length<3){this.info.textContent=" 至少需要3个点";return;}const points=this.points.map(p=>({longitude:p[0],latitude:p[1]}));this.end();this.post("OfflineRegionSelected",{points});}
  cancel(){if(!this.active)return;this.points=this.previous||[];this.draw();this.end();this.post("OfflineRegionCanceled",{});}
  end(){this.panel?.remove();this.panel=null;if(this.doubleZoom)this.map.doubleClickZoom.enable();this.map.getCanvas().style.cursor="";this.onEnd();this.active=false;this.map.jumpTo({bearing:this.camera.bearing,pitch:this.camera.pitch});this.map.__hikingOfflineSelecting=false;}
  clear(){if(this.active)this.cancel();this.points=[];if(this.map.getSource("offline-region"))this.map.getSource("offline-region").setData(empty);}
}

