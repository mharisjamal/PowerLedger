using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// What floats over Aero's stage (Aero look design §1): the top bar's and the sidebar's menus, glass context menus
/// (A.Menu) opened under their trigger; the dialogs, which open out of their trigger on the spring over a scrim; and the
/// toasts, glass capsules at the foot that go by themselves. Esc closes a menu (the menu's own) or a dialog (the
/// window's), and the focus goes back to what opened it.
/// </summary>
internal partial class AeroWindow
{
    private GlassPanel? _modal;
    private FrameworkElement? _modalFrom;
    private Action? _modalClosed;
    private GlassPanel? _toast;
    private DispatcherTimer? _toastTimer;

    /// <summary>The menu open now, for a test; null with none.</summary>
    internal ContextMenu? OpenMenu { get; private set; }

    /// <summary>The dialog open now, for a test; null with none.</summary>
    internal GlassPanel? Modal => _modal;

    /// <summary>What the toast on show says, for a test; null with none.</summary>
    internal string? ToastText => _toast is null ? null : AutomationProperties.GetName(_toast);

    // ---------------------------------------------------------------- the menus

    /// <summary>The bell: PCs waiting for approval, then today's unusual hours (design §1, §4).</summary>
    private void BellClick(object sender, RoutedEventArgs e)
    {
        var culture = CultureInfo.CurrentCulture;
        var pending = _shell.Household.PendingApprovals;
        var items = new List<Control> { Header("Approvals") };
        items.Add(pending > 0 ? Item(Bell.Approvals(pending), null, () => _shell.Page = Page.Household) : Line(Bell.Approvals(0)));
        items.Add(Header("Unusual use today"));
        var alerts = Alerts;
        if (alerts.Count == 0) items.Add(Line(_shell.Insights?.Report is null ? "Insights haven't been worked out yet." : "Nothing unusual today."));
        foreach (var alert in alerts)
            items.Add(Item(Bell.Line(alert, TimeZoneInfo.Local, culture), Bell.Figure(alert, culture), () => _shell.Page = Page.Insights));
        Open(BellButton, items);
    }

    /// <summary>The service pulse: its state, and Restart (or Start, while it's down).</summary>
    private void ServiceClick(object sender, RoutedEventArgs e)
    {
        var status = _shell.Now.Status;
        var items = new List<Control> { Header("Service"), Line(status.Running ? $"{status.State}. {status.Sampling}" : status.State) };
        items.Add(status.Running
            ? Item("Restart the service", null, () => OpenModal(ServiceButton, RestartDialog()))
            : Item("Start the service", null, () => _shell.Now.StartService.Execute(null)));
        Open(ServiceButton, items);
    }

    /// <summary>The household button: the PCs with their figures, the Household page and Add a PC.</summary>
    private void HouseholdClick(object sender, RoutedEventArgs e)
    {
        var rows = YourPcs.Rows(_shell.Household.Members, _shell.Now.Live.Watts, CultureInfo.CurrentCulture);
        var items = new List<Control> { Header(_shell.Household.HasHousehold ? "Your household" : "Not in a household yet") };
        items.AddRange(rows.Select(row => Item(row.Name, row.Figure, () => _shell.Page = Page.Household)));
        items.Add(Rule());
        items.Add(Item("Open Household", null, () => _shell.Page = Page.Household));
        items.Add(Item("Add a PC", null, () => _shell.Household.AddPc.Execute(null)));
        Open(HouseholdButton, items);
    }

