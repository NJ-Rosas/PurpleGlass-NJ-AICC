namespace PurpleGlass.Modules.Conversation.Application;

public static class VoiceResponsePolicy
{
    public static string Constrain(string response, int maximumCharacters)
    {
        if (maximumCharacters is < 20 or > 8_000)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        string normalized = string.Join(' ', response.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return "I'm sorry, I'm having trouble responding right now.";
        if (normalized.Length <= maximumCharacters) return normalized;

        string candidate = normalized[..maximumCharacters].TrimEnd();
        int boundary = candidate.LastIndexOfAny(['.', '!', '?']);
        if (boundary >= maximumCharacters / 2) return candidate[..(boundary + 1)];
        boundary = candidate.LastIndexOf(' ');
        if (boundary >= maximumCharacters / 2) candidate = candidate[..boundary];
        string stem = candidate.TrimEnd(' ', ',', ';', ':', '-');
        if (stem.Length >= maximumCharacters)
            stem = stem[..(maximumCharacters - 1)].TrimEnd(' ', ',', ';', ':', '-');
        return $"{stem}…";
    }
}
