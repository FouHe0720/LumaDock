using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LumaDock.App;
using LumaDock.Core;
using LumaDock.Storage;

namespace LumaDock.Workspace.Ui.Tests;

internal static partial class Program
{
    // Real rendered controls and the production client-message adapter. These
    // contracts do not inject a physical pointer or open any application.
    private static async Task CompactDockChecks(AppController controller, DockWindow dock, string data,
        Action<string, Action> check, Dictionary<string, object> observations)
    {
        var settings = controller.State.Settings;
        var originalPosition = settings.Position; var originalSize = settings.IconSize;
        var originalShape = settings.DockShape; var originalPins = controller.State.PinnedIds.ToList();
        var originalApps = controller.State.Apps.ToList();
        var originalPlacement = settings.DockPlacement;
        var handle = new WindowInteropHelper(dock).Handle;
        var compactLayouts = new List<object>();
        try
        {
            foreach (var shape in new[] { "Capsule", "Pebble", "Rail" })
            foreach (var size in new[] { 40d, 48d })
            {
                settings.IconSize = size; settings.DockShape = shape; settings.Position = "Bottom";
                dock.UpdateSettings(); await Frame();
                check($"The familiar {shape} plate retains its established thickness at {size} DIP", () =>
                {
                    var body = Field<FrameworkElement>(dock, "body");
                    Require(Math.Abs(body.ActualHeight - (size + (shape == "Rail" ? 36 : 42))) <= 1,
                        "Compact-size padding unexpectedly changed the regular-sized plate thickness.");
                });
            }
            settings.IconSize = 16; settings.DockShape = "Capsule"; settings.DockPlacement = "Edge";
            foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
            {
                settings.Position = position; dock.UpdateSettings(); await Frame();
                var vertical = position is "Left" or "Right";
                var body = Field<FrameworkElement>(dock, "body");
                var buttons = DockTileButtons(dock);
                var glass = ElementBounds(body, dock);
                var move = Field<FrameworkElement>(dock, "positionGrip");
                var resize = Field<FrameworkElement>(dock, "resizeGrip");
                check($"The 16 DIP {position} Dock keeps small real icons and distinct empty end lanes", () =>
                {
                    Require(Math.Abs(settings.IconSize - 16) < .01, "The smallest size was clamped back to the old minimum.");
                    Require(Math.Abs((vertical ? body.ActualWidth : body.ActualHeight) - 32) <= 1,
                        "The smallest Dock kept its old oversized cross-axis padding.");
                    foreach (var tile in Field<System.Collections.IEnumerable>(dock, "tiles").Cast<object>())
                    {
                        var icon = Field<FrameworkElement>(tile, "Icon");
                        Require(icon.ActualWidth > 0 && icon.ActualHeight > 0 && icon.ActualWidth <= 16.5 && icon.ActualHeight <= 16.5,
                            "The small Dock retained an oversized launcher or application icon.");
                    }
                    foreach (var grip in new[] { move, resize })
                    {
                        Require(grip.IsVisible && Math.Abs((vertical ? grip.ActualHeight : grip.ActualWidth) - 24) <= 1
                            && (vertical ? grip.ActualWidth : grip.ActualHeight) >= 21.5,
                            "A compact Dock end lost its independent usable grip hit area.");
                        var gripBounds = ElementBounds(grip, dock);
                        Require(buttons.All(button => !Overlaps(gripBounds, ElementBounds(button, dock))),
                            "A resting application button overlaps the dedicated move or resize grip.");
                    }
                    var first = ElementBounds(buttons[0], dock); var last = ElementBounds(buttons[^1], dock);
                    Require((vertical ? first.Top - glass.Top : first.Left - glass.Left) >= 31
                        && (vertical ? glass.Bottom - last.Bottom : glass.Right - last.Right) >= 31,
                        "One of the two glass ends failed to reserve its empty 32 DIP lane.");
                    compactLayouts.Add(new { position, iconSize = settings.IconSize, glass, move = ElementBounds(move, dock), resize = ElementBounds(resize, dock),
                        first, last, dpi = Dpi(handle) });
                });
                check($"The {position} empty end grips route client input without activating application buttons", () =>
                {
                    var activations = 0; RoutedEventHandler countClick = (_, _) => activations++;
                    foreach (var button in buttons) button.Click += countClick;
                    try
                    {
                        foreach (var grip in new[] { move, resize })
                        foreach (var x in new[] { .15, .5, .85 })
                        foreach (var y in new[] { .15, .5, .85 })
                        {
                            var local = grip.TranslatePoint(new Point(grip.ActualWidth * x, grip.ActualHeight * y), dock);
                            var screen = dock.PointToScreen(local);
                            Require(ButtonAt(dock, local) == null, "A grip point still resolves to an application button.");
                            Require(Hit(handle, (int)Math.Round(screen.X), (int)Math.Round(screen.Y)) == 1,
                                "An empty grip point does not retain client mouse input.");
                            Require(ClientPointer(dock, 0x0201, screen) && dock.IsPositionDragging
                                && dock.IsSizeDragging == ReferenceEquals(grip, resize),
                                "The grip did not start its intended move or resize gesture.");
                            Require(ClientPointer(dock, 0x0202, screen) && !dock.IsPositionDragging && GetCapture() != handle,
                                "A stationary grip release retained a gesture or native capture.");
                        }
                        foreach (var tile in Field<System.Collections.IEnumerable>(dock, "tiles").Cast<object>())
                        {
                            var icon = Field<FrameworkElement>(tile, "Icon");
                            var local = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), dock);
                            Require(ButtonAt(dock, local) != null && !ClientPointer(dock, 0x0201, dock.PointToScreen(local)),
                                "A visible application icon was intercepted as a whole-Dock gesture.");
                        }
                        Require(activations == 0, "The grip contract invoked an application action.");
                    }
                    finally { foreach (var button in buttons) button.Click -= countClick; if (dock.IsPositionDragging) dock.EndDirectManipulation(true); }
                });
            }
            observations["compactDockLayouts"] = compactLayouts;

