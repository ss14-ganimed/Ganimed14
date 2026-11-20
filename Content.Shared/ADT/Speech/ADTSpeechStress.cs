using System.Text.RegularExpressions;

namespace Content.Shared.ADT.Speech;

/// <summary>
/// Strips leftover "+" stress markers from chat text. The markers are still present in some accent/replacement
/// dictionaries (they were written for TTS), the TTS system itself has been removed.
/// </summary>
public static class ADTSpeechStress
{
    public const char Marker = '+';

    private static readonly Regex MarkerRegex = new(
        @"\+(?!\s*[0-9])|(?<![0-9]\s*)\+",
        RegexOptions.Compiled);

    public static string Strip(string message)
    {
        return message.IndexOf(Marker) < 0
            ? message
            : MarkerRegex.Replace(message, string.Empty);
    }
}
