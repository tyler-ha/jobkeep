using System.Reflection;

namespace Jobkeep.Tests.Architecture;

/// <summary>
/// The one rule Phase 13 rests on: <b>a module never references another module.</b>
///
/// Everything else in the phase — the schema split, the contracts, the dispatcher —
/// is in service of making a module a thing you can lift out and deploy on its own.
/// A single stray <c>ProjectReference</c> undoes that quietly, at the moment someone
/// needs one type from next door, and nothing else in the build would say so.
///
/// Why reflection over assembly references rather than an architecture-test package:
/// the rule is literally about assembly references, and this is the layer that decides
/// whether a module can compile alone. A package like NetArchTest works on namespaces
/// and types, which is the right tool for the layering rules *inside* a module
/// (Domain/ must not touch EF) — those arrive in 13.6, and can bring it with them.
/// Testing the assembly graph needs no dependency at all.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly string[] Modules =
    [
        "Jobkeep.Modules.Applications",
        "Jobkeep.Modules.Analytics",
        "Jobkeep.Modules.Ai",
        "Jobkeep.Modules.Ats",
        "Jobkeep.Modules.Documents",
    ];

    // The exceptions, named one by one rather than allowed as a category.
    //
    // Documents -> Applications is architecture.md decision 15: CommitImport calls
    // CreateApplicationHandler and AddRequirementToPostingHandler directly, so that
    // Applications' own rules run on a committed draft instead of being re-implemented
    // in Documents. That decision accepted the compile-time coupling openly, and Phase
    // 13.2 replaces it with a contract call.
    //
    // When 13.2 lands, delete this entry and the ProjectReference together. If it is
    // ever tempting to ADD an entry here, that is the boundary telling you the use case
    // is in the wrong module.
    private static readonly HashSet<(string From, string To)> AllowedEdges =
    [
        ("Jobkeep.Modules.Documents", "Jobkeep.Modules.Applications"),
    ];

    [Fact]
    public void No_module_references_another_module()
    {
        var violations = new List<string>();

        foreach (var module in Modules)
        {
            foreach (var referenced in ReferencedJobkeepAssemblies(module))
            {
                if (!referenced.StartsWith("Jobkeep.Modules.")) continue;
                if (referenced == module) continue;
                if (AllowedEdges.Contains((module, referenced))) continue;

                violations.Add($"{module} -> {referenced}");
            }
        }

        Assert.True(violations.Count == 0,
            "A module references another module. Cross a boundary through Jobkeep.Contracts, "
            + "not through a project reference:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void No_module_references_the_composition_root()
    {
        // Api wires everything together and is the only project that knows about HTTP.
        // A module reaching back into it inverts the dependency that makes the module
        // deployable on its own — and it is an easy mistake, because Api is where the
        // IChatClient registration and the endpoint helpers live.
        var violations = Modules
            .SelectMany(m => ReferencedJobkeepAssemblies(m).Select(r => (Module: m, Ref: r)))
            .Where(x => x.Ref == "Jobkeep.Api")
            .Select(x => $"{x.Module} -> {x.Ref}")
            .ToList();

        Assert.True(violations.Count == 0,
            "A module references the composition root:\n  " + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("Jobkeep.SharedKernel")]
    [InlineData("Jobkeep.Contracts")]
    public void Foundation_projects_depend_on_nothing_of_ours(string assemblyName)
    {
        // SharedKernel holds primitives (SliceResult, NaturalKey, IAuditable,
        // ModelOptions); Contracts holds the interfaces and DTOs modules use to talk to
        // each other. Both are referenced by everything, which is exactly why they must
        // reference nothing — a dependency here is a dependency for every module at
        // once, and it is how a "Common" assembly becomes the thing that cannot be
        // split. Contracts additionally becomes the wire schema when a module is
        // extracted, so anything in it must survive a network hop.
        var ours = ReferencedJobkeepAssemblies(assemblyName).ToList();

        Assert.True(ours.Count == 0,
            $"{assemblyName} must not depend on any other Jobkeep assembly, but references: "
            + string.Join(", ", ours));
    }


    [Fact]
    public void The_recorded_exception_is_actually_visible_to_this_test()
    {
        // A canary, and it earns its place twice.
        //
        // First: the C# compiler emits an assembly reference only when a type from it
        // is actually used. So a ProjectReference that nothing consumes yet is INVISIBLE
        // here — this test suite proves a module does not *use* another module, which is
        // the thing that matters, but it is not the same claim as "has no reference".
        // Knowing which claim is being made is the point of writing it down.
        //
        // Second: it fails the moment 13.2 replaces the CommitImport handler calls with
        // a contract, which is the reminder to delete the allowlist entry and the
        // ProjectReference rather than leaving a stale exception standing.
        var documentsReferences = ReferencedJobkeepAssemblies("Jobkeep.Modules.Documents");

        Assert.True(documentsReferences.Contains("Jobkeep.Modules.Applications"),
            "Documents no longer uses Applications. If 13.2 has landed, remove the entry "
            + "from AllowedEdges, drop the ProjectReference from "
            + "Jobkeep.Modules.Documents.csproj, and delete this test.");
    }

    private static IEnumerable<string> ReferencedJobkeepAssemblies(string assemblyName)
        // Load by name rather than off a type: naming a type would mean this test file
        // holds a reference to every module, which is the thing it is here to forbid.
        => Assembly.Load(new AssemblyName(assemblyName))
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Jobkeep."));
}
