using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>
/// A line of a list that opens to its details (Plan AE, the Apps ledger): a button whose click asks the ViewModel to open
/// or close it, and whose <see cref="IsExpanded"/> the ViewModel's line sets. A screen reader hears it as expandable, with
/// its state, and can open and close it as it would any expander.
/// </summary>
internal sealed class Disclosure : Button
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(Disclosure), new PropertyMetadata(false, (d, e) => ((Disclosure)d).Announce((bool)e.OldValue, (bool)e.NewValue)));

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>Opens or closes it, as a click does.</summary>
    internal void Activate() => OnClick();

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void Announce(bool was, bool now)
    {
        if (UIElementAutomationPeer.FromElement(this) is Peer peer)
        {
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, State(was), State(now));
        }
    }

    private static ExpandCollapseState State(bool expanded) => expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    private sealed class Peer(Disclosure owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => State(owner.IsExpanded);

        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

        public void Expand()
        {
            if (!owner.IsExpanded) owner.Activate();
        }

        public void Collapse()
        {
            if (owner.IsExpanded) owner.Activate();
        }
    }
}
