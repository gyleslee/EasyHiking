import {groundDistance,groundProfile} from './contour-distance.js';
export function shortMarkerName(name){const text=(name||'').trim();if(/^未命名(标注|标注点|节点|攻略节点)$/.test(text))return '';const chars=Array.from(text);return chars.length>5?chars.slice(0,5).join('')+'…':chars.join('');}
export function markerMode(value){return Number.isInteger(value)&&value>=0&&value<=3?value:3;}
export const markerDisplayProfile=Object.freeze({textZoom:12,thumbnailZoom:14,maxImages:96,maxVisible:48,imageWidth:64,imageHeight:48});
export class MarkerDisplay {
 constructor(map,request){this.map=map;this.request=request;this.mode=3;this.data=new Map();this.urls=new Map();this.images=new Map();this.requested=new Set();this.pending=new Set();this.generation=0;this.moved=()=>{clearTimeout(this.timer);this.timer=setTimeout(()=>this.refresh(),80);};map.on('moveend',this.moved);map.on('zoomend',this.moved);}
 reset(){this.generation++;clearTimeout(this.timer);this.data.clear();this.urls.clear();this.requested.clear();for(const name of this.images.values())if(this.map.hasImage(name))this.map.removeImage(name);this.images.clear();this.pending.clear();}
 setMode(mode){this.mode=markerMode(mode);this.refresh();}
 set(source,data){this.data.set(source,data);for(const f of data.features||[]){const p=f.properties,id=p.waypointId||p.guideNodeId;if((p.mediaInternalCount||p.mediaExternalCount)&&!this.requested.has(id)){this.requested.add(id);this.request(f);}}this.refresh();}
 thumbnail(id,url){if(this.urls.get(id)!==url&&this.images.has(id)){const image=this.images.get(id);this.images.delete(id);if(this.map.hasImage(image))this.map.removeImage(image);}this.urls.set(id,url);this.moved();}
 refresh(){
  for(const id of ['waypoint-labels','marker-label-z15','waypoint-media-badges','guide-node-labels','guide-node-label-z15','guide-node-media-badges'])if(this.map.getLayer(id))this.map.setLayoutProperty(id,'visibility','none');
  const visible=[];
  for(const [source,data] of this.data){
   const kind=source==='waypoints'?'waypoint':'guide',text=kind+'-display',thumb=kind+'-thumbnails';
   if(!this.map.getLayer(thumb))this.map.addLayer({id:thumb,type:'symbol',source,minzoom:14,filter:['!=',['get','thumbnailImage'],''],layout:{'icon-image':['get','thumbnailImage'],'icon-size':['interpolate',['linear'],['zoom'],14,['*',.5,['get','symbolScale']],17,['*',.65,['get','symbolScale']],20,['*',.75,['get','symbolScale']]],'icon-anchor':'bottom','icon-offset':[0,-8],'icon-padding':12,'icon-allow-overlap':false,'icon-ignore-placement':false,'icon-pitch-alignment':'viewport','icon-rotation-alignment':'viewport'}});
   if(!this.map.getLayer(text))this.map.addLayer({id:text,type:'symbol',source,minzoom:12,filter:['!=',['get','shortLabel'],''],layout:{'text-field':['get','shortLabel'],'text-size':['*',12,['max',.72,['get','symbolScale']]],'text-anchor':'top','text-offset':[0,1.1],'text-padding':5,'text-allow-overlap':false,'text-ignore-placement':false,'text-pitch-alignment':'viewport'},paint:{'text-color':'#20252a','text-halo-color':'white','text-halo-width':1.5}});
   this.map.setLayoutProperty(text,'visibility',this.mode===1||this.mode===3?'visible':'none');this.map.setLayoutProperty(thumb,'visibility',this.mode>=2?'visible':'none');
   const features=(data.features||[]).map(f=>{const p=f.properties,id=p.waypointId||p.guideNodeId,center=this.map.getCenter(),distance=groundDistance(...f.geometry.coordinates,center.lng,center.lat),radius=groundProfile(this.map.getZoom(),center.lat).nearStart;const symbolScale=this.map.getTerrain()&&this.map.getPitch()>15?Math.max(.55,Math.min(1,radius/Math.max(1,distance))):1;return {...f,properties:{...p,symbolScale,shortLabel:shortMarkerName(p.namedLabel),thumbnailImage:this.images.get(id)||''}};});
   this.map.getSource(source)?.setData({...data,features});
   if(this.mode>=2&&this.map.getZoom()>=14)for(const f of features){const p=f.properties,id=p.waypointId||p.guideNodeId,point=this.map.project(f.geometry.coordinates),c=this.map.getCanvas();if(point.x>=-40&&point.y>=-40&&point.x<=c.clientWidth+40&&point.y<=c.clientHeight+40&&this.urls.has(id))visible.push({id,d:(point.x-c.clientWidth/2)**2+(point.y-c.clientHeight/2)**2});}
  }
  visible.sort((a,b)=>a.d-b.d);this.wanted=new Set(visible.slice(0,48).map(x=>x.id));
  for(const id of this.wanted)if(!this.images.has(id)&&!this.pending.has(id)&&this.pending.size<4)this.load(id);
 }
 async load(id){
  const generation=this.generation;this.pending.add(id);
  try{const response=await fetch(this.urls.get(id));if(!response.ok)throw Error('缩略图暂不可用');const bitmap=await createImageBitmap(await response.blob());
   const canvas=document.createElement('canvas');canvas.width=64;canvas.height=48;const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,64,48);const ratio=Math.min(60/bitmap.width,44/bitmap.height);ctx.drawImage(bitmap,(64-bitmap.width*ratio)/2,(48-bitmap.height*ratio)/2,bitmap.width*ratio,bitmap.height*ratio);bitmap.close();
   if(generation!==this.generation)return;
   while(this.images.size>=96){const old=[...this.images.keys()].find(k=>!this.wanted.has(k));if(!old)break;const name=this.images.get(old);this.images.delete(old);if(this.map.hasImage(name))this.map.removeImage(name);}
   if(this.images.size>=96)return;const name='marker-thumbnail-'+id;if(!this.map.hasImage(name))this.map.addImage(name,ctx.getImageData(0,0,64,48));this.images.set(id,name);
  }catch(error){if(generation===this.generation){this.lastError=String(error);this.urls.delete(id);}}finally{if(generation===this.generation){this.pending.delete(id);this.moved();}}
 }
}
