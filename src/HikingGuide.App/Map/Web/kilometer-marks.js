// Exact geographic anchors come from the original track, independently of display LOD.
export class KilometerMarks {
  constructor(map) { this.map=map;this.images=new Set();this.data={type:'FeatureCollection',features:[]};map.on('moveend',()=>this.refresh()); }
  set(data) {
    this.data=data;this.refresh();
  }
  refresh() {
    const map=this.map;
    let data=this.data;
    // Long expeditions must not allocate a texture for every kilometer worldwide.
    if(data.features.length>512){const canvas=map.getCanvas(),cells=new Set();const visible=[];for(const f of [...data.features].sort((a,b)=>Number(b.properties.endpoint)-Number(a.properties.endpoint))){const p=map.project(f.geometry.coordinates);if(p.x< -60||p.y< -60||p.x>canvas.clientWidth+60||p.y>canvas.clientHeight+60)continue;const cell=Math.floor(p.x/32)+','+Math.floor(p.y/36);if(cells.has(cell))continue;cells.add(cell);visible.push(f);if(visible.length>=512)break;}data={...data,features:visible};}
    if(!map.getSource('kilometer-marks'))map.addSource('kilometer-marks',{type:'geojson',data:{type:'FeatureCollection',features:[]},maxzoom:22});
    for(const feature of data.features||[]) {
      const label=String(feature.properties.label),id='km-pin-'+label;
      if(!this.images.has(id)) {
        const canvas=document.createElement('canvas');canvas.width=88;canvas.height=100;
        const c=canvas.getContext('2d');c.beginPath();c.moveTo(44,97);c.bezierCurveTo(34,82,8,62,8,39);c.arc(44,39,36,Math.PI,0);c.bezierCurveTo(80,62,54,82,44,97);c.closePath();
        c.fillStyle='#28312b';c.fill();c.strokeStyle='#ffffff';c.lineWidth=4;c.stroke();
        c.fillStyle='#ffffff';c.font=`600 ${label.length>4?22:label.length>2?27:34}px sans-serif`;c.textAlign='center';c.textBaseline='middle';c.fillText(label,44,40,64);
        map.addImage(id,c.getImageData(0,0,88,100),{pixelRatio:3});this.images.add(id);
      }
    }
    if(!map.getLayer('kilometer-marks'))map.addLayer({id:'kilometer-marks',type:'symbol',source:'kilometer-marks',minzoom:10,layout:{'icon-image':['concat','km-pin-',['get','label']],'icon-anchor':'bottom','icon-size':['interpolate',['linear'],['zoom'],10,.7,15,.9,18,1],'icon-pitch-alignment':'viewport','icon-rotation-alignment':'viewport','icon-allow-overlap':false,'icon-padding':3}});
    map.getSource('kilometer-marks').setData(data);
    // No historical sprites accumulate when switching projects.
    const used=new Set((data.features||[]).map(f=>'km-pin-'+f.properties.label));
    for(const id of this.images)if(!used.has(id)){if(map.hasImage(id))map.removeImage(id);this.images.delete(id);}
  }
  clear(){this.set({type:'FeatureCollection',features:[]});}
}
