using System.Diagnostics;
using System.Threading.Channels;
using DotLXST;
using DotLXST.Hardware;

namespace Rnphone;

internal sealed class PhoneHardware : IAsyncDisposable
{
    private readonly LinuxDigitalPins? _pins;
    private readonly MatrixKeypad? _keypad;
    private readonly Lcd1602? _display;
    private readonly HardwareTelephoneControls _controls = new();
    private readonly Channel<(string Key, KeyTransition Transition)> _keys = Channel.CreateBounded<(string, KeyTransition)>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task _worker = Task.CompletedTask;
    private Task _dial = Task.CompletedTask;
    private readonly Action<string> _error;
    private Task? _disposal;
    private readonly object _gate = new();

    public PhoneHardware(PhoneConfiguration configuration, Action<string> error)
    {
        _error = error;
        try
        {
            if (configuration.Keypad is not null)
            {
                _pins = new(Environment.GetEnvironmentVariable("DOTLXST_GPIOCHIP") ?? "/dev/gpiochip0");
                _keypad = new(_pins) { Error = (context, exception) => error($"{context} {exception?.Message}") };
                if (configuration.HookPin is { } hook) _keypad.EnableHook(hook);
                _keypad.KeyChanged += (key, transition) =>
                {
                    if (!_keys.Writer.TryWrite((key, transition))) error("Hardware key queue overflow; event discarded.");
                };
            }
            if (configuration.Display is not null)
            {
                _display = new(new LinuxI2cByteBus(Environment.GetEnvironmentVariable("DOTLXST_I2C_DEVICE") ?? "/dev/i2c-1"), ownsBus: true);
                _display.Initialize();
                _display.WriteText("Telephone Ready");
            }
        }
        catch
        {
            try { _display?.Dispose(); }
            finally { _pins?.Dispose(); }
            throw;
        }
    }

    public void Start(TelephoneEndpoint telephone, Func<PhoneConfiguration> configuration, Func<string, CancellationToken, Task> dial)
    {
        if (_keypad is null && _display is null) return;
        _keypad?.Start(_stop.Token);
        _worker = Task.Run(async () =>
        {
            var previous = CallState.Available;
            var connected = TimeSpan.Zero;
            (string First, string Second)? rendered = null;
            void Show(string first, string second)
            {
                if (_display is null || rendered == (first, second)) return;
                _display.WriteText(first);
                _display.WriteText(second, y: 1);
                rendered = (first, second);
            }
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var settings = configuration();
                        _controls.Phonebook = settings.Phonebook.Select(entry =>
                            new HardwarePhonebookEntry(entry.Value.Hash, entry.Key, entry.Value.Alias)).ToArray();
                        var state = telephone.State;
                        if (state != previous)
                        {
                            if (state == CallState.Available) _controls.Ready(_clock.Elapsed);
                            if (state == CallState.Ringing && _controls.State == HardwareDialState.Sleeping)
                                _controls.Ready(_clock.Elapsed);
                            if (state == CallState.Established) connected = _clock.Elapsed;
                            previous = state;
                        }
                        while (_keys.Reader.TryRead(out var key))
                        {
                            var command = _controls.HandleKey(key.Key, key.Transition, _clock.Elapsed, state, telephone.IsIncomingCall);
                            if (command?.Kind == HardwareCommandKind.Answer) await telephone.AnswerAsync(_stop.Token);
                            if (command?.Kind == HardwareCommandKind.Hangup) telephone.Hangup();
                            if (command?.Kind == HardwareCommandKind.Dial)
                            {
                                if (_dial.IsCompleted) _dial = DialAsync(command.Identity!);
                            }
                            state = telephone.State;
                        }
                        if (_controls.Tick(_clock.Elapsed, state, telephone.IsBusy)) { _display?.Sleep(); rendered = null; }
                        if (_display is not null && _controls.State != HardwareDialState.Sleeping)
                        {
                            if (state == CallState.Established)
                            {
                                Show("Call connected", $"{Math.Round((_clock.Elapsed - connected).TotalSeconds)}s");
                            }
                            else if (state == CallState.Ringing && telephone.RemoteIdentity is { } remote)
                            {
                                var hash = remote.HexHash;
                                var entry = _controls.Phonebook.LastOrDefault(value => value.Identity == hash);
                                Show(entry?.Name ?? hash[..16], entry?.Alias is { } alias ? ("(" + alias + ")").PadLeft(16) : hash[16..]);
                            }
                            else
                            {
                                Show(state is CallState.Calling or CallState.Connecting ? "Calling" : _controls.FirstLine, _controls.SecondLine);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception exception) { _error("Telephone hardware: " + exception.Message); }
                    await Task.Delay(100, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        async Task DialAsync(string identity)
        {
            try { await dial(identity, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { _error("Hardware dial: " + exception.Message); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _stop.Cancel();
            return new(_disposal = FinishAsync());
        }
    }

    private async Task FinishAsync()
    {
        try
        {
            if (_keypad is not null) await _keypad.DisposeAsync();
            await _worker.ConfigureAwait(false);
            await _dial.ConfigureAwait(false);
        }
        finally
        {
            try { _display?.Dispose(); }
            finally { _pins?.Dispose(); _stop.Dispose(); }
        }
    }
}
