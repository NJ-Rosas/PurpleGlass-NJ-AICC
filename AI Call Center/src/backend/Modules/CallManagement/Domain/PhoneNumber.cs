using System.Text.RegularExpressions;

namespace PurpleGlass.Modules.CallManagement.Domain;

public static partial class PhoneNumber
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A phone number is required.", nameof(value));
        }

        string candidate = value.Trim();
        if (!candidate.StartsWith('+'))
        {
            throw new ArgumentException("Phone numbers must include a leading + and country code.", nameof(value));
        }

        string normalized = "+" + string.Concat(candidate[1..].Where(char.IsDigit));
        if (!E164Pattern().IsMatch(normalized))
        {
            throw new ArgumentException("Phone numbers must use a valid E.164 representation.", nameof(value));
        }

        return normalized;
    }

    [GeneratedRegex("^\\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Pattern();
}
