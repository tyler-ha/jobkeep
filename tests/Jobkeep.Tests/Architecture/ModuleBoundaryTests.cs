using System.Reflection;
using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Documents;
using Jobkeep.SharedKernel;
using Jobkeep.Tests.Infrastructure;

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
        "Jobkeep.Modules.Match",
        "Jobkeep.Modules.Documents",
        // Phase 13.2 promoted Skills a step early: ISkillCatalog needed an owner
        // once four modules were find-or-creating against the shared table.
        "Jobkeep.Modules.Skills",
        // Phase 11.1a. Added to this list on the day the project was created, which
        // is the only time it is free: a module that is not in this array is a
        // module the boundary rule does not cover, and nothing else would say so.
        "Jobkeep.Modules.Identity",
    ];

    // DELETED IN 13.2e: ModulesStillOnAppDbContext, and the conditional in
    // No_module_takes_the_shared_context that read it.
    //
    // It listed the modules that had not yet moved onto their own I<X>DbContext,
    // and its own comment said to delete it when it emptied. Match was the last
    // entry. It is worth being clear about what kind of thing it was: not a
    // policy with an exception, but a WORK ITEM written in the place the work
    // would be checked — which is why it came with a canary
    // (The_shared_context_allowlist_still_names_real_work, deleted with it) that
    // failed if an entry outlived the conversion. Both are gone because the list
    // reached zero, which is the outcome they were built to make visible.

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
    public void Jobkeep_Persistence_references_only_SharedKernel()
    {
        // Phase 13.3a. Jobkeep.Persistence holds the two model-wide EF rules and
        // the audit interceptor, so every one of the six contexts arriving in
        // 13.3b will reference it. That is exactly the position from which a
        // project turns into the "Common" assembly nobody can split: it is
        // upstream of everything, so anything added to it is added to every
        // module at once.
        //
        // It cannot be held to the Foundation_projects rule above, because it
        // legitimately needs IAuditable. So the rule is one level weaker and
        // still checkable: SharedKernel, and nothing else of ours. In particular
        // NOT a module — a reference to one would mean an entity had been added,
        // which is the thing its csproj says must not happen. That was easier to
        // state before 13.3b, when every entity lived in one project it could be
        // named against; the rule is unchanged and the check is the same.
        var ours = ReferencedJobkeepAssemblies("Jobkeep.Persistence")
            .Where(name => name != "Jobkeep.SharedKernel")
            .ToList();

        Assert.True(ours.Count == 0,
            "Jobkeep.Persistence may reference SharedKernel and nothing else of ours, "
            + "but references: " + string.Join(", ", ours));
    }

    [Fact]
    public void No_module_takes_a_context_it_does_not_own()
    {
        // PHASE 13.3b REPLACED THIS TEST'S SUBJECT, because deleting AppDbContext would
        // otherwise have quietly made it vacuous.
        //
        // It used to look for a constructor parameter typed AppDbContext, which was the
        // one type that could see all thirteen tables. That type is gone, so the literal
        // check would now pass forever while proving nothing — the most expensive kind
        // of green test.
        //
        // The rule that replaces it is stronger and does not depend on a name: a
        // module's handler may take a DbContext DECLARED IN ITS OWN ASSEMBLY, and no
        // other. That catches three things at once — another module's context, a future
        // re-introduced shared one, and TestDbContext, the aggregate context in tests/
        // that exists so 122 arrange call sites did not have to change. The last is why
        // the check is written this way rather than as a second name to look for.
        //
        // Constructor parameters rather than fields, because DI is how a handler gets
        // one. A module that new'd up a context would need a connection string it has no
        // way to reach.
        var violations = new List<string>();

        foreach (var module in Modules)
        {
            var assembly = Assembly.Load(new AssemblyName(module));

            foreach (var type in assembly.GetTypes())
                foreach (var ctor in type.GetConstructors())
                    foreach (var parameter in ctor.GetParameters())
                    {
                        if (!IsDbContext(parameter.ParameterType)) continue;
                        if (parameter.ParameterType.Assembly == assembly) continue;

                        violations.Add(
                            $"{module}: {type.Name}({parameter.ParameterType.Name} {parameter.Name})");
                    }
        }

        Assert.True(violations.Count == 0,
            "A module takes a DbContext it does not own. Depend on your own module's "
            + "context and reach another module through Jobkeep.Contracts:\n  "
            + string.Join("\n  ", violations));
    }

    private static bool IsDbContext(Type type)
    {
        // By name up the base chain, so this file needs no reference to EF Core. The
        // same reasoning as loading assemblies by name below: a test about what modules
        // may depend on should not itself depend on much.
        for (var t = type; t is not null; t = t.BaseType)
            if (t.FullName == "Microsoft.EntityFrameworkCore.DbContext")
                return true;

        return false;
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
