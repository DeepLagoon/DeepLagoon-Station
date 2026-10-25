using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.WebUI;

public sealed class TguiSpriteCacheSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly TguiSpriteImages _images = default!;

    public override void Initialize()
    {
        base.Initialize();
        _prototypes.PrototypesReloaded += OnReload;
    }

    private void OnReload(PrototypesReloadedEventArgs args) => _images.Reset();

    public override void Shutdown()
    {
        _prototypes.PrototypesReloaded -= OnReload;
        _images.Reset();
        base.Shutdown();
    }
}
