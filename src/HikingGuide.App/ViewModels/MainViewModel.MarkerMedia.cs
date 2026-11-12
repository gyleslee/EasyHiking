using System.IO;
using HikingGuide.Application.Maps;
using HikingGuide.App.Services;
using HikingGuide.Core.Models;
namespace HikingGuide.App.ViewModels;
public sealed partial class MainViewModel
{
    internal HikingGuide.Application.Media.IExternalMediaDownloadService? MarkerDownloaderForAutomation {get;set;}
    internal void OpenMarkerMediaForAutomation()=>_=OpenMarkerImageViewerAsync();
    private readonly SemaphoreSlim _markerThumbnailGate=new(2,2);
    private CancellationTokenSource _markerThumbnailLifetime=new();
    private Guid? _markerThumbnailProject;
    private readonly Dictionary<string,byte[]> _markerThumbnailBytes=[];
    private async Task PrepareMarkerThumbnailAsync(MapEventMessage request)
    {
        var project=CurrentProject;var id=request.WaypointId??request.GuideNodeId;
        if(project is null||project.Id!=request.ProjectId||id is null)return;
        if(_markerThumbnailProject!=project.Id||_markerThumbnailLifetime.IsCancellationRequested){_markerThumbnailLifetime.Cancel();_markerThumbnailLifetime.Dispose();_markerThumbnailLifetime=new();_markerThumbnailProject=project.Id;_markerThumbnailBytes.Clear();}
        var token=_markerThumbnailLifetime.Token;
        try
        {
            await _markerThumbnailGate.WaitAsync(token);
            try
            {
                if(!ReferenceEquals(CurrentProject,project))return;
                var attachment=project.MediaAttachments.Where(a=>a.OwnerId==id).OrderBy(a=>a.DisplayOrder).FirstOrDefault();
                var reference=project.ExternalMediaReferences.Where(a=>a.OwnerId==id).OrderBy(a=>a.DisplayOrder).FirstOrDefault();
                var key=$"{project.Id}/{id}/{attachment?.MediaBlobId}/{reference?.Url}";
                if(!_markerThumbnailBytes.TryGetValue(key,out var bytes))
                {
                    if(attachment is not null){var blob=project.MediaBlobs.First(b=>b.Id==attachment.MediaBlobId);bytes=await _thumbnails.GetInternalThumbnailAsync(project,blob,token);}
                    else if(reference is not null){var cached=await Task.Run(()=>(MarkerDownloaderForAutomation??_externalMediaDownloader).DownloadAsync(reference.Url,cancellationToken:token),token);bytes=await _thumbnails.GetExternalThumbnailAsync(cached.CachePath,token);}
                    if(bytes is null)return;
                    if(_markerThumbnailBytes.Count>=256)_markerThumbnailBytes.Remove(_markerThumbnailBytes.Keys.First());_markerThumbnailBytes[key]=bytes;
                }
                if(!token.IsCancellationRequested&&ReferenceEquals(CurrentProject,project))await Map.SetMarkerPopupThumbnailAsync(project.Id,request.Revision,id.Value,bytes);
            }
            finally{_markerThumbnailGate.Release();}
        }
        catch(OperationCanceledException){}
        catch(Exception error){_log.Warning($"标注缩略图暂不可用：{error.GetType().Name}");await Map.SetMarkerPopupErrorAsync(project.Id,request.Revision,id.Value,"缩略图暂不可用，点击下载并查看可重试。");}
    }
    private bool _markerViewerActive;
    private async Task OpenMarkerImageViewerAsync()
    {
        if(_markerViewerActive||CurrentProject is not {} project||SelectedMarker is not {} marker)return;
        _markerViewerActive=true;
        try
        {
            StatusText="正在准备同轨迹图片标注顺序…";
            var album=await Task.Run(()=>new HikingGuide.Application.Media.MarkerPhotoNavigation().Build(project,marker));
            if(!ReferenceEquals(CurrentProject,project)||_applicationClosePending)return;
            var index=album.ToList().FindIndex(m=>m.Id==marker.Id);var saved=false;
            IReadOnlyList<FavoriteImageViewItem> Items(MapMarkerBase owner)=>BuildMarkerImageItems(project,owner,()=>saved=true);
            bool CanMove(int step)=>ReferenceEquals(CurrentProject,project)&&index>=0&&index+step>=0&&index+step<album.Count;
            ImageViewerGroup? Navigate(int step)
            {
                if(!CanMove(step))return null;index+=step;var next=album[index];
                if(_mapNodes.TryGetValue(next.Id,out var node))SelectNavigationNode(node,false);
                return new($"{next.Name??"标注"}｜图片",Items(next));
            }
            var items=Items(marker);if(items.Count==0){StatusText="该标注没有图片。";return;}
            var viewer=new FavoriteImageViewerWindow(items,title:$"{marker.Name??"标注"}｜图片",navigate:Navigate,canNavigate:CanMove){Owner=System.Windows.Application.Current.MainWindow};
            StatusText="同轨迹有图片标注已就绪。";viewer.ShowDialog();if(saved)RefreshProjectView();
        }
        catch(Exception error){_log.Error(error,"打开标注图片失败");_dialogs.ShowError("打开图片失败",error.Message);}
        finally{_markerViewerActive=false;}
    }
    private IReadOnlyList<FavoriteImageViewItem> BuildMarkerImageItems(HikingProject project,MapMarkerBase marker,Action saved)
    {
        var items=new List<FavoriteImageViewItem>();
        foreach(var attachment in project.MediaAttachments.Where(a=>a.OwnerId==marker.Id).OrderBy(a=>a.DisplayOrder))
        {
            var blob=project.MediaBlobs.First(b=>b.Id==attachment.MediaBlobId);
            items.Add(new(attachment.OriginalFileName??"项目图片",async token=>{var bytes=await Task.Run(()=>_projectMedia.ReadOriginalAsync(blob,token),token);return await Task.Run(()=>FavoriteImageService.Decode(bytes),token);},attachment.DisplayOrder));
        }
        foreach(var reference in project.ExternalMediaReferences.Where(r=>r.OwnerId==marker.Id&&!project.MediaAttachments.Any(a=>a.OwnerId==marker.Id&&a.SourceReference==r.Url)).OrderBy(r=>r.DisplayOrder))
            items.Add(new(reference.DisplayName??"网络图片",async token=>{var result=await Task.Run(()=>(MarkerDownloaderForAutomation??_externalMediaDownloader).DownloadAsync(reference.Url,cancellationToken:token),token);token.ThrowIfCancellationRequested();await SaveExternalToProjectAsync(project,reference,result);saved();return await Task.Run(async()=>FavoriteImageService.Decode(await File.ReadAllBytesAsync(result.CachePath,token)),token);},reference.DisplayOrder));
        return items.OrderBy(i=>i.DisplayOrder).ToArray();
    }
}
