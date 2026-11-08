using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using HikingGuide.Application.Routing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed record RoutePlanningPointRow(int Order, string Type, string Latitude,
    string Longitude, string NextLegMode);

public sealed partial class MainViewModel
{
    private IRoutingProvider _routingProvider = new UnavailableProvider();
    private RoutePlanService? _routePlan;
    private bool _routePlanningBusy;
    private readonly CancellationTokenSource _routeLifetime = new();
    private CancellationTokenSource? _routeRequest;
    private RelayCommand _startBRouterPlanCommand = null!;
    private RelayCommand _startManualPlanCommand = null!;
    private RelayCommand _startMixedPlanCommand = null!;
    private RelayCommand _nextBRouterLegCommand = null!;
    private RelayCommand _nextManualLegCommand = null!;
    private RelayCommand _undoPlanningPointCommand = null!;
    private RelayCommand _redoPlanningPointCommand = null!;
    private RelayCommand _clearPlanningCommand = null!;
    private AsyncRelayCommand _completePlanningCommand = null!;
    private RelayCommand _cancelPlanningCommand = null!;
    private string _routePlanningStatus = "未开始规划";

    public ObservableCollection<RoutePlanningPointRow> RoutePlanningPoints { get; } = [];
    public bool IsRoutePlanningActive => _routePlan is not null;
    internal RoutePlan? RoutePlanForAutomation => _routePlan?.Plan;
    public string RoutePlanningStatus
    {
        get => _routePlanningStatus;
        private set => SetField(ref _routePlanningStatus, value);
    }
    public string CurrentRouteLegModeLabel => _routePlan?.Plan.CurrentLegMode == RouteLegMode.Manual
        ? "下一段：手绘直线" : "下一段：BRouter 徒步最短";
    public string RoutePlanningModeLabel => _routePlan?.Plan.PlanningMode switch
    {
        RoutePlanningMode.BRouter => "BRouter路由规划",
        RoutePlanningMode.Manual => "手绘规划",
        RoutePlanningMode.Mixed => "混合规划",
        _ => "未开始规划"
    };
    public bool IsMixedRoutePlanning => _routePlan?.Plan.PlanningMode == RoutePlanningMode.Mixed;
    public ICommand StartBRouterPlanCommand => _startBRouterPlanCommand;
    public ICommand StartManualPlanCommand => _startManualPlanCommand;
    public ICommand StartMixedPlanCommand => _startMixedPlanCommand;
    public ICommand NextBRouterLegCommand => _nextBRouterLegCommand;
    public ICommand NextManualLegCommand => _nextManualLegCommand;
    public ICommand UndoPlanningPointCommand => _undoPlanningPointCommand;
    public ICommand RedoPlanningPointCommand => _redoPlanningPointCommand;
    public ICommand ClearPlanningCommand => _clearPlanningCommand;
    public ICommand CompletePlanningCommand => _completePlanningCommand;
    public ICommand CancelPlanningCommand => _cancelPlanningCommand;
    public event EventHandler? RoutePlanningStarted;
    public event EventHandler? OpenBRouterDataRequested;

    public void ConfigureRouting(IRoutingProvider provider) =>
        _routingProvider = provider ?? throw new ArgumentNullException(nameof(provider));

    private void InitializeRoutingCommands()
    {
        _startBRouterPlanCommand = new RelayCommand(() => _ = StartRoutePlanAsync(RoutePlanningMode.BRouter),
            () => !IsBusy && CurrentProject is not null && !IsTrackEditActive);
        _startManualPlanCommand = new RelayCommand(() => _ = StartRoutePlanAsync(RoutePlanningMode.Manual),
            () => !IsBusy && CurrentProject is not null && !IsTrackEditActive);
        _startMixedPlanCommand = new RelayCommand(() => _ = StartRoutePlanAsync(RoutePlanningMode.Mixed),
            () => !IsBusy && CurrentProject is not null && !IsTrackEditActive);
        _nextBRouterLegCommand = new RelayCommand(() => SwitchNextLeg(RouteLegMode.BRouter),
            () => IsMixedRoutePlanning && !_routePlanningBusy);
        _nextManualLegCommand = new RelayCommand(() => SwitchNextLeg(RouteLegMode.Manual),
            () => IsMixedRoutePlanning && !_routePlanningBusy);
        _undoPlanningPointCommand = new RelayCommand(() => ChangeLastPoint(false),
            () => _routePlan?.Plan.ControlPoints.Count > 0 && !_routePlanningBusy);
        _redoPlanningPointCommand = new RelayCommand(() => ChangeLastPoint(true),
            () => _routePlan?.CanRedo == true && !_routePlanningBusy);
        _clearPlanningCommand = new RelayCommand(ClearRoutePlan,
            () => _routePlan?.Plan.ControlPoints.Count > 0 && !_routePlanningBusy);
        _completePlanningCommand = new AsyncRelayCommand(CompleteRoutePlanAsync,
            () => _routePlan?.Plan.Legs.Count > 0 && !_routePlanningBusy && !IsBusy);
        _cancelPlanningCommand = new RelayCommand(() => CancelRoutePlan(true),
            () => _routePlan is not null && !_routePlanningBusy);
    }

