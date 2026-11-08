// 预生成当前画面及外扩一圈的路网；只读现有rd5，共用原MVT持久缓存。
export function roadPrefetchTiles(bounds,center,zoom,minZoom=10){
  const z=Math.max(minZoom,Math.min(15,Math.floor(zoom)-1));if(zoom<minZoom)return [];
  const result=[];
  for(const level of [...new Set([Math.max(minZoom,z-1),z])]){
    const n=2**level,tx=lon=>(lon+180)/360*n,ty=lat=>(1-Math.asinh(Math.tan(Math.max(-85.05112878,Math.min(85.05112878,lat))*Math.PI/180))/Math.PI)/2*n;
    const cx=Math.floor(tx(center.lng)),cy=Math.floor(ty(center.lat));let west=bounds.getWest(),east=bounds.getEast();if(east<west)east+=360;
    const shift=360*Math.round((center.lng-(west+east)/2)/360);west+=shift;east+=shift;
    const x0=Math.max(cx-8,Math.floor(tx(west))-1),x1=Math.min(cx+8,Math.floor(tx(east))+1);
    const y0=Math.max(0,cy-8,Math.floor(ty(bounds.getNorth()))-1),y1=Math.min(n-1,cy+8,Math.floor(ty(bounds.getSouth()))+1);
    const tiles=[];for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++)tiles.push({z:level,x:((x%n)+n)%n,y,distance:(x-cx)**2+(y-cy)**2});
    tiles.sort((a,b)=>a.distance-b.distance);result.push(...tiles.slice(0,level===z?71:25));
  }
  return result;
}
export class RoadPrefetch {
  constructor(map,fetchTile=(url,options)=>fetch(url,options)){
    this.map=map;this.fetchTile=fetchTile;this.done=new Set();this.completed=0;this.config={};
    map.on('moveend',()=>this.schedule());map.on('remove',()=>this.stop());
  }
  configure(config){if(config?.signature!==this.config.signature){this.done.clear();this.completed=0;}this.config=config||{};this.schedule();}
  stop(){clearTimeout(this.timer);this.abort?.abort();}
  schedule(){this.stop();if(this.config.enabled&&this.map.getTerrain?.())this.timer=setTimeout(()=>this.load(),400);}
  async load(){
    if(!this.config.enabled||!this.map.getTerrain?.())return;
    const controller=this.abort=new AbortController(),config=this.config;
    const tiles=roadPrefetchTiles(this.map.getBounds(),this.map.getCenter(),this.map.getZoom(),config.minZoom).filter(t=>!this.done.has(`${t.z}/${t.x}/${t.y}`));
    let cursor=0;
    const worker=async()=>{while(cursor<tiles.length&&!controller.signal.aborted){
      const t=tiles[cursor++],key=`${t.z}/${t.x}/${t.y}`;
      try{const r=await this.fetchTile(`https://hikingguide-roads.local/${config.signature}/${key}.pbf`,{signal:controller.signal});if(r.ok){await r.arrayBuffer();if(controller.signal.aborted)return;this.done.add(key);this.completed++;if(this.done.size>512)this.done.delete(this.done.values().next().value);}}catch{if(controller.signal.aborted)return;}
    }};
    await Promise.all([worker(),worker()]);
  }
}
