using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Documents;

namespace LumaDock.App.Center;

/// <summary>A steady caret avoids repeatedly invalidating the whole Acrylic client surface.</summary>
internal static class StaticCaret
{
    internal static FrameworkElement Create(TextBox text)
    {
        text.CaretBrush = Brushes.Transparent;
        var layer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var caret = new Rectangle { Width = 1.25, Visibility = Visibility.Collapsed };
        caret.SetBinding(Shape.FillProperty, new Binding(nameof(TextBox.Foreground)) { Source = text });
        layer.Children.Add(caret);
        bool queued = false;
        Window? host = null;
        void Update()
        {
            if (!text.IsKeyboardFocused || text.SelectionLength != 0 || !text.IsVisible)
            { caret.Visibility = Visibility.Collapsed; return; }
            // A transparent CaretBrush alone leaves WPF's repeating opacity clock
            // active. Stop that visual's animation through the public UIElement API;
            // the text editor, IME, selection and accessibility caret stay intact.
            // TextBox's ScrollViewer owns an inner adorner layer. The nearest
            // ancestor layer alone does not include that template subtree.
            StopBlink(text, text);
            if (AdornerLayer.GetAdornerLayer(text) is { } adorners) StopBlink(adorners, text);
            var bounds = text.GetRectFromCharacterIndex(Math.Clamp(text.CaretIndex, 0, text.Text.Length), true);
            if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
            { caret.Visibility = Visibility.Collapsed; return; }
            caret.Height = Math.Max(1, bounds.Height);
            Canvas.SetLeft(caret, Math.Clamp(bounds.X, 0, Math.Max(0, text.ActualWidth - 1.25)));
            Canvas.SetTop(caret, Math.Max(0, bounds.Y));
            caret.Visibility = Visibility.Visible;
        }
        void QueueUpdate()
        {
            if (queued) return;
            queued = true;
            text.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { queued = false; Update(); });
        }
        text.SelectionChanged += (_, _) => QueueUpdate();
        text.TextChanged += (_, _) => QueueUpdate();
        text.GotKeyboardFocus += (_, _) => QueueUpdate();
        text.LostKeyboardFocus += (_, _) => Update();
        text.SizeChanged += (_, _) => QueueUpdate();
        text.IsVisibleChanged += (_, _) => Update();
        text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueUpdate()));
        void Activated(object? sender, EventArgs e) => QueueUpdate();
        layer.Loaded += (_, _) =>
        {
            host = Window.GetWindow(text);
            if (host != null) host.Activated += Activated;
            QueueUpdate();
        };
        layer.Unloaded += (_, _) =>
        {
            if (host != null) host.Activated -= Activated;
            host = null;
        };
        return layer;
    }
    private static void StopBlink(DependencyObject node, TextBox owner)
    {
        if (node is Adorner adorner && node.GetType().FullName == "System.Windows.Documents.CaretElement"
            && owner.IsAncestorOf(adorner.AdornedElement))
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(adorner); i++)
                if (VisualTreeHelper.GetChild(adorner, i) is UIElement visual)
                { visual.BeginAnimation(UIElement.OpacityProperty, null); visual.Opacity = 0; }
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) StopBlink(VisualTreeHelper.GetChild(node, i), owner);
    }
}
