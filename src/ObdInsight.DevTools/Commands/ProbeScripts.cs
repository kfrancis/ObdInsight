namespace ObdInsight.DevTools.Commands;

/// <summary>What the operator is asked to do, and how the runner should time it.</summary>
public enum ProbeStepKind
{
    /// <summary>Wait a fixed period with no stimulus. Used for the noise baseline.</summary>
    Idle,

    /// <summary>Prompt, wait for confirmation, then hold the state for a capture window.</summary>
    Action
}

/// <summary>One instruction in a probe script.</summary>
/// <param name="Kind">Timed wait, or prompt-and-hold.</param>
/// <param name="Instruction">Shown to the operator, verbatim.</param>
/// <param name="Label">Marker label recorded at the moment the operator confirms.</param>
/// <param name="Seconds">Idle: how long to wait. Action: how long to hold after confirming.</param>
public sealed record ProbeStep(ProbeStepKind Kind, string Instruction, string Label, int Seconds);

/// <summary>A named sequence of probes, safe to run under stated conditions.</summary>
public sealed record ProbeScript(string Name, string Description, string SafeWhen, IReadOnlyList<ProbeStep> Steps);

/// <summary>
///     Declarative stimulus scripts implementing the discovery protocol in
///     <c>.local/CAN_TOOLING_PLAN.md</c> section 7.2.
///     The scripts are vehicle-independent; only the results are vehicle-specific. Running
///     `lighting` + `body` + `driver-input` on an undocumented car yields a usable body-signal map
///     in one parked session with no prior documentation - which is the point.
///     Three properties matter and are easy to lose if a human improvises the sequence:
///     1. An idle baseline comes FIRST. Every bit that moves with no stimulus is a counter, CRC,
///     or drifting sensor, and must be masked out of every later comparison. Skipping this is
///     why a naive before/after diff drowns in false positives - 0x284's free-running counter
///     being the local example.
///     2. Each probe ALTERNATES at least three times. A bit that happens to flip once has a
///     1-in-2^(N-1) chance of tracking the full A,B,A,B pattern; three alternations already
///     filters hard, and it is the only thing that separates a real signal from coincidence.
///     3. Confounder probes run in the same session. Without operating the RIGHT indicator you
///     cannot tell a LeftSignal bit from a shared AnySignalActive bit.
/// </summary>
public static class ProbeScripts
{
    private const int HoldSeconds = 3;
    private const int Alternations = 3;

    public static IReadOnlyList<ProbeScript> All =>
    [
        Gaps, Lighting, Body, DriverInput, Hvac, DrivetrainStatic, Charging
    ];

    /// <summary>
    ///     The 2026-08-31 gaps, and nothing else. Re-running a full script to recover five probes
    ///     wastes the scarce resource, which is time with the vehicle - everything else from that
    ///     session stands.
    ///     Covers: parking-brake (never actually operated - no control was found), driver-door (the
    ///     operator was seated in it), the four gear selections that did not engage, and the whole
    ///     HVAC group (that session captured ~25 frames/s against ~1000 elsewhere, so the bus was
    ///     near-idle and the run proves nothing).
    ///     Hazards are deliberately absent: blink detection now finds them in the data already
    ///     recorded, so that gap closed offline.
    /// </summary>
    /// <remarks>
    ///     Computed on access, not a static initializer. Static field initializers run in textual
    ///     order, and this one composes scripts declared below it - as an initializer it would bind
    ///     them before they exist and yield an empty script that still compiles cleanly.
    /// </remarks>
    public static ProbeScript Gaps => Build(
        "gaps",
        "Re-run of the probes that failed or were never performed on 2026-08-31.",
        "parked, WHEELS CHOCKED, ignition ON and staying in READY throughout",
        [
            DriverInput.Steps.Where(s => s.Label.StartsWith("parking-brake", StringComparison.Ordinal)),
            Body.Steps.Where(s => s.Label.StartsWith("driver-door", StringComparison.Ordinal)),
            DrivetrainStatic.Steps.Where(s => s.Label.StartsWith("gear-neutral", StringComparison.Ordinal)),
            DrivetrainStatic.Steps.Where(s => s.Label.StartsWith("gear-drive", StringComparison.Ordinal)),
            DrivetrainStatic.Steps.Where(s => s.Label.StartsWith("gear-b", StringComparison.Ordinal)),
            DrivetrainStatic.Steps.Where(s => s.Label.StartsWith("eco-mode", StringComparison.Ordinal)),
            Hvac.Steps.Where(s => s.Kind == ProbeStepKind.Action)
        ]);

