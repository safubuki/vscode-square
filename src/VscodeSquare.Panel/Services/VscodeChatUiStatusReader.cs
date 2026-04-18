using System.Runtime.InteropServices;
using System.Windows.Automation;
using VscodeSquare.Panel.Models;

namespace VscodeSquare.Panel.Services;

public sealed class VscodeChatUiStatusReader
{
    private const int MaxElementsToInspect = 420;
    private const int MaxTextLengthForStatus = 48;

    private static readonly string[] RunningStatusTexts =
    [
        "作業中",
        "実行中",
        "処理中",
        "生成中",
        "考え中",
        "Working",
        "Running",
        "Generating",
        "Thinking"
    ];

    private static readonly string[] StopActionTexts =
    [
        "停止",
        "中止",
        "キャンセル",
        "Stop",
        "Cancel"
    ];

    private static readonly string[] ChatContextFragments =
    [
        "chat",
        "copilot",
        "codex",
        "agent",
        "interactive",
        "action-label",
        "codicon"
    ];

    private static readonly string[] StopClassFragments =
    [
        "codicon-stop",
        "codicon-debug-stop",
        "codicon-circle-slash"
    ];

    public AiStatusSnapshot? TryRead(WindowSlot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    internal AiStatusSnapshot? TryRead(WindowSlotStatusSnapshot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    private AiStatusSnapshot? TryRead(IntPtr windowHandle)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
            {
                return null;
            }

            return TryRead(root);
        }
        catch (ElementNotAvailableException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
        catch (COMException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
    }

    private static AiStatusSnapshot? TryRead(AutomationElement root)
    {
        var walker = TreeWalker.RawViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);

        var inspected = 0;
        while (queue.Count > 0 && inspected < MaxElementsToInspect)
        {
            var element = queue.Dequeue();
            inspected++;

            if (TryReadRunningSignal(element, out var detail))
            {
                return new AiStatusSnapshot(AiStatus.Running, detail, DateTimeOffset.Now);
            }

            EnqueueChildren(walker, element, queue);
        }

        return null;
    }

    private static void EnqueueChildren(
        TreeWalker walker,
        AutomationElement parent,
        Queue<AutomationElement> queue)
    {
        AutomationElement? child = null;
        try
        {
            child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                queue.Enqueue(child);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }
    }

    private static bool TryReadRunningSignal(AutomationElement element, out string detail)
    {
        var name = GetStringProperty(element, AutomationElement.NameProperty);
        var automationId = GetStringProperty(element, AutomationElement.AutomationIdProperty);
        var className = GetStringProperty(element, AutomationElement.ClassNameProperty);
        var combinedContext = $"{automationId} {className}";
        var isVisible = IsVisible(element);

        if (isVisible && IsCurrentStatusText(name))
        {
            detail = $"VS Code UI: {name} を検出しました。";
            return true;
        }

        if (isVisible
            && IsEnabled(element)
            && (ContainsAny(className, StopClassFragments)
                || ContainsStopAction(name) && ContainsAny(combinedContext, ChatContextFragments)))
        {
            detail = string.IsNullOrWhiteSpace(name)
                ? "VS Code UI: チャット停止ボタンを検出しました。"
                : $"VS Code UI: {TrimForDetail(name)} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool IsVisible(AutomationElement element)
    {
        try
        {
            var offscreen = element.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true);
            if (offscreen is bool isOffscreen && isOffscreen)
            {
                return false;
            }

            var rectangle = element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty, true);
            return rectangle is not System.Windows.Rect rect || rect.Width > 0 && rect.Height > 0;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsEnabled(AutomationElement element)
    {
        try
        {
            var enabled = element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true);
            return enabled is not bool value || value;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsCurrentStatusText(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxTextLengthForStatus)
        {
            return false;
        }

        return RunningStatusTexts.Any(signal => string.Equals(text, signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsStopAction(string value)
    {
        return StopActionTexts.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string value, IEnumerable<string> fragments)
    {
        return fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetStringProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value == AutomationElement.NotSupported || value is null
                ? string.Empty
                : value.ToString() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static string TrimForDetail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 80 ? trimmed : $"{trimmed[..77]}...";
    }
}
