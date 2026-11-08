using System.Windows;
using System.Windows.Controls.Primitives;
using HikingGuide.Core.Favorites;

namespace HikingGuide.App;

/// <summary>全应用共享一个非交互Popup，所有面板都通过同一个请求门禁。</summary>
public sealed class FavoriteHoverCardController
{
    public static FavoriteHoverCardController Shared { get; } = new();
    public FavoriteHoverSession Session { get; } = new();
    private readonly Popup _popup = new() { Placement = PlacementMode.Top, AllowsTransparency = true,
        StaysOpen = true, IsHitTestVisible = false, Focusable = false };
    public bool IsOpen => _popup.IsOpen;
    public int OpenPopupCount => _popup.IsOpen ? 1 : 0;
    public (long Generation, CancellationToken Token) Enter(Guid id, FrameworkElement target)
    { Close(); return Session.Enter(id, target); }
    public bool Matches(Guid id, FrameworkElement target, long generation) =>
        Session.Matches(id, target, generation) && target.IsVisible && target.IsMouseOver;
    public void Open(Guid id, FrameworkElement target, long generation, UIElement content)
    {
        if (!Matches(id, target, generation) || !Session.TryOpen(id, target, generation)) return;
        content.IsHitTestVisible = false; _popup.PlacementTarget = target; _popup.Child = content; _popup.IsOpen = true;
    }
    public void Close() { _popup.IsOpen = false; _popup.Child = null; _popup.PlacementTarget = null; Session.Close(); }
}
