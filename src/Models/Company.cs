using System.Text.Json.Serialization;

namespace Jobkeep.Models;

// An employer. Kept as its own row (unique Name) so multiple postings and
// applications can share one company instead of duplicating it — that's what
// enables company-level rollups like "3 roles at Canva".
public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;   // unique
    public string? Website { get; set; }
    public string? Industry { get; set; }
    public string? HqLocation { get; set; }

    // Back-reference — ignored in REST JSON to avoid cycles / [null] noise.
    [JsonIgnore] public List<JobPosting> Postings { get; set; } = new();
}
