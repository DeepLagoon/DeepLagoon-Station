using System;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.Administration;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.Localization;

namespace Content.Server._DeepLagoon.Music;

[AdminCommand(AdminFlags.Round)]
public sealed class MusicDiagnoseCommand : IConsoleCommand
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly MusicApi _api = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    public string Command => "musicdiag";
    public string Description => _loc.GetString("dl-music-cmd-diag-desc");
    public string Help => _loc.GetString("dl-music-cmd-diag-help");

    public MusicDiagnoseCommand() => IoCManager.InjectDependencies(this);

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var enabled = _cfg.GetCVar(MusicCVars.Enabled);
        var token = _cfg.GetCVar(MusicCVars.Token);

        shell.WriteLine($"dl_music.enabled = {enabled}");
        shell.WriteLine($"dl_music.api_base = {_cfg.GetCVar(MusicCVars.ApiBase)}");

        if (string.IsNullOrWhiteSpace(token))
        {
            shell.WriteLine(_loc.GetString("dl-music-diag-no-token"));
            return;
        }

        var trimmed = MusicRules.SanitizeToken(token);
        var prefix = trimmed.Length >= 3 ? trimmed[..3] : trimmed;
        shell.WriteLine(_loc.GetString("dl-music-diag-token",
            ("length", trimmed.Length), ("prefix", prefix)));

        if (trimmed.StartsWith("OAuth", StringComparison.OrdinalIgnoreCase))
            shell.WriteLine(_loc.GetString("dl-music-diag-oauth-prefix"));

        if (trimmed.Length != token.Trim().Length)
            shell.WriteLine(_loc.GetString("dl-music-diag-trimmed"));

        if (!enabled)
        {
            shell.WriteLine(_loc.GetString("dl-music-diag-disabled"));
            return;
        }

        var probe = args.Length > 0 ? string.Join(' ', args) : "music";
        shell.WriteLine(_loc.GetString("dl-music-diag-probing", ("query", probe)));
        _ = ProbeAsync(shell, probe);
    }

    private async Task ProbeAsync(IConsoleShell shell, string query)
    {
        var (tracks, status) = await IoCManager.Resolve<IEntitySystemManager>()
            .GetEntitySystem<MusicSystem>().SearchAsync(query);

        if (status != MusicSearchStatus.Ok)
        {
            shell.WriteLine(_loc.GetString("dl-music-diag-failed", ("status", status)));
            shell.WriteLine(_loc.GetString(status switch
            {
                MusicSearchStatus.NoToken => "dl-music-diag-hint-token",
                MusicSearchStatus.Disabled => "dl-music-diag-hint-disabled",
                MusicSearchStatus.RateLimited => "dl-music-diag-hint-rate",
                MusicSearchStatus.BadQuery => "dl-music-diag-hint-query",
                MusicSearchStatus.InvalidToken => "dl-music-diag-hint-invalid",
                _ => "dl-music-diag-hint-error",
            }));
            return;
        }

        shell.WriteLine(_loc.GetString("dl-music-diag-found", ("count", tracks.Length)));
        for (var i = 0; i < Math.Min(tracks.Length, 5); i++)
            shell.WriteLine($"  {i + 1}. {tracks[i].Title} - {tracks[i].Artist} ({tracks[i].Id})");

        if (tracks.Length == 0)
            shell.WriteLine(_loc.GetString("dl-music-diag-empty"));
    }
}
