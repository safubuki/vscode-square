namespace VscodeSquare.Panel.Models;

public sealed class SavedPanelStateDocument
{
    public List<SavedSlotState> VisibleSlots { get; set; } = [];

    public List<SavedStoredPanelState> StoredPanels { get; set; } = [];
}

public sealed class SavedStoredPanelState
{
    public int Index { get; set; }

    public string PanelTitle { get; set; } = string.Empty;

    public string WorkspacePath { get; set; } = string.Empty;
}