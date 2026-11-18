using HikingGuide.Core.Models;
namespace HikingGuide.App.ViewModels;
public sealed record PropertyRow(string Name, string Value, string? Color = null, Track? Track = null, string? Hint = null);
