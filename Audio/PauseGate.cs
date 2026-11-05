namespace MusicBot.Audio;

/// <summary>
/// "Portão" que bloqueia a escrita de áudio enquanto a música está em pausa.
/// </summary>
public sealed class PauseGate
{
    private TaskCompletionSource _gate = Completed();

    public bool IsPaused { get; private set; }

    public Task WaitAsync(CancellationToken cancellationToken)
        => IsPaused ? _gate.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        Interlocked.Exchange(ref _gate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _gate.TrySetResult();
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
