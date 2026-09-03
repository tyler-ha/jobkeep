using Jobkeep.SharedKernel;
namespace Jobkeep.Modules.Skills.Domain;

// PHASE 14 — another name for a skill that already has a row.
//
// The problem this exists for, measured on the real dev database rather than
// imagined: `Agile` and `Agile Methodologies` were two rows, and so were
// `Docker` and `containers`. Phase 7's natural key made `C#` and `c#` one row,
// which is the whole of what a `lower()` index can do — it cannot know that two
// DIFFERENT strings name one thing. Everything downstream then counts them
// separately: the demand chart splits one skill's total in two, and the skill
// gap reports a skill as missing that the CV names under its other name.
// `docs/phases/phase-5-ats-check.md` found exactly that against a real CV.
//
// WHY A TABLE AND NOT A DICTIONARY IN CODE
// ----------------------------------------
// A static map would be a deploy to edit and invisible in `psql`. This is
// vocabulary — it grows every time an ad uses a word we have not seen — so it
// belongs where the vocabulary is. The seed file populates it; nothing stops a
// row being added by hand later, and that is the point.
//
// THE INVARIANT, WHICH IS THE ONLY SUBTLE PART
// --------------------------------------------
// An alias's natural key must never equal a SKILL's natural key. If both exist,
// one name points at two rows and which one wins is a matter of query order.
// Two things keep that from mattering:
//
//   * SkillSeeder refuses to insert such an alias, and says so rather than
//     throwing — reference data with one bad row should not stop the app.
//   * SkillCatalog looks in `skills` FIRST and `skill_aliases` only on a miss,
//     so if the invariant is ever broken by hand the real skill row wins and
//     the alias is inert. That ordering is deliberate, not incidental.
//
// The unique index below enforces the other half: one alias cannot point at two
// skills.
public class SkillAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // The skill this name resolves to. No navigation property, matching Skill
    // itself — see the note there about 13.3b. The foreign key IS declared in
    // the configuration, and it is allowed to be: both tables live in the
    // `skills` schema and belong to this module, so it crosses no boundary. The
    // six keys Phase 13.3b dropped were the ones that crossed schemas.
    public Guid SkillId { get; set; }

    // The alternative spelling, kept as written so it can be read back and
    // judged. "Agile Methodologies", not "agile methodologies".
    public string Alias { get; set; } = string.Empty;

    // Phase 7's mechanism, reused verbatim rather than reinvented: a STORED
    // generated column carrying the unique index, so Postgres computes it and no
    // C# writer can forget to. NaturalKey.Of is the C# half for this column
    // exactly as it is for Skill.NameNormalized, and the same warning applies —
    // if the two ever disagree, a lookup misses a row the index then refuses to
    // insert.
    public string AliasNormalized { get; private set; } = string.Empty;
}
