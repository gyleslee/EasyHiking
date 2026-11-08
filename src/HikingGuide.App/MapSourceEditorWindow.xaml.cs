using System.Globalization;
using System.Windows;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.App;

public partial class MapSourceEditorWindow : Window
{
    private readonly MapSourceDefinition _source;
    private readonly MapSourceTemplateService _templates;

    public MapSourceEditorWindow(MapSourceDefinition source, MapSourceTemplateService templates)
    {
        InitializeComponent();
        _source = source;
        _templates = templates;
        IdText.Text = source.Id.ToString(); NameText.Text = source.Name; UrlText.Text = source.UrlTemplate;
        MinZoomText.Text = source.MinZoom.ToString(CultureInfo.InvariantCulture); MaxZoomText.Text = source.MaxZoom.ToString(CultureInfo.InvariantCulture);
        OpacityText.Text = (source.Opacity * 100).ToString("0.##", CultureInfo.CurrentCulture);
        CacheCapacityText.Text = source.CacheCapacityMb.ToString(CultureInfo.InvariantCulture);
        CoordinateSystemBox.SelectedIndex=(int)source.CoordinateSystem;
        UserAgentText.Text = source.UserAgent; RefererText.Text = source.Referer; AttributionText.Text = source.Attribution;
        EnabledCheck.IsChecked = source.Enabled; UsageBox.SelectedIndex=source.EffectiveUsage switch {MapSourceUsage.Base=>0,MapSourceUsage.Overlay=>1,_=>2};
        HeadersText.Text = string.Join(Environment.NewLine, source.Headers.Select(value => $"{value.Key}: {value.Value}"));
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if(!int.TryParse(CacheCapacityText.Text,out var capacity) || capacity<0 || capacity>1048576)
        {MessageBox.Show(this,"缓存容量请输入0～1048576之间的整数（MB），0表示关闭磁盘缓存。","输入无效",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        if (!int.TryParse(MinZoomText.Text, out var minZoom) || !int.TryParse(MaxZoomText.Text, out var maxZoom) ||
            !double.TryParse(OpacityText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var opacityPercent))
        {
            MessageBox.Show(this, "缩放范围和透明度必须是有效数字。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in HeadersText.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
            {
                MessageBox.Show(this, $"高级请求头格式无效：{rawLine}", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            headers[rawLine[..separator].Trim()] = rawLine[(separator + 1)..].Trim();
        }
        _source.Name = NameText.Text.Trim(); _source.UrlTemplate = UrlText.Text.Trim();
        _source.CacheCapacityMb = capacity;
        _source.CoordinateSystem=(MapSourceCoordinateSystem)CoordinateSystemBox.SelectedIndex;
        _source.MinZoom = minZoom; _source.MaxZoom = maxZoom; _source.Opacity = opacityPercent / 100d;
        _source.UserAgent = NullIfEmpty(UserAgentText.Text); _source.Referer = NullIfEmpty(RefererText.Text);
        _source.Attribution = NullIfEmpty(AttributionText.Text); _source.Enabled = EnabledCheck.IsChecked == true;
        _source.Usage=UsageBox.SelectedIndex switch {0=>MapSourceUsage.Base,1=>MapSourceUsage.Overlay,_=>MapSourceUsage.Both};
        _source.Headers = headers;
        var validation = _templates.Validate(_source);
        if (!validation.IsValid)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, validation.Errors), "图源设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
