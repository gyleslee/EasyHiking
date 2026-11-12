import {detailStrength,distanceWeights,contourIntervals,labelInterval,groundProfile,mercatorPoint,groundDistance,labelBudget,selectContourLabels} from './contour-distance.js';
// Data textures contain the original Terrain-RGB bytes and an independent validity alpha.
// 2D draws tile quads; 3D draws into MapLibre's public terrain tile compositor.
export const surfaceSize=516;
export const surfaceBorder=2;
export const contourFragment = `#version 300 es
precision highp float;
precision highp int;
uniform sampler2D u_dem;
uniform vec2 u_uvOrigin;
uniform float u_uvScale;
uniform float u_interval;
uniform vec2 u_distanceIntervals;
uniform vec4 u_depthBreaks;
uniform vec2 u_groundFocus;
uniform vec3 u_tileWorld;
uniform float u_worldMeters;
uniform float u_detailStrength;
uniform float u_major;
uniform float u_width;
uniform float u_majorWidth;
uniform float u_opacity;
uniform vec3 u_color;
uniform vec3 u_majorColor;
uniform mat4 u_screenMatrix;
uniform vec2 u_screenSize;
uniform bool u_smooth;
in vec2 v_uv;
out vec4 fragColor;
float decodeHeight(vec4 p) { return dot(round(p.rgb*255.0),vec3(65536.0,256.0,1.0))*0.1-10000.0; }
float slope(float a,float b){return a*b>0.0?2.0*a*b/(a+b):0.0;}
float cubic(vec4 v,float t){
  float m0=slope(v.y-v.x,v.z-v.y),m1=slope(v.z-v.y,v.w-v.z);
  float t2=t*t,t3=t2*t;
  return (2.0*t3-3.0*t2+1.0)*v.y+(t3-2.0*t2+t)*m0+(-2.0*t3+3.0*t2)*v.z+(t3-t2)*m1;
}
vec4 contourInk(float interval,float h,float gradient,float aa){
  float level=floor(h/interval+0.5)*interval;
  if(abs(level)<0.01)return vec4(0.0);
  bool major=u_major>0.0 && abs(level/u_major-round(level/u_major))<0.00001;
  float width=major?u_majorWidth:u_width;
  float distancePixels=abs(h-level)/max(gradient,0.000001),halfWidth=width*0.5;
  float coverage=clamp((halfWidth+aa-distancePixels)/(2.0*aa),0.0,1.0)-clamp((-halfWidth+aa-distancePixels)/(2.0*aa),0.0,1.0);
  float alpha=coverage*u_opacity;
  // 已经小于屏幕分辨能力的远景线逐渐淡出，避免地平线又糊成整片颜色。
  if(u_detailStrength>0.0&&interval>=u_distanceIntervals.x)
    alpha*=mix(1.0,smoothstep(1.5,5.0,interval/max(gradient,0.000001)),u_detailStrength);
  return vec4((major?u_majorColor:u_color)*alpha,alpha);
}
void main() {
  vec2 xy=(u_uvOrigin+v_uv*u_uvScale)*512.0+vec2(1.5);
  ivec2 p=ivec2(floor(xy));vec2 f=fract(xy);
  vec4 a=texelFetch(u_dem,p,0), b=texelFetch(u_dem,p+ivec2(1,0),0);
  vec4 c=texelFetch(u_dem,p+ivec2(0,1),0), d=texelFetch(u_dem,p+ivec2(1,1),0);
  float valid=min(min(a.a,b.a),min(c.a,d.a));
  vec4 heights=vec4(decodeHeight(a),decodeHeight(b),decodeHeight(c),decodeHeight(d));
  float localRange=max(max(heights.x,heights.y),max(heights.z,heights.w))-min(min(heights.x,heights.y),min(heights.z,heights.w));
  float h=mix(mix(heights.x,heights.y,f.x),mix(heights.z,heights.w,f.x),f.y);
  if(u_smooth && valid>0.99){
    vec4 rows=vec4(0.0);float stencilValid=1.0;
    for(int y=0;y<4;y++){
      vec4 values=vec4(0.0);
      for(int x=0;x<4;x++){vec4 sampleValue=texelFetch(u_dem,p+ivec2(x-1,y-1),0);values[x]=decodeHeight(sampleValue);stencilValid=min(stencilValid,sampleValue.a);}
      rows[y]=cubic(values,f.x);
    }
    if(stencilValid>0.99)h=clamp(cubic(rows,f.y),min(min(heights.x,heights.y),min(heights.z,heights.w)),max(max(heights.x,heights.y),max(heights.z,heights.w)));
  }
  vec4 clip=u_screenMatrix*vec4(v_uv,h,1.0);
  vec2 screen=clip.xy/max(abs(clip.w),0.000001)*u_screenSize*0.5;
  vec2 jx=dFdx(screen),jy=dFdy(screen);
  float det=jx.x*jy.y-jx.y*jy.x;
  vec2 dh=vec2(dFdx(h),dFdy(h));
  float gradient=abs(det)>0.000001?length(vec2(dh.x*jy.y-dh.y*jx.y,dh.y*jx.x-dh.x*jy.x)/det):0.0;
  // Filter coverage in the destination pixel footprint as well as screen space.
  // A steep projected terrain texel can cover several screen pixels.
  float aa=max(0.5,0.5*(abs(dh.x)+abs(dh.y))/max(gradient,0.000001));
  vec3 weights=vec3(1.0,0.0,0.0);
  if(u_detailStrength>0.0){
    vec2 delta=u_tileWorld.xy+v_uv*u_tileWorld.z-u_groundFocus;
    delta.x-=floor(delta.x+0.5);
    float depth=length(delta)*u_worldMeters;
    float nearFade=smoothstep(u_depthBreaks.x,u_depthBreaks.y,depth),farFade=smoothstep(u_depthBreaks.z,u_depthBreaks.w,depth);
    weights=vec3(1.0-u_detailStrength*nearFade,u_detailStrength*(nearFade-farFade),u_detailStrength*farFade);
  }
  fragColor=contourInk(u_interval,h,gradient,aa)*weights.x;
  if(weights.y>0.0)fragColor+=contourInk(u_distanceIntervals.x,h,gradient,aa)*weights.y;
  if(weights.z>0.0)fragColor+=contourInk(u_distanceIntervals.y,h,gradient,aa)*weights.z;
  if(valid<0.99 || gradient<0.0001 || localRange<0.001)fragColor=vec4(0.0);
}`;

