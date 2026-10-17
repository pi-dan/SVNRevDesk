using System.Text.Json.Serialization;

namespace SVNRevDesk.Desktop.Services;

[JsonConverter(typeof(JsonStringEnumConverter<WindowCloseBehavior>))]
public enum WindowCloseBehavior
{
    Ask,
    MinimizeToTray,
    Exit
}
