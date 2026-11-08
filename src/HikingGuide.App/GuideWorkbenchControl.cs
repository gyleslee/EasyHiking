using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Elevation;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Guides;
using Microsoft.Win32;

namespace HikingGuide.App;

public sealed partial class GuideWorkbenchControl : UserControl
{
    public sealed record SourceChoice(Guid? Id, string Name, bool Gps = false);
    public sealed class Row : INotifyPropertyChanged
    {
        public required Guid Id { get; init; }
        public required Dictionary<string, object?> Values { get; init; }
        public required Action<Row, string, string> Save { get; init; }
        public string CalculationStatus { get; set; } = "";
        public Dictionary<string, string> BoundaryTooltips { get; init; } = [];
        public string BoundaryTooltip(string key) => BoundaryTooltips.GetValueOrDefault(key) ?? GuideTablePresentation.BoundaryTooltip(Values.GetValueOrDefault(key)?.ToString() ?? "");
        public void UpdateValues(Row row)
        {
            foreach (var pair in row.Values) Values[pair.Key] = pair.Value;
            BoundaryTooltips.Clear(); foreach (var pair in row.BoundaryTooltips) BoundaryTooltips[pair.Key] = pair.Value;
            CalculationStatus = row.CalculationStatus;
            PropertyChanged?.Invoke(this, new("Item[]")); PropertyChanged?.Invoke(this, new(nameof(CalculationStatus)));
        }
        public string this[string key]
        {
            get => key.StartsWith("Tooltip:", StringComparison.Ordinal) ? BoundaryTooltip(key[8..]) : Values.GetValueOrDefault(key) switch { TimeOnly t => t.ToString("HH:mm"), double n => GuideTablePresentation.CellNumber(key, n), null => key is "ActualArrival" or "PlannedDuration" or "RoadCondition" || key.StartsWith("Arrival:", StringComparison.Ordinal) ? "" : "—", var v => key.StartsWith("Plan:", StringComparison.Ordinal) ? GuideTablePresentation.BoundaryLabel(v.ToString() ?? "") : v.ToString() ?? "" };
            set
            {
                // 虚拟化回收/切换ItemsSource会暂时把ComboBox.SelectedItem置null。
                // 这不是用户选择“留空”，不能清除人工值或产生验证错误。
                if (value is null || this[key] == value) return;
                Save(this, key, value); Values[key] = value; PropertyChanged?.Invoke(this, new("Item[]"));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class DayRow : INotifyPropertyChanged
    {
        public required GuideDailyItinerary Day { get; init; }
        public required Action<string> Save { get; init; }
        public required Action<string> SaveNotes { get; init; }
        public required string Start { get; init; }
        public required string End { get; init; }
        public int Number => Day.DayNumber;
        public string Distance => Day.DistanceMeters is double n ? GuideTablePresentation.Number(n / 1000, 1) : "";
        public string Ascent => Day.AscentMeters is double n ? GuideTablePresentation.Number(n, 0) : "";
        public string Descent => Day.DescentMeters is double n ? GuideTablePresentation.Number(n, 0) : "";
        public string Duration => Day.PlannedDurationHours is double n ? GuideTablePresentation.Number(n, 1) : "未填写完整";
        private string? _departure;
        public string Departure { get => _departure ?? Day.Departure?.ToString("HH:mm") ?? ""; set { if (value is null || Departure == value) return; Save(value); _departure = value; PropertyChanged?.Invoke(this, new(nameof(Departure))); } }
        private string? _notes;
        public string Notes { get => _notes ?? Day.Notes; set { if (value is null || Notes == value) return; SaveNotes(value); _notes = value; PropertyChanged?.Invoke(this, new(nameof(Notes))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static App Host => (App)System.Windows.Application.Current;
    private MainViewModel? Main => DataContext as MainViewModel;
    private readonly ComboBox _source = new() { MinWidth = 210, DisplayMemberPath = "Name", Margin = new(3) };
    private readonly TextBox[] _speeds = [new() { Width = 55 }, new() { Width = 55 }, new() { Width = 55 }];
    private readonly TextBlock _status = new() { Margin = new(4), TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, FrozenColumnCount = 3, SelectionMode = DataGridSelectionMode.Single };
    private readonly ComboBox _plan = new() { Width = 130, DisplayMemberPath = "Name" };
    private readonly Button _confirmDailyStarts = new() { Content = "确认每日边界", Margin = new(3), Padding = new(5, 2, 5, 2), IsEnabled = false,
        ToolTip = "每日起点已删除或方案待确认时，核对并确认当前显示的行程；保留出发时间、人工用时和备注，不自动猜测替代节点。" };
    private readonly DataGrid _days = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MaxHeight = 120 };
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private HikingProject? _project;
    private CancellationTokenSource? _calculation;
    private string? _inputFingerprint, _structure, _presentation;
    private bool _loading, _checking, _refreshQueued, _committing;
    private GuideExportSnapshot? _snapshot;
    public DataGrid Table => _table;
    public DataGrid DailyTable => _days;
    public string Status => _status.Text;
    internal Button DailyBoundaryConfirmationButton => _confirmDailyStarts;
    public string? LastAutomationEditError { get; private set; }
    public Task? CalculationTask { get; private set; }
    private bool _closeSuspended;
    private Task _calculationCompletion = Task.CompletedTask;
    public void SuspendForClose() { _closeSuspended = true; _monitor.Stop(); _calculation?.Cancel(); }
    public Task WaitForBackgroundUpdatesAsync() => Task.WhenAll(CalculationTask ?? Task.CompletedTask, _calculationCompletion);
    public void ResumeAfterCancelledClose() { _closeSuspended = false; SelectProject(Main?.MapContextProject); if (IsLoaded) _monitor.Start(); }
    public void SetSourceForAutomation(Guid? id, bool gps) => _source.SelectedItem = _source.Items.OfType<SourceChoice>().First(c => c.Gps == gps && (gps || c.Id == id));
    public void SetSpeedsForAutomation(string flat, string gentle, string steep) { _speeds[0].Text = flat; _speeds[1].Text = gentle; _speeds[2].Text = steep; SaveSpeeds(); }
    public void RefreshForAutomation() => RefreshRows();
    internal void SelectPlanForAutomation(Guid id) => _plan.SelectedItem = _plan.Items.OfType<PlanChoice>().First(p => p.Id == id);
    public async Task<bool> EditCellForAutomationAsync(int index, string key, string text)
    {
        // 等待前一次提交排队的刷新和虚拟化布局完成，再取得当前行身份。
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        _table.UpdateLayout();
        var row = _table.Items.OfType<Row>().ElementAt(index); var column = _table.Columns.First(c => c.SortMemberPath == key);
        _table.ScrollIntoView(row, column); _table.SelectedItem = row; _table.CurrentCell = new(row, column);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); _table.UpdateLayout();
        DependencyObject? current = column.GetCellContent(row);
        while (current is not null && current is not DataGridCell) current = VisualTreeHelper.GetParent(current);
        var targetCell = current as DataGridCell;
        targetCell?.Focus(); _table.CurrentCell = new(row, column);
        if (key.StartsWith("Location:", StringComparison.Ordinal))
        {
            var content = column.GetCellContent(row);
            var combo = content as ComboBox ?? (content is null ? null : FavoritesPanel.Descendants<ComboBox>(content).FirstOrDefault());
            if (combo is null) { LastAutomationEditError = "地点选择器未生成"; return false; }
            combo.IsDropDownOpen = true; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            combo.SelectedItem = text; combo.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource(); combo.IsDropDownOpen = false;
            return row[key] == text && CommitEdits();
        }
        if (!_table.BeginEdit() && targetCell?.IsEditing != true) { LastAutomationEditError = "不能进入编辑：" + key + ";列只读=" + column.IsReadOnly + ";单元格只读=" + targetCell?.IsReadOnly + ";当前列=" + _table.CurrentColumn?.SortMemberPath + ";验证=" + string.Join(";", FavoritesPanel.Descendants<FrameworkElement>(_table).SelectMany(Validation.GetErrors).Select(e => e.ErrorContent)); return false; }
        await Dispatcher.InvokeAsync(() => _table.UpdateLayout(), DispatcherPriority.Render);
        var cell = column.GetCellContent(row); var editor = cell as TextBox ?? (cell is null ? null : FavoritesPanel.Descendants<TextBox>(cell).FirstOrDefault());
        if (column is DataGridComboBoxColumn)
        {
            var combo = cell as ComboBox;
            if (combo is null) { LastAutomationEditError = "下拉单元格尚未生成：" + cell?.GetType().FullName; return false; }
            combo.IsDropDownOpen = true; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            combo.SelectedItem = text; combo.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource(); combo.IsDropDownOpen = false;
            var committed = CommitEdits(); if (!committed) LastAutomationEditError = "下拉选项未提交：" + _status.Text; return committed;
        }
        if (editor is null)
        {
            var combo = cell as ComboBox ?? (cell is null ? null : FavoritesPanel.Descendants<ComboBox>(cell).FirstOrDefault());
            if (combo is null) { LastAutomationEditError = "找不到编辑器：" + cell?.GetType().FullName; return false; } combo.SelectedItem = text; combo.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
        }
        else { editor.Text = text; editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
        return CommitEdits();
    }
    public async Task<bool> EditDepartureForAutomationAsync(int index, string text)
    {
        var row = _days.Items.OfType<DayRow>().ElementAt(index); var column = _days.Columns.First(c => (string)c.Header == "出发时间");
        _days.ScrollIntoView(row, column); _days.CurrentCell = new(row, column); _days.Focus();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var content = column.GetCellContent(row);
        var picker = content as GuideTimePicker ?? (content is null ? null : FavoritesPanel.Descendants<GuideTimePicker>(content).FirstOrDefault());
        if (picker is null) return false;
        picker.Value = text;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); return CommitEdits();
    }
    private GuideDataCoordinator Coordinator => new(Host.DemCatalog, (IElevationProvider?)Host.DemProvider ?? EmptyProvider.Instance);
    private sealed class EmptyProvider : IElevationProvider { public static readonly EmptyProvider Instance = new(); public ValueTask<double?> SampleAsync(Guid id, HikingGuide.Core.Geometry.GeoCoordinate p, CancellationToken cancellationToken = default) => ValueTask.FromResult<double?>(null); }

    public GuideWorkbenchControl()
    {
        UseBorderOnlySelection(_table);
        UseBorderOnlySelection(_days);
        ObserveAutoFit(_table); ObserveAutoFit(_days);
        var dock = new DockPanel { Margin = new(5) }; Content = dock;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        var settings = new WrapPanel(); top.Children.Add(settings);
        AddLabel(settings, "攻略高程来源："); settings.Children.Add(_source);
        foreach (var (name, index) in new[] { ("下降/平路速度", 0), ("缓坡爬升速度", 1), ("陡坡爬升速度", 2) })
        { AddLabel(settings, name); _speeds[index].Margin = new(3); _speeds[index].ToolTip = "km/h；留空不计算均速用时，数值必须大于0。"; settings.Children.Add(_speeds[index]); _speeds[index].LostKeyboardFocus += (_, _) => SaveSpeeds(); _speeds[index].KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveSpeeds(); }; }
        var flatThreshold = new LocusTerrainDistanceProfile().FlatSlopeThresholdPercent;
        _speeds[1].ToolTip = FormattableString.Invariant($"缓坡：坡度大于{flatThreshold}%且小于{GuideTimingService.SteepUphillThresholdPercent}%；速度单位km/h，留空不计算均速用时，数值必须大于0。");
        _speeds[2].ToolTip = FormattableString.Invariant($"陡坡：坡度大于或等于{GuideTimingService.SteepUphillThresholdPercent}%；速度单位km/h，留空不计算均速用时，数值必须大于0。");
        AddLabel(settings, "km/h");
        Button(settings, "分析 / 更新", async () => { if (CommitEdits()) await CalculateAsync(true); });
        Button(settings, "取消", () => { _calculation?.Cancel(); return Task.CompletedTask; });
        Button(settings, "行进方案管理", () => { ManagePlans(); return Task.CompletedTask; });
        settings.Children.Add(_confirmDailyStarts);
        _confirmDailyStarts.Click += (_, _) => { if (_plan.SelectedItem is PlanChoice choice) ConfirmDailyBoundaries(choice.Id, Window.GetWindow(this)); };
        Button(settings, "地点情况管理", () => { ManageLocations(); return Task.CompletedTask; });
        Button(settings, "人工数据归档", () => { ShowArchives(); return Task.CompletedTask; });
        Button(settings, "导出攻略Excel", ExportAsync);
        top.Children.Add(_status);
        var bottom = new DockPanel { Margin = new(0, 5, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var dayControls = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(dayControls, Dock.Top); bottom.Children.Add(dayControls);
        AddLabel(dayControls, "每日行程方案："); dayControls.Children.Add(_plan); AddLabel(dayControls, "选择出发时间后自动更新计划抵达时间；方案列点击节点设置每日起点。边界失效时请先确认每日边界。");
        bottom.Children.Add(_days); dock.Children.Add(_table);
        foreach (var (name, property, editable) in new[] { ("天数", "Number", false), ("起点", "Start", false), ("终点", "End", false), ("出发时间", "Departure", true), ("距离km", "Distance", false), ("爬升m", "Ascent", false), ("下降m", "Descent", false), ("计划总用时h", "Duration", false), ("备注", "Notes", true) })
        {
            DataGridColumn column = property == "Departure" ? CreateDepartureColumn() : new DataGridTextColumn
            { Binding = new Binding(property) { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay, ValidatesOnExceptions = true },
              ElementStyle = CenteredTextStyle(), EditingElementStyle = CenteredEditorStyle(property == "Notes"), IsReadOnly = !editable };
            column.Header = name; column.SortMemberPath = property; column.MinWidth = 40; column.CanUserResize = true;
            column.Width = property == "Notes" ? 240 : 90;
            RegisterColumnWidth(column, "Daily:" + property); _days.Columns.Add(column);
        }
        _table.BeginningEdit += (_, e) => { if (_snapshot is not null && _snapshot.Rows[0].NodeId == ((Row)e.Row.Item).Id && e.Column.SortMemberPath == "PlannedDuration") e.Cancel = true; };
        EnableKeyboardEditing(_table); EnableKeyboardEditing(_days);
        _table.CellEditEnding += (_, _) => QueueRefresh(); _days.CellEditEnding += (_, _) => QueueRefresh();
        _table.MouseDoubleClick += (_, _) => { if (_table.CurrentColumn?.SortMemberPath == "RoadCondition" && _table.SelectedItem is Row row) EditRoad(row); };
        _table.SelectionChanged += (_, _) => { if (!_loading && _table.SelectedItem is Row row) Main?.LocateGuideNode(row.Id); };
        _source.SelectionChanged += async (_, _) => { if (!_loading && _project is not null && _source.SelectedItem is SourceChoice source) { if (!CommitEdits()) return; _project.UpdateGuideData(d => { d.Settings = d.Settings with { ElevationSourceKind = source.Gps ? GuideElevationSourceKind.Gps : GuideElevationSourceKind.Dem, DemDatabaseId = source.Id }; if (d.Derived is not null) d.Derived = d.Derived with { IsStale = true }; }); SaveSoon(); await CalculateAsync(); } };
        _plan.SelectionChanged += (_, _) => { if (!_loading) RefreshDays(); };
        Loaded += (_, _) => { RestoreRegisteredColumnWidths(); SelectProject(Main?.MapContextProject); _monitor.Start(); };
        Unloaded += async (_, _) => { _monitor.Stop(); _calculation?.Cancel(); await FlushColumnWidthsAsync(); };
        _columnWidthSaveTimer.Tick += async (_, _) => await FlushColumnWidthsAsync();
        _monitor.Tick += async (_, _) => { CalculationTask = CheckInputsAsync(); await CalculationTask; };
    }

    private static void UseBorderOnlySelection(DataGrid grid)
    {
        // 系统默认选中前景为白色，而只读文本保持浅灰背景，导致文字不可见。
        // 仅用边框标记选中，焦点移入/移出时都保持内容和编辑器可读。
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var rowSelected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSelected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        rowSelected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        rowStyle.Triggers.Add(rowSelected);
        grid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0, 120, 215))));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        cellStyle.Triggers.Add(selected);
        grid.CellStyle = cellStyle;
    }

    private static void AddLabel(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new(4), VerticalAlignment = VerticalAlignment.Center });
    private void Button(Panel panel, string text, Func<Task> action)
    { var button = new Button { Content = text, Margin = new(3), Padding = new(5, 2, 5, 2) }; panel.Children.Add(button); button.Click += async (_, _) => { try { await action(); } catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "攻略数据", MessageBoxButton.OK, MessageBoxImage.Warning); } }; }

    public void SelectProject(HikingProject? project)
    {
        if (_closeSuspended) return;
        if (ReferenceEquals(project, _project))
        {
            var presentation = project is null ? null : Presentation(project);
            if (_presentation != presentation && !_table.IsKeyboardFocusWithin && !_days.IsKeyboardFocusWithin) RefreshRows();
            return;
        }
        CommitEdits(); _calculation?.Cancel(); _project = project; _inputFingerprint = null; _structure = null;
        _loading = true;
        try
        {
            var choices = new List<SourceChoice> { new(null, "GPS原始高程", true) };
            choices.AddRange(Host.DemCatalog.Databases.Select(d => new SourceChoice(d.Id, d.Name)));
            if (project?.GuideData.Settings.DemDatabaseId is Guid savedDem && choices.All(c => c.Id != savedDem)) choices.Add(new(savedDem, "项目保存的DEM（未连接）"));
            _source.ItemsSource = choices;
            if (project is not null)
            {
                var s = project.GuideData.Settings;
                _source.SelectedItem = choices.FirstOrDefault(c => s.ElevationSourceKind == GuideElevationSourceKind.Gps ? c.Gps : !c.Gps && c.Id == s.DemDatabaseId);
                _speeds[0].Text = s.FlatDownSpeedKmh?.ToString() ?? ""; _speeds[1].Text = s.GentleUpSpeedKmh?.ToString() ?? ""; _speeds[2].Text = s.SteepUpSpeedKmh?.ToString() ?? "";
            }
            else { _source.SelectedItem = null; foreach (var box in _speeds) box.Text = ""; }
        }
        finally { _loading = false; }
        RefreshRows(); CalculationTask = CheckInputsAsync();
    }

    private void SaveSoon() => Main?.QueueGuideAutoSave(_project);
    private bool SaveSpeeds()
    {
        if (_loading || _project is null) return true;
        try
        {
            var values = _speeds.Select(b => Number(b.Text, false)).ToArray();
            var old = _project.GuideData.Settings;
            if (old.FlatDownSpeedKmh == values[0] && old.GentleUpSpeedKmh == values[1] && old.SteepUpSpeedKmh == values[2]) return true;
            _project.UpdateGuideData(d => d.Settings = old with { FlatDownSpeedKmh = values[0], GentleUpSpeedKmh = values[1], SteepUpSpeedKmh = values[2] });
            SaveSoon(); QueueRefresh(); return true;
        }
        catch (Exception error) { _status.Text = error.Message; return false; }
    }
    private static double? Number(string text, bool zero)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) || !double.IsFinite(n) || (zero ? n < 0 : n <= 0)) throw new ArgumentException(zero ? "计划用时必须为不小于0的小时数。" : "速度必须为大于0的km/h数值。");
        return n;
    }
    private static TimeOnly? Time(string text)
    { if (string.IsNullOrWhiteSpace(text)) return null; if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) throw new ArgumentException("时间必须为HH:mm，例如07:30。"); return value; }
    public bool CommitEdits()
    {
        if (_committing || _loading) return true;
        _committing = true;
        try
        {
        var result = _table.CommitEdit(DataGridEditingUnit.Cell, true) && _table.CommitEdit(DataGridEditingUnit.Row, true) && _days.CommitEdit(DataGridEditingUnit.Cell, true) && _days.CommitEdit(DataGridEditingUnit.Row, true) && SaveSpeeds();
        if (!result) _status.Text = "输入尚未提交，请修正红框中的数值或时间。";
        return result;
        }
        finally { _committing = false; }
    }
    private void QueueRefresh()
    { if (_refreshQueued) return; _refreshQueued = true; Dispatcher.BeginInvoke(() => { _refreshQueued = false; if (_table.CommitEdit(DataGridEditingUnit.Cell, true) && _table.CommitEdit(DataGridEditingUnit.Row, true) && _days.CommitEdit(DataGridEditingUnit.Cell, true) && _days.CommitEdit(DataGridEditingUnit.Row, true)) RefreshRows(); }, DispatcherPriority.Background); }

    private void SaveCell(Row row, string key, string text)
    {
        var project = _project ?? throw new InvalidOperationException("项目已关闭。");
        var ordered = GuideDataReconciler.OrderedNodes(project).ToArray(); var index = Array.FindIndex(ordered, n => n.Id == row.Id);
        if (index < 0) throw new InvalidOperationException("攻略节点已发生变化，请刷新。");
        if (key == "ActualArrival") { var time = Time(text); project.UpdateGuideData(d => { var old = d.Manual.Nodes.GetValueOrDefault(row.Id) ?? new() { GuideNodeId = row.Id }; d.Manual.Nodes[row.Id] = old with { ActualArrival = time }; }); }
        else if (key is "PlannedDuration" or "RoadCondition")
        {
            if (index == 0)
            {
                if (key != "RoadCondition") throw new ArgumentException("首节点没有前一分段，不能填写分段用时。");
                project.UpdateGuideData(d => { var old = d.Manual.Nodes.GetValueOrDefault(row.Id) ?? new() { GuideNodeId = row.Id }; d.Manual.Nodes[row.Id] = old with { StartRoadCondition = text }; });
                SaveSoon(); QueueRefresh(); return;
            }
            var segment = new GuideSegmentKey(project.MainGuideTrackId!.Value, ordered[index - 1].Id, row.Id);
            var hours = key == "PlannedDuration" ? Number(text, true) : null;
            project.UpdateGuideData(d => { var old = d.Manual.Segments.GetValueOrDefault(segment.StorageKey) ?? new() { SegmentKey = segment }; d.Manual.Segments[segment.StorageKey] = key == "PlannedDuration" ? old with { PlannedDurationHours = hours } : old with { RoadCondition = text }; });
        }
        else if (key.StartsWith("Location:", StringComparison.Ordinal))
        {
            var id = Guid.Parse(key[9..]); var value = text switch { "✔" => LocationConditionValue.Yes, "不确定" => LocationConditionValue.Unknown, "✘" => LocationConditionValue.No, "" => LocationConditionValue.Blank, _ => throw new ArgumentException("地点情况必须为留空、✔、不确定或✘。") };
            project.UpdateGuideData(d => { var old = d.Manual.Nodes.GetValueOrDefault(row.Id) ?? new() { GuideNodeId = row.Id }; var locations = new Dictionary<Guid, LocationConditionValue>(old.LocationConditions) { [id] = value }; d.Manual.Nodes[row.Id] = old with { LocationConditions = locations }; });
        }
        else throw new ArgumentException("自动字段不能人工修改。");
        SaveSoon(); QueueRefresh();
    }

    private void RefreshRows()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (_project?.MainGuideTrack is null || GuideDataReconciler.OrderedNodes(_project).Count < 2) { _snapshot = null; _table.ItemsSource = null; _days.ItemsSource = null; _status.Text = "请设置攻略轨迹并添加至少两个有效攻略节点。"; return; }
            // 即使DEM暂未连接，也不能把旧分界缓存标为当前统计；历史值保留，等待后台重算。
            if (_project.GuideData.Derived is { IsStale: false } legacy && legacy.ProfileVersion != GuideDataCoordinator.Version)
                _project.UpdateGuideData(d => d.Derived = legacy with { IsStale = true });
            var ids = GuideDataReconciler.OrderedNodes(_project).Select(n => n.Id).ToArray();
            var structure = _project.MainGuideTrackId + ":" + string.Join(',', ids);
            var savedStructureMatches = _structure is null && _project.GuideData.Derived is { } saved && saved.Nodes.Select(n => n.GuideNodeId).SequenceEqual(ids)
                && _project.GuideData.Plans.All(p => p.DailyStartNodeIds.Contains(ids[0]))
                && _project.GuideData.Manual.Nodes.Keys.ToHashSet().IsSupersetOf(ids)
                && _project.GuideData.Manual.Segments.Values.All(s => Enumerable.Range(1, ids.Length - 1).Any(i => s.SegmentKey == new GuideSegmentKey(_project.MainGuideTrackId!.Value, ids[i - 1], ids[i])));
            if (savedStructureMatches) _structure = structure;
            if (_structure != structure)
            {
                GuideReconcileResult? reconcile = null;
                _project.UpdateGuideData(d => reconcile = GuideDataReconciler.Reconcile(d, _project.MainGuideTrackId!.Value, ids));
                _structure = structure;
                if (reconcile!.ArchivedSegmentCount > 0) _status.Text = $"攻略节点变化导致{reconcile.ArchivedSegmentCount}段人工分段数据需要重新确认；原数据已归档。";
            }
            _presentation = Presentation(_project);
            _snapshot = GuideExportSnapshotBuilder.Build(_project);
            var identity = string.Join('|', _snapshot.Columns.Select(c => c.Key));
            if (!Equals(_table.Tag, identity)) { foreach (var old in _table.Columns) UnregisterColumnWidth(old); _table.Columns.Clear(); foreach (var c in _snapshot.Columns) AddColumn(c); _table.Tag = identity; }
            var selected = (_table.SelectedItem as Row)?.Id;
            var derivedNodes = _project.GuideData.Derived?.Nodes.ToDictionary(n => n.GuideNodeId);
            var rows = _snapshot.Rows.Select(r => new Row { Id = r.NodeId, Values = r.Values.ToDictionary(), Save = SaveCell,
                BoundaryTooltips = _snapshot.PlanTimings.ToDictionary(p => "Plan:" + p.Key, p =>
                {
                    var days = p.Value.Days.Where(day => day.StartNodeId == r.NodeId || day.EndNodeId == r.NodeId).ToArray();
                    return days.Length == 0 ? GuideTablePresentation.BoundaryTooltip("") : string.Join("\n\n", days.Select(GuideDisplayFormats.Summary));
                }),
                CalculationStatus = derivedNodes?.GetValueOrDefault(r.NodeId) is not { } node ? "待计算" : node.ElevationMeters is null || node.IncomingSegment is { IsComplete: false } ? "无法完整计算：所选来源高程不完整，不补零。" : "高程数据完整" }).ToArray();
            if (rows.Length > 0) rows[0].Values["PlannedDuration"] = "—";
            // 同一结构只更新行值，不重建正在使用的行/单元格，保留键盘当前位置。
            var existing = _table.Items.OfType<Row>().ToArray();
            if (existing.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id)))
                for (var i = 0; i < rows.Length; i++) existing[i].UpdateValues(rows[i]);
            else { _table.ItemsSource = rows; _table.SelectedItem = rows.FirstOrDefault(r => r.Id == selected); }
            var selectedPlan = (_plan.SelectedItem as PlanChoice)?.Id;
            var choices = _project.GuideData.Plans.OrderBy(p => p.Order).Select((p, i) => new PlanChoice(p.PlanId, $"行进方案{i + 1}" + (_snapshot.PlanTimings[p.PlanId].NeedsReview ? "（需确认）" : ""))).ToArray();
            _plan.ItemsSource = choices; _plan.SelectedItem = choices.FirstOrDefault(p => p.Id == selectedPlan) ?? choices[0];
            if (_calculation is null)
            {
                var messages = new List<string>();
                if (_project.GuideData.Derived is { } derived)
                {
                    messages.Add(derived.IsStale ? "历史统计 / 待更新" : "统计已更新");
                    messages.Add($"{(_project.GuideData.Settings.ElevationSourceKind == GuideElevationSourceKind.Gps ? "GPS海拔" : "DEM")}完整度{(derived.CoverageRatio is double coverage ? coverage.ToString("P1") : "未知")}");
                    messages.Add($"{derived.CalculatedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
                }
                messages.AddRange(_snapshot.Warnings);
                // 快照已包含边界失效警告，不再重复追加同一件事。
                _status.Text = GuideTablePresentation.JoinStatusMessages(messages);
            }
        }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _loading = false; }
        RefreshDays();
    }
    private void AddColumn(GuideOutputColumn c)
    {
        var editable = c.Key is "ActualArrival" or "PlannedDuration" or "RoadCondition" || c.Location;
        DataGridColumn column;
        if (c.Key.StartsWith("Plan:", StringComparison.Ordinal))
        {
            var factory = new FrameworkElementFactory(typeof(Button));
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding("[" + c.Key + "]"));
            label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); factory.AppendChild(label);
            factory.SetValue(FrameworkElement.MinHeightProperty, 22d); factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            factory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("[Tooltip:" + c.Key + "]") { Converter = new BoundaryTooltipConverter() });
            factory.SetValue(ToolTipService.ShowDurationProperty, int.MaxValue);
            factory.AddHandler(FrameworkElement.MouseLeaveEvent, new MouseEventHandler((sender, _) => { if (sender is Button button && button.ToolTip is ToolTip tip) tip.IsOpen = false; }));
            factory.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is FrameworkElement element && element.DataContext is Row row) ToggleBoundary(Guid.Parse(c.Key[5..]), row.Id); }));
            column = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = factory }, IsReadOnly = true };
        }
        else if (c.Location)
        {
            // 直接显示四态选择器，不依赖DataGrid双击进入编辑模式。
            // 方案按钮和地点下拉都是提交即落模型的交互控件。
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(ItemsControl.ItemsSourceProperty, new[] { "", "✔", "不确定", "✘" });
            factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("[" + c.Key + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
            factory.SetValue(FrameworkElement.ToolTipProperty, "留空 / ✔ / 不确定 / ✘；选择后立即保存到项目模型。");
            column = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = factory }, IsReadOnly = true };
        }
        else
        {
            var style = CenteredTextStyle();
            if (c.Key == "Index") { style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch)); }
            if (!editable) style.Setters.Add(new Setter(TextBlock.BackgroundProperty, new SolidColorBrush(Color.FromRgb(245, 247, 248))));
            if (!editable) style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(nameof(Row.CalculationStatus))));
            column = new DataGridTextColumn { Binding = new Binding("[" + c.Key + "]") { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay, ValidatesOnExceptions = true, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, ElementStyle = style, EditingElementStyle = CenteredEditorStyle(c.Key == "RoadCondition"), IsReadOnly = !editable };
        }
        column.Header = c.Header; column.SortMemberPath = c.Key; column.CanUserSort = false; column.Width = c.Key is "GuideNodeName" or "RoadCondition" ? 180 : c.Key.StartsWith("Plan:", StringComparison.Ordinal) ? 160 : 105;
        if (c.Key == "Index")
        {
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            column.HeaderStyle = headerStyle;
        }
        RegisterColumnWidth(column, GuideTablePresentation.ColumnWidthKey(c)); _table.Columns.Add(column);
    }
    private sealed record PlanChoice(Guid Id, string Name);
    private static string Presentation(HikingProject project) => project.MainGuideTrack?.Name + "|" + string.Join('|', GuideDataReconciler.OrderedNodes(project).Select(n => n.Name));
    private void RefreshDays()
    {
        _confirmDailyStarts.IsEnabled = _snapshot is not null && _plan.SelectedItem is PlanChoice selected && _snapshot.PlanTimings[selected.Id].NeedsReview;
        if (_snapshot is null || _project is null || _plan.SelectedItem is not PlanChoice choice) { _days.ItemsSource = null; return; }
        var plan = _project.GuideData.Plans.First(p => p.PlanId == choice.Id); var project = _project;
        var names = _snapshot.Rows.ToDictionary(r => r.NodeId, r => (string)r.Values["GuideNodeName"]!);
        _days.ItemsSource = _snapshot.PlanTimings[choice.Id].Days.Select(d => new DayRow { Day = d, Start = names[d.StartNodeId], End = names[d.EndNodeId],
            Save = text => { var time = Time(text); project.UpdateGuideData(data => { var current = data.Plans.First(p => p.PlanId == choice.Id); current.DepartureTimes[d.StartNodeId] = time; current.ModifiedAt = DateTimeOffset.UtcNow; }); SaveSoon(); QueueRefresh(); },
            SaveNotes = text => { project.UpdateGuideData(data => { var current = data.Plans.First(p => p.PlanId == choice.Id); current.DailyNotes[d.StartNodeId] = text; current.ModifiedAt = DateTimeOffset.UtcNow; }); SaveSoon(); }
        }).ToArray();
    }
    public void ToggleBoundary(Guid planId, Guid nodeId)
    {
        if (_project is null || !CommitEdits()) return;
        var nodes = GuideDataReconciler.OrderedNodes(_project);
        if (nodeId == nodes[0].Id || nodeId == nodes[^1].Id) { _status.Text = "首节点固定为D1起点；末节点只作为最后一天终点。"; return; }
        _project.UpdateGuideData(d => { var p = d.Plans.First(p => p.PlanId == planId); if (!p.DailyStartNodeIds.Remove(nodeId)) p.DailyStartNodeIds.Add(nodeId); p.ModifiedAt = DateTimeOffset.UtcNow; });
        SaveSoon(); RefreshRows();
    }
    private async Task CheckInputsAsync()
    {
        if (_closeSuspended || _checking || _project is null || _calculation is not null || _table.IsKeyboardFocusWithin || _days.IsKeyboardFocusWithin) return;
        _checking = true;
        try
        {
            var project = _project;
            if (GuideDataReconciler.OrderedNodes(project).Count < 2 || project.MainGuideTrack is null) { RefreshRows(); return; }
            var fingerprint = await Task.Run(() => Coordinator.Fingerprint(project));
            if (_closeSuspended || !ReferenceEquals(_project, project)) return;
            if (_inputFingerprint == fingerprint && project.GuideData.Derived is { IsStale: false })
            { if (_presentation != Presentation(project)) RefreshRows(); return; }
            _inputFingerprint = fingerprint;
            if (project.GuideData.Derived is { } old && old.InputFingerprint != fingerprint) project.UpdateGuideData(d => d.Derived = old with { IsStale = true });
            RefreshRows(); await CalculateAsync();
        }
        catch (Exception error) { _status.Text = GuideTablePresentation.JoinStatusMessages(new[] { error.Message + (_project?.GuideData.Derived is not null ? "（保留历史统计，待更新）" : "") }.Concat(_snapshot?.Warnings ?? [])); }
        finally { _checking = false; }
    }
    public async Task CalculateAsync(bool force = false)
    {
        if (_closeSuspended || _project is null) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _calculationCompletion = completion.Task;
        _calculation?.Cancel(); var cancellation = new CancellationTokenSource(); _calculation = cancellation; var project = _project;
        _status.Text = "正在后台更新攻略数据……";
        string? failure = null;
        try
        {
            var result = await Coordinator.CalculateAsync(project, new Progress<(int Done, int Total)>(p => { if (ReferenceEquals(_project, project) && ReferenceEquals(_calculation, cancellation)) _status.Text = $"正在计算攻略节点：{p.Done}/{p.Total}"; }), cancellation.Token, force);
            if (!ReferenceEquals(_project, project) || !ReferenceEquals(_calculation, cancellation)) return;
            _inputFingerprint = result.IsStale ? null : result.InputFingerprint; SaveSoon();
        }
        catch (OperationCanceledException) { failure = "攻略数据更新已取消；保留历史数据。"; }
        catch (Exception error) { failure = error.Message + "（保留历史数据，待更新）"; }
        finally { if (ReferenceEquals(_calculation, cancellation)) { _calculation = null; if (!_closeSuspended) { RefreshRows(); if (failure is not null) _status.Text = GuideTablePresentation.JoinStatusMessages(new[] { failure }.Concat(_snapshot?.Warnings ?? [])); } } cancellation.Dispose(); completion.TrySetResult(); }
    }

    private void EditRoad(Row row)
    {
        if (!CommitEdits()) return;
        var text = new TextBox { Text = row["RoadCondition"], AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new DockPanel { Margin = new(10) }; var save = new Button { Content = "保存路况", Margin = new(0, 8, 0, 0), Padding = new(8) }; DockPanel.SetDock(save, Dock.Bottom); panel.Children.Add(save); panel.Children.Add(text);
        var window = new Window { Owner = Window.GetWindow(this), Title = "编辑完整分段路况", Width = 650, Height = 450, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += (_, _) => { SaveCell(row, "RoadCondition", text.Text); window.DialogResult = true; };
        window.ShowDialog(); RefreshRows();
    }
    private void ShowArchives()
    {
        if (_project is null) return;
        var text = string.Join("\n\n", _project.GuideData.Manual.ArchivedSegments.Select(a => $"归档时间：{a.ArchivedAt.LocalDateTime}\n原因：{a.Reason}\n计划用时：{a.Data.PlannedDurationHours}\n{a.Data.RoadCondition}"));
        new Window { Owner = Window.GetWindow(this), Title = "人工分段数据归档（只读，可复制）", Width = 700, Height = 450, Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }
    private void ManagePlans()
    {
        if (_project is null || !CommitEdits()) return;
        var project = _project; var list = new ListBox { DisplayMemberPath = "Name", MinHeight = 160 }; var panel = new DockPanel { Margin = new(10) }; var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Owner = Window.GetWindow(this), Title = "行进方案管理", Width = 440, Height = 280, Content = panel };
        void Refresh() { var selected = (list.SelectedItem as PlanChoice)?.Id; var plans = project.GuideData.Plans.OrderBy(p => p.Order).ToArray(); for (var i = 0; i < plans.Length; i++) plans[i].Order = i; var choices = plans.Select((p, i) => new PlanChoice(p.PlanId, $"行进方案{i + 1}" + (p.NeedsReview ? "（需重新确认）" : ""))).ToArray(); list.ItemsSource = choices; list.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0]; SaveSoon(); RefreshRows(); }
        Button(buttons, "增加方案", () => { project.UpdateGuideData(d => d.Plans.Add(new() { Order = d.Plans.Count })); Refresh(); return Task.CompletedTask; });
        Button(buttons, "删除方案", () => { if (project.GuideData.Plans.Count == 1) throw new ArgumentException("至少保留一个方案。"); if (list.SelectedItem is PlanChoice c && MessageBox.Show(window, "确认删除该行进方案？其每日边界和出发时间将删除。", "删除方案", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { project.UpdateGuideData(d => d.Plans.RemoveAll(p => p.PlanId == c.Id)); Refresh(); } return Task.CompletedTask; });
        foreach (var (label, delta) in new[] { ("上移", -1), ("下移", 1) }) Button(buttons, label, () => { if (list.SelectedItem is PlanChoice c) project.UpdateGuideData(d => { var sorted = d.Plans.OrderBy(p => p.Order).ToList(); var index = sorted.FindIndex(p => p.PlanId == c.Id); var other = index + delta; if (other < 0 || other >= sorted.Count) return; (sorted[index], sorted[other]) = (sorted[other], sorted[index]); for (var i = 0; i < sorted.Count; i++) sorted[i].Order = i; }); Refresh(); return Task.CompletedTask; });
        Button(buttons, "确认现存每日起点", () => { if (list.SelectedItem is PlanChoice c && ConfirmDailyBoundaries(c.Id, window)) Refresh(); return Task.CompletedTask; });
        Refresh(); window.ShowDialog(); RefreshRows();
    }
    private bool ConfirmDailyBoundaries(Guid planId, Window? owner)
    {
        if (_project is not { } project || !CommitEdits()) return false;
        var snapshot = GuideExportSnapshotBuilder.Build(project); var plan = project.GuideData.Plans.First(p => p.PlanId == planId);
        var names = snapshot.Rows.ToDictionary(r => r.NodeId, r => (string)r.Values["GuideNodeName"]!);
        var days = snapshot.PlanTimings[planId].Days;
        var text = "请核对以下现存每日边界：\n\n" + string.Join('\n', days.Select(d => $"D{d.DayNumber}：{names[d.StartNodeId]} → {names[d.EndNodeId]}"))
            + "\n\n确认后仅移除失效的每日起点引用，保留现存节点、全部出发时间、人工分段用时和备注，不猜测替代节点。是否沿用以上行程并恢复计划抵达时间计算？";
        if (MessageBox.Show(owner, text, "确认每日边界", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        var ids = GuideDataReconciler.OrderedNodes(project).Select(n => n.Id).ToArray();
        project.UpdateGuideData(d => GuideDataReconciler.ConfirmExistingDailyStarts(d.Plans.First(p => p.PlanId == plan.PlanId), ids));
        SaveSoon(); RefreshRows(); return true;
    }
    private void ManageLocations()
    {
        if (_project is null || !CommitEdits()) return;
        var project = _project; var list = new ListBox { DisplayMemberPath = "Name" }; var name = new ComboBox { IsEditable = true, ItemsSource = GuideDataReconciler.LocationPresets, Width = 170 }; var panel = new DockPanel { Margin = new(10) }; var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); buttons.Children.Add(name); panel.Children.Add(list);
        var window = new Window { Owner = Window.GetWindow(this), Title = "地点情况管理（移除不删除已填值）", Width = 540, Height = 350, Content = panel };
        void Refresh() { list.ItemsSource = project.GuideData.Settings.LocationConditions.Where(d => d.IsActive).OrderBy(d => d.Order).ToArray(); SaveSoon(); RefreshRows(); }
        Button(buttons, "添加 / 恢复", () => { project.UpdateGuideData(d => GuideDataReconciler.AddCondition(d.Settings, name.Text, GuideDataReconciler.LocationPresets.Contains(name.Text) ? name.Text : null)); Refresh(); return Task.CompletedTask; });
        Button(buttons, "移除", () => { if (list.SelectedItem is LocationConditionDefinition c) project.UpdateGuideData(d => GuideDataReconciler.RemoveCondition(d.Settings, c.DefinitionId)); Refresh(); return Task.CompletedTask; });
        Button(buttons, "改名", () => { if (list.SelectedItem is LocationConditionDefinition c) { var text = name.Text.Trim(); if (text.Length == 0 || project.GuideData.Settings.LocationConditions.Any(d => d.IsActive && d.DefinitionId != c.DefinitionId && d.Name == text)) throw new ArgumentException("地点情况名称为空或重复。"); project.UpdateGuideData(d => d.Settings.LocationConditions[d.Settings.LocationConditions.IndexOf(c)] = c with { Name = text }); Refresh(); } return Task.CompletedTask; });
        foreach (var (label, delta) in new[] { ("上移", -1), ("下移", 1) }) Button(buttons, label, () => { if (list.SelectedItem is LocationConditionDefinition c) project.UpdateGuideData(d => { var sorted = d.Settings.LocationConditions.Where(x => x.IsActive).OrderBy(x => x.Order).ToList(); var index = sorted.FindIndex(x => x.DefinitionId == c.DefinitionId); var other = index + delta; if (other < 0 || other >= sorted.Count) return; (sorted[index], sorted[other]) = (sorted[other], sorted[index]); for (var i = 0; i < sorted.Count; i++) { var old = sorted[i]; d.Settings.LocationConditions[d.Settings.LocationConditions.IndexOf(old)] = old with { Order = i }; } }); Refresh(); return Task.CompletedTask; });
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is LocationConditionDefinition c) name.Text = c.Name; };
        Refresh(); window.ShowDialog(); RefreshRows();
    }
    private async Task ExportAsync()
    {
        if (_project is null || !CommitEdits()) return;
        var project = _project;
        try
        {
            var fingerprint = await Task.Run(() => Coordinator.Fingerprint(project));
            if (!ReferenceEquals(_project, project)) return;
            if (project.GuideData.Derived is { } old && old.InputFingerprint != fingerprint) project.UpdateGuideData(d => d.Derived = old with { IsStale = true });
        }
        catch (Exception error) { _status.Text = error.Message + "（导出使用带警告的历史数据）"; }
        var snapshot = GuideExportSnapshotBuilder.Build(_project);
        if (snapshot.Warnings.Count > 0 && MessageBox.Show(Window.GetWindow(this), string.Join("\n", snapshot.Warnings) + "\n\n是否按当前结果继续导出？", "攻略导出提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Title = "导出完整攻略Excel", Filter = "Excel工作簿 (*.xlsx)|*.xlsx", FileName = snapshot.TrackName + "攻略.xlsx", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _status.Text = "正在后台导出攻略Excel……";
        await new ExcelGuideExporter().ExportAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "攻略数据模板.xlsx"), dialog.FileName, snapshot, true);
        _status.Text = "攻略Excel已导出并重新打开验证：" + dialog.FileName;
    }
}
