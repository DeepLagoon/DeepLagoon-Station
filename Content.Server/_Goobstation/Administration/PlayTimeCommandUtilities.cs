using System.Globalization;
using System.Text.RegularExpressions;

namespace Content.Server._Goobstation.Administration;

public static class PlayTimeCommandUtilities
{
    /// <summary>
    /// Accepts minutes or durations such as 1d 2h 30m, including Russian units
    /// and either decimal separator, independently of the server culture.
    /// </summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim().ToLowerInvariant().Replace(',', '.');
        if (!Regex.IsMatch(text, @"^\d+(?:\.\d+)?$") &&
            !Regex.IsMatch(text, @"^(?:\d+(?:\.\d+)?\s*[dhmдчм]\s*)+$"))
            return false;

        double minutes = 0;
        foreach (Match part in Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*([dhmдчм]?)"))
        {
            if (!double.TryParse(part.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value))
                return false;

            minutes += part.Groups[2].Value switch
            {
                "d" or "д" => value * 1440,
                "h" or "ч" => value * 60,
                _ => value
            };
        }

        if (!double.IsFinite(minutes) || minutes >= TimeSpan.MaxValue.TotalMinutes)
            return false;

        time = TimeSpan.FromMinutes(minutes);
        return true;
    }
}
