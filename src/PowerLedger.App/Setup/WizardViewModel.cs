using System.ComponentModel;
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
/// The first-run wizard (spec §9): the tariff, the detected hardware to confirm, with each external monitor the service
/// detected (Plan J), and what measured, calibrated and estimated mean on this machine. A step saves before the wizard
/// moves on and stays put when the save fails; an empty tariff means later. Without the service the machine can't be
/// saved, so that step says so and moves on. The service may come up after the wizard does, as at the first start after
/// installing, so the wizard reads again when it connects. Finishing is remembered in ui.json.
/// </summary>
internal sealed class WizardViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;
    private readonly IMachineHistory _history;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private SetupStep _step;
    private ServiceStatus? _status;
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
        _link.ConnectionChanged += OnConnectionChanged;
        Machine.PropertyChanged += OnMachineChanged;
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
        _threads.Background(() => _ = ReadAsync(refill: true));
    }

    public void Dispose()
    {
        _link.ConnectionChanged -= OnConnectionChanged;
        Machine.PropertyChanged -= OnMachineChanged;
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

    /// <summary>What this machine's readings will be, from the service's sources and the chassis in the machine profile.
    /// Only a laptop's readings come from its own battery: the battery a desktop shows is a UPS, which powers more than
    /// the machine, so that battery is never the reason a desktop's readings are measured. A UPS or power supply read
    /// directly over USB is different: when one gives the machine's total (spec: Plan L), its reading is measured unless
    /// the UPS itself gives only its load as a share of its rated VA. A laptop keeps its own story regardless, since one
    /// running off its battery never takes either as its total, and the wizard is asked about the laptop, not the mains.</summary>
    internal static string ReadingsFor(ServiceStatus? status, ChassisKind chassis)
    {
        if (status is null) return "The service isn't running yet. Once it is, each reading on the Now screen shows its quality.";
        if (chassis != ChassisKind.Laptop && status.Last is { Total: TotalSource.Ups or TotalSource.PowerSupply } last)
        {
            var kind = last.Total == TotalSource.Ups ? PowerDeviceKind.Ups : PowerDeviceKind.PowerSupply;
            var name = status.PowerDevices?.FirstOrDefault(d => d.Kind == kind)?.Name ?? (kind == PowerDeviceKind.Ups ? "UPS" : "power supply");
            return last.Quality == Quality.Measured
                ? $"This machine reads its {name} over USB, so its readings are measured."
                : $"This machine reads its {name} over USB. "
                  + "A UPS that only gives its load as a share of its rated VA is estimated, not measured.";
        }
        bool Has(string name) => status.Sources.Any(s => s.Name == name && s.Supported);
        const string battery = "On battery its readings are measured; plugged in, they are calibrated once the model has learned from battery time, and estimated until then.";
        const string processor = "This machine reports its processor's energy, so the processor is measured; the rest is estimated from the machine profile";
        const string ups = "The battery Windows shows is taken for a UPS, which powers more than this machine, so it isn't used.";
        return (chassis, Has("battery"), Has("energy-meter")) switch
        {
            (ChassisKind.Laptop, true, true) => "This machine has a battery and a processor energy meter. " + battery,
            (ChassisKind.Laptop, true, false) => "This machine has a battery. " + battery,
            (_, true, true) => processor + ". " + ups,
            (_, true, false) => "This machine's readings are estimated from load and the machine profile. " + ups,
            (_, false, true) => processor + ", since only a battery shows the whole machine's draw.",
            _ => "This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.",
        };
    }

    /// <param name="refill">Fill the machine form even when it holds values; a reconnect only fills an empty one.</param>
    private async Task ReadAsync(bool refill)
    {
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var detected = _history.Detected();
        _threads.Post(() =>
        {
            if (settings is not null && (refill || !Machine.IsLoaded)) Machine.Load(settings);
            if (status is not null) Machine.ShowMonitors(status.Monitors ?? []);   // a service before monitors lists none
            Detected = detected?.Summary(_culture) ?? "The service hasn't detected this machine yet; it does when it starts.";
            _status = status;
            Readings = ReadingsFor(_status, Machine.Chassis);
        });
    }

    /// <summary>The service came up after the wizard did: read what it knows. Raised on the link's thread.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (connected) _threads.Background(() => _ = ReadAsync(refill: false));
    }

    /// <summary>The readings step speaks of the chassis the machine step chose. Raised on the UI thread.</summary>
    private void OnMachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServiceForm.Chassis)) Readings = ReadingsFor(_status, Machine.Chassis);
    }

    private void FinishSetup()
    {
        _ui.FinishFirstRun();
        Finished?.Invoke();
    }
}
