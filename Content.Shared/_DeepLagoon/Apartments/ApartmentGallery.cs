using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Apartments;

[Serializable, NetSerializable]
public sealed class ApartmentPainting
{
    public string Id = "", Title = "", Preview = "";
    public bool Framed;
}

[Serializable, NetSerializable]
public sealed class ApartmentGalleryState(Guid token, List<ApartmentPainting> paintings, int page, int total, string error = "") : EntityEventArgs
{
    public const int PageSize = 12;
    public readonly Guid Token = token;
    public readonly List<ApartmentPainting> Paintings = paintings;
    public readonly int Page = page, Total = total;
    public readonly string Error = error;
}
