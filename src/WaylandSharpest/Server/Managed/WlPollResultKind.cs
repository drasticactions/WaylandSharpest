namespace Wayland.Server.Managed;

/// <summary>What a wait reported.</summary>
internal enum WlPollResultKind
{
    /// <summary>A watched file descriptor became ready.</summary>
    Fd,

    /// <summary>A watched signal was delivered.</summary>
    Signal,
}
