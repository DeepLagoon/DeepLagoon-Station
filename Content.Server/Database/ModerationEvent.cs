using Content.Shared.Administration.Notes;
using Content.Shared.Database;
using Robust.Shared.Network;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Server.Database;

/// <summary>A committed moderation action. Deliberately excludes IP addresses and hardware IDs.</summary>
public sealed record ModerationEvent(
    NoteType Type, int Id, string Action,
    [property: JsonConverter(typeof(ModerationUserIdConverter))] NetUserId? Player, Guid? Actor,
    string Message, NoteSeverity? Severity, bool Secret, DateTimeOffset? Expires,
    int? Round, string[]? Roles, DateTimeOffset Time, string? PreviousMessage = null,
    bool PreviousSecret = true);

public sealed class ModerationUserIdConverter : JsonConverter<NetUserId>
{
    public override NetUserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, NetUserId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UserId);
}
