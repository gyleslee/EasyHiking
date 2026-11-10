// One camera LOD and one GeoJSON source for track colour, guide colour and arrows.
import { prepareTrackArrowRuns, trackArrowLayoutProfile } from "./track-arrow-layout.js";
export const trackArrowProfile = Object.freeze({
  lengthPixels: 10,
  imageWidth: 512, imageHeight: 128
});
export const trackArrowImages = ["dark", "light"].flatMap(tone =>
  ["narrow", "wide"].map(size => `track-chevron-${tone}-${size}`));

// Choose the stronger contrast against the visible line colour, including guide coverage.
export function trackArrowTone(properties) {
  const hex = /^#[0-9a-f]{6}$/i.test(properties.color || "") ? properties.color : "#2877C7";
  let rgb = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255);
  if (properties.guideVisible) rgb = rgb.map((v, i) => v * .28 + [230 / 255, 169 / 255, 0][i] * .72);
  const linear = rgb.map(v => v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4);
  const luminance = linear[0] * .2126 + linear[1] * .7152 + linear[2] * .0722;
  const darkLuminance = ((17 / 255 + .055) / 1.055) ** 2.4;
  return (luminance + .05) / (darkLuminance + .05) >= 1.05 / (luminance + .05) ? "dark" : "light";
}

export function trackArrowPattern(selection = {}) {
  const selected = ["any",
    ["in", ["get", "trackId"], ["literal", selection.trackIds || []]],
    ["in", ["get", "splitSectionId"], ["literal", selection.sectionIds || []]],
    ["==", ["get", "guideSegmentId"], selection.guideSegmentId || "__none__"]];
  return ["concat", "track-chevron-", ["case", selected, "dark", ["get", "arrowTone"]], "-",
    ["case", ["any", selected, ["==", ["get", "isActive"], true], ["==", ["get", "guideVisible"], true]], "wide", "narrow"]];
}

export function visibleTrackChunks(features,bounds) {
  if(!bounds||![bounds.west,bounds.east,bounds.south,bounds.north].every(Number.isFinite)||bounds.east-bounds.west>240||bounds.east<bounds.west)return features;
  const dx=Math.max(.001,(bounds.east-bounds.west)*.6),dy=Math.max(.001,(bounds.north-bounds.south)*.6);
  return features.filter(f=>{const b=f.__displayBounds;return !b||b[2]>=bounds.west-dx&&b[0]<=bounds.east+dx&&b[3]>=bounds.south-dy&&b[1]<=bounds.north+dy;});
}
export class TrackScene {
  constructor(map){this.map=map;this.features=[];this.generation=0;this.key="";this.timer=null;this.arrowRuns=new WeakMap();
    map.on("moveend",()=>{clearTimeout(this.timer);this.timer=setTimeout(()=>this.refresh(),80);});
    map.on("zoom",()=>this.updateArrows());map.on("moveend",()=>this.updateArrows());
  }
  set(data){this.features=data?.features||[];this.generation++;for(const f of this.features){f.properties={...f.properties,arrowTone:trackArrowTone(f.properties||{})};let w=Infinity,s=Infinity,e=-Infinity,n=-Infinity;for(const line of f.geometry?.coordinates||[])for(const p of line){w=Math.min(w,p[0]);e=Math.max(e,p[0]);s=Math.min(s,p[1]);n=Math.max(n,p[1]);}f.__displayBounds=[w,s,e,n];}this.refresh();this.updateArrows();this.setArrowSelection(this.arrowSelection);}
  setArrowSelection(selection={}) {
    this.arrowSelection=selection;
    const expression=trackArrowPattern(selection),key=JSON.stringify(expression);
    if(this.arrowSelectionKey===key)return;
    this.arrowSelectionKey=key;
    this.map.setPaintProperty("track-direction-arrows","line-pattern",expression);
  }
  refresh(){let b;try{const v=this.map.getBounds();b={west:v.getWest(),east:v.getEast(),south:v.getSouth(),north:v.getNorth()};}catch{return;}
    const shown=visibleTrackChunks(this.features,b),zoom=this.map.getZoom(),layoutKey=zoom.toFixed(3);
    const key=this.generation+":"+layoutKey+":"+shown.map(f=>f.properties.chunkId).join("|");if(key===this.key)return;this.key=key;
    const prepared=shown.flatMap(f=>{let saved=this.arrowRuns.get(f);if(saved?.key!==layoutKey){saved={key:layoutKey,runs:prepareTrackArrowRuns(f,zoom)};this.arrowRuns.set(f,saved);}return saved.runs;});
    this.map.getSource("tracks")?.setData({type:"FeatureCollection",features:prepared});
  }
  updateArrows(){const map=this.map;if(!map.getLayer("track-direction-arrows"))return;
    const profile=trackArrowProfile;
    const width=trackArrowLayoutProfile.spacingPixels/(profile.imageWidth/profile.imageHeight);
    const key=Math.round(width*128);
    if(this.arrowImageKey===key&&trackArrowImages.every(id=>map.hasImage(id)))return;
    this.arrowImageKey=key;
    const units=profile.imageHeight/width;
    for(const tone of ["dark","light"])for(const size of ["narrow","wide"]){
      const canvas=document.createElement("canvas");canvas.width=profile.imageWidth;canvas.height=profile.imageHeight;
      const c=canvas.getContext("2d"),wide=size==="wide";
      c.translate(profile.imageWidth/2,profile.imageHeight/2);c.scale(units,units);
      c.lineCap="round";c.lineJoin="round";
      const halfHeight=wide?1.1:.7;
      for(const shift of [-profile.lengthPixels*.3,profile.lengthPixels*.3]){
        c.beginPath();c.moveTo(shift-profile.lengthPixels*.24,-halfHeight);
        c.lineTo(shift+profile.lengthPixels*.24,0);c.lineTo(shift-profile.lengthPixels*.24,halfHeight);
        c.strokeStyle=tone==="dark"?"#ffffff":"#111111";c.lineWidth=wide?1.6:1.35;c.stroke();
        c.strokeStyle=tone==="dark"?"#111111":"#ffffff";c.lineWidth=wide?1:.9;c.stroke();
      }
      const image=c.getImageData(0,0,profile.imageWidth,profile.imageHeight),id=`track-chevron-${tone}-${size}`;
      if(map.hasImage(id))map.updateImage(id,image);else map.addImage(id,image);
    }
    map.setPaintProperty("track-direction-arrows","line-width",width);
  }
}
