namespace NorthPole.Hq;

public sealed class HqOptions
{
    /// <summary>Where wishes, drawings, Santa's voice files and the QR codes are kept.</summary>
    public string DataDirectory { get; set; } = "data";

    public List<ChildOptions> Family { get; set; } = [];
    public SantaOptions Santa { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();
    public QrOptions Qr { get; set; } = new();
    public ElvesOptions Elves { get; set; } = new();
    public HqMqttOptions Mqtt { get; set; } = new();

    /// <summary>How many wishes each child may send per day.</summary>
    public int WishesPerDay { get; set; } = 1;
}

public sealed class ChildOptions
{
    /// <summary>Stable id used in files and URLs, e.g. "limelight-mole".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Age { get; set; }

    /// <summary>Anything Santa should know: "loves dinosaurs", "just learned to ride a bike".</summary>
    public string? AboutMe { get; set; }
}

public enum BrainMode { Claude, Offline }

public sealed class SantaOptions
{
    public BrainMode Brain { get; set; } = BrainMode.Claude;

    /// <summary>Sonnet 5.5 reads handwriting and drawings well and costs well under a cent per wish.</summary>
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Leave empty and set the ANTHROPIC_API_KEY environment variable instead (recommended).</summary>
    public string? ApiKey { get; set; }

    /// <summary>How many earlier wishes Santa sees, so he can say "how is that teddy from last week?"</summary>
    public int RememberWishes { get; set; } = 5;

    /// <summary>The language Santa writes and speaks in. Pick a matching Piper voice.</summary>
    public string Language { get; set; } = "British English";

    /// <summary>Your family's own Christmas world: elf names, pets, traditions.</summary>
    public string FamilyLore { get; set; } = "";
}

public enum VoiceMode { Piper, None }

public sealed class VoiceOptions
{
    public VoiceMode Mode { get; set; } = VoiceMode.Piper;

    /// <summary>Piper's HTTP server (python3 -m piper.http_server ...).</summary>
    public string PiperUrl { get; set; } = "http://localhost:5000";

    /// <summary>Optional: a voice name if the Piper server has several loaded.</summary>
    public string? PiperVoice { get; set; }

    /// <summary>&gt; 1 speaks slower. Santa is never in a hurry.</summary>
    public double LengthScale { get; set; } = 1.15;

    /// <summary>Play Santa out loud at the North Pole station (the Pi 5's speaker).</summary>
    public bool PlayOnStation { get; set; } = true;

    /// <summary>Command used to play a WAV file; the file path is appended.</summary>
    public string PlayCommand { get; set; } = "aplay -q";

    /// <summary>Optional sound played when the train arrives (sleigh bells, a whistle). Relative to the data directory.</summary>
    public string? ArrivalSound { get; set; } = "sounds/arrival.wav";
}

public sealed class QrOptions
{
    /// <summary>
    /// The address phones use to reach this Pi. Keep it short so the QR code stays small enough
    /// to build from LEGO tiles on one 32×32 baseplate (e.g. http://np.local with the Pi's hostname set to "np").
    /// </summary>
    public string BaseUrl { get; set; } = "http://np.local";

    /// <summary>Baseplate size in studs for the LEGO mosaic plan.</summary>
    public int BaseplateStuds { get; set; } = 32;
}

public sealed class ElvesOptions
{
    /// <summary>PIN for the parents' dashboard. Change it, the kids will try 1225 first.</summary>
    public string Pin { get; set; } = "1225";
}

public sealed class HqMqttOptions
{
    public bool EmbeddedBroker { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
}
