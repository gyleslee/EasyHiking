using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

public sealed class NavigationNode(string displayName, object? model = null, bool isMapVisible = true,
    Action<bool>? mapVisibilityChanged = null, bool isActiveProject = false) : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isExpanded;
    private bool _isMapVisible = isMapVisible;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DisplayName { get; } = displayName;

    public object? Model { get; } = model;
    public string SelectionKey { get; set; } = string.Empty;
    public bool IsTrack => Model is Track;
    public bool IsMarker => Model is MapMarkerBase;
    public bool IsTrackSegment => Model is SplitSection;
    public bool IsTrackOrSplitSection => Model is Track or SplitSection;
    public bool IsEditableGeometry => Model is Track or SplitSection or GuideSegment;
    public bool IsProject => Model is HikingProject;
    public string TrackColor => Model is Track track ? track.DisplayColor ?? "#2877C7" : "Transparent";
    public bool IsActiveProject { get; } = isActiveProject;
    public bool CanToggleMapVisibility => mapVisibilityChanged is not null;
    public bool IsMapVisible
    {
        get => _isMapVisible;
        set
        {
            if (_isMapVisible == value) return;
            _isMapVisible = value;
            OnPropertyChanged();
            mapVisibilityChanged?.Invoke(value);
        }
    }

    public ObservableCollection<NavigationNode> Children { get; } = [];

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set { if (_isMultiSelected == value) return; _isMultiSelected = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
