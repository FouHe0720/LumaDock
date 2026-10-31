using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace LumaDock.App.Center;

/// <summary>An app tile is one named action; its artwork and label are decorative children.</summary>
internal sealed class AppTileButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new AppTilePeer(this);

    private sealed class AppTilePeer(AppTileButton owner) : ButtonAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.DataContext is AppTileViewModel app ? app.Name : base.GetNameCore();
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
