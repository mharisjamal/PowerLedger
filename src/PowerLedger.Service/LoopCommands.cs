using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>Something only the sampling loop may do, sent from another thread. <see cref="Done"/> completes once the loop has done it.</summary>
internal abstract class LoopCommand
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Done => _done.Task;

    internal void Complete() => _done.TrySetResult();

    internal void Fail(Exception error) => _done.TrySetException(error);
}

/// <summary>The machine is about to sleep: write everything and close the session.</summary>
internal sealed class SuspendCommand : LoopCommand
{
}

/// <summary>The machine woke: rebuild the sensors, detect the hardware, open a session.</summary>
internal sealed class ResumeCommand : LoopCommand
{
}

/// <summary>Replace the settings; refused whole when any value is out of range.</summary>
internal sealed class ApplySettingsCommand(ServiceSettings settings) : LoopCommand
{
    public ServiceSettings Settings { get; } = settings;
}

/// <summary>Forget what calibration has learned for this machine.</summary>
internal sealed class ResetCalibrationCommand : LoopCommand
{
}

/// <summary>The loop's inbox.</summary>
internal sealed class LoopCommands
{
    private readonly Channel<LoopCommand> _channel = Channel.CreateUnbounded<LoopCommand>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<LoopCommand> Reader => _channel.Reader;

    /// <summary>Queues the command. The task completes once the loop has carried it out, and fails if the loop refused it.</summary>
    public Task SendAsync(LoopCommand command)
    {
        if (!_channel.Writer.TryWrite(command)) command.Fail(new InvalidOperationException("The service is stopping."));
        return command.Done;
    }

    /// <summary>Closes the inbox and fails whatever is still queued, so no sender waits for ever.</summary>
    internal void Close()
    {
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var command)) command.Fail(new InvalidOperationException("The service is stopping."));
    }
}
