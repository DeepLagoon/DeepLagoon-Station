using System.IO;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server.Mapping;

/// <summary>
///     Handles autosaving maps.
/// </summary>
public sealed partial class MappingSystem : EntitySystem
{
    [Dependency] private IConsoleHost _conHost = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    private SharedMapSystem _map => Robust.Shared.IoC.IoCManager.Resolve<Robust.Shared.GameObjects.IEntityManager>().System<SharedMapSystem>();
    [Dependency] private IResourceManager _resMan = default!;
    [Dependency] private MapLoaderSystem _loader = default!;

    // Not a comp because I don't want to deal with this getting saved onto maps ever
    /// <summary>
    ///     map id -> next autosave timespan & original filename.
    /// </summary>
    /// <returns></returns>
    private Dictionary<EntityUid, (TimeSpan next, string fileName)> _currentlyAutosaving = new();

    private bool _autosaveEnabled;
    private Task<AutosaveResult>? _pendingSave;
    private EntityUid _savingEntity;
    private ResPath _savingPath;

    private readonly record struct AutosaveResult(ResPath Path, TimeSpan Total, TimeSpan Capture, TimeSpan Queue,
        TimeSpan Yaml, TimeSpan Disk, long Bytes);

    public override void Initialize()
    {
        base.Initialize();

        _conHost.RegisterCommand("toggleautosave",
            "Toggles autosaving for a map.",
            "autosave <map> <path if enabling>",
            ToggleAutosaveCommand);

        Subs.CVar(_cfg, CCVars.AutosaveEnabled, SetAutosaveEnabled, true);
    }

    private void SetAutosaveEnabled(bool b)
    {
        if (!b)
            _currentlyAutosaving.Clear();
        _autosaveEnabled = b;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pendingSave != null)
        {
            if (!_pendingSave.IsCompleted)
                return;

            CompleteAutosave();
        }

        if (!_autosaveEnabled || _currentlyAutosaving.Count == 0)
            return;

        EntityUid? next = null;
        var earliest = _timing.RealTime;
        foreach (var (uid, (time, _)) in _currentlyAutosaving)
        {
            if (earliest <= time)
                continue;

            if (LifeStage(uid) >= EntityLifeStage.MapInitialized)
            {
                Log.Warning($"Can't autosave entity {uid}; it doesn't exist, or is initialized. Removing from autosave.");
                _currentlyAutosaving.Remove(uid);
                continue;
            }

            earliest = time;
            next = uid;
        }

        if (next is not { } entity)
            return;

