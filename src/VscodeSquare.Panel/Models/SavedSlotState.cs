namespace VscodeSquare.Panel.Models;

public sealed class SavedSlotState
{
    public string Name { get; set; } = string.Empty;

    public string PanelTitle { get; set; } = string.Empty;

    public long WindowHandle { get; set; }
}
