using System.IO;
using System.Text.Json;
using HikingGuide.App.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;

public partial class App
{
    private async Task RunTerrainNavigationSmokeAsync(MainWindow window, MainViewModel main, WebView2 web, string folder, Dictionary<string,bool> checks, List<object> diagnostics)
    {
        async Task<JsonElement> JS(string code) => JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
        Task<JsonElement> Camera() => JS("window.hikingGuide3DTest.inspect().camera");
        async Task Mouse(string type, double x, double y, string button="none", int buttons=0, int modifiers=0) =>
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type,x,y,button,buttons,modifiers,clickCount=type=="mouseMoved"?0:1 }));
        async Task Drag(double dx, double dy, bool shift=false)
        {
            var button=shift?"left":"right";var buttons=shift?1:2;var modifiers=shift?8:0;
            await Mouse("mouseMoved",250,180);
            await Mouse("mousePressed",250,180,button,buttons,modifiers);
            for(var i=1;i<=8;i++){await Mouse("mouseMoved",250+dx*i/8,180+dy*i/8,button,buttons,modifiers);await Task.Delay(25);}
            await Mouse("mouseReleased",250+dx,180+dy,button,0,modifiers);await Task.Delay(200);
        }
        async Task Click(string selector)
        {
            var p=await JS($"(()=>{{const r=document.querySelector('{selector}').getBoundingClientRect();return {{x:r.x+r.width/2,y:r.y+r.height/2}};}})()");
            var x=p.GetProperty("x").GetDouble();var y=p.GetProperty("y").GetDouble();
            await Mouse("mouseMoved",x,y);await Mouse("mousePressed",x,y,"left",1);await Mouse("mouseReleased",x,y,"left");await Task.Delay(400);
        }
        static double Value(JsonElement c,string name)=>c.GetProperty(name).GetDouble();
        static double Delta(double a,double b)=>(a-b+540)%360-180;
        await JS("window.hikingGuide3DTest.camera(55,0)");await Task.Delay(300);
        var sky=(await JS("window.hikingGuide3DTest.inspect().sky"));
        checks["T160天蓝地平线向浅蓝天顶渐变"]=sky.GetProperty("horizon-color").GetString()=="#87CEEB"&&sky.GetProperty("sky-color").GetString()=="#B5E2F5";
        checks["T160导航提示已改为右键与左键"]=(await JS("document.querySelector('.terrain-orbit-hint').textContent")).GetString()!.StartsWith("右键 / Shift＋左键");
        var oldOffline=GlobalSettingsService.Current.OfflineTerrain;
        GlobalSettingsService.Current.OfflineTerrain=oldOffline with{ContourOpacity=.35};
        await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(600);
        var opacity=await JS("window.hikingGuideTerrainTest.inspect()");
        checks["T160在3D中粗细等高线与数字同步透明度"]=opacity.GetProperty("layers").EnumerateArray().Where(l=>l.GetProperty("id").GetString()!.Contains("contours")).All(l=>Math.Abs(l.GetProperty("opacity").GetDouble()-.35)<1e-8)&&Math.Abs(opacity.GetProperty("labelOpacity").GetDouble()-.35)<1e-8;
        GlobalSettingsService.Current.OfflineTerrain=oldOffline;await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
        var before=await Camera();var blocked=main.Map.TerrainBlockedEditCount;
        await Drag(180,0);var after=await Camera();
        checks["导航实际右拖改变方位不改变俯仰"]=Math.Abs(Delta(Value(after,"bearing"),Value(before,"bearing"))-63)<1&&Math.Abs(Value(after,"pitch")-55)<.1;
        checks["导航旋转不误触坐标编辑"]=main.Map.TerrainBlockedEditCount==blocked;
        var view=FavoritesPanel.Descendants<MapView>(window).Single();
        checks["导航右拖后不误弹操作菜单"]=view.ActiveMapMenuForAutomation?.IsOpen!=true&&view.ActiveTrackMenuForAutomation?.IsOpen!=true&&view.ActiveFavoriteMenu?.IsOpen!=true;
        var total=63d;var last=Value(after,"bearing");
        for(var i=0;i<6;i++){await Drag(180,0);var c=await Camera();var b=Value(c,"bearing");total+=Delta(b,last);last=b;}
        checks["导航连续旋转超过360度仍保持3D"]=total>400&&main.Map.EffectiveTerrainState;
        await JS("window.hikingGuide3DTest.camera(55,1)");await Drag(10,0);after=await Camera();
        checks["导航接近正北不自动吸回"]=Math.Abs(Value(after,"bearing")-4.5)<.2;
        await Drag(-80,40,true);after=await Camera();
        checks["导航Shift左拖同时旋转俯仰"]=Math.Abs(Delta(Value(after,"bearing"),4.5)+28)<.2&&Math.Abs(Value(after,"pitch")-65)<.2;
        before=after;await Click(".terrain-north");after=await Camera();
        var beforeCenter=before.GetProperty("center").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        var afterCenter=after.GetProperty("center").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        checks["导航实际向北按钮只归零方位"]=Math.Abs(Value(after,"bearing"))<.05&&Math.Abs(Value(after,"pitch")-Value(before,"pitch"))<.05&&
            Math.Abs(Value(after,"zoom")-Value(before,"zoom"))<.001&&beforeCenter.Zip(afterCenter,(a,b)=>Math.Abs(a-b)).Max()<1e-7;
        diagnostics.Add(new{NorthBefore=before,NorthAfter=after});
        await Click(".terrain-orbit-controls button:nth-child(2)");after=await Camera();
        checks["导航实际右转按钮增加15度"]=Math.Abs(Value(after,"bearing")-15)<.1;
        await Click(".terrain-orbit-controls button:nth-child(4)");after=await Camera();
        checks["导航实际俯视按钮减小10度"]=Math.Abs(Value(after,"pitch")-55)<.1;
        await Task.Delay(800);
        checks["导航用户旋转结果写入全局相机"]=GlobalSettingsService.Current.TerrainCamera is {} saved&&Math.Abs(saved.Bearing-15)<.1&&Math.Abs(saved.Pitch-55)<.1;
        await CaptureT10ClientAsync(window,Path.Combine(folder,"3D-自由旋转与向北.png"));
        await Mouse("mousePressed",240,200,"right",2);await Mouse("mouseReleased",240,200,"right");await Task.Delay(250);
        checks["导航右键轻点保留原操作菜单"]=view.ActiveMapMenuForAutomation?.IsOpen==true||view.ActiveTrackMenuForAutomation?.IsOpen==true||view.ActiveFavoriteMenu?.IsOpen==true;
        if(view.ActiveMapMenuForAutomation is {} menu)menu.IsOpen=false;
        if(view.ActiveTrackMenuForAutomation is {} trackMenu)trackMenu.IsOpen=false;
        if(view.ActiveFavoriteMenu is {} favoriteMenu)favoriteMenu.IsOpen=false;
        await JS("window.hikingGuide3DTest.camera(55,0)");await Task.Delay(250);
        diagnostics.Add(new{NavigationTotalRotationDegrees=total,Camera=after});
    }
}