    public static ProbeScript Lighting { get; } = Build(
        "lighting",
        "Exterior lighting: indicators, hazards, headlights, fog, reverse lamps.",
        "parked",
        [
            Toggle("LEFT turn signal", "left-signal"),
            Toggle("RIGHT turn signal", "right-signal"), // confounder for left-signal
            Toggle("HAZARD lights", "hazards"),
            Toggle("HEADLIGHTS (low beam)", "headlights-low"),
            Toggle("HIGH BEAM", "headlights-high"),
            Toggle("FOG lights (skip if not fitted)", "fog")
        ]);

    public static ProbeScript Body { get; } = Build(
        "body",
        "Doors, hood, hatch, central locking.",
        "parked",
        [
            // Seated in the car you cannot cycle your own door, which is why driver-door found
            // nothing on 2026-08-31 while the other three doors resolved cleanly.
            Toggle("DRIVER door", "driver-door",
                "STAND OUTSIDE the car. Open the DRIVER door and leave it open",
                "Close the DRIVER door"),
            Toggle("PASSENGER door: open, then close", "passenger-door"),
            Toggle("REAR LEFT door: open, then close", "rear-left-door"),
            Toggle("REAR RIGHT door: open, then close", "rear-right-door"),
            Toggle("HATCH/BOOT: open, then close", "hatch"),
            Toggle("HOOD: open, then close (skip if awkward)", "hood"),
            Toggle("LOCK the car, then UNLOCK", "central-lock")
        ]);

    public static ProbeScript DriverInput { get; } = Build(
        "driver-input",
        "Brake, parking brake, horn, wipers, steering.",
        "parked",
        [
            Toggle("BRAKE pedal", "brake",
                "Press the BRAKE pedal firmly and HOLD it",
                "RELEASE the brake pedal completely"),

            // The AZE0 Leaf has no parking-brake button or lever. It is a small foot pedal at
            // the far left of the footwell, released by a pull handle under the dash. Saying
            // only "parking brake" is what caused the 2026-08-31 mix-up.
            Toggle("PARKING brake", "parking-brake",
                "PARKING BRAKE: press the small pedal at the FAR LEFT of the footwell (left of "
                + "the brake pedal) until it clicks. Keep your feet OFF the main brake pedal",
                "RELEASE it with the pull handle under the dash labelled BRAKE RELEASE. Feet "
                + "still OFF the main brake pedal"),

            Toggle("HORN: press briefly, then release", "horn"),
            Toggle("WIPERS: intermittent on, then off", "wipers-int"),
            Toggle("WIPERS: fast on, then off", "wipers-fast"),
            Toggle("STEERING: full LEFT, then back to centre", "steering-left"),
            Toggle("STEERING: full RIGHT, then back to centre", "steering-right")
        ]);

    public static ProbeScript Hvac { get; } = Build(
        "hvac",
        "Climate control: A/C, fan, temperature, recirculation, defrost.",
        "parked",
        [
            Toggle("A/C: on, then off", "ac"),
            Toggle("FAN: maximum, then off", "fan-max"),
            Toggle("TEMPERATURE: maximum heat, then back to minimum", "temp-max"),
            Toggle("RECIRCULATION: on, then off", "recirc"),
            Toggle("FRONT DEFROST: on, then off", "defrost-front"),
            Toggle("REAR DEFROST: on, then off", "defrost-rear")
        ]);

