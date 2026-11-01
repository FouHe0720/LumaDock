using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Reflection;
using LumaDock.Storage;

namespace LumaDock.App.Center;

/// <summary>A steady caret avoids repeatedly invalidating the whole Acrylic client surface.</summary>
internal static class StaticCaret
{
    private static readonly Type? NativeCaretType = typeof(Adorner).Assembly.GetType("System.Windows.Documents.CaretElement");
    private static readonly Dictionary<Type, FieldInfo?> ClockFields = [];
    private static int compatibilityFailureLogged;

    internal static FrameworkElement Create(TextBox text)
    {
        text.CaretBrush = Brushes.Transparent;
        var layer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var caret = new Rectangle { Width = 1.25, Visibility = Visibility.Collapsed };
        caret.SetBinding(Shape.FillProperty, new Binding(nameof(TextBox.Foreground)) { Source = text });
        layer.Children.Add(caret);
        bool queued = false;
        bool stopQueued = false, watchingLayout = false, updating = false;
        WeakReference<AnimationClock>? retainedClock = null;
        Window? host = null;
        void StopRetainedClock(bool force = false)
        {
            if (retainedClock?.TryGetTarget(out var clock) == true) StopClock(clock, force);
        }
        void RememberClock(AnimationClock clock)
        {
            if (retainedClock?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, clock)) StopClock(previous);
            retainedClock = new WeakReference<AnimationClock>(clock);
            StopClock(clock);
        }
        void StopNativeCaret()
        {
            StopRetainedClock();
            StopBlink(text, text, RememberClock);
            // The TextBox template has its own adorner layer; the nearest outer
            // layer does not necessarily include that subtree.
            if (AdornerLayer.GetAdornerLayer(text) is { } adorners) StopBlink(adorners, text, RememberClock);
        }
        void StopAfterGeometry()
        {
            if (stopQueued) return;
            stopQueued = true;
            text.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                stopQueued = false;
                if (layer.IsLoaded) StopNativeCaret();
                // Also cancel a Begin request that has not reached its first tick.
                StopRetainedClock(force: true);
            });
        }
        void LayoutUpdated(object? sender, EventArgs e)
        {
            // Font/theme changes can refresh WPF's caret without changing the
            // TextBox dimensions. This passive handler never schedules layout.
            if (!updating && text.IsKeyboardFocused && layer.IsLoaded) StopNativeCaret();
        }
        void WatchLayout(bool enable)
        {
            enable &= layer.IsLoaded;
            if (watchingLayout == enable) return;
            watchingLayout = enable;
            if (enable) text.LayoutUpdated += LayoutUpdated; else text.LayoutUpdated -= LayoutUpdated;
        }
        void Update()
        {
            updating = true;
            try
            {
                if (!layer.IsLoaded || !text.IsKeyboardFocused || text.SelectionLength != 0 || !text.IsVisible)
                { caret.Visibility = Visibility.Collapsed; StopNativeCaret(); StopRetainedClock(force: true); return; }
                // Geometry validation may restart WPF's retained root clock.
                // Stop after it, including when the character rectangle is empty.
                var bounds = text.GetRectFromCharacterIndex(Math.Clamp(text.CaretIndex, 0, text.Text.Length), true);
                StopNativeCaret(); StopAfterGeometry();
                if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
                { caret.Visibility = Visibility.Collapsed; return; }
                caret.Height = Math.Max(1, bounds.Height);
                Canvas.SetLeft(caret, Math.Clamp(bounds.X, 0, Math.Max(0, text.ActualWidth - 1.25)));
                Canvas.SetTop(caret, Math.Max(0, bounds.Y));
                caret.Visibility = Visibility.Visible;
            }
            finally { updating = false; }
        }
        void QueueUpdate()
        {
            if (queued) return;
            queued = true;
            text.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { queued = false; Update(); });
        }
        text.SelectionChanged += (_, _) => QueueUpdate();
        text.TextChanged += (_, _) => QueueUpdate();
        text.GotKeyboardFocus += (_, _) => { WatchLayout(true); QueueUpdate(); };
        text.LostKeyboardFocus += (_, _) => { WatchLayout(false); Update(); };
        text.SizeChanged += (_, _) => QueueUpdate();
        text.IsVisibleChanged += (_, _) => Update();
        text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueUpdate()));
        void Activated(object? sender, EventArgs e) => QueueUpdate();
        layer.Loaded += (_, _) =>
        {
            host = Window.GetWindow(text);
            if (host != null) host.Activated += Activated;
            WatchLayout(text.IsKeyboardFocused);
            QueueUpdate();
        };
        layer.Unloaded += (_, _) =>
        {
            if (host != null) host.Activated -= Activated;
            host = null;
            WatchLayout(false); StopNativeCaret(); StopRetainedClock(force: true);
        };
        return layer;
    }
    private static void StopBlink(DependencyObject node, TextBox owner, Action<AnimationClock> remember)
    {
        if (node is Adorner adorner && node.GetType() == NativeCaretType
            && owner.IsAncestorOf(adorner.AdornedElement))
        {
            // WPF retains this controllable root even after BeginAnimation(null)
            // removes it from the visual. Read the known field only; use the
            // public clock API, retaining the native IME/accessibility caret.
            try
            {
                FieldInfo? field;
                var type = node.GetType();
                lock (ClockFields)
                {
                    if (!ClockFields.TryGetValue(type, out field))
                    {
                        field = type.GetField("_blinkAnimationClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (field?.FieldType != typeof(AnimationClock)) field = null;
                        ClockFields[type] = field;
                    }
                }
                if (field == null) CompatibilityFailure();
                else if (field.GetValue(node) is AnimationClock clock) remember(clock);
            }
            catch (Exception exception) when (exception is MemberAccessException or TargetException or InvalidOperationException or ArgumentException)
            { CompatibilityFailure(); }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(adorner); i++)
                if (VisualTreeHelper.GetChild(adorner, i) is UIElement visual)
                {
                    if (visual.HasAnimatedProperties) visual.BeginAnimation(UIElement.OpacityProperty, null);
                    if (visual.Opacity != 0) visual.Opacity = 0;
                }
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) StopBlink(VisualTreeHelper.GetChild(node, i), owner, remember);
    }
    private static void StopClock(AnimationClock clock, bool force = false)
    {
        if (!force && clock.CurrentState != ClockState.Active) return;
        try { if (clock.Controller is { } controller) controller.Stop(); else CompatibilityFailure(); }
        catch (InvalidOperationException) { CompatibilityFailure(); }
    }
    private static void CompatibilityFailure()
    {
        if (Interlocked.Exchange(ref compatibilityFailureLogged, 1) == 0)
            Log.Write("WPF caret clock compatibility unavailable; using the steady caret overlay without root-clock suppression.");
    }
}