    /// <summary>Switch look: the other two looks, each with a line of what it is (design §2).</summary>
    private void SwitchLookClick(object sender, RoutedEventArgs e)
    {
        var items = new List<Control> { Header("Switch look") };
        foreach (var look in AeroLooks.Others)
        {
            var face = new StackPanel();
            face.Children.Add(new TextBlock { Text = look.ToString() });
            var line = new TextBlock { Text = AeroLooks.Describe(look), Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
            line.SetResourceReference(TextBlock.FontSizeProperty, "A.T.Small");
            face.Children.Add(line);
            var item = Item("", null, () => SwitchTo(look));
            item.Header = face;
            AutomationProperties.SetName(item, look.ToString());
            items.Add(item);
        }
        Open(SwitchLookButton, items, above: true);
    }

    /// <summary>Opens <paramref name="items"/> as a glass menu under <paramref name="trigger"/>, its right edge on the
    /// trigger's (or above it, for the sidebar's foot); a second click on the same trigger closes it.</summary>
    private void Open(FrameworkElement trigger, IReadOnlyList<Control> items, bool above = false)
    {
        if (OpenMenu is { IsOpen: true } open && open.PlacementTarget == trigger)
        {
            open.IsOpen = false;
            return;
        }
        var menu = new ContextMenu { PlacementTarget = trigger };
        menu.SetResourceReference(StyleProperty, "A.Menu");
        menu.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "A.MenuItem");
        foreach (var item in items) menu.Items.Add(item);
        if (above) menu.Placement = PlacementMode.Top;
        else
        {
            menu.Placement = PlacementMode.Custom;
            // The template stands 14 px inside the popup for its shadow: line the glass itself up with the trigger.
            menu.CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(new Point(target.Width - popup.Width + 14, target.Height - 6), PopupPrimaryAxis.Horizontal)];
        }
        menu.Closed += (_, _) =>
        {
            if (OpenMenu == menu) OpenMenu = null;
            if (trigger.ContextMenu == menu) trigger.ContextMenu = null;
        };
        trigger.ContextMenu = menu;   // the menu finds the window's styles through its owner
        OpenMenu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem Header(string text)
    {
        var face = new TextBlock { Text = text };
        face.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text3");
        face.SetResourceReference(TextBlock.FontSizeProperty, "A.T.Tiny");
        return new MenuItem { Header = face, Focusable = false, IsHitTestVisible = false, Padding = new Thickness(0) };
    }

    /// <summary>A rule between a menu's parts. It names its own style: a context menu gives its item style (A.MenuItem) to
    /// every item that has none of its own, and that style on a separator stops the menu opening at all.</summary>
    private static Separator Rule()
    {
        var rule = new Separator();
        rule.SetResourceReference(StyleProperty, MenuItem.SeparatorStyleKey);
        return rule;
    }

    private static MenuItem Line(string text)
    {
        var face = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
        face.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
        return new MenuItem { Header = face, Focusable = false, IsHitTestVisible = false };
    }

    private static MenuItem Item(string text, string? figure, Action act)
    {
        var item = new MenuItem { Header = text, InputGestureText = figure ?? "" };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => act();
        return item;
    }

    // ---------------------------------------------------------------- the dialogs

    /// <summary>Restart the service?: what it does, and Restart, which asks Windows first.</summary>
    private FrameworkElement RestartDialog()
    {
        var body = DialogHead("Restart the service?", "Readings pause for a few seconds while it restarts. Windows asks first, as the service runs for every user.");
        body.Children.Add(Actions(
            DialogButton("Cancel", accent: false, () => CloseModal()),
            DialogButton("Restart", accent: true, () =>
            {
                CloseModal();
                ServiceRestart.Run();
                Toast("Restarting the service");
            })));
        return body;
    }

    private static StackPanel DialogHead(string title, string line)
    {
        var body = new StackPanel();
        var head = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        head.SetResourceReference(StyleProperty, "A.Text.Title");
        head.SetResourceReference(TextBlock.FontSizeProperty, "A.T.Modal");
        var sub = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        sub.SetResourceReference(StyleProperty, "A.Text.Secondary");
        body.Children.Add(head);
        body.Children.Add(sub);
        return body;
    }

