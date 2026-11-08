import {RoadPrefetch} from './road-prefetch.js';
// 只读路网独立于轨迹编辑；样式变化复用同一矢量瓦片源。
export function roadFilter(conditions) {
  const match=(key,values)=>["any",...values.split("|").map(v=>v==="~"?["!has",key]:v==="*"?["has",key]:["==",key,v])];
  return ["all",...conditions.map(c=>c.key.includes("|")?["any",...c.key.split("|").map(k=>match(k,c.values))]:match(c.key,c.values))];
}
export function roadWidth(width,scale="stroke") {
  if(scale==="none")return width;
  return ["interpolate",["exponential",1.5],["zoom"],12,width,22,width*Math.pow(1.5,10)];
}
// 3D远近瓦片的LOD不同。层级范围只能控制屏幕绘制，不能让worker按瓦片LOD丢弃主题几何。
export function roadOpacity(min,max,opacity){return ["step",["zoom"],0,min,opacity,max,0];}
export function roadOpacityAt(value,zoom){
  if(!Array.isArray(value))return value;let result=value[2];for(let i=3;i<value.length;i+=2){if(zoom<value[i])break;result=value[i+1];}return result;
}
export function roadDash(text,width,scale="stroke",cap="butt") {
  if(!text?.trim())return null;
  const dash=text.split(",").map(Number),base=Math.max(width,.01);
  if(scale==="all"||scale==="none")return dash.map(v=>v/base);
  // Mapsforge默认只缩放线宽，虚线的像素长度不缩放。MapLibre虚线单位为线宽倍数。
  const value=["step",["zoom"],["literal",dash.map(v=>v/base)]];
  // Z18后圆头的直径逐渐大于固定像素间隔，会吞掉白色间隔；
  // 保留Z18的虚实比例，继续放大时虚线与线宽同步放大。
  for(let z=13;z<=(cap==="round"?18:22);z++)value.push(z,["literal",dash.map(v=>v/(base*Math.pow(1.5,z-12)))]);
  return value;
}
export class OsmRoadLayer {
  constructor(map,post){this.map=map;this.post=post;this.ids=[];this.config={};this.signature="";this.attribution=false;this.prefetch=new RoadPrefetch(map);}
  before(){return this.map.getStyle().layers.find(l=>l.type!=="background"&&l.type!=="raster"&&!l.id.startsWith("osm-road-")&&!l.id.startsWith("dem-"))?.id;}
  configure(value){
    const map=this.map;this.config=value||{};
    const active=!!value?.enabled&&value.opacity>0&&value.styles?.some(s=>s.enabled);
    const signature=active?value.signature:"";
    for(const id of this.ids)if(map.getLayer(id))map.removeLayer(id);this.ids=[];
    if((signature!==this.signature||value.minZoom!==this.minZoom)&&map.getSource("osm-road-source"))map.removeSource("osm-road-source");
    this.minZoom=value.minZoom;
    this.signature=signature;
    if(active){
      if(!map.getSource("osm-road-source"))map.addSource("osm-road-source",{type:"vector",tiles:[`https://hikingguide-roads.local/${signature}/{z}/{x}/{y}.pbf`],minzoom:value.minZoom,maxzoom:15,attribution:'© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors'});
      const strokes=[];
      for(const style of value.styles.filter(s=>s.enabled)){
        if(style.casingEnabled)strokes.push({style,casing:true,order:style.casingOrder});
        strokes.push({style,casing:false,order:style.order});
      }
      for(const {style:s,casing:c} of strokes.sort((a,b)=>a.order-b.order)){
        const min=Math.max(value.minZoom,s.minZoom),max=Math.min(24,s.maxZoom+1);if(min>=max)continue;
        const width=s.width+(c?2*s.casingWidth:0);if(width<=0)continue;
        const id=`osm-road-${s.id}-${c?"casing":"core"}`,scale=c?s.casingScaleMode:s.scaleMode;
        const paint={"line-color":c?s.casingColor:s.color,"line-width":roadWidth(width,scale),"line-opacity":roadOpacity(min,max,value.opacity*(c?s.casingAlpha:s.alpha))};
        const dash=roadDash(c?s.casingDashArray:s.dashArray,width,scale,c?s.casingCap:s.lineCap);if(dash)paint["line-dasharray"]=dash;
        map.addLayer({id,type:"line",source:"osm-road-source","source-layer":"roads",minzoom:0,maxzoom:24,filter:roadFilter(s.conditions),layout:{"line-cap":c?s.casingCap:s.lineCap,"line-join":"round"},paint},this.before());this.ids.push(id);
      }
    } else if(map.getSource("osm-road-source")){map.removeSource("osm-road-source");}
    this.attribution=active;this.reorder();this.prefetch.configure({...value,enabled:active});
  }
  reorder(){for(const id of this.ids)if(this.map.getLayer(id))this.map.moveLayer(id,this.before());}
  sample(type="path"){const f=this.ids.length?this.map.queryRenderedFeatures({layers:this.ids}).find(f=>f.properties.highway===type&&!f.properties.bridge&&!f.properties.tunnel&&!f.properties.sac_scale):null;const coordinates=f?.geometry?.coordinates;return coordinates?.length?coordinates[Math.floor(coordinates.length/2)]:null;}
  inspect(){const m=this.map;return{enabled:!!this.signature,layers:this.ids.length,loaded:!!m.getSource("osm-road-source")&&m.isSourceLoaded("osm-road-source"),features:this.ids.length?m.queryRenderedFeatures({layers:this.ids}).length:0,opacities:this.ids.map(id=>roadOpacityAt(m.getPaintProperty(id,"line-opacity"),m.getZoom())),signature:this.signature,prefetched:this.prefetch.completed,order:m.getStyle().layers.map(l=>l.id)};}
}
