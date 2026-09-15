using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>The first-run wizard's steps (spec §9).</summary>
internal enum SetupStep
{
    Tariff,
    Machine,
    Readings,
}

/// <summary>
/// The first-run wizard (spec §9): the tariff, the detected hardware to confirm, and what measured, calibrated and
/// estimated mean on this machine. A step saves before the wizard moves on and stays put when the save fails; an empty
/// tariff means later. Without the service the machine can't be saved, so that step says so and moves on. Finishing is
/// remembered in ui.json.
/// </summary>
internal sealed class WizardViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly IMachineHistory _history;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private SetupStep _step;
    private string _detected = "";
    private string _readings = "";
    private string? _message;
    private bool _moving;

    public WizardViewModel(
        IServiceLink link, IMachineHistory history, IUiSettings ui, UiThreads threads, TimeProvider clock, TimeZoneInfo zone,
        CultureInfo culture, string regionCurrency)
    {
        _link = link;
        _history = history;
        _ui = ui;
        _threads = threads;
        _culture = culture;
        Tariff = new TariffForm(link, threads, clock, zone, culture, regionCurrency);
        Machine = new ServiceForm(link, threads, culture);
        Next = new RelayCommand(() => _ = NextAsync());
        Back = new RelayCommand(() => Step = Step == SetupStep.Readings ? SetupStep.Machine : SetupStep.Tariff);
        Finish = new RelayCommand(FinishSetup);
    }

    /// <summary>Raised when the user finishes; the shell shows the Now screen.</summary>
    public event Action? Finished;

    public SetupStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(IsFirst));
            OnPropertyChanged(nameof(IsLast));
        }
    }

    public bool IsFirst => Step == SetupStep.Tariff;

    public bool IsLast => Step == SetupStep.Readings;

    public TariffForm Tariff { get; }

    public ServiceForm Machine { get; }

    /// <summary>What the service detected, in one line.</summary>
    public string Detected { get => _detected; private set => SetProperty(ref _detected, value); }

    /// <summary>Which qualities this machine's readings will have.</summary>
    public string Readings { get => _readings; private set => SetProperty(ref _readings, value); }

    /// <summary>Why a step moved on without saving, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public ICommand Next { get; }

    public ICommand Back { get; }

    public ICommand Finish { get; }

    /// <summary>Begins at the tariff and reads what the service knows, off the UI thread. Call on the UI thread.</summary>
    public void Start()
    {
        Step = SetupStep.Tariff;
        Message = null;
        _threads.Background(() => _ = ReadAsync());
    }

    /// <summary>Saves this step, and moves on when that worked. Call on the UI thread.</summary>
    internal async Task NextAsync()
    {
        if (_moving) return;
        _moving = true;
        var step = Step;
        var saved = step switch
        {
            SetupStep.Tariff => Tariff.IsEmpty || await Tariff.SaveAsync().ConfigureAwait(false),
            SetupStep.Machine => !Machine.IsLoaded || await Machine.SaveAsync().ConfigureAwait(false),
            _ => true,
        };
        _threads.Post(() =>
        {
            _moving = false;
            if (!saved) return;
            if (step == SetupStep.Machine && !Machine.IsLoaded)
            {
                Message = "The service isn't running, so the machine wasn't saved. Settings has it once the service starts.";
            }
            Step = step == SetupStep.Tariff ? SetupStep.Machine : SetupStep.Readings;
        });
    }

    /// <summary>What this machine's readings will be, from the service's sources.</summary>
    internal static string ReadingsFor(ServiceStatus? status)
    {
        if (status is null) return "The service isn't running yet. Once it is, each reading on the Now screen shows its quality.";
        bool Has(string name) => status.Sources.Any(s => s.Name == name && s.Supported);
        const string battery = "On battery its readings are measured; plugged in, they are calibrated once the model has learned from battery time, and estimated until then.";
        if (Has("battery") && Has("energy-meter")) return "This machine has a battery and a processor energy meter. " + battery;
        if (Has("battery")) return "This machine has a battery. " + battery;
        if (Has("energy-meter"))
            return "This machine reports its processor's energy, so the processor is measured; the rest is estimated from the machine profile, since only a battery shows the whole machine's draw.";
        return "This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.";
    }

    private async Task ReadAsync()
    {
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var detected = _history.Detected();
        _threads.Post(() =>
        {
            if (settings is not null) Machine.Load(settings);
            Detected = detected?.Summary(_culture) ?? "The service hasn't detected this machine yet; it does when it starts.";
            Readings = ReadingsFor(status);
        });
    }

    private void FinishSetup()
    {
        _ui.FinishFirstRun();
        Finished?.Invoke();
    }
}