    private static StackPanel Actions(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(button);
        }
        return row;
    }

    private static Button DialogButton(string text, bool accent, Action act)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(StyleProperty, accent ? "A.AccentBtn" : "A.GhostBtn");
        button.Click += (_, _) => act();
        return button;
    }

    /// <summary>
    /// Opens a dialog out of <paramref name="trigger"/> (design §1: "modals that open from their trigger"): it grows on
    /// the spring from the trigger's middle to the window's, over a scrim that fades in and takes the pointer; under
    /// reduced motion it only fades. The keyboard cycles in it until it closes. What it covers keeps its colours, so the
    /// frost shows the page as it is rather than greyed out. <paramref name="closed"/> hears it close, however it closes
    /// (a button, Esc, the scrim, or another dialog in its place); <paramref name="style"/> names its A.Modal style.
    /// </summary>
    internal GlassPanel OpenModal(FrameworkElement? trigger, FrameworkElement body, Action? closed = null, string style = "A.Modal")
    {
        CloseModal(restoreFocus: false);
        OpenMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        Scrim.Visibility = Visibility.Visible;
        AeroMotion.Fade(Scrim, OpacityProperty, 1, AeroMotion.Scrim, AeroMotion.Glide, from: 0);
        var modal = new GlassPanel { Content = body };
        modal.SetResourceReference(StyleProperty, style);
        KeyboardNavigation.SetTabNavigation(modal, KeyboardNavigationMode.Cycle);
        ModalHost.Children.Add(modal);
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        modal.RenderTransformOrigin = new Point(.5, .5);
        modal.RenderTransform = new TransformGroup { Children = { scale, shift } };
        if (trigger is not null && !AeroMotion.Reduced && trigger.IsVisible)
        {
            var at = trigger.TranslatePoint(new Point(trigger.ActualWidth / 2, trigger.ActualHeight / 2), Layer);
            AeroMotion.Move(shift, TranslateTransform.XProperty, 0, AeroMotion.Modal, AeroMotion.Spring, from: at.X - Layer.ActualWidth / 2);
            AeroMotion.Move(shift, TranslateTransform.YProperty, 0, AeroMotion.Modal, AeroMotion.Spring, from: at.Y - Layer.ActualHeight / 2);
            AeroMotion.Move(scale, ScaleTransform.ScaleXProperty, 1, AeroMotion.Modal, AeroMotion.Spring, from: .4);
            AeroMotion.Move(scale, ScaleTransform.ScaleYProperty, 1, AeroMotion.Modal, AeroMotion.Spring, from: .4);
        }
        AeroMotion.Fade(modal, OpacityProperty, 1, AeroMotion.Modal, AeroMotion.Glide, from: 0);
        FollowShapeFor(AeroMotion.MoveMs(AeroMotion.Modal));   // the window's shape grows with it out of the trigger
        Frost(modal);
        _modal = modal;
        _modalFrom = trigger;
        _modalClosed = closed;
        modal.Dispatcher.BeginInvoke(() =>
        {
            if (_modal == modal && !modal.IsKeyboardFocusWithin) modal.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Input);
        return modal;
    }

    internal void CloseModal(bool restoreFocus = true)
    {
        if (_modal is null) return;
        ModalHost.Children.Remove(_modal);
        _modal = null;
        Scrim.Visibility = Visibility.Collapsed;
        if (restoreFocus) _modalFrom?.Focus();
        _modalFrom = null;
        var closed = _modalClosed;
        _modalClosed = null;
        closed?.Invoke();
    }

    private void ScrimDown(object sender, MouseButtonEventArgs e) => CloseModal();

    // ---------------------------------------------------------------- the toasts

    /// <summary>A glass capsule at the foot saying <paramref name="text"/>, read out politely, which springs up and goes
    /// by itself after <see cref="AeroMotion.ToastHold"/>.</summary>
    internal void Toast(string text)
    {
        if (_toast is not null) ToastHost.Children.Remove(_toast);
        _toastTimer?.Stop();
        var face = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "A.B.Accent");
        face.Children.Add(dot);
        var words = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.SetResourceReference(StyleProperty, "A.Text.Body");
        face.Children.Add(words);
        var toast = new GlassPanel { Content = face };
        toast.SetResourceReference(StyleProperty, "A.Toast");
        AutomationProperties.SetName(toast, text);
        ToastHost.Children.Add(toast);
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        toast.RenderTransformOrigin = new Point(.5, .5);
        toast.RenderTransform = new TransformGroup { Children = { scale, shift } };
        AeroMotion.Fade(toast, OpacityProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: 0);
        AeroMotion.Move(shift, TranslateTransform.YProperty, 0, AeroMotion.Toast, AeroMotion.Spring, from: 16);
        AeroMotion.Move(scale, ScaleTransform.ScaleXProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: .9);
        AeroMotion.Move(scale, ScaleTransform.ScaleYProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: .9);
        FollowShapeFor(AeroMotion.MoveMs(AeroMotion.Toast));
        Frost(toast);
        UIElementAutomationPeer.CreatePeerForElement(toast)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _toast = toast;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AeroMotion.ToastHold) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            AeroMotion.Fade(toast, OpacityProperty, 0, AeroMotion.ReducedFade, AeroMotion.Glide, done: () =>
            {
                ToastHost.Children.Remove(toast);
                if (_toast == toast) _toast = null;
            });
        };
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- frost for what floats

    /// <summary>
    /// Real frost on a dialog or a toast (the prototype's AttachFrost): the stage under it, blurred, in its frost layer,
    /// so the panes behind read as glass seen through glass rather than as text through a tint. BlurEffect is kept to
    /// these small surfaces (CLAUDE.md), and the view of the stage is lined up when the pane is laid out or the window
    /// resized, never per frame; the spring it opens on moves it a little off its rest place for a moment, which the
    /// blur hides.
    /// </summary>
    private void Frost(GlassPanel pane)
    {
        pane.ApplyTemplate();
        if (pane.Template?.FindName("PART_Frost", pane) is not Border host) return;
        const double bleed = 40;
        // The window's ground, then its room (the backdrop), then the stage: the stage alone is see-through between its panes.
        var room = new VisualBrush(Room) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var brush = new VisualBrush(Stage) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var layers = new Grid { Margin = new Thickness(-bleed), IsHitTestVisible = false };
        layers.SetBinding(Panel.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { Source = this });
        layers.Children.Add(new System.Windows.Shapes.Rectangle { Fill = room });
        layers.Children.Add(new System.Windows.Shapes.Rectangle { Fill = brush });
        layers.Effect = new BlurEffect { Radius = 24, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        host.Child = layers;
        void Align()
        {
            if (!pane.IsLoaded || pane.ActualWidth <= 0) return;
            var r = pane.CornerRadius.TopLeft;
            host.Clip = new RectangleGeometry(new Rect(host.RenderSize), r, r);
            // Where the pane sits over the stage at rest: its layout place, without the spring's transform.
            var at = pane.TranslatePoint(new Point(0, 0), Layer);
            if (pane.RenderTransform is TransformGroup { Value: var m } && !m.IsIdentity) at = new Point(at.X - m.OffsetX, at.Y - m.OffsetY);
            var stageAt = Stage.TranslatePoint(new Point(0, 0), Layer);
            brush.Viewbox = new Rect(at.X - stageAt.X - bleed, at.Y - stageAt.Y - bleed, pane.ActualWidth + 2 * bleed, pane.ActualHeight + 2 * bleed);
            var roomAt = Room.TranslatePoint(new Point(0, 0), Layer);
            room.Viewbox = new Rect(at.X - roomAt.X - bleed, at.Y - roomAt.Y - bleed, pane.ActualWidth + 2 * bleed, pane.ActualHeight + 2 * bleed);
        }
        SizeChangedEventHandler resized = (_, _) => Align();
        pane.SizeChanged += resized;
        pane.Loaded += (_, _) => Align();
        SizeChanged += resized;
        pane.Unloaded += (_, _) => SizeChanged -= resized;   // a toast or dialog gone lets go of the window
    }
}
