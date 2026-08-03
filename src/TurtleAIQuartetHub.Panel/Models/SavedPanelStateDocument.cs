namespace TurtleAIQuartetHub.Panel.Models;

public sealed class SavedPanelStateDocument
{
    public List<SavedSlotState> VisibleSlots { get; set; } = [];

    public List<SavedStoredPanelState> StoredPanels { get; set; } = [];

    public List<SavedStoredPanelPageState> StoredPanelPages { get; set; } = [];
}

public sealed class SavedStoredPanelPageState
{
    public int Index { get; set; }

    /// <summary>ユーザーが付けたタブ名。既定名のままなら空文字で保存される。</summary>
    public string CustomHeader { get; set; } = string.Empty;
}

public sealed class SavedStoredPanelState
{
    public int Index { get; set; }

    public string PanelTitle { get; set; } = string.Empty;

    public string WorkspacePath { get; set; } = string.Empty;

    public string ApplicationId { get; set; } = string.Empty;
}
