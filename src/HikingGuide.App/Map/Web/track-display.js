// Appearance only: filters and paint share the existing full-precision track source.
export const defaultLine = Object.freeze({width:3,opacity:.82,lineStyle:"solid",outlineWidth:0,arrows:true,endpoints:"none",endpointSize:7,endpointMinZoom:12,showEndpointText:true});
export const defaultTrackDisplay = Object.freeze({"selectedColor":"#FD9700","selected":{"width":8,"opacity":0.9,"lineStyle":"dashdot","outlineWidth":1,"arrows":true,"endpoints":"both","endpointSize":7,"endpointMinZoom":9,"showEndpointText":true},"mainGuide":{"width":7,"opacity":1,"lineStyle":"solid","outlineWidth":0.5,"arrows":true,"endpoints":"both","endpointSize":6,"endpointMinZoom":11,"showEndpointText":true},"ordinary":{"width":5,"opacity":0.9,"lineStyle":"solid","outlineWidth":0,"arrows":true,"endpoints":"both","endpointSize":5,"endpointMinZoom":12,"showEndpointText":true}});
export const trackDash = style => ({dash:[3,2],dot:[.15,2.2],dashdot:[3,2,.15,2]})[style] || null;
export function trackSelectedFilter(selection={}) {
 return ["any",["in",["get","trackId"],["literal",selection.trackIds||[]]],["in",["get","splitSectionId"],["literal",selection.sectionIds||[]]],["==",["get","guideSegmentId"],selection.guideSegmentId||"__none__"]];
}
export function trackCategoryFilters(selection={}) {
 const picked=trackSelectedFilter(selection);
 return {ordinary:["all",["!",picked],["==",["get","isActive"],false]],mainGuide:["all",["!",picked],["==",["get","isActive"],true]],selected:picked};
}
export function trackLineFilters(selection={}) {
 return {ordinary:["==",["get","isActive"],false],mainGuide:["==",["get","isActive"],true],selected:trackSelectedFilter(selection)};
}
export const endpointColors = ["match",["get","endpoint"],"start","#2563EB","end","#16A34A","#9333ea"];
export function endpointText(mode) {return ["match",["get","endpoint"],"start","起","end","终",mode==="start"?"起":mode==="end"?"终":"起终"];}
export function endpointFilter(category,style) {
 const kind=style.endpoints==="none"?["==",["get","endpoint"],"__none__"]:style.endpoints==="both"?["in",["get","endpoint"],["literal",["start","end","both"]]]:["in",["get","endpoint"],["literal",[style.endpoints,"both"]]];
 return ["all",category,kind];
}
export class TrackDisplay {
 constructor(map){this.map=map;this.styles=defaultTrackDisplay;this.selection={};this.ready=false;}
 configure(styles){this.styles={...defaultTrackDisplay,...styles};this.apply();}
 setEndpoints(data){this.ensure();this.map.getSource("trackEndpoints")?.setData(data);}
 select(selection){this.selection=selection;this.apply();}
 ensure(){
  const m=this.map;if(this.ready||!m.getLayer("tracks-normal"))return;
  m.addSource("trackEndpoints",{type:"geojson",data:{type:"FeatureCollection",features:[]}});
  for(const [key,line] of [["ordinary","tracks-normal"],["mainGuide","tracks-active"],["selected","tracks-selected"]]){
   m.addLayer({id:`track-outline-${key}`,type:"line",source:"tracks",layout:{"line-cap":"round","line-join":"round"},paint:{"line-color":"#ffffff","line-width":0}},line);
   m.addLayer({id:`track-endpoints-${key}`,type:"circle",source:"trackEndpoints",paint:{"circle-radius":7,"circle-color":endpointColors,"circle-stroke-color":"#ffffff","circle-stroke-width":1.5,"circle-pitch-alignment":"viewport","circle-pitch-scale":"viewport"}},"waypoints");
   m.addLayer({id:`track-endpoint-labels-${key}`,type:"symbol",source:"trackEndpoints",layout:{"text-field":endpointText("both"),"text-size":11,"text-offset":[0,0],"text-anchor":"center","text-allow-overlap":true,"text-ignore-placement":true,"text-pitch-alignment":"viewport"},paint:{"text-color":"#ffffff","text-halo-width":0}},"waypoints");
  }
  this.ready=true;
 }
 apply(){
  this.ensure();if(!this.ready)return;
  const m=this.map,filters=trackCategoryFilters(this.selection),lines=trackLineFilters(this.selection),s=this.styles;
  // Selection is an overlay: keep the original category line visible through every gap.
  for(const id of ["guide-segments","guide-segments-selected","split-sections-selected"])m.setLayoutProperty(id,"visibility","none");
  for(const [key,line] of [["ordinary","tracks-normal"],["mainGuide","tracks-active"],["selected","tracks-selected"]]){
   const p=s[key]||defaultTrackDisplay[key],filter=lines[key],color=key==="selected"?s.selectedColor:["get","color"];
   m.setFilter(line,filter);m.setPaintProperty(line,"line-color",color);m.setPaintProperty(line,"line-width",p.width);m.setPaintProperty(line,"line-opacity",p.opacity);m.setPaintProperty(line,"line-dasharray",trackDash(p.lineStyle));
   const outline=`track-outline-${key}`;m.setFilter(outline,filter);m.setPaintProperty(outline,"line-width",p.outlineWidth);m.setPaintProperty(outline,"line-gap-width",p.width);m.setPaintProperty(outline,"line-opacity",p.opacity);m.setPaintProperty(outline,"line-dasharray",key==="selected"&&p.outlineWidth>0?trackDash(p.lineStyle)?.map(n=>n*p.width/p.outlineWidth)??null:null);
   m.setPaintProperty(outline,"line-color",key==="selected"?contrast(s.selectedColor):["case",["==",["get","arrowTone"],"dark"],"#111111","#ffffff"]);m.setLayoutProperty(outline,"visibility",p.outlineWidth>0?"visible":"none");
   for(const id of [`track-endpoints-${key}`,`track-endpoint-labels-${key}`]){m.setFilter(id,endpointFilter(filters[key],p));m.setLayerZoomRange(id,p.endpointMinZoom,24);}
   const combined=["all",["==",["get","endpoint"],"both"],p.endpoints==="both"];
   m.setPaintProperty(`track-endpoints-${key}`,"circle-radius",["case",combined,p.endpointSize*1.3,p.endpointSize]);
   m.setLayoutProperty(`track-endpoint-labels-${key}`,"text-size",["case",combined,Math.max(6,p.endpointSize),Math.max(6,p.endpointSize*1.3)]);m.setPaintProperty(`track-endpoints-${key}`,"circle-opacity",p.opacity);m.setPaintProperty(`track-endpoints-${key}`,"circle-stroke-opacity",p.opacity);
   m.setPaintProperty(`track-endpoint-labels-${key}`,"text-opacity",p.opacity);m.setLayoutProperty(`track-endpoint-labels-${key}`,"visibility",p.showEndpointText?"visible":"none");
   m.setLayoutProperty(`track-endpoint-labels-${key}`,"text-field",endpointText(p.endpoints));
  }
  m.setFilter("track-direction-arrows",["all",["==",["get","arrowsAllowed"],true],["any",...Object.keys(filters).filter(k=>s[k].arrows).map(k=>filters[k])]]);
  m.setPaintProperty("track-direction-arrows","line-opacity",["case",filters.selected,s.selected.opacity,filters.mainGuide,s.mainGuide.opacity,s.ordinary.opacity]);
  this.reorder();
 }
 reorder(){if(!this.ready)return;const m=this.map;for(const id of ["track-outline-ordinary","tracks-normal","track-outline-mainGuide","tracks-active","track-outline-selected","tracks-selected","track-direction-arrows"])if(m.getLayer(id))m.moveLayer(id,"waypoints");
  // Imported start/end waypoints must not cover the letters inside the endpoint markers.
  for(const k of Object.keys(trackCategoryFilters()))for(const id of [`track-endpoints-${k}`,`track-endpoint-labels-${k}`])if(m.getLayer(id))m.moveLayer(id);
 }
 inspect(){return {settings:this.styles,filters:trackCategoryFilters(this.selection),lineFilters:trackLineFilters(this.selection),endpoints:this.map.getStyle().sources.trackEndpoints?.data};}
}
function contrast(hex){const n=parseInt(hex.slice(1),16);return ((n>>16)*.299+((n>>8)&255)*.587+(n&255)*.114)>140?"#111111":"#ffffff";}
