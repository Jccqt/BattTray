namespace BattTray.Devices;

/// <summary>
/// One property a provider read, kept untransformed alongside the value it was decoded
/// into. The pair is the point: a reading that disagrees with the vendor app is either a
/// decoding bug (bytes fine, value wrong) or the device lying (both agree), and only the
/// raw form tells those apart.
/// </summary>
/// <param name="Name">Human label, e.g. "battery level".</param>
/// <param name="Key">Where the value came from, precisely enough to look up in the SDK.</param>
/// <param name="Raw">Bytes as reported, hex, with the type the node claimed.</param>
/// <param name="Decoded">What this app made of those bytes, or null if it ignored them.</param>
internal sealed record DiagnosticProperty(string Name, string Key, string Raw, string? Decoded);

/// <summary>A single hardware node a provider inspected, with the evidence it read from it.</summary>
internal sealed record DiagnosticNode(
    Transport Transport,
    string Title,
    string InstanceId,
    IReadOnlyList<DiagnosticProperty> Properties);
