namespace Smurfm3u.Core.Entities;

/// <summary>Key/value store backing <c>Settings</c>; one row per setting so migrations stay boring.</summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
