import * as maplibregl from "./vendor/maplibre-gl.mjs";

export async function loadFavoriteSymbols(map, style) {
  for (const [key, svg] of [["favorite-purple-star", style.svgMarkup], ["favorite-gold-star", style.selectedSvgMarkup]]) {
    if (map.hasImage(key)) continue;
    await new Promise((resolve, reject) => { const image = new Image(24, 24);
      image.onload = () => { map.addImage(key, image); resolve(); };
      image.onerror = () => reject(new Error("收藏点图标加载失败。"));
      image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
    });
  }
  const chosen = ["==", ["get", "favoriteId"], "__none__"];
  map.addLayer({ id: "favorites-selected-glow-layer", type: "circle", source: "favorites", filter: chosen,
    paint: { "circle-radius": style.glowRadius, "circle-color": style.selectedColor,
      "circle-opacity": style.glowOpacity, "circle-blur": style.glowBlur } });
  map.addLayer({ id: "favorites-symbol-layer", type: "symbol", source: "favorites",
    layout: { "icon-image": "favorite-purple-star", "icon-size": style.sizePixels / 24,
      "icon-allow-overlap": true, "icon-ignore-placement": true } });
  map.addLayer({ id: "favorites-selected-symbol-layer", type: "symbol", source: "favorites", filter: chosen,
    layout: { "icon-image": "favorite-gold-star", "icon-size": style.sizePixels / 24,
      "icon-allow-overlap": true, "icon-ignore-placement": true } });
  // 本地图像字形仍通过一个Symbol Layer显示，不依赖在线字体或每点DOM Marker。
  map.addLayer({ id: "favorites-label-layer", type: "symbol", source: "favorites", filter: ["!=", ["get", "name"], ""],
    layout: { "icon-image": ["get", "labelImage"], "icon-anchor": "top", "icon-offset": [0, 13],
      "icon-allow-overlap": true, "icon-ignore-placement": true,
      "text-allow-overlap": true, "text-ignore-placement": true } });
}