const vertex = `#version 300 es
precision highp float;
in vec2 a_pos;
uniform mat4 u_matrix;
out vec2 v_uv;
void main(){v_uv=a_pos;gl_Position=u_matrix*vec4(a_pos,0.0,1.0);}`;
const terrainMatrix=new Float32Array([2,0,0,0, 0,-2,0,0, 0,0,1,0, -1,1,0,1]);
const empty=()=>({type:'FeatureCollection',features:[]});
export function contourInterval(zoom){return zoom<13?0:zoom<15?100:10;}
export function parentTile(tile,maxZoom){
 const z=Math.min(tile.z,maxZoom),scale=2**(tile.z-z),n=2**z;
 const x=Math.floor(tile.x/scale),y=Math.floor(tile.y/scale);
 return {z,x:((x%n)+n)%n,y,origin:[(tile.x-x*scale)/scale,(tile.y-y*scale)/scale],scale:1/scale};
}
export function heightAt(bytes,x,y){
 const p=(y*surfaceSize+x)*4;if(bytes[p+3]!==255)return null;
 return (bytes[p]*65536+bytes[p+1]*256+bytes[p+2])*.1-10000;
}
export function bilinearHeight(bytes,x,y){
 const ix=Math.floor(x),iy=Math.floor(y);if(ix<0||iy<0||ix>=surfaceSize-1||iy>=surfaceSize-1)return null;
 const a=heightAt(bytes,ix,iy),b=heightAt(bytes,ix+1,iy),c=heightAt(bytes,ix,iy+1),d=heightAt(bytes,ix+1,iy+1);
 if([a,b,c,d].some(h=>h===null))return null;
 const fx=x-ix,fy=y-iy;return (a+(b-a)*fx)*(1-fy)+(c+(d-c)*fx)*fy;
}
export function monotoneCubic(v,t){
 const slope=(a,b)=>a*b>0?2*a*b/(a+b):0,t2=t*t,t3=t2*t;
 return (2*t3-3*t2+1)*v[1]+(t3-2*t2+t)*slope(v[1]-v[0],v[2]-v[1])+(-2*t3+3*t2)*v[2]+(t3-t2)*slope(v[2]-v[1],v[3]-v[2]);
}
export function smoothHeight(bytes,x,y){
 const linear=bilinearHeight(bytes,x,y);if(linear===null)return null;
 const ix=Math.floor(x),iy=Math.floor(y);if(ix<1||iy<1||ix+2>=surfaceSize||iy+2>=surfaceSize)return linear;
 const rows=[];for(let row=-1;row<=2;row++){
  const values=[];for(let col=-1;col<=2;col++){const h=heightAt(bytes,ix+col,iy+row);if(h===null)return linear;values.push(h);}
  rows.push(monotoneCubic(values,x-ix));
 }
 const central=[heightAt(bytes,ix,iy),heightAt(bytes,ix+1,iy),heightAt(bytes,ix,iy+1),heightAt(bytes,ix+1,iy+1)];
 return Math.max(Math.min(...central),Math.min(Math.max(...central),monotoneCubic(rows,y-iy)));
}
export class GpuContourLayer {
 constructor(map,onError=()=>{}){
  this.id='dem-gpu-contours';this.type='custom';this.renderingMode='2d';this.map=map;this.onError=onError;
  this.terrainTileRevision=0;this.tiles=new Map();this.pending=new Map();this.queue=new Map();this.generation=0;
  this.config={};this.compiles=0;this.draws2D=0;this.draws3D=0;this.requestCount=0;this.failures=0;
  this.maxTiles=64;this.maxConcurrent=2;this.labelImages=new Set();this.retryAfter=new Map();
  this.labelSignatures=new Map();
  this.moved=()=>this.schedule();this.contextLost=()=>this.lost();this.contextRestored=()=>this.restored();
 }
 onAdd(map,gl){
  this.map=map;this.gl=gl;this.initialize();
  map.on('moveend',this.moved);map.on('zoom',this.moved);map.on('style.load',this.moved);
  map.getCanvas().addEventListener('webglcontextlost',this.contextLost);
  map.getCanvas().addEventListener('webglcontextrestored',this.contextRestored);
  this.schedule();
 }
 initialize(){
  const gl=this.gl;
  if(this.program)gl.deleteProgram(this.program);if(this.vao)gl.deleteVertexArray(this.vao);if(this.buffer)gl.deleteBuffer(this.buffer);
  this.program=null;this.vao=null;this.buffer=null;
  if(!(gl instanceof WebGL2RenderingContext))throw Error('当前图形环境不支持GPU动态等高线（需要WebGL2）。');
  const shaders=[];let program=null;
  try{
   for(const [type,source] of [[gl.VERTEX_SHADER,vertex],[gl.FRAGMENT_SHADER,contourFragment]]){
    const s=gl.createShader(type);shaders.push(s);gl.shaderSource(s,source);gl.compileShader(s);
    if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));
   }
   program=gl.createProgram();for(const s of shaders)gl.attachShader(program,s);gl.linkProgram(program);
   if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(program));
   this.program=program;this.compiles++;
   this.uniforms=Object.fromEntries(['dem','uvOrigin','uvScale','interval','distanceIntervals','depthBreaks','groundFocus','tileWorld','worldMeters','detailStrength','major','width','majorWidth','opacity','color','majorColor','matrix','screenMatrix','screenSize','smooth'].map(n=>[n,gl.getUniformLocation(program,'u_'+n)]));
   const oldVao=gl.getParameter(gl.VERTEX_ARRAY_BINDING),oldBuffer=gl.getParameter(gl.ARRAY_BUFFER_BINDING);
   this.vao=gl.createVertexArray();gl.bindVertexArray(this.vao);this.buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer);
   gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([0,0,1,0,0,1,0,1,1,0,1,1]),gl.STATIC_DRAW);
   const position=gl.getAttribLocation(program,'a_pos');gl.enableVertexAttribArray(position);gl.vertexAttribPointer(position,2,gl.FLOAT,false,0,0);
   gl.bindVertexArray(oldVao);gl.bindBuffer(gl.ARRAY_BUFFER,oldBuffer);
  }catch(e){if(program)gl.deleteProgram(program);throw e;}
  finally{for(const s of shaders)gl.deleteShader(s);}
 }
 configure(value){
  const identity=value.surface?.surfaceUrlTemplate||null;
  if(identity!==this.identity){this.identity=identity;this.generation++;this.cancel();this.releaseTiles();this.retryAfter.clear();this.map.getSource('dem-contour-labels')?.setData(empty());this.labelCount=0;}
  const key=JSON.stringify(value);const changed=key!==this.key;this.key=key;this.config=value;
  if(!this.active()){this.cancel();this.queue.clear();this.map.getSource('dem-contour-labels')?.setData(empty());}
  if(changed)this.invalidate();this.schedule();
 }
 active(){return !!this.config.contours&&!!this.identity&&this.config.contourOpacity!==0&&contourInterval(this.map.getZoom())>0&&!this.disabled;}
 intervalForView(){return this.map.getZoom()<15?(this.config.lowInterval??100):(this.config.highInterval??10);}
 invalidate(){this.terrainTileRevision++;this.map.triggerRepaint();}
 schedule(){
  clearTimeout(this.timer);this.timer=setTimeout(()=>this.updateVisible(),60);
  const interval=contourInterval(this.map.getZoom());if(interval!==this.interval){this.interval=interval;this.invalidate();}
 }
 updateVisible(){
  if(!this.active()||!this.gl||this.gl.isContextLost())return;
  const z=Math.min(this.config.surface.maxNativeZoom,Math.floor(this.map.getZoom())),n=2**z,b=this.map.getBounds();
  const tx=lon=>(lon+180)/360*n;
  const ty=lat=>(1-Math.asinh(Math.tan(Math.max(-85.051128,Math.min(85.051128,lat))*Math.PI/180))/Math.PI)/2*n;
  const c=this.map.getCenter(),cx=tx(c.lng),cy=ty(c.lat);this.visible=[];
  let west=Math.floor(tx(b.getWest()))-1,east=Math.floor(tx(b.getEast()))+1;
  if(east-west>n){west=Math.floor(cx)-Math.floor(n/2);east=west+n-1;}
  let north=Math.max(0,Math.floor(ty(b.getNorth()))-1),south=Math.min(n-1,Math.floor(ty(b.getSouth()))+1);
  if((east-west+1)*(south-north+1)>1024){west=Math.max(west,Math.floor(cx)-8);east=Math.min(east,Math.floor(cx)+8);north=Math.max(north,Math.floor(cy)-8);south=Math.min(south,Math.floor(cy)+8);}
  for(let y=north;y<=south;y++)
   for(let x=west;x<=east;x++)this.visible.push({z,x:((x%n)+n)%n,y,worldX:x,priority:(x-cx)**2+(y-cy)**2});
  this.visible.sort((a,b)=>a.priority-b.priority);this.visible=this.visible.slice(0,this.maxTiles);
  for(const tile of this.visible)this.request(tile);
  this.trim();this.labels();this.invalidate();
 }
 keyFor(t){return `${t.z}/${t.x}/${t.y}`;}
 request(tile){
  const key=this.keyFor(tile);const cached=this.tiles.get(key);
  if(cached){cached.used=performance.now();return cached;}
  if((this.retryAfter.get(key)||0)>performance.now())return null;
  if(!this.pending.has(key)&&this.queue.size<128)this.queue.set(key,tile);
  this.pump();return null;
 }
 pump(){
  while(this.active()&&this.pending.size<this.maxConcurrent&&this.queue.size){
   const [key,tile]=this.queue.entries().next().value;this.queue.delete(key);
   if(this.tiles.has(key))continue;
   const controller=new AbortController(),generation=this.generation;this.pending.set(key,controller);
   const url=this.identity.replace('{z}',tile.z).replace('{x}',tile.x).replace('{y}',tile.y);this.requestCount++;
   fetch(url,{signal:controller.signal}).then(async response=>{
    if(!response.ok)throw Error(`高程瓦片请求失败 ${response.status}`);
    const data=new Uint8Array(await response.arrayBuffer());
    if(data.length!==surfaceSize*surfaceSize*4)throw Error('GPU高程瓦片长度无效');
    if(generation!==this.generation||controller.signal.aborted||this.gl.isContextLost())return;
    this.tiles.set(key,{...tile,data,texture:null,used:performance.now()});this.trim();this.invalidate();this.labels();
   }).catch(e=>{if(e.name!=='AbortError'&&generation===this.generation){this.failures++;this.retryAfter.set(key,performance.now()+5000);this.onError(e.message);}})
    .finally(()=>{if(this.pending.get(key)===controller)this.pending.delete(key);this.pump();});
  }
 }
 cancel(){for(const c of this.pending.values())c.abort();this.pending.clear();this.queue.clear();}
 trim(){
  const keep=new Set((this.visible||[]).map(t=>this.keyFor(t)));
  const candidates=[...this.tiles].sort((a,b)=>(keep.has(a[0])-keep.has(b[0]))||a[1].used-b[1].used);
  while(this.tiles.size>this.maxTiles){const [key,tile]=candidates.shift();if(tile.texture)this.gl.deleteTexture(tile.texture);this.tiles.delete(key);}
 }
 texture(tile){
  tile.used=performance.now();
  if(tile.texture)return tile.texture;
  const gl=this.gl;tile.texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,tile.texture);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
  gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8,surfaceSize,surfaceSize,0,gl.RGBA,gl.UNSIGNED_BYTE,tile.data);return tile.texture;
 }
 withState(draw){
  const gl=this.gl;
  const state={program:gl.getParameter(gl.CURRENT_PROGRAM),vao:gl.getParameter(gl.VERTEX_ARRAY_BINDING),active:gl.getParameter(gl.ACTIVE_TEXTURE),blend:gl.isEnabled(gl.BLEND),depth:gl.isEnabled(gl.DEPTH_TEST),stencil:gl.isEnabled(gl.STENCIL_TEST),srcRGB:gl.getParameter(gl.BLEND_SRC_RGB),dstRGB:gl.getParameter(gl.BLEND_DST_RGB),srcAlpha:gl.getParameter(gl.BLEND_SRC_ALPHA),dstAlpha:gl.getParameter(gl.BLEND_DST_ALPHA)};
  gl.activeTexture(gl.TEXTURE0);state.texture=gl.getParameter(gl.TEXTURE_BINDING_2D);
  try{gl.useProgram(this.program);gl.bindVertexArray(this.vao);gl.enable(gl.BLEND);gl.disable(gl.DEPTH_TEST);gl.disable(gl.STENCIL_TEST);gl.blendFunc(gl.ONE,gl.ONE_MINUS_SRC_ALPHA);draw();}
  finally{gl.bindTexture(gl.TEXTURE_2D,state.texture);gl.activeTexture(state.active);gl.useProgram(state.program);gl.bindVertexArray(state.vao);
   for(const [key,cap] of [['blend',gl.BLEND],['depth',gl.DEPTH_TEST],['stencil',gl.STENCIL_TEST]])state[key]?gl.enable(cap):gl.disable(cap);
   gl.blendFuncSeparate(state.srcRGB,state.dstRGB,state.srcAlpha,state.dstAlpha);}
 }
 draw(tile,matrix,origin=[0,0],scale=1,screenMatrix=matrix,terrain=false,tileWorld=[0,0,1]){
  const gl=this.gl,u=this.uniforms,c=this.config,color=(c.color||'#8B5A2B').match(/[0-9a-f]{2}/gi).map(v=>parseInt(v,16)/255);
  gl.bindTexture(gl.TEXTURE_2D,this.texture(tile));gl.uniform1i(u.dem,0);
  gl.uniform2fv(u.uvOrigin,origin);gl.uniform1f(u.uvScale,scale);gl.uniformMatrix4fv(u.matrix,false,matrix);
  gl.uniform1f(u.interval,this.intervalForView());gl.uniform1f(u.major,c.majorEnabled===false?0:c.majorInterval??50);
  const center=this.map.getCenter(),intervals=contourIntervals(this.map.getZoom(),c.lowInterval??100,c.highInterval??10),p=groundProfile(this.map.getZoom(),center.lat);
  gl.uniform2f(u.distanceIntervals,intervals[1],intervals[2]);gl.uniform4f(u.depthBreaks,p.nearStart,p.nearEnd,p.farStart,p.farEnd);
  gl.uniform2fv(u.groundFocus,mercatorPoint(center.lng,center.lat));gl.uniform3fv(u.tileWorld,tileWorld);gl.uniform1f(u.worldMeters,40075016.68557849*Math.cos(center.lat*Math.PI/180));gl.uniform1f(u.detailStrength,terrain?detailStrength(this.map.getPitch()):0);
  gl.uniform1i(u.smooth,c.smooth===false?0:1);
  const canvas=this.map.getCanvas(),ratio=canvas.width/canvas.clientWidth;
  gl.uniformMatrix4fv(u.screenMatrix,false,screenMatrix);gl.uniform2f(u.screenSize,canvas.width,canvas.height);
  gl.uniform1f(u.width,(c.lineWidth??1.7)*ratio);gl.uniform1f(u.majorWidth,(c.majorLineWidth??3.4)*ratio);
  gl.uniform1f(u.opacity,c.contourOpacity??1);gl.uniform3fv(u.color,color);gl.uniform3fv(u.majorColor,(c.majorColor||c.color||'#8B5A2B').match(/[0-9a-f]{2}/gi).map(v=>parseInt(v,16)/255));gl.drawArrays(gl.TRIANGLES,0,6);
 }
 render(gl,options){
  if(!this.active()||!this.program)return;
  this.withState(()=>{for(const t of this.visible||[]){const tile=this.tiles.get(this.keyFor(t));if(!tile)continue;
   const m=options.defaultProjectionData.mainMatrix,n=2**t.z,x=t.worldX/n,y=t.y/n,result=new Float32Array(m);
   for(let row=0;row<4;row++){result[row]=m[row]/n;result[4+row]=m[4+row]/n;result[8+row]=0;result[12+row]=m[row]*x+m[4+row]*y+m[12+row];}
   this.draw(tile,result);this.draws2D++;
  }});
 }
 prerender(gl,options){
  this.screenProjection=options.defaultProjectionData.mainMatrix;
  const center=this.map.getCenter();
  const camera=[this.map.getZoom(),this.map.getPitch(),this.map.getBearing(),center.lng,center.lat,this.map.getTerrain()?.exaggeration].join('/');
  if(camera!==this.lastCamera){this.lastCamera=camera;if(this.map.getTerrain())this.invalidate();}
 }
 renderToTerrainTile(gl,{tileID,width}){
  if(!this.active()||!this.program)return;
  const parent=parentTile(tileID.canonical,this.config.surface.maxNativeZoom);const tile=this.request(parent);if(!tile)return;
  const canonical=tileID.canonical,n=2**canonical.z,x=(canonical.x+(tileID.wrap||0)*n)/n,y=canonical.y/n,m=this.screenProjection;
  if(!m)return;
  const screenMatrix=new Float32Array(m),latitude=Math.atan(Math.sinh(Math.PI*(1-2*(y+.5/n)))),meters=1/(40075016.68557849*Math.cos(latitude));
  const exaggeration=this.map.getTerrain()?.exaggeration??1;
  for(let row=0;row<4;row++){screenMatrix[row]=m[row]/n;screenMatrix[4+row]=m[4+row]/n;screenMatrix[8+row]=m[8+row]*meters*exaggeration;screenMatrix[12+row]=m[row]*x+m[4+row]*y+m[12+row];}
  this.withState(()=>this.draw(tile,terrainMatrix,parent.origin,parent.scale,screenMatrix,true,[x,y,1/n]));
  this.draws3D++;
 }
 distanceAt(longitude,latitude,height){
  const terrain=!!this.map.getTerrain(),pitch=terrain?this.map.getPitch():0;
  const center=this.map.getCenter(),relative=groundDistance(longitude,latitude,center.lng,center.lat);
  const weights=distanceWeights(relative,pitch,groundProfile(this.map.getZoom(),center.lat)),intervals=contourIntervals(this.map.getZoom(),this.config.lowInterval??100,this.config.highInterval??10);
  return {relative,weights,interval:labelInterval(intervals,weights),band:weights[2]>.12?2:weights[1]>.12?1:0};
 }
 labels(){
  // Bounded point-only search: no contour lines, connectivity, vector tiles or GPU readback.
  if(this.map.isMoving())return;
  if(!this.active())return;
  const candidates=[],features=[],canvas=document.createElement('canvas');
  const sample=this.config.smooth===false?bilinearHeight:smoothHeight;
  const before=this.map.getStyle().layers.find(l=>!['background','raster','custom'].includes(l.type)&&!l.id.startsWith('dem-'))?.id;
  if(!this.map.getSource('dem-contour-labels'))this.map.addSource('dem-contour-labels',{type:'geojson',data:empty()});
  if(!this.map.getLayer('dem-contour-labels'))this.map.addLayer({id:'dem-contour-labels',source:'dem-contour-labels',type:'symbol',minzoom:13,
   layout:{'icon-image':['get','image'],'icon-pitch-alignment':'viewport','icon-rotation-alignment':'viewport','icon-padding':12},paint:{'icon-opacity':this.config.contourOpacity??1}},before);
  this.map.setLayoutProperty('dem-contour-labels','icon-image',['get','image']);
  this.map.setPaintProperty('dem-contour-labels','icon-opacity',this.config.contourOpacity??1);
  const color=this.config.color||'#8B5A2B',major=this.config.majorInterval??50;
  const canvasWidth=this.map.getCanvas().clientWidth,canvasHeight=this.map.getCanvas().clientHeight;
  for(let sy=70;sy<canvasHeight-40&&candidates.length<320;sy+=100)for(let sx=70;sx<canvasWidth-40&&candidates.length<320;sx+=120){
    const coordinate=this.map.unproject([sx,sy]);const z=Math.min(this.config.surface.maxNativeZoom,Math.floor(this.map.getZoom())),n=2**z;
    const wx=(coordinate.lng+180)/360*n,wy=(1-Math.asinh(Math.tan(coordinate.lat*Math.PI/180))/Math.PI)/2*n;
    const tx=Math.floor(wx),ty=Math.floor(wy),entry=this.tiles.get(this.keyFor({z,x:((tx%n)+n)%n,y:ty}));if(!entry)continue;
    let px=(wx-tx)*512+1.5,py=(wy-ty)*512+1.5,h=sample(entry.data,px,py);if(h===null)continue;
    const interval=this.distanceAt(coordinate.lng,coordinate.lat,h).interval;
    const level=Math.round(h/interval)*interval;if(level===0)continue;let validGradient=false;
    for(let i=0;i<6;i++){
     const a=sample(entry.data,px-1,py),b=sample(entry.data,px+1,py),c=sample(entry.data,px,py-1),d=sample(entry.data,px,py+1);
     if([a,b,c,d].some(v=>v===null))break;
     const dx=(b-a)/2,dy=(d-c)/2,g=dx*dx+dy*dy;if(g<1e-6)break;
     validGradient=true;
     const delta=Math.max(-8,Math.min(8,(h-level)/g));px-=delta*dx;py-=delta*dy;
     h=sample(entry.data,px,py);if(h===null||Math.abs(h-level)<.02)break;
    }
    if(!validGradient||h===null||Math.abs(h-level)>.1)continue;
    const lon=(tx+(px-1.5)/512)/n*360-180,lat=Math.atan(Math.sinh(Math.PI*(1-2*(ty+(py-1.5)/512)/n)))*180/Math.PI;
    const p=this.map.project([lon,lat]);if(Math.hypot(p.x-sx,p.y-sy)>65||p.x<10||p.y<10||p.x>canvasWidth-10||p.y>canvasHeight-10)continue;
    const detail=this.distanceAt(lon,lat,h);
    if(Math.abs(level/detail.interval-Math.round(level/detail.interval))>1e-7)continue;
    candidates.push({x:p.x,y:p.y,lon,lat,level,band:detail.band});
  }
  const labelOptions={flat:!this.map.getTerrain(),width:canvasWidth,height:canvasHeight};
  this.labelCandidateCount=candidates.length;this.labelLimit=labelBudget(candidates.length,labelOptions);this.labelBands=[0,0,0];
  for(const {lon,lat,level,band} of selectContourLabels(candidates,labelOptions)){
    const labelColor=this.config.majorEnabled!==false&&Math.abs(level/major-Math.round(level/major))<1e-8?(this.config.majorColor||color):color;
    const image=`gpu-contour-label-slot-${features.length}`,signature=`${level}/${labelColor}`;
    if(!this.map.hasImage(image)||this.labelSignatures.get(image)!==signature){
     const ctx=canvas.getContext('2d');canvas.width=100;canvas.height=32;ctx.font='22px sans-serif';ctx.textAlign='center';ctx.textBaseline='middle';ctx.lineJoin='round';ctx.lineWidth=5;ctx.strokeStyle='white';ctx.strokeText(String(level),50,16);ctx.fillStyle=labelColor;ctx.fillText(String(level),50,16);
     const data=ctx.getImageData(0,0,100,32);if(this.map.hasImage(image))this.map.updateImage(image,data);else this.map.addImage(image,data,{pixelRatio:2});this.labelImages.add(image);this.labelSignatures.set(image,signature);
    }
    if(this.map.hasImage(image)){features.push({type:'Feature',geometry:{type:'Point',coordinates:[lon,lat]},properties:{image,elevation:level,band,major:Math.abs(level/major-Math.round(level/major))<1e-8}});this.labelBands[band]++;}
  }
  this.labelCount=features.length;this.map.getSource('dem-contour-labels')?.setData({type:'FeatureCollection',features});
 }
 releaseTiles(){for(const t of this.tiles.values())if(t.texture&&!this.gl.isContextLost())this.gl.deleteTexture(t.texture);this.tiles.clear();}
 lost(){this.cancel();this.program=null;this.vao=null;this.buffer=null;for(const t of this.tiles.values())t.texture=null;}
 restored(){try{this.initialize();this.invalidate();this.schedule();}catch(e){this.disabled=true;this.onError(e.message);}}
 onRemove(){clearTimeout(this.timer);this.cancel();this.releaseTiles();const gl=this.gl;if(!gl.isContextLost()){if(this.program)gl.deleteProgram(this.program);if(this.vao)gl.deleteVertexArray(this.vao);if(this.buffer)gl.deleteBuffer(this.buffer);}
  this.map.getSource('dem-contour-labels')?.setData(empty());this.labelCount=0;this.program=null;this.vao=null;this.buffer=null;
  this.map.off('moveend',this.moved);this.map.off('zoom',this.moved);this.map.off('style.load',this.moved);this.map.getCanvas().removeEventListener('webglcontextlost',this.contextLost);this.map.getCanvas().removeEventListener('webglcontextrestored',this.contextRestored);
  for(const image of this.labelImages)if(this.map.hasImage(image))this.map.removeImage(image);this.labelImages.clear();
 }
 inspect(){return{active:this.active(),smooth:this.config.smooth!==false,identity:this.identity?.startsWith('data:')?'synthetic-data':this.identity,tiles:this.tiles.size,pending:this.pending.size,queued:this.queue.size,compiles:this.compiles,draws2D:this.draws2D,draws3D:this.draws3D,labels:this.labelCount||0,labelCandidates:this.labelCandidateCount||0,labelLimit:this.labelLimit||0,labelBands:this.labelBands||[0,0,0],distanceIntervals:contourIntervals(this.map.getZoom(),this.config.lowInterval??100,this.config.highInterval??10),groundRange:groundProfile(this.map.getZoom(),this.map.getCenter().lat),requests:this.requestCount,failures:this.failures,gpuBytes:[...this.tiles.values()].filter(t=>t.texture).length*surfaceSize*surfaceSize*4,cpuBytes:this.tiles.size*surfaceSize*surfaceSize*4,revision:this.terrainTileRevision};}
}
