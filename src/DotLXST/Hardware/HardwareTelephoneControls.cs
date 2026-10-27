namespace DotLXST.Hardware;

public enum HardwareDialState { Idle, Dialing, Sleeping }
public enum HardwareCommandKind { Dial, Answer, Hangup }
public sealed record HardwareCommand(HardwareCommandKind Kind, string? Identity = null);
public sealed record HardwarePhonebookEntry(string Identity, string Name, string? Alias);

/// <summary>Device-independent rnphone keypad/display interaction; callers serialize access.</summary>
public sealed class HardwareTelephoneControls
{
    public HardwareDialState State { get; private set; }
    public string Input { get; private set; } = "";
    public TimeSpan LastEvent { get; private set; }
    public IReadOnlyList<HardwarePhonebookEntry> Phonebook { get; set; } = [];
    public string FirstLine { get; private set; } = "Telephone Ready";
    public string SecondLine { get; private set; } = "";

    public void Ready(TimeSpan elapsed)
    {
        State = HardwareDialState.Idle;
        Input = "";
        LastEvent = elapsed;
        FirstLine = "Telephone Ready";
        SecondLine = "";
    }

    public bool Tick(TimeSpan elapsed, CallState callState, bool busy)
    {
        if (callState == CallState.Available && !busy && State == HardwareDialState.Idle &&
            elapsed - LastEvent >= TimeSpan.FromSeconds(15))
        {
            State = HardwareDialState.Sleeping;
            return true;
        }
        return false;
    }

    public HardwareCommand? HandleKey(string key, KeyTransition transition, TimeSpan elapsed,
        CallState callState, bool incoming = false)
    {
        LastEvent = elapsed;
        if (State == HardwareDialState.Sleeping) Ready(elapsed);
        var down = transition == KeyTransition.Down;
        if (callState == CallState.Ringing && incoming)
        {
            if ((key == "D" && down) || (key == "hook" && !down)) return new(HardwareCommandKind.Answer);
            if (key == "C" && down) return new(HardwareCommandKind.Hangup);
        }
        else if (callState != CallState.Available)
        {
            if ((key == "D" && down) || (key == "hook" && down)) return new(HardwareCommandKind.Hangup);
        }
        else if (State == HardwareDialState.Idle)
        {
            if (down && (key == "A" || IsDigit(key)))
            {
                Input = key == "A" ? "" : Input + key;
                State = HardwareDialState.Dialing;
                UpdateDisplay();
            }
        }
        else
        {
            if (down)
            {
                if (IsDigit(key) && Input.Length < 256) Input += key;
                if (key == "A") Ready(elapsed);
                if (key == "B" && Input.Length != 0) Input = Input[..^1];
                if (key == "C") Input = "";
            }
            if ((key == "D" && down) || (key == "hook" && !down))
            {
                var match = Phonebook.FirstOrDefault(entry => entry.Alias == Input);
                if (match is not null)
                {
                    Input = "";
                    State = HardwareDialState.Idle;
                    return new(HardwareCommandKind.Dial, match.Identity);
                }
            }
            UpdateDisplay();
        }
        return null;
    }

    private static bool IsDigit(string key) => key.Length == 1 && char.IsAsciiDigit(key[0]);
    private void UpdateDisplay()
    {
        if (State != HardwareDialState.Dialing) return;
        FirstLine = Input;
        SecondLine = Phonebook.LastOrDefault(entry => entry.Alias == Input)?.Name ??
            (Input.Length == 0 ? "Enter number" : "Unknown");
    }
}
