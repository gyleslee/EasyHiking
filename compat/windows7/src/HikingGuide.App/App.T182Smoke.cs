using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Elevation;
using Microsoft.Web.WebView2.Wpf;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunTerrainGeneratorSmokeAsync(MainWindow window,MainViewModel main,string output)
    {
        var checks=new Dictionary<string,bool>();var diagnostics=new List<object>();string? error=null;
        var folder=Path.GetDirectoryName(output)!;
        try
        {
            var dem=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Name.Contains("Fathom",StringComparison.OrdinalIgnoreCase));
            var alternate=DemCatalog.Databases.First(d=>d.Status==DemDatabaseStatus.Ready&&d.Id!=dem.Id);
            GlobalSettingsService.Current.OfflineTerrain=new(){DemDatabaseId=dem.Id,ContoursVisible=false,HillshadeVisible=false};
            GlobalSettingsService.Current.Terrain3D=new(){Enabled=false,TerrainDemDatabaseId=dem.Id,ShowContours=true,ShowHillshade=true};
            await GlobalSettingsService.SaveAsync();
            for(var i=0;i<200&&!main.Map.IsReady;i++)await Task.Delay(100);
            if(!main.Map.IsReady)throw new InvalidOperationException("地图尚未就绪。");
            await main.Map.ApplyGlobalMapLayersAsync(true);
            var mapView=FavoritesPanel.Descendants<MapView>(window).Single();
            var bridge=T10ManualField<WebViewMapBridge>(mapView,"_bridge");
            var service=T10ManualField<DemTerrainTiles>(bridge,"_terrainTiles");
            var web=FavoritesPanel.Descendants<WebView2>(window).First(w=>w.Source?.Host=="hikingguide.local");
            async Task<JsonElement> JS(string code)=>JsonSerializer.Deserialize<JsonElement>(await web.CoreWebView2.ExecuteScriptAsync(code));
            async Task Wait(Func<Task<bool>> ready,string message,int seconds=15)
            {for(var i=0;i<seconds*10;i++){if(await ready())return;await Task.Delay(100);}throw new InvalidOperationException(message);}
            OfflineTerrainSettings Active()=>T10ManualField<OfflineTerrainSettings>(service,"_settings");
            async Task<JsonElement> State()=>await JS("window.hikingGuideTerrainTest.inspect()");
            await main.Map.ApplyGlobalMapLayersAsync(true);
            await JS("window.hikingGuide3DTest.at([113.32,24.42],16)");
            async Task Generator(bool contours,bool shade,Guid? chosen=null)
            {
                var manager=new MapSourceManagerWindow(GlobalSettingsService,MapSourceTestService,MapSourceTemplateService){Owner=window};
                manager.Show();manager.LibraryTabs.SelectedIndex=1;await T10ManualLayoutAsync(manager);
                manager.TerrainDem.SelectedValue=chosen??dem.Id;
                manager.TerrainContours.IsChecked=contours;manager.TerrainHillshade.IsChecked=shade;
                manager.TerrainContourOpacity.Value=37;manager.TerrainOpacity.Value=46;
                manager.TerrainColor.Content="#7A4D28";manager.TerrainMajorInterval.Text="100";
                FavoritesPanel.Descendants<Button>(manager).Single(b=>b.Content?.ToString()=="应用并保存等高线生成设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>Task.FromResult(manager.TerrainStatus.Text.Contains("设置已保存",StringComparison.Ordinal)),"生成器按钮未保存设置："+manager.TerrainStatus.Text);
                await Wait(()=>Task.FromResult(Active().ContoursVisible==contours&&Active().HillshadeVisible==shade&&Active().DemDatabaseId==(chosen??dem.Id)),"生成器设置未传入实际瓦片服务");
                manager.Close();await Task.Delay(250);
            }
            async Task Check(string prefix,bool contours,bool shade)
            {
                var state=await State();var layers=state.GetProperty("layers");
                static bool Visible(JsonElement layer)=>layer.ValueKind!=JsonValueKind.Null&&layer.GetProperty("visible").GetBoolean();
                checks[prefix+"图层显隐服从生成器"]=Visible(layers[0])==shade&&Visible(layers[1])==contours&&Visible(layers[2])==contours;
                var active=Active();
                checks[prefix+"DEM和样式服从生成器"]=active.DemDatabaseId==dem.Id&&active.ContourColor=="#7A4D28"&&active.MajorContourIntervalMeters==100&&Math.Abs(active.ContourOpacity-.37)<1e-8&&Math.Abs(active.HillshadeOpacity-.46)<1e-8;
                var sig=T10ManualField<string>(service,"_signature");
                foreach(var kind in new[]{"contours","labels","hillshade"})
                {
                    if(kind=="hillshade"?shade:contours)continue;
                    var generated=service.GeneratedTiles;var blocked=false;
                    try{await service.GetAsync(new Uri($"https://hikingguide-terrain.local/{sig}/{kind}/15/26698/14090."+(kind=="labels"?"json":"png")));}
                    catch(OperationCanceledException){blocked=true;}
                    checks[prefix+kind+"关闭后拒绝生成请求"]=blocked&&service.GeneratedTiles==generated;
                }
                if(!contours)checks[prefix+"关闭后不残留3D数字"]=(await JS("window.hikingGuide3DTest.inspect().labelCount")).GetInt32()==0;
            }
            async Task Loaded()=>await Wait(async()=>{
                var layers=(await State()).GetProperty("layers");
                return layers[0].ValueKind!=JsonValueKind.Null&&layers[0].GetProperty("loaded").GetBoolean()&&layers[2].ValueKind!=JsonValueKind.Null&&layers[2].GetProperty("loaded").GetBoolean();
            },"当前视野的等高线/阴影未加载完成",90);
            async Task Controls(string prefix)
            {
                var value=await JS("(()=>{const n=document.querySelector('.terrain-navigation'),z=document.querySelector('.maplibregl-ctrl-zoom-in'),m=document.querySelector('#map').getBoundingClientRect(),r=n.getBoundingClientRect();return {bottomRight:n.parentElement.classList.contains('maplibregl-ctrl-bottom-right')&&z.closest('.maplibregl-ctrl-bottom-right')!==null,width:r.width,inBounds:r.bottom<=m.bottom&&r.right<=m.right,bottomAnchored:Math.abs(n.parentElement.getBoundingClientRect().bottom-m.bottom)<1,order:(()=>{const a=document.querySelector('.maplibregl-ctrl-attrib').getBoundingClientRect(),g=z.parentElement.getBoundingClientRect();return g.bottom<=r.top&&r.bottom<=a.top;})(),overflow:[n,...n.querySelectorAll('button')].some(e=>e.scrollWidth>e.clientWidth+1),orbitHidden:n.querySelector('.terrain-orbit-controls').hidden};})()");
                checks[prefix+"缩放与导航位于地图右下角"]=value.GetProperty("bottomRight").GetBoolean()&&value.GetProperty("inBounds").GetBoolean()&&value.GetProperty("bottomAnchored").GetBoolean()&&value.GetProperty("order").GetBoolean();
                checks[prefix+"导航宽度减半且文字无横向溢出"]=Math.Abs(value.GetProperty("width").GetDouble()-81)<.1&&!value.GetProperty("overflow").GetBoolean();
                diagnostics.Add(new{Mode=prefix,Controls=value});
            }
            foreach(var mode in new[]{false,true})
            {
                var prefix=mode?"3D":"2D";
                // 故意保留与生成器相反的旧3D开关，验证它们不再拥有第二套控制权。
                GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D! with{Enabled=mode,ShowContours=true,ShowHillshade=true};
                await GlobalSettingsService.SaveAsync();await main.Map.ApplyGlobalMapLayersAsync(true);
                if(mode){await JS("window.hikingGuide3DTest.camera(55,30)");await Wait(async()=>(await JS("window.hikingGuide3DTest.inspect().effective")).GetBoolean(),"3D地形未就绪",90);}
                foreach(var flags in new[]{(false,false),(true,false),(false,true),(true,true)})
                {await Generator(flags.Item1,flags.Item2);await Check(prefix+$"[{flags.Item1},{flags.Item2}]",flags.Item1,flags.Item2);}
                await Loaded();await Controls(prefix);
                await using(var capture=File.Create(Path.Combine(folder,$"T182-{prefix}-右下角导航与生成器.png")))
                    await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,capture);
                if(mode)
                {
                    await Generator(true,true,alternate.Id);
                    checks["3D山体不覆盖生成器独立选择的DEM"]=Active().DemDatabaseId==alternate.Id&&GlobalSettingsService.Current.Terrain3D!.TerrainDemDatabaseId==dem.Id;
                    await Generator(false,false);await Check("3D最后关闭",false,false);
                    await using(var off=File.Create(Path.Combine(folder,"T182-3D-等高线与阴影均关闭.png")))
                        await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,off);
                    var dialog=new Terrain3DWindow(main.Map){Owner=window};dialog.Show();
                    await Wait(()=>Task.FromResult(dialog.DemBox.Items.Count>0&&dialog.StatusText.Text.Contains("当前为3D",StringComparison.Ordinal)),"3D设置窗口未加载");
                    checks["3D窗口显示生成器状态且重复控件不可写"]=dialog.Contours.IsChecked==false&&dialog.Shade.IsChecked==false&&!dialog.Contours.IsEnabled&&!dialog.Shade.IsEnabled&&!dialog.ShadeOpacity.IsEnabled&&Math.Abs(dialog.ShadeOpacity.Value-46)<1e-8;
                    var before=GlobalSettingsService.Current.OfflineTerrain;
                    var commit=typeof(Terrain3DWindow).GetMethod("CommitAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                    await (Task<bool>)commit.Invoke(dialog,null)!;
                    checks["保存3D设置不改写生成器"]=before==GlobalSettingsService.Current.OfflineTerrain;
                    await CaptureT10ClientAsync(dialog,Path.Combine(folder,"T182-3D设置-生成器状态回显.png"));dialog.Close();
                    await Generator(true,true);
                    GlobalSettingsService.Current.Terrain3D=GlobalSettingsService.Current.Terrain3D! with{ShowContours=false,ShowHillshade=false};
                    await main.Map.ApplyGlobalMapLayersAsync(true);await Check("3D旧开关关闭",true,true);
                    GlobalSettingsService.Current.OfflineTerrain=before with{DemDatabaseId=null,ContoursVisible=true,HillshadeVisible=true};
                    await main.Map.ApplyGlobalMapLayersAsync(true);await Task.Delay(300);
                    var config=JsonSerializer.Deserialize<JsonElement>((await State()).GetProperty("signature").GetString()!);
                    checks["3D未选择生成器DEM时不自动借用山体DEM"]=!config.GetProperty("contours").GetBoolean()&&!config.GetProperty("hillshade").GetBoolean()&&Active().DemDatabaseId is null;
                    await Generator(true,true);await Loaded();
                    await RunTerrainNavigationSmokeAsync(window,main,web,folder,checks,diagnostics);
                }
            }
            await main.Map.ToggleTerrainAsync();await Task.Delay(600);await Check("退出3D后",true,true);await Controls("退出3D后");
            checks["模式切换不改写生成器持久化参数"]=!GlobalSettingsService.Current.OfflineTerrain.ScreenAlignedLabels&&GlobalSettingsService.Current.OfflineTerrain.DemDatabaseId==dem.Id;
        }
        catch(Exception exception){error=exception.ToString();}
        var passed=error is null&&checks.Count>0&&checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Passed=passed,Version=HikingGuide.Core.Software.SoftwareIdentity.DisplayVersion,Checks=checks,Diagnostics=diagnostics,Error=error},new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        return passed;
    }
}
