using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Content.Server._DeepLagoon.DiscordLink;

public static class DiscordApiTokens
{
    public static string Recovery(string candidate, string root, IEnumerable<string> scoped)
    {
        if (candidate.Length < 32 || candidate == root || scoped.Contains(candidate)) return "";
        var labels = new[] { "identity", "moderation", "coin", "supporter", "whitelist" }
            .Select(scope => "lagoon-api-" + scope + "-v1").Append("lagoon-launcher-enrollment-v1");
        return labels.Any(label => candidate == Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(root), Encoding.UTF8.GetBytes(label))).ToLowerInvariant()) ? "" : candidate;
    }
}
