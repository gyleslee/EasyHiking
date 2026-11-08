export function zoomLabel(zoom){return `Z${Math.max(0,Math.floor(Number.isFinite(zoom)?zoom:0))}`;}
// 将层级放在原生缩放按钮左侧，2D和3D共用，不接管缩放行为。
export class ZoomNavigationControl {
  constructor(NavigationControl,environment={document}){this.NavigationControl=NavigationControl;this.document=environment.document;}
  onAdd(map){
    this.map=map;const root=this.document.createElement('div');root.className='maplibregl-ctrl zoom-navigation';
    this.readout=this.document.createElement('span');this.readout.className='map-zoom-level';this.readout.setAttribute('aria-live','polite');
    this.navigation=new this.NavigationControl({showCompass:false});root.append(this.readout,this.navigation.onAdd(map));this.root=root;
    this.changed=()=>this.refresh();map.on('zoom',this.changed);this.refresh();return root;
  }
  refresh(){const z=this.map.getZoom();const label=zoomLabel(z);if(this.readout.textContent!==label)this.readout.textContent=label;this.readout.title=`当前地图缩放层级：${z.toFixed(2)}`;this.readout.setAttribute('aria-label',`当前地图缩放层级 ${label}`);}
  onRemove(){this.map.off('zoom',this.changed);this.navigation.onRemove();this.root.remove();}
}