export class FavoriteMapInteraction {
  constructor(map, post, revision, data, setData) {
    Object.assign(this, { map, post, revision, data, setData });
    this.selectedId = null; this.selectionRevision = 0; this.hover = null; this.sequence = 0; this.drag = null;
    this.pending = null; this.ignoreClickUntil = 0; this.labelKeys = new Set();
    map.on("mousemove", e => this.pointerMove(e));
    map.on("mousedown", e => this.pointerDown(e));
    map.on("mouseup", e => this.pointerUp(e));
    const closeForMapMovement = event => { this.hoverBlockedPoint = this.lastPointer; this.lastMapClose = { type: event.type, point: this.lastPointer, hoverId: this.hover?.id }; this.closeHover(); };
    map.on("movestart", closeForMapMovement);
    map.on("zoomstart", closeForMapMovement);
    map.getCanvas().addEventListener("mouseleave", () => this.leaveMarker());
    window.addEventListener("blur", () => { this.closeHover(); this.cancelDrag(); });
    window.addEventListener("keydown", e => { if (e.key === "Escape") this.cancelDrag(); });
    window.addEventListener("mouseup", e => { if (this.drag && e.target !== map.getCanvas()) this.cancelDrag(); });
    window.addEventListener("pointercancel", () => this.cancelDrag());
  }
  applyData(data, visible, style, selectedId, selectionRevision) {
    this.cancelDrag(); this.closeHover(); this.pending = null; this.style = style; this.visible = visible;
    const keys = new Set();
    for (const feature of data.features) {
      const name = String(feature.properties.name || "").trim();
      feature.properties.name = name;
      if (!name) { feature.properties.labelImage = ""; continue; }
      const key = "favorite-name-" + feature.properties.favoriteId + "-" + name;
      keys.add(key); feature.properties.labelImage = key;
      if (!this.map.hasImage(key)) {
        const canvas = document.createElement("canvas"), context = canvas.getContext("2d");
        context.font = '14px "Microsoft YaHei", sans-serif';
        const lines = []; let line = "";
        for (const char of name) { if (context.measureText(line + char).width > 400 && line) { lines.push(line); line = ""; } line += char; }
        lines.push(line); canvas.width = Math.ceil(Math.max(...lines.map(s => context.measureText(s).width))) + 8;
        canvas.height = lines.length * 19 + 6; context.font = '14px "Microsoft YaHei", sans-serif';
        context.textAlign = "center"; context.textBaseline = "top"; context.lineWidth = 3;
        context.strokeStyle = "white"; context.fillStyle = "#20252a";
        lines.forEach((s, i) => { context.strokeText(s, canvas.width / 2, 3 + 19 * i); context.fillText(s, canvas.width / 2, 3 + 19 * i); });
        this.map.addImage(key, context.getImageData(0, 0, canvas.width, canvas.height));
      }
    }
    this.setData(data); this.map.getSource("favorites").setData(data);
    for (const key of this.labelKeys) if (!keys.has(key) && this.map.hasImage(key)) this.map.removeImage(key);
    this.labelKeys = keys; this.select(selectedId, selectionRevision);
    for (const layer of ["favorites-symbol-layer", "favorites-selected-symbol-layer", "favorites-selected-glow-layer", "favorites-label-layer"])
      this.map.setLayoutProperty(layer, "visibility", visible ? "visible" : "none");
  }
  select(id, selectionRevision) {
    if (selectionRevision != null) { if (selectionRevision < this.selectionRevision) id = this.selectedId; else this.selectionRevision = selectionRevision; }
    this.selectedId = id || null;
    for (const layer of ["favorites-selected-symbol-layer", "favorites-selected-glow-layer"])
      if (this.map.getLayer(layer)) this.map.setFilter(layer, ["==", ["get", "favoriteId"], id || "__none__"]);
    if (this.map.getLayer("favorites-symbol-layer")) this.map.setFilter("favorites-symbol-layer", ["!=", ["get", "favoriteId"], id || "__none__"]);
  }
  hit(point) {
    const layers = ["favorites-selected-symbol-layer", "favorites-symbol-layer"].filter(id => this.map.getLayer(id));
    return layers.length ? this.map.queryRenderedFeatures(point, { layers })[0] || null : null;
  }
  clicked(feature) {
    if (performance.now() < this.ignoreClickUntil) return;
    this.select(feature.properties.favoriteId);
    this.post("FavoriteClicked", { revision: this.revision(), favoriteId: this.selectedId });
  }
  pointerMove(e) {
    if(this.map.__hikingOfflineSelecting)return;
    this.lastPointer = { x: e.point.x, y: e.point.y };
    if (e.originalEvent?.target?.closest?.(".favorite-map-popup")) return;
    if (this.drag) {
      if (!this.drag.active && (Math.abs(e.point.x - this.drag.point.x) >= this.thresholdX || Math.abs(e.point.y - this.drag.point.y) >= this.thresholdY)) {
        this.drag.active = true; this.closeHover();
        this.post("FavoriteDragStarted", { revision: this.drag.revision, favoriteId: this.drag.id, requestId: this.drag.request });
      }
      if (this.drag.active) { this.drag.current = [e.lngLat.lng, e.lngLat.lat]; this.preview(this.drag.id, this.drag.current); }
      return;
    }
    if (this.pending || this.map.isMoving()) return;
    // 地图变换可能重新派发相同坐标的mousemove；静止鼠标不应立即重开已关闭的信息牌。
    if (this.hoverBlockedPoint) {
      // WebView2在125%缩放下可把同一物理鼠标位置舍入为相邻CSS像素。
      // 2px内的排队事件/量化抖动不视为重新移入；真实移开再进入仍可打开。
      if (Math.abs(e.point.x - this.hoverBlockedPoint.x) <= 2 && Math.abs(e.point.y - this.hoverBlockedPoint.y) <= 2) return;
      this.hoverBlockedPoint = null;
      this.lastHoverUnblock = { point: this.lastPointer, eventType: e.originalEvent?.type, trusted: e.originalEvent?.isTrusted };
    }
    const feature = this.hit(e.point);
    if (!feature) { this.leaveMarker(); return; }
    const id = feature.properties.favoriteId;
    if (this.hover?.id === id) { this.hover.marker = true; clearTimeout(this.closeTimer); return; }
    this.closeHover(); const request = ++this.sequence;
    this.hover = { id, request, revision: this.revision(), marker: true, card: false };
    this.post("FavoriteHoverEntered", { revision: this.revision(), favoriteId: id, requestId: request });
  }
  leaveMarker() { if (this.hover?.marker) { this.hover.marker = false; this.scheduleClose(); } }
  scheduleClose() {
    clearTimeout(this.closeTimer); const hover = this.hover;
    this.closeTimer = setTimeout(() => { if (this.hover === hover && !hover.marker && !hover.card) this.closeHover(); }, this.style?.hoverCloseMilliseconds || 125);
  }
  closeHover() {
    clearTimeout(this.closeTimer); const old = this.hover; this.hover = null;
    this.popup?.remove(); this.popup = null;
    if (old) this.post("FavoriteHoverLeft", { revision: old.revision, favoriteId: old.id, requestId: old.request });
  }
  showHover(payload, revision) {
    const hover = this.hover;
    if (!hover || hover.id !== payload.favoriteId || hover.request !== payload.requestId || hover.revision !== revision || this.drag || this.pending) return;
    const feature = this.data().features.find(f => f.properties.favoriteId === hover.id); if (!feature) return;
    const position = this.map.project(feature.geometry.coordinates), canvas = this.map.getCanvas();
    const sideRoom = Math.max(position.x, canvas.clientWidth - position.x);
    let anchor, maximumHeight = Math.min(380, canvas.clientHeight - 40);
    if (sideRoom >= 350) {
      const side = position.x < canvas.clientWidth / 2 ? "left" : "right";
      anchor = position.y < maximumHeight / 2 + 20 ? "top-" + side : position.y > canvas.clientHeight - maximumHeight / 2 - 20 ? "bottom-" + side : side;
    } else {
      anchor = position.y >= canvas.clientHeight / 2 ? "bottom" : "top";
      maximumHeight = Math.max(90, (anchor === "bottom" ? position.y : canvas.clientHeight - position.y) - 35);
    }
    const card = document.createElement("div"); card.className = "favorite-map-card";
    card.style.cssText = `max-width:300px;max-height:${Math.max(90, maximumHeight)}px;overflow:auto;white-space:pre-wrap;word-break:break-word`;
    const add = text => { const line = document.createElement("div"); line.textContent = text; card.appendChild(line); };
    add(payload.name); add("收藏夹：" + payload.collection); add(payload.description);
    add("海拔：" + (payload.elevation == null ? "无数据" : payload.elevation.toFixed(2) + "米"));
    add(`纬度：${payload.latitude.toFixed(6)}\n经度：${payload.longitude.toFixed(6)}`);
    if (payload.thumbnailUrl) {
      const image = document.createElement("img"), width = payload.thumbnailWidth || 240, height = payload.thumbnailHeight || 150;
      const scale = Math.min(240 / width, 150 / height); image.style.cssText = `width:${width * scale}px;height:${height * scale}px;object-fit:contain`;
      image.src = payload.thumbnailUrl; card.appendChild(image);
    }
    if (payload.imageCount) add("图片：" + payload.imageCount + "张");
    for (const url of payload.links || []) {
      try { const parsed = new URL(url); if (!["https:", "http:"].includes(parsed.protocol)) continue;
        const link = document.createElement("a"); link.textContent = url; link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
        link.addEventListener("click", e => { e.preventDefault(); this.post("FavoriteLinkRequested", { revision: this.revision(), favoriteId: hover.id, message: url }); }); card.appendChild(link);
      } catch { /* 无效URL不创建链接 */ }
    }
    this.popup?.remove(); this.popup = new maplibregl.Popup({ closeButton: false, closeOnClick: false, anchor, offset: 12, maxWidth: Math.min(320, canvas.clientWidth - 30) + "px" })
      .setLngLat(feature.geometry.coordinates).setDOMContent(card).addTo(this.map);
    const popupElement = this.popup.getElement(); popupElement.classList.add("favorite-map-popup");
    popupElement.addEventListener("mouseenter", () => { if (this.hover === hover) { hover.card = true; hover.marker = false; clearTimeout(this.closeTimer); } });
    popupElement.addEventListener("mouseleave", event => {
      if (this.hover !== hover) return;
      hover.card = false; const rect = this.map.getCanvas().getBoundingClientRect();
      hover.marker = this.hit({ x: event.clientX - rect.left, y: event.clientY - rect.top })?.properties?.favoriteId === hover.id;
      if (!hover.marker) this.closeHover();
    });
  }
  pointerDown(e) {
    if(this.map.__hikingOfflineSelecting)return;
    if (e.originalEvent?.target?.closest?.(".favorite-map-popup")) return;
    if (e.originalEvent?.button !== 0) return;
    const feature = this.hit(e.point); if (!feature) return;
    if(this.map.getTerrain()){this.post("TerrainEditBlocked");this.clicked(feature);return;}
    e.preventDefault(); this.clicked(feature); this.closeHover();
    this.drag = { id: feature.properties.favoriteId, start: [...feature.geometry.coordinates], current: [...feature.geometry.coordinates],
      point: e.point, active: false, request: ++this.sequence, revision: this.revision(), pan: this.map.dragPan.isEnabled() };
    this.map.dragPan.disable();
  }
  pointerUp() {
    const drag = this.drag; if (!drag) return; this.drag = null;
    if (drag.pan) this.map.dragPan.enable();
    this.ignoreClickUntil = performance.now() + 180;
    if (!drag.active) return;
    this.pending = drag;
    this.post("FavoriteDragCompleted", { revision: drag.revision, favoriteId: drag.id, requestId: drag.request,
      latitude: drag.current[1], longitude: drag.current[0] });
  }
  preview(id, coordinate) {
    const data = this.data(); const feature = data.features.find(f => f.properties.favoriteId === id);
    if (feature) { feature.geometry.coordinates = coordinate; this.map.getSource("favorites").setData(data); }
  }
  cancelDrag() {
    const drag = this.drag; if (!drag) return; this.drag = null; this.preview(drag.id, drag.start);
    if (drag.pan) this.map.dragPan.enable(); this.ignoreClickUntil = performance.now() + 180;
    this.post("FavoriteDragCanceled", { revision: drag.revision, favoriteId: drag.id, requestId: drag.request });
  }
  endMove(payload) {
    if (!this.pending || this.pending.request !== payload.requestId || this.pending.id !== payload.favoriteId) return;
    this.preview(payload.favoriteId, [payload.longitude, payload.latitude]); this.pending = null;
  }
}

