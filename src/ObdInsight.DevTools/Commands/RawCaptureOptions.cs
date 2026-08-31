namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Everything a raw capture needs to run without asking a human anything.
///
/// The interactive command fills this from prompts; the headless path fills it from command
/// line arguments. Both then run the identical capture body, so a remotely-driven session and a
/// hand-driven one cannot diverge in behaviour.
/// </summary>
public sealed record RawCaptureOptions
{
    /// <summary>Free-text bus label, becomes part of the output directory name.</summary>
    public required string BusLabel { get; init; }

    /// <summary>Capture window. 0 means "until stopped" - not valid headlessly.</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>Directory under which a per-session capture folder is created.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>
    /// When true: no prompts, no live-rendered table, no keyboard. Console output is plain text
    /// suitable for an SSH pipe, and the summary JSON path is written to stdout on success.
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>
    /// Optional file watched for stimulus markers while capturing. Each line appended becomes a
    /// timestamped, labelled marker:
    /// <code>echo left-signal-on &gt;&gt; markers.txt</code>
    /// This replaces the interactive SPACE key when running headlessly, and is strictly better
    /// for the stimulus protocol: markers arrive already labelled instead of being labelled from
    /// memory afterwards, and they can be triggered from a phone at the wheel rather than by
    /// reaching for the laptop.
    /// </summary>
    public string? MarkerFilePath { get; init; }

    /// <summary>
    /// Surfaces BLE transport diagnostics (device lookup, GATT session, service and
    /// characteristic discovery). Off by default because it is noisy; essential when a connect
    /// fails, since "Failed to connect" alone does not say which stage gave up.
    /// </summary>
    public bool Verbose { get; init; }
}
