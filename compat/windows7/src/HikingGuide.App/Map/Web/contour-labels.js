// 仅3D分离标签；位置来自原等高线生成器，数字是本地Canvas图标，不依赖网络字库。
export class ContourLabels {
  constructor(map){this.map=map;this.version=0;this.config={};this.timer=null;map.on("moveend",()=>this.schedule());}
  setPaused(paused){if(this.paused===paused)return;this.paused=paused;clearTimeout(this.timer);if(paused){this.loadVersion=(this.loadVersion||0)+1;this.abort?.abort();}else this.schedule();}
  configure(config){this.config=config;if(this.map.getLayer("dem-contour-labels"))this.map.setPaintProperty("dem-contour-labels","icon-opacity",config.contourOpacity??1);const key=JSON.stringify([config.signature,config.contours,config.screenAlignedLabels,config.color]);if(key===this.key)return;this.key=key;this.version++;this.abort?.abort();this.featureCount=0;this.map.getSource("dem-contour-labels")?.setData({type:"FeatureCollection",features:[]});this.schedule();}
  schedule(){clearTimeout(this.timer);if(!this.paused)this.timer=setTimeout(()=>this.load(),180);}
  image(elevation,color) {
    const id=`contour-label-${color}-${elevation}`;if(this.map.hasImage(id))return id;
    const canvas=document.createElement("canvas");const context=canvas.getContext("2d");context.font="24px sans-serif";
    canvas.width=Math.ceil(context.measureText(String(elevation)).width)+10;canvas.height=34;
    context.font="24px sans-serif";context.textAlign="center";context.textBaseline="middle";context.lineJoin="round";
    context.lineWidth=5;context.strokeStyle="white";context.strokeText(String(elevation),canvas.width/2,17);
    context.fillStyle=color;context.fillText(String(elevation),canvas.width/2,17);
    const data=context.getImageData(0,0,canvas.width,canvas.height);this.map.addImage(id,{width:canvas.width,height:canvas.height,data:data.data},{pixelRatio:2});return id;
  }
  async load() {
    if(this.paused)return;
    const config=this.config;const version=this.version;const generation=this.loadVersion=(this.loadVersion||0)+1;this.abort?.abort();const controller=this.abort=new AbortController();
    if(!config.contours||!config.screenAlignedLabels||this.map.getZoom()<13){this.map.getSource("dem-contour-labels")?.setData({type:"FeatureCollection",features:[]});return;}
    if(!this.map.getSource("dem-contour-labels")) {
      this.map.addSource("dem-contour-labels",{type:"geojson",data:{type:"FeatureCollection",features:[]}});
      const before=this.map.getStyle().layers.find(l=>l.type!=="background"&&l.type!=="raster")?.id;
      this.map.addLayer({id:"dem-contour-labels",type:"symbol",source:"dem-contour-labels",minzoom:13,paint:{"icon-opacity":this.config.contourOpacity??1},
        layout:{"icon-image":["get","labelImage"],"icon-rotation-alignment":"viewport","icon-pitch-alignment":"viewport","icon-allow-overlap":false,"icon-padding":6}},before);
    }
    const z=Math.min(this.map.getZoom()<15?14:22,Math.max(13,Math.round(this.map.getZoom())));const n=2**z;const bounds=this.map.getBounds();
    const tx=lon=>Math.floor((lon+180)/360*n);const ty=lat=>Math.floor((1-Math.asinh(Math.tan(Math.max(-85,Math.min(85,lat))*Math.PI/180))/Math.PI)/2*n);
    const center=this.map.getCenter();const cx=tx(center.lng);const cy=ty(center.lat);const tiles=[];
    for(let y=Math.max(0,ty(bounds.getNorth()));y<=Math.min(n-1,ty(bounds.getSouth()));y++)
      for(let x=tx(bounds.getWest());x<=tx(bounds.getEast());x++)tiles.push({x:((x%n)+n)%n,y,priority:Math.abs(x-cx)+Math.abs(y-cy)});
    tiles.sort((a,b)=>a.priority-b.priority);const features=[];let cursor=0;
    const worker=async()=>{while(cursor<Math.min(tiles.length,64)){
      const tile=tiles[cursor++];
      try{
        const response=await fetch(`https://hikingguide-terrain.local/${config.signature}/labels/${z}/${tile.x}/${tile.y}.json`,{signal:controller.signal});
        if(!response.ok)continue;
        const labels=await response.json();if(version!==this.version||generation!==this.loadVersion)return;
        for(const label of labels){
          const point=this.map.project([label.longitude,label.latitude]);
          if(this.map.queryRenderedFeatures([[point.x-10,point.y-10],[point.x+10,point.y+10]],{layers:["tracks-hit","guide-nodes","waypoints"]}).length)continue;
          features.push({type:"Feature",properties:{elevation:label.elevation,labelImage:this.image(label.elevation,config.color||"#8B5A2B")},geometry:{type:"Point",coordinates:[label.longitude,label.latitude]}});
        }
      }catch(error){if(error.name!=="AbortError")continue;return;}
    }};
    await Promise.all([worker(),worker()]);if(version===this.version&&generation===this.loadVersion){this.featureCount=features.length;this.map.getSource("dem-contour-labels")?.setData({type:"FeatureCollection",features});}
  }
}