        var name = _currentlyAutosaving[entity].fileName;
        _currentlyAutosaving[entity] = (CalculateNextTime(), name);
        StartAutosave(entity, name);
    }

    private void StartAutosave(EntityUid entity, string name)
    {
        var saveDirectory = Path.Combine(_cfg.GetCVar(CCVars.AutosaveDirectory), name);
        var directory = ResPath.FromRelativeSystemPath(saveDirectory, Path.DirectorySeparatorChar).ToRootedPath();
        var path = directory / $"{DateTime.Now:yyyy-M-dd_HH.mm.ss}-AUTO.yml";
        var category = HasComp<MapComponent>(entity) ? FileCategory.Map : FileCategory.Grid;
        var start = Stopwatch.GetTimestamp();
        try
        {
            var (node, actualCategory) = _loader.SerializeEntitiesRecursive([entity],
                SerializationOptions.Default with { Category = category });
            if (actualCategory != category)
                throw new InvalidDataException($"Expected to save a {category}, got {actualCategory}.");

            var captured = Stopwatch.GetTimestamp();
            var storage = _resMan.UserData;
            // The in-memory provider is used by tests and has no thread synchronization.
            if (storage is VirtualWritableDirProvider)
            {
                ReportAutosave(entity, WriteAutosave(storage, path, node, start, captured));
                return;
            }

            _savingEntity = entity;
            _savingPath = path;
            // Only the detached data node reaches the worker. ECS capture and serialization hooks run on the game thread.
            _pendingSave = Task.Run(() => WriteAutosave(storage, path, node, start, captured));
        }
        catch (Exception e)
        {
            Log.Error($"Autosave failed for {entity} to {path}: {e}");
        }
    }

    private static AutosaveResult WriteAutosave(IWritableDirProvider storage, ResPath path, MappingDataNode node,
        long start, long captured)
    {
        var yamlStart = Stopwatch.GetTimestamp();
        using var yaml = new MemoryStream();
        using (var writer = new StreamWriter(yaml, EncodingHelpers.UTF8, leaveOpen: true))
            node.Write(writer);
        var diskStart = Stopwatch.GetTimestamp();

        var directory = path.Directory;
        storage.CreateDir(directory);
        var destination = path;
        for (var suffix = 1; storage.Exists(destination); suffix++)
            destination = directory / $"{path.FilenameWithoutExtension}-{suffix}.yml";

        var temporary = directory / $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = storage.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                yaml.WriteTo(file);
            storage.Rename(temporary, destination);
        }
        catch
        {
            storage.Delete(temporary);
            throw;
        }

        var end = Stopwatch.GetTimestamp();
        return new AutosaveResult(destination, Stopwatch.GetElapsedTime(start, end),
            Stopwatch.GetElapsedTime(start, captured), Stopwatch.GetElapsedTime(captured, yamlStart),
            Stopwatch.GetElapsedTime(yamlStart, diskStart),
            Stopwatch.GetElapsedTime(diskStart, end), yaml.Length);
    }

    private void CompleteAutosave()
    {
        var pending = _pendingSave!;
        _pendingSave = null;
        try
        {
            ReportAutosave(_savingEntity, pending.GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            Log.Error($"Autosave failed for {_savingEntity} to {_savingPath}: {e}");
        }
    }

    private void ReportAutosave(EntityUid entity, AutosaveResult result)
    {
        var budget = _timing.TickPeriod;
        if (result.Total < budget)
            return;

        Log.Warning(FormattableString.Invariant(
            $"Slow map autosave: entity={entity} file={result.Path} bytes={result.Bytes} elapsed_ms={result.Total.TotalMilliseconds:F2} ecs_ms={result.Capture.TotalMilliseconds:F2} yaml_ms={result.Yaml.TotalMilliseconds:F2} disk_ms={result.Disk.TotalMilliseconds:F2} worker_queue_ms={result.Queue.TotalMilliseconds:F2} tick_budget_ms={budget.TotalMilliseconds:F2}"));
    }

    public override void Shutdown()
    {
        if (_pendingSave != null)
            CompleteAutosave();

        base.Shutdown();
    }

    private TimeSpan CalculateNextTime()
    {
        return _timing.RealTime + TimeSpan.FromSeconds(_cfg.GetCVar(CCVars.AutosaveInterval));
    }

    private double ReadableTimeLeft(EntityUid uid)
    {
        return Math.Round(_currentlyAutosaving[uid].next.TotalSeconds - _timing.RealTime.TotalSeconds);
    }

    #region Public API

    public void ToggleAutosave(MapId map, string? path = null)
    {
        if (_map.TryGetMap(map, out var uid))
            ToggleAutosave(uid.Value, path);
    }

    public void ToggleAutosave(EntityUid uid, string? path=null)
    {
        if (!_autosaveEnabled)
            return;

        if (_currentlyAutosaving.Remove(uid) || path == null)
            return;

        if (LifeStage(uid) >= EntityLifeStage.MapInitialized)
        {
            Log.Error("Tried to enable autosaving on a post map-init entity.");
            return;
        }

        if (!HasComp<MapComponent>(uid) && !HasComp<MapGridComponent>(uid))
        {
            Log.Error($"{ToPrettyString(uid)} is neither a grid or map");
            return;
        }

        _currentlyAutosaving[uid] = (CalculateNextTime(), Path.GetFileName(path));
        Log.Info($"Started autosaving map {path} ({uid}). Next save in {ReadableTimeLeft(uid)} seconds.");
    }

    #endregion

    #region Commands

    [AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
    private void ToggleAutosaveCommand(IConsoleShell shell, string argstr, string[] args)
    {
        if (args.Length != 1 && args.Length != 2)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!int.TryParse(args[0], out var intMapId))
        {
            shell.WriteError(Loc.GetString("cmd-mapping-failure-integer", ("arg", args[0])));
            return;
        }

        string? path = null;
        if (args.Length == 2)
        {
            path = args[1];
        }

        var mapId = new MapId(intMapId);
        ToggleAutosave(mapId, path);
    }

    #endregion
}
