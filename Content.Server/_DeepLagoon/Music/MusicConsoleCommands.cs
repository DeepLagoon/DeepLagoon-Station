using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Shared._DeepLagoon.Music;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.Localization;

namespace Content.Server._DeepLagoon.Music;

[AdminCommand(AdminFlags.Round)]
public sealed class MusicPlayCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    public string Command => "musicplay";
    public string Description => _loc.GetString("dl-music-cmd-play-desc");
    public string Help => _loc.GetString("dl-music-cmd-play-help");

    public MusicPlayCommand() => IoCManager.InjectDependencies(this);

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(_loc.GetString("dl-music-cmd-play-usage"));
            return;
        }

        if (!MusicRules.ValidTrackId(args[0], out var id))
        {
            shell.WriteError(_loc.GetString("dl-music-cmd-play-bad-id", ("id", args[0])));
            return;
        }

        var title = args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty;
        var music = _systems.GetEntitySystem<MusicSystem>();

        _ = PlayAsync(shell, music, id, title);
    }

    private async Task PlayAsync(IConsoleShell shell, MusicSystem music, string id, string title)
    {
        var ok = await music.PlayGlobalAsync(id, title, string.Empty);
        shell.WriteLine(ok
            ? _loc.GetString("dl-music-cmd-play-ok", ("id", id))
            : _loc.GetString("dl-music-cmd-play-fail", ("id", id)));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class MusicStopCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    public string Command => "musicstop";
    public string Description => _loc.GetString("dl-music-cmd-stop-desc");
    public string Help => "musicstop";

    public MusicStopCommand() => IoCManager.InjectDependencies(this);

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _systems.GetEntitySystem<MusicSystem>().Stop(MusicChannels.Global);
        shell.WriteLine(_loc.GetString("dl-music-cmd-stop-ok"));
    }
}

[AdminCommand(AdminFlags.Round)]
public sealed class MusicStatusCommand : IConsoleCommand
{
    [Dependency] private readonly IEntitySystemManager _systems = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    public string Command => "musicstatus";
    public string Description => _loc.GetString("dl-music-cmd-status-desc");
    public string Help => "musicstatus";

    public MusicStatusCommand() => IoCManager.InjectDependencies(this);

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var music = _systems.GetEntitySystem<MusicSystem>();
        var channel = MusicChannels.Global;
        var state = music.Find(channel);

        shell.WriteLine(_loc.GetString("dl-music-cmd-status-state",
            ("state", (state?.Status ?? MusicPlaybackStatus.Stopped).ToString().ToLowerInvariant())));
        shell.WriteLine(_loc.GetString("dl-music-cmd-status-listeners", ("count", music.ListenerCount)));
        shell.WriteLine(_loc.GetString("dl-music-cmd-status-gain", ("gain", (state?.Gain ?? 0f).ToString("0.##"))));

        var current = state?.Current;

        if (current == null)
        {
            shell.WriteLine(_loc.GetString("dl-music-cmd-status-nothing"));
            return;
        }

        var artist = string.IsNullOrEmpty(current.Artist)
            ? _loc.GetString("dl-music-artist-unknown")
            : current.Artist;

        shell.WriteLine(_loc.GetString("dl-music-cmd-status-playing",
            ("title", current.Title), ("artist", artist), ("id", current.Id),
            ("duration", ((int) current.Duration).ToString())));
    }
}