    public static ProbeScript DrivetrainStatic { get; } = Build(
        "drivetrain-static",
        "Gear selector and ignition states. Vehicle must not move.",
        "parked, WHEELS CHOCKED, foot on brake",
        [
            // The Leaf shifter is a sprung joystick that springs back to centre; the GEAR stays
            // engaged, and PARK is a separate button. Most selections also require the brake
            // pedal held. On 2026-08-31 only gear-reverse resolved, almost certainly because
            // the other selections were never actually engaged.
            Toggle("REVERSE", "gear-reverse",
                "Foot ON the brake. Nudge the shifter to R and let it spring back - the car "
                + "stays in R. Confirm the dash shows R",
                "Press the P button. Confirm the dash shows P"),

            Toggle("NEUTRAL", "gear-neutral",
                "Foot ON the brake. Hold the shifter toward N for ~2 s until the dash shows N, "
                + "then let go. Confirm the dash shows N",
                "Press the P button. Confirm the dash shows P"),

            Toggle("DRIVE", "gear-drive",
                "Foot ON the brake. Nudge the shifter to D and let it spring back. Confirm the "
                + "dash shows D",
                "Press the P button. Confirm the dash shows P"),

            Toggle("B mode", "gear-b",
                "Foot ON the brake, car in D. Nudge the shifter to D a second time to enter B. "
                + "Confirm the dash shows B",
                "Press the P button. Confirm the dash shows P"),

            Toggle("ECO mode", "eco-mode",
                "Press the ECO button. Confirm the ECO indicator is lit",
                "Press the ECO button again. Confirm the ECO indicator is off")
        ]);

    public static ProbeScript Charging { get; } = Build(
        "charging",
        "Charge port and AC charging state.",
        "parked, charging cable available",
        [
            Toggle("PLUG IN the charge cable, then UNPLUG it", "charge-plug"),
            Toggle("START charging, then STOP it", "charge-start")
        ]);

    public static ProbeScript? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Expands one stimulus into the alternating sequence the scoring depends on:
    ///     ON/OFF repeated <see cref="Alternations" /> times, each state held for a capture window.
    ///     Supply <paramref name="onText" /> and <paramref name="offText" /> wherever "do the first
    ///     action / now reverse it" leaves any doubt about which control is meant. Ambiguity here is
    ///     not cosmetic: on 2026-08-31 a vaguely-worded parking-brake probe was performed on the
    ///     regular brake pedal in reverse phase, producing an entire session of misattributed data.
    /// </summary>
    private static IEnumerable<ProbeStep> Toggle(string what, string label, string? onText = null,
        string? offText = null)
    {
        for (var i = 1; i <= Alternations; i++)
        {
            yield return new ProbeStep(
                ProbeStepKind.Action,
                $"{onText ?? what + " - do the FIRST action"}  (repetition {i}/{Alternations})",
                $"{label}-on",
                HoldSeconds);

            yield return new ProbeStep(
                ProbeStepKind.Action,
                $"{offText ?? what + " - now REVERSE it"}  (repetition {i}/{Alternations})",
                $"{label}-off",
                HoldSeconds);
        }
    }

    /// <summary>
    ///     Wraps the probes in the baseline phases. The leading idle window builds the noise mask;
    ///     the trailing one detects drift and warm-up effects between the start and end of a session,
    ///     so a bit that merely wandered is not mistaken for one that responded.
    /// </summary>
    private static ProbeScript Build(
        string name,
        string description,
        string safeWhen,
        IEnumerable<IEnumerable<ProbeStep>> probes)
    {
        var steps = new List<ProbeStep>
        {
            new(ProbeStepKind.Idle, "Sit still. Touch nothing at all.", "idle-baseline", 30)
        };

        steps.AddRange(probes.SelectMany(p => p));
        steps.Add(new ProbeStep(ProbeStepKind.Idle, "Sit still again. Touch nothing.", "idle-rebaseline", 15));

        return new ProbeScript(name, description, safeWhen, steps);
    }
}
