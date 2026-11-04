namespace LXST;

public sealed partial class TelephoneEndpoint
{
    /// <summary>Pauses or resumes the active call's AGC without changing transmit squelch.</summary>
    public void PauseAutomaticGainControl(bool pause = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_call is { UseAutomaticGainControl: true, EstablishedAt: not null } call)
                call.AgcPaused = pause;
        }
    }

    public void ResumeAutomaticGainControl(bool resume = true) => PauseAutomaticGainControl(!resume);
}
