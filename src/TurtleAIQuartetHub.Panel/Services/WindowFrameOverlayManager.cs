using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowFrameOverlayManager : IDisposable
{
    private const int OverlayPadding = 5;
    private readonly WindowArranger _windowArranger;
    private readonly Dictionary<string, SlotFrameOverlayWindow> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlaySnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public WindowFrameOverlayManager(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
    }

    public void Update(IEnumerable<WindowSlot> slots, bool overlaysVisible)
    {
        if (!overlaysVisible)
        {
            HideAll();
            return;
        }

        var visibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (!ShouldShowOverlay(slot)
                || !_windowArranger.TryGetWindowBounds(slot.WindowHandle, out var bounds))
            {
                Hide(slot.Name);
                continue;
            }

            var overlayBounds = new WindowArranger.WindowBounds(
                bounds.Left - OverlayPadding,
                bounds.Top - OverlayPadding,
                bounds.Width + OverlayPadding * 2,
                bounds.Height + OverlayPadding * 2);

            var visual = GetVisual(slot);
            var snapshot = new OverlaySnapshot(overlayBounds, slot.AiStatus, slot.IsFocused);
            var overlay = GetOrCreate(slot.Name);

            if (!_snapshots.TryGetValue(slot.Name, out var previous) || previous != snapshot)
            {
                overlay.ApplyVisual(visual);
                overlay.UpdateBounds(overlayBounds);
                _windowArranger.PositionOverlayAbove(overlay.Handle, slot.WindowHandle, overlayBounds);
                _snapshots[slot.Name] = snapshot;
            }

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
            AiStatus.Running => new FrameVisual(ColorFromHex("#49E88F"), ColorFromHex("#49E88F"), 3.4, 16, slot.IsFocused ? 0.88 : 0.72, TimeSpan.FromMilliseconds(680)),
            AiStatus.Completed => new FrameVisual(ColorFromHex("#43C8FF"), ColorFromHex("#43C8FF"), 3.0, 15, slot.IsFocused ? 0.84 : 0.62, TimeSpan.FromSeconds(2.0)),
            AiStatus.WaitingForConfirmation => new FrameVisual(ColorFromHex("#F2CA57"), ColorFromHex("#F2CA57"), 3.2, 16, slot.IsFocused ? 0.88 : 0.7, TimeSpan.FromSeconds(1.3)),
            AiStatus.Error => new FrameVisual(ColorFromHex("#E37B70"), ColorFromHex("#E37B70"), 3.0, 14, slot.IsFocused ? 0.8 : 0.58, TimeSpan.FromSeconds(1.6)),
            AiStatus.NeedsAttention => new FrameVisual(ColorFromHex("#C9A441"), ColorFromHex("#C9A441"), 3.0, 14, slot.IsFocused ? 0.8 : 0.58, TimeSpan.FromSeconds(1.6)),
            _ => new FrameVisual(ColorFromHex("#000000"), ColorFromHex("#000000"), 0, 0, 0, null)
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
        Color GlowColor,
        double BorderThickness,
        double BlurRadius,
        double Opacity,
        TimeSpan? PulseDuration);

    private sealed class SlotFrameOverlayWindow : Window
    {
        private readonly Border _frameBorder;
        private readonly DropShadowEffect _glowEffect;
        private readonly SolidColorBrush _borderBrush;
        private bool _isPulseActive;

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
            _glowEffect = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 12,
                Opacity = 0
            };

            _frameBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = _borderBrush,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(12),
                Opacity = 0,
                Effect = _glowEffect
            };

            Content = new Grid
            {
                IsHitTestVisible = false,
                Margin = new Thickness(3),
                Children = { _frameBorder }
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
            _frameBorder.BorderThickness = new Thickness(visual.BorderThickness);
            _frameBorder.CornerRadius = new CornerRadius(12 + visual.BorderThickness);
            _frameBorder.Opacity = visual.Opacity;
            _glowEffect.Color = visual.GlowColor;
            _glowEffect.BlurRadius = visual.BlurRadius;
            _glowEffect.Opacity = Math.Min(0.9, visual.Opacity + 0.05);

            if (visual.PulseDuration.HasValue && visual.Opacity > 0)
            {
                StartPulse(visual.Opacity, visual.PulseDuration.Value);
            }
            else
            {
                StopPulse();
            }
        }

        public new void Hide()
        {
            StopPulse();
            base.Hide();
        }

        private void StartPulse(double baseOpacity, TimeSpan duration)
        {
            _isPulseActive = true;

            var borderAnimation = new DoubleAnimation
            {
                From = Math.Max(0.28, baseOpacity * 0.7),
                To = Math.Min(0.96, baseOpacity),
                Duration = duration,
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            var glowAnimation = new DoubleAnimation
            {
                From = Math.Max(0.14, (_glowEffect.Opacity) * 0.62),
                To = Math.Min(0.96, _glowEffect.Opacity),
                Duration = duration,
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            _frameBorder.BeginAnimation(UIElement.OpacityProperty, borderAnimation, HandoffBehavior.SnapshotAndReplace);
            _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, glowAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        private void StopPulse()
        {
            if (!_isPulseActive)
            {
                return;
            }

            _frameBorder.BeginAnimation(UIElement.OpacityProperty, null);
            _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            _isPulseActive = false;
        }
    }
}
