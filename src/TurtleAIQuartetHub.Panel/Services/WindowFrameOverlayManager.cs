using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowFrameOverlayManager : IDisposable
{
    private readonly WindowArranger _windowArranger;
    private readonly Dictionary<string, SlotFrameOverlayWindow> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlaySnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public WindowFrameOverlayManager(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
    }

    public void Update(IEnumerable<WindowSlot> slots, bool overlaysVisible)
    {
        var slotList = slots as IReadOnlyCollection<WindowSlot> ?? slots.ToArray();
        if (!overlaysVisible || slotList.Any(slot => slot.IsFocused))
        {
            HideAll();
            return;
        }

        var visibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slotList)
        {
            if (!ShouldShowOverlay(slot)
                || !_windowArranger.TryGetWindowBounds(slot.WindowHandle, out var bounds))
            {
                Hide(slot.Name);
                continue;
            }

            var visual = GetVisual(slot);
            var overlayOverhang = (int)Math.Ceiling(visual.BorderThickness);
            var overlayBounds = new WindowArranger.WindowBounds(
                bounds.Left - overlayOverhang,
                bounds.Top - overlayOverhang,
                bounds.Width + overlayOverhang * 2,
                bounds.Height + overlayOverhang * 2);

            var snapshot = new OverlaySnapshot(overlayBounds, slot.AiStatus, slot.IsFocused);
            var overlay = GetOrCreate(slot.Name);

            if (!_snapshots.TryGetValue(slot.Name, out var previous) || previous != snapshot)
            {
                overlay.ApplyVisual(visual);
                overlay.UpdateBounds(overlayBounds);
                _snapshots[slot.Name] = snapshot;
            }

            _windowArranger.PositionOverlayAbove(overlay.Handle, slot.WindowHandle, overlayBounds);
            overlay.EnsureShown();
            visibleKeys.Add(slot.Name);
        }

        foreach (var entry in _overlays)
        {
            if (!visibleKeys.Contains(entry.Key))
            {
                entry.Value.Hide();
                _snapshots.Remove(entry.Key);
            }
        }
    }

    public void HideAll()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Hide();
        }

        _snapshots.Clear();
    }

    private SlotFrameOverlayWindow GetOrCreate(string slotName)
    {
        if (_overlays.TryGetValue(slotName, out var overlay))
        {
            return overlay;
        }

        overlay = new SlotFrameOverlayWindow();
        _overlays[slotName] = overlay;
        return overlay;
    }

    private static bool ShouldShowOverlay(WindowSlot slot)
    {
        return slot.WindowHandle != IntPtr.Zero
            && slot.WindowStatus == SlotWindowStatus.Ready
            && !slot.IsHidden
            && slot.AiStatus is AiStatus.Running
                or AiStatus.Completed
                or AiStatus.WaitingForConfirmation;
    }

    private static FrameVisual GetVisual(WindowSlot slot)
    {
        return slot.AiStatus switch
        {
            AiStatus.Running => new FrameVisual(ColorFromHex("#53FF9B"), 5.0, 1.0),
            AiStatus.Completed => new FrameVisual(ColorFromHex("#45D7FF"), 4.6, 0.98),
            AiStatus.WaitingForConfirmation => new FrameVisual(ColorFromHex("#FFE16B"), 4.8, 1.0),
            AiStatus.Error => new FrameVisual(ColorFromHex("#FF8C82"), 4.6, 0.96),
            AiStatus.NeedsAttention => new FrameVisual(ColorFromHex("#FFD15C"), 4.6, 0.96),
            _ => new FrameVisual(ColorFromHex("#000000"), 0, 0)
        };
    }

    private static Color ColorFromHex(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private void Hide(string slotName)
    {
        if (_overlays.TryGetValue(slotName, out var overlay))
        {
            overlay.Hide();
        }

        _snapshots.Remove(slotName);
    }

    public void Dispose()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Close();
        }

        _overlays.Clear();
        _snapshots.Clear();
    }

    private readonly record struct OverlaySnapshot(WindowArranger.WindowBounds Bounds, AiStatus Status, bool IsFocused);

    private readonly record struct FrameVisual(
        Color BorderColor,
        double BorderThickness,
        double Opacity);

    private sealed class SlotFrameOverlayWindow : Window
    {
        private readonly Border _frameBorder;
        private readonly Border _outerFrameBorder;
        private readonly Border _innerFrameBorder;
        private readonly SolidColorBrush _borderBrush;
        private readonly SolidColorBrush _outerBorderBrush;
        private readonly SolidColorBrush _innerBorderBrush;

        public SlotFrameOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;

            _borderBrush = new SolidColorBrush(Colors.Transparent);
            _outerBorderBrush = new SolidColorBrush(Colors.Transparent);
            _innerBorderBrush = new SolidColorBrush(Colors.Transparent);

            _outerFrameBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = _outerBorderBrush,
                BorderThickness = new Thickness(7),
                CornerRadius = new CornerRadius(15),
                Opacity = 0
            };

            _frameBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = _borderBrush,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(12),
                Opacity = 0
            };

            _innerFrameBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = _innerBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(5),
                Opacity = 0
            };

            Content = new Grid
            {
                IsHitTestVisible = false,
                Margin = new Thickness(0),
                Children = { _outerFrameBorder, _frameBorder, _innerFrameBorder }
            };
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        public void EnsureShown()
        {
            if (!IsVisible)
            {
                Show();
            }
        }

        public void UpdateBounds(WindowArranger.WindowBounds bounds)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(12, bounds.Width);
            Height = Math.Max(12, bounds.Height);
        }

        public void ApplyVisual(FrameVisual visual)
        {
            _borderBrush.Color = visual.BorderColor;
            _outerBorderBrush.Color = visual.BorderColor;
            _innerBorderBrush.Color = visual.BorderColor;
            _outerFrameBorder.BorderThickness = new Thickness(visual.BorderThickness + 3.0);
            _outerFrameBorder.CornerRadius = new CornerRadius(15 + visual.BorderThickness);
            _outerFrameBorder.Opacity = visual.Opacity * 0.24;
            _frameBorder.BorderThickness = new Thickness(visual.BorderThickness);
            _frameBorder.CornerRadius = new CornerRadius(12 + visual.BorderThickness);
            _frameBorder.Opacity = visual.Opacity;
            _innerFrameBorder.BorderThickness = new Thickness(1.0);
            _innerFrameBorder.CornerRadius = new CornerRadius(9 + visual.BorderThickness);
            _innerFrameBorder.Margin = new Thickness(visual.BorderThickness + 2.0);
            _innerFrameBorder.Opacity = visual.Opacity * 0.42;
        }

        public new void Hide()
        {
            base.Hide();
        }
    }
}
