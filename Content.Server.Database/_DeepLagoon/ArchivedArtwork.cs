using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

[Index(nameof(WorkId), nameof(RevisionHash), IsUnique = true)]
public sealed class ArchivedArtwork
{
    public Guid Id { get; set; }
    public Guid WorkId { get; set; }
    [MaxLength(64)] public string RevisionHash { get; set; } = "";
    public Guid Author { get; set; }
    [MaxLength(128)] public string AuthorName { get; set; } = "";
    [MaxLength(80)] public string Title { get; set; } = "";
    public string Document { get; set; } = "";
    public string Preview { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool Hidden { get; set; }
}
