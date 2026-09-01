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
        // Phase 13.2 promoted Skills a step early: ISkillCatalog needed an owner
        // once four modules were find-or-creating against the shared table.
        "Jobkeep.Modules.Skills",
    ];

    // Phase 13.2. The modules that may still take AppDbContext, shrinking to empty
    // as the step lands module by module. Each entry is a module whose handlers
    // have not yet moved onto their own I<X>DbContext.
    //
    // This list is the work item, not a policy. When it is empty, delete it and the
    // conditional in No_module_takes_the_shared_context along with it.
    private static readonly HashSet<string> ModulesStillOnAppDbContext =
    [
        "Jobkeep.Modules.Applications",
        "Jobkeep.Modules.Ats",
    ];

    // The exceptions, named one by one rather than allowed as a category.
    //
    // It is EMPTY, and that is the state Phase 13 is trying to reach and hold. It held
    // exactly one entry — Documents -> Applications, architecture.md decision 15, where
    // CommitImport called CreateApplicationHandler and AddRequirementToPostingHandler
    // directly so that Applications' own rules ran on a committed draft instead of
    // being re-implemented in Documents. 13.2c replaced that with
    // IApplicationContract.CommitPostingAsync and the entry went with it.
    //
    // The list is kept rather than deleted because the test below reads better with a
    // named exception mechanism than with a special case, and because an empty
    // allowlist is a stronger statement than no allowlist. If it is ever tempting to
    // ADD an entry, that is the boundary telling you the use case is in the wrong
    // module.
    private static readonly HashSet<(string From, string To)> AllowedEdges = [];

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
    public void No_module_takes_the_shared_context()
    {
        // The reference rule above is about ASSEMBLIES; this one is about the type
        // that made the assemblies necessary. Every module still references
        // Jobkeep.Infrastructure.Data — it holds the entities until 13.3 — so any
        // of them could name AppDbContext and reach all thirteen tables, and the
        // boundary test would pass while the boundary did not exist.
        //
        // Phase 13.2 gives each module an I<X>DbContext exposing only its own
        // DbSets. This is what makes that stick: a handler cannot quietly take the
        // shared context back, because the missing property is what stops it
        // naming another module's table.
        //
        // Constructor parameters rather than fields, because DI is how a handler
        // gets one. A module that new'd up an AppDbContext would need a connection
        // string it has no way to reach.
        var violations = new List<string>();

        foreach (var module in Modules)
        {
            if (ModulesStillOnAppDbContext.Contains(module)) continue;

            var assembly = Assembly.Load(new AssemblyName(module));

            foreach (var type in assembly.GetTypes())
                foreach (var ctor in type.GetConstructors())
                    foreach (var parameter in ctor.GetParameters())
                        if (parameter.ParameterType.Name == "AppDbContext")
                            violations.Add($"{module}: {type.Name}({parameter.Name})");
        }

        Assert.True(violations.Count == 0,
            "A module takes AppDbContext. Depend on that module's I<X>DbContext instead, "
            + "and reach another module through Jobkeep.Contracts:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void The_shared_context_allowlist_still_names_real_work()
    {
        // The canary for the list above, and the same idea as the one below it: a
        // stale exception is worse than no exception, because it reads as a rule.
        // If a module has been converted but its name is still here, this fails and
        // says so — which is the reminder to delete the entry rather than leave a
        // permission standing that nothing needs.
        var converted = ModulesStillOnAppDbContext
            .Where(module => !Assembly.Load(new AssemblyName(module))
                .GetTypes()
                .SelectMany(type => type.GetConstructors())
                .SelectMany(ctor => ctor.GetParameters())
                .Any(parameter => parameter.ParameterType.Name == "AppDbContext"))
            .ToList();

        Assert.True(converted.Count == 0,
            "These modules no longer take AppDbContext, so remove them from "
            + "ModulesStillOnAppDbContext:\n  " + string.Join("\n  ", converted));
    }

    // DELETED IN 13.2c: The_recorded_exception_is_actually_visible_to_this_test.
    //
    // It asserted that Documents DID reference Applications, and it was written to fail
    // at exactly the moment that stopped being true — which it did, on schedule. Its
    // second job is worth keeping even though the test is gone: the C# compiler emits an
    // assembly reference only when a type from it is actually used, so a ProjectReference
    // nothing consumes is INVISIBLE to these tests. What this file proves is that a
    // module does not *use* another module. That is the thing that matters, and it is
    // not the same claim as "has no reference in its csproj".

    private static IEnumerable<string> ReferencedJobkeepAssemblies(string assemblyName)
        // Load by name rather than off a type: naming a type would mean this test file
        // holds a reference to every module, which is the thing it is here to forbid.
        => Assembly.Load(new AssemblyName(assemblyName))
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Jobkeep."));
}
