using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Unread.App;

public sealed class PaneResizeEventArgs(double width, bool completed) : EventArgs
{
    public double Width { get; } = width;
    public bool Completed { get; } = completed;
}

/// <summary>A native, keyboard and UI Automation accessible vertical pane divider.</summary>
public sealed class PaneResizer : Control
{
    private uint? _pointer;
    private double _origin, _startWidth;
    private bool _hovered;
    private InputSystemCursor? _cursor;
    internal double Minimum { get; private set; }
    internal double Maximum { get; private set; }
    internal double Value { get; private set; }
    public event EventHandler<PaneResizeEventArgs>? ResizeRequested;

    public PaneResizer()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ManipulationMode = ManipulationModes.None;
        Loaded += (_, _) => ProtectedCursor = _cursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Unloaded += (_, _) => { ProtectedCursor = null; _cursor?.Dispose(); _cursor = null; };
    }

    internal void SetRange(double minimum, double maximum, double value)
    {
        var oldMinimum = Minimum; var oldMaximum = Maximum; var oldValue = Value;
        Minimum = minimum; Maximum = maximum; Value = value;
        if (FrameworkElementAutomationPeer.FromElement(this) is not PaneResizerAutomationPeer peer) return;
        if (oldMinimum != minimum) peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.MinimumProperty, oldMinimum, minimum);
        if (oldMaximum != maximum) peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.MaximumProperty, oldMaximum, maximum);
        if (oldValue != value) peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, value);
    }

    internal void ResizeTo(double value, bool completed)
    {
        if (!IsEnabled || Visibility != Visibility.Visible || !double.IsFinite(value)) return;
        ResizeRequested?.Invoke(this, new(Math.Clamp(value, Minimum, Maximum), completed));
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_pointer is not null || (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
            && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)) return;
        Focus(FocusState.Pointer);
        if (!CapturePointer(e.Pointer)) return;
        _pointer = e.Pointer.PointerId;
        _origin = e.GetCurrentPoint(Parent as UIElement).Position.X;
        _startWidth = Value;
        UpdateState();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointer != e.Pointer.PointerId) return;
        // Measure in the stationary parent: this divider itself moves as its pane changes width.
        ResizeTo(_startWidth + e.GetCurrentPoint(Parent as UIElement).Position.X - _origin, false);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer != e.Pointer.PointerId) return;
        ResizeTo(_startWidth + e.GetCurrentPoint(Parent as UIElement).Position.X - _origin, false);
        FinishDrag();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        FinishDrag();
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        FinishDrag();
    }

    private void FinishDrag()
    {
        if (_pointer is null) return;
        _pointer = null;
        UpdateState();
        ResizeTo(Value, true);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    { base.OnPointerEntered(e); _hovered = true; UpdateState(); }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    { base.OnPointerExited(e); _hovered = false; UpdateState(); }

    private void UpdateState() => VisualStateManager.GoToState(this, _pointer is not null ? "Pressed" : _hovered ? "PointerOver" : "Normal", false);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        var width = e.Key switch
        {
            VirtualKey.Left => Value - 8,
            VirtualKey.Right => Value + 8,
            VirtualKey.PageUp => Value - 32,
            VirtualKey.PageDown => Value + 32,
            VirtualKey.Home => Minimum,
            VirtualKey.End => Maximum,
            _ => double.NaN
        };
        if (double.IsNaN(width)) return;
        ResizeTo(width, true);
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PaneResizerAutomationPeer(this);

    private sealed class PaneResizerAutomationPeer(PaneResizer owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PaneResizer);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Vertical;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 32;
        public double SmallChange => 8;
        public double Maximum => owner.Maximum;
        public double Minimum => owner.Minimum;
        public double Value => owner.Value;
        public void SetValue(double value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.ResizeTo(value, true);
        }
    }
}
