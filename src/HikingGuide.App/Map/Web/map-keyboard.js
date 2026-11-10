export function mapPanKey(event) {
  if(event.defaultPrevented||event.ctrlKey||event.metaKey||event.altKey)return null;
  if(event.target?.closest?.('input,textarea,select,[contenteditable]:not([contenteditable="false"]),[role="textbox"]'))return null;
  // 中文输入法下key可能为Process，code仍是物理按键。地图不是文字输入区。
  const key=/^Key[WASD]$/.test(event.code||'')?event.code.slice(3).toLowerCase():!event.isComposing?event.key?.toLowerCase():null;
  return ({w:[0,-100],a:[-100,0],s:[0,100],d:[100,0]})[key]||null;
}
