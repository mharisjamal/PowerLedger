using System.Windows.Controls;

namespace PowerLedger.App.Aero;

/// <summary>Aero's Household page (Aero look design §1; Plan S, P4): the household's figures in wells, its PCs with their
/// discs, how many wait for approval, Manage and Sign in as glass panels, over <see cref="HouseholdViewModel"/> and its
/// <see cref="SignInViewModel"/>. Pairing by code, approving, the recovery code and sign-in's browser step stay the shared
/// dialogs, which take Aero's colours from the palette.</summary>
public partial class HouseholdView : UserControl
{
    public HouseholdView() => InitializeComponent();
}