            settings.Position = "Bottom"; dock.UpdateSettings(); controller.Save(); await Frame();
            var config = Path.Combine(data, "config.json");
            var before = File.ReadAllBytes(config); var commits = 0;
            var buttonsBefore = DockTileButtons(dock); var imagesBefore = DockIconImages(dock);
            var cached = IconCacheCount(controller); var generation = Field<int>(dock, "generation");
            EventHandler changed = (_, _) => commits++; controller.Changed += changed;
            try
            {
                var start = GripPoint(dock, "resizeGrip");
                Require(ClientPointer(dock, 0x0201, start), "Cannot begin compact resize.");
                for (var index = 1; index <= 20; index++) dock.UpdateDirectManipulation(new Point(start.X + index * 4, start.Y + index));
                dock.UpdateLayout();
                check("A small Dock enlarges with retained visuals and no configuration or icon-cache work during preview", () =>
                {
                    Require(settings.IconSize > 16 && Field<int>(dock, "generation") == generation
                        && SameObjects(buttonsBefore, DockTileButtons(dock)) && SameObjects(imagesBefore, DockIconImages(dock)),
                        "A compact resize rebuilt controls instead of changing their retained geometry.");
                    Require(commits == 0 && before.SequenceEqual(File.ReadAllBytes(config)) && IconCacheCount(controller) <= cached,
                        "A compact resize wrote configuration, raised changes, or loaded more icons before release.");
                });
                dock.EndDirectManipulation(true); await Frame();
                check("Cancelling compact resize restores the 16 DIP minimum without persisting a partial size", () =>
                    Require(Math.Abs(settings.IconSize - 16) < .01 && commits == 0 && GetCapture() != handle
                        && before.SequenceEqual(File.ReadAllBytes(config)), "A cancelled compact resize changed saved size or retained capture."));
                start = GripPoint(dock, "resizeGrip"); Require(ClientPointer(dock, 0x0201, start), "Cannot begin minimum-bound resize.");
                ClientPointer(dock, 0x0202, new Point(start.X - 200, start.Y - 120)); await Frame();
                check("Shrinking below 16 DIP stops at the usable minimum without a redundant save", () =>
                    Require(Math.Abs(settings.IconSize - 16) < .01 && commits == 0 && !dock.IsPositionDragging,
                        "The compact lower bound was exceeded or an unchanged minimum rewrote configuration."));
            }
            finally { controller.Changed -= changed; if (dock.IsPositionDragging) dock.EndDirectManipulation(true); }

            // Scroll to the end of a deliberately long list. The last action
            // must never take the independent resize handle's input region.
            controller.State.Apps.AddRange(Enumerable.Range(0, 96).Select(index => new AppEntry
            {
                Id = "compact-overflow-" + index, Name = "Compact overflow " + index,
                LaunchPath = originalApps[0].LaunchPath, Source = "Manual"
            }));
            controller.State.PinnedIds = controller.State.Apps.Select(entry => entry.Id).ToList();
            foreach (var position in new[] { "Bottom", "Left" })
            {
                settings.Position = position; dock.UpdateSettings(); await Frame();
                var scroller = Field<ScrollViewer>(dock, "scroll");
                if (position == "Left") scroller.ScrollToBottom(); else scroller.ScrollToRightEnd();
                await Frame();
                check($"The compact overfull {position} Dock keeps its end grips separate after scrolling", () =>
                {
                    Require(position == "Left" ? scroller.ScrollableHeight > 0 : scroller.ScrollableWidth > 0,
                        "The overflow fixture did not produce a scroll range.");
                    foreach (var name in new[] { "positionGrip", "resizeGrip" })
                    {
                        var grip = Field<FrameworkElement>(dock, name);
                        var point = grip.TranslatePoint(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2), dock);
                        Require(ButtonAt(dock, point) == null, "A scrolled application owns an end grip's input.");
                        Require(ClientPointer(dock, 0x0201, dock.PointToScreen(point)) && dock.IsSizeDragging == (name == "resizeGrip"),
                            "A scrolled grip starts the wrong gesture or launches an application instead.");
                        dock.EndDirectManipulation(true);
                    }
                });
            }
        }
        finally
        {
            if (dock.IsPositionDragging) dock.EndDirectManipulation(true);
            controller.State.Apps = originalApps; controller.State.PinnedIds = originalPins;
            settings.Position = originalPosition; settings.IconSize = originalSize; settings.DockShape = originalShape; settings.DockPlacement = originalPlacement;
            dock.UpdateSettings();
        }
    }

    private static Rect ElementBounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static bool Overlaps(Rect a, Rect b) => Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > .5
        && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > .5;
    private static Button? ButtonAt(DockWindow dock, Point point)
    {
        for (var node = dock.InputHitTest(point) as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) if (node is Button button) return button;
        return null;
    }
}
