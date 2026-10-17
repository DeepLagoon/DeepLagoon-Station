using System.ComponentModel.DataAnnotations.Schema;

namespace Content.Server.Database;

public partial class Preference
{
    [Column("jukebox_library_json")]
    public string JukeboxLibraryJson { get; set; } = "{}";
}