    private async Task StartRoutePlanAsync(RoutePlanningMode mode)
    {
        if (CurrentProject is not { } project || IsTrackEditActive) return;
        if (_routePlan?.Plan.ControlPoints.Count > 0 && !ConfirmDiscardRoutePlan()) return;
        _routeRequest?.Cancel();
        _routePlan = new RoutePlanService(project.Id, mode, _routingProvider);
        RoutePlanningStatus = "请在地图上点击第一个规划点。";
        RefreshRoutePlanningDisplay();
        await Map.BeginRoutePlanningAsync();
        await Map.SetRoutePreviewAsync(_routePlan.Plan);
        RoutePlanningStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnRoutePlanningPointPicked(object? sender, HikingGuide.Application.Maps.MapEventMessage value)
    {
        if (_routePlan is null || CurrentProject?.Id != _routePlan.Plan.ProjectId ||
            value.ProjectId != _routePlan.Plan.ProjectId ||
            value.Latitude is not double latitude || value.Longitude is not double longitude ||
            _routePlanningBusy) return;
        _ = AddRoutePointAsync(_routePlan, new GeoCoordinate(latitude, longitude));
    }

    private async Task AddRoutePointAsync(RoutePlanService service, GeoCoordinate coordinate)
    {
        _routePlanningBusy = true;
        _routeRequest = CancellationTokenSource.CreateLinkedTokenSource(_routeLifetime.Token);
        RoutePlanningStatus = service.Plan.ControlPoints.Count == 0
            ? "正在设置起点……" : "正在后台计算新增分段……";
        RaiseRoutingCommandStates();
        try
        {
            await service.AddPointAsync(coordinate, _routeRequest.Token);
            if (!ReferenceEquals(_routePlan, service) || CurrentProject?.Id != service.Plan.ProjectId) return;
            RoutePlanningStatus = $"已选择 {service.Plan.ControlPoints.Count} 个规划点、{service.Plan.Legs.Count} 段。";
            RefreshRoutePlanningDisplay();
            await Map.SetRoutePreviewAsync(service.Plan);
        }
        catch (OperationCanceledException)
        {
            RoutePlanningStatus = "路线计算已取消，原有规划保持不变。";
        }
        catch (Exception exception)
        {
            _log.Error(exception, "[RoutePlan] 新分段计算失败");
            RoutePlanningStatus = "新增分段失败，原有规划保持不变。";
            if (exception.Message.Contains("缺少BRouter路由数据", StringComparison.Ordinal))
            {
                if (MessageBox.Show($"{exception.Message}\n\n是否打开BRouter路由数据管理？",
                        "当前区域缺少路由数据", MessageBoxButton.YesNo,
                        MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenBRouterDataRequested?.Invoke(this, EventArgs.Empty);
            }
            else _dialogs.ShowError("路线规划失败", exception.Message);
        }
        finally
        {
            _routeRequest?.Dispose();
            _routeRequest = null;
            _routePlanningBusy = false;
            RaiseRoutingCommandStates();
        }
    }

    private void SwitchNextLeg(RouteLegMode mode)
    {
        if (_routePlan?.Plan.PlanningMode != RoutePlanningMode.Mixed) return;
        _routePlan.SetNextLegMode(mode);
        RoutePlanningStatus = CurrentRouteLegModeLabel;
        RefreshRoutePlanningDisplay();
    }

    private void ChangeLastPoint(bool redo)
    {
        if (_routePlan is null) return;
        if (redo ? !_routePlan.RedoLastPoint() : !_routePlan.UndoLastPoint()) return;
        RoutePlanningStatus = redo ? "已恢复原规划分段，不重新请求BRouter。" : "已撤销最后一个规划点。";
        RefreshRoutePlanningDisplay();
        _ = Map.SetRoutePreviewAsync(_routePlan.Plan);
    }

    private void ClearRoutePlan()
    {
        if (_routePlan is null || !ConfirmDiscardRoutePlan()) return;
        _routePlan.Clear();
        RoutePlanningStatus = "规划已清空，请点击地图重新选点。";
        RefreshRoutePlanningDisplay();
        _ = Map.SetRoutePreviewAsync(_routePlan.Plan);
    }

    private async Task CompleteRoutePlanAsync()
    {
        if (_routePlan is not { } service || CurrentProject?.Id != service.Plan.ProjectId) return;
        var window = new RouteTrackInfoWindow(service.Plan.Name)
            { Owner = System.Windows.Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        try
        {
            var track = service.BuildTrack(window.TrackName, window.TrackDescription);
            var project = CurrentProject!;
            _history.Execute("完成路线规划", () => project.AddTrack(track, false),
                () => project.RemoveTrack(track.Id));
            _routePlan = null;
            await Map.EndRoutePlanningAsync();
            RefreshRoutePlanningDisplay();
            RefreshProjectView();
            RoutePlanningStatus = $"已生成普通轨迹：{track.Name}；未自动设为主攻略轨迹。";
        }
        catch (Exception exception)
        {
            _dialogs.ShowError("完成规划失败", exception.Message);
        }
    }

    private bool ConfirmDiscardRoutePlan() => MessageBox.Show(
        "当前有未完成路线规划。放弃本次规划吗？", "未完成路线规划",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void CancelRoutePlan(bool confirm)
    {
        if (_routePlan is null) return;
        if (confirm && _routePlan.Plan.ControlPoints.Count > 0 && !ConfirmDiscardRoutePlan()) return;
        _routeRequest?.Cancel();
        _routePlan = null;
        RoutePlanningStatus = "已取消规划，项目轨迹未改变。";
        RefreshRoutePlanningDisplay();
        _ = Map.EndRoutePlanningAsync();
    }

    private void CheckRoutePlanProjectSwitch()
    {
        if (_routePlan is null || CurrentProject?.Id == _routePlan.Plan.ProjectId) return;
        if (_routePlan.Plan.ControlPoints.Count > 0 && !ConfirmDiscardRoutePlan())
        {
            var owner = _projects.OpenProjects.FirstOrDefault(item =>
                item.Project.Id == _routePlan.Plan.ProjectId);
            if (owner is not null)
            {
                _projects.Activate(owner.ContextId);
                return;
            }
        }
        CancelRoutePlan(false);
    }

    private void RefreshRoutePlanningDisplay()
    {
        RoutePlanningPoints.Clear();
        if (_routePlan is { } service)
        {
            foreach (var point in service.Plan.ControlPoints)
                RoutePlanningPoints.Add(new(point.Order + 1,
                    point.Type == RouteControlPointType.Start ? "起点" :
                    point.Type == RouteControlPointType.End ? "终点" : "途经点",
                    point.Coordinate.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    point.Coordinate.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    point.Order < service.Plan.Legs.Count
                        ? service.Plan.Legs[point.Order].Mode == RouteLegMode.Manual ? "手绘" : "BRouter"
                        : CurrentRouteLegModeLabel));
        }
        OnPropertyChanged(nameof(IsRoutePlanningActive));
        OnPropertyChanged(nameof(IsMixedRoutePlanning));
        OnPropertyChanged(nameof(CurrentRouteLegModeLabel));
        OnPropertyChanged(nameof(RoutePlanningModeLabel));
        RaiseRoutingCommandStates();
    }

    private void RaiseRoutingCommandStates()
    {
        _startBRouterPlanCommand?.RaiseCanExecuteChanged();
        _startManualPlanCommand?.RaiseCanExecuteChanged();
        _startMixedPlanCommand?.RaiseCanExecuteChanged();
        _nextBRouterLegCommand?.RaiseCanExecuteChanged();
        _nextManualLegCommand?.RaiseCanExecuteChanged();
        _undoPlanningPointCommand?.RaiseCanExecuteChanged();
        _redoPlanningPointCommand?.RaiseCanExecuteChanged();
        _clearPlanningCommand?.RaiseCanExecuteChanged();
        _completePlanningCommand?.RaiseCanExecuteChanged();
        _cancelPlanningCommand?.RaiseCanExecuteChanged();
    }

    private sealed class UnavailableProvider : IRoutingProvider
    {
        public Task<RoutingResult> RouteFootShortestAsync(GeoCoordinate start, GeoCoordinate end,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("尚未准备BRouter本地引擎或路由数据。请先打开BRouter设置。");
    }
}
