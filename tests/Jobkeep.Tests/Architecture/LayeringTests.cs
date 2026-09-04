using System.Reflection;
using System.Runtime.CompilerServices;

namespace Jobkeep.Tests.Architecture;

/// <summary>
/// Phase 13.6. Two rules that live one level below <see cref="ModuleBoundaryTests"/>:
/// that one is about which assemblies may reference which, this one is about what the
/// namespaces inside them are allowed to say.
///
/// Why no architecture-test package, when ModuleBoundaryTests' own comment said 13.6
/// "can bring it with them": both rules turned out to be a dozen lines of reflection,
/// and a package is a dependency every future session pays for. The comment was
/// written before the rules were, and it guessed high. If a third rule needs the
/// query surface NetArchTest gives, add it then.
/// </summary>
public class LayeringTests
{
    private static readonly string[] Assemblies =
    [
        "Jobkeep.SharedKernel",
        "Jobkeep.Contracts",
        "Jobkeep.Persistence",
        "Jobkeep.Modules.Applications",
        "Jobkeep.Modules.Analytics",
        "Jobkeep.Modules.Ai",
        "Jobkeep.Modules.Ats",
        "Jobkeep.Modules.Documents",
        "Jobkeep.Modules.Skills",
        "Jobkeep.Api",
    ];

    [Fact]
    public void A_namespace_begins_with_the_name_of_the_project_that_holds_it()
    {
        // THE RULE PHASE 13.6 EXISTS TO ESTABLISH, and it is worth being precise about
        // what it buys, because "tidy namespaces" is not a reason to spend a session.
        //
        // Before 13.6 four namespaces spanned two projects each — Jobkeep.Models held
        // entities from five modules plus Contracts plus SharedKernel; Jobkeep.Shared
        // held SharedKernel and Api; Jobkeep.GraphQL named no project at all; and the
        // contract interfaces sat in Jobkeep.Modules.<X> alongside the modules they
        // describe. The whole of Phase 13 is about making the reference graph real, and
        // a namespace that spans projects makes that graph invisible at exactly the
        // place a person reads it: the using block.
        //
        // It is not cosmetic, and there is a scar to prove it. DispatchTests loaded its
        // six module assemblies through one type each, and the Skills line read
        // typeof(Jobkeep.Modules.Skills.ISkillCatalog) — a type in the CONTRACTS
        // assembly. So the list named Contracts twice, never loaded Skills at all, and
        // every handler in that module went unchecked behind a line that compiled and
        // passed. The rename is what surfaced it: the reference stopped resolving.
        var violations = new List<string>();

        foreach (var name in Assemblies)
        {
            var assembly = Assembly.Load(new AssemblyName(name));

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsNested || IsGenerated(type)) continue;

                // Program.cs is top-level statements, so its generated entry-point type
                // has no namespace. Nothing to check and nothing to fix.
                if (type.Namespace is null) continue;

                // Only our own namespaces. Mediator's source generator emits into
                // Mediator.Internals and Microsoft.Extensions.DependencyInjection from
                // inside Jobkeep.Api — correct for a generator, and not ours to name.
                // The rule is about what a Jobkeep namespace claims, not about every
                // type that ends up in the assembly.
                if (!type.Namespace.StartsWith("Jobkeep")) continue;

                if (type.Namespace == name || type.Namespace.StartsWith(name + ".")) continue;

                violations.Add($"{name}: {type.Namespace}.{type.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            "A namespace does not begin with the name of the project that holds it, so the "
            + "using block no longer says which assembly a type comes from:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void A_modules_Domain_knows_nothing_of_EF_or_of_the_rest_of_its_module()
    {
        // The inside-a-module layering rule. Entities are the one layer that outlives a
        // service extraction unchanged, so they must not name the machinery around them:
        // not EF, not the module's DbContext, not a handler. In practice the way this
        // gets broken is a DbSet property or an IEntityTypeConfiguration written on the
        // entity "while I'm here" — which is why the module's own configuration classes
        // live in Persistence/ and this test forbids the shortcut rather than trusting it.
        //
        // Signatures only: base types, interfaces, fields (backing fields included,
        // because EF writes through them), properties, constructor and method parameters,
        // return types and attributes. THE CEILING: a method BODY that called into EF
        // would not be seen. That is accepted — these are POCOs, a body doing EF work
        // would need a context it has no way to reach, and the alternative is an IL
        // reader or a package. Upgrade to one only if a real violation ever slips past.
        var violations = new List<string>();
        var examined = 0;

        foreach (var name in Assemblies.Where(a => a.StartsWith("Jobkeep.Modules.")))
        {
            var assembly = Assembly.Load(new AssemblyName(name));
            var domain = name + ".Domain";

            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace != domain || IsGenerated(type)) continue;
                examined++;

                foreach (var referenced in SignatureTypes(type))
                {
                    var ns = referenced.Namespace;
                    if (ns is null) continue;

                    var offence =
                        ns.StartsWith("Microsoft.EntityFrameworkCore") ? "EF Core"
                        : ns == name || (ns.StartsWith(name + ".") && ns != domain) ? "its own module outside Domain/"
                        : null;

                    if (offence is not null)
                        violations.Add($"{type.Name} -> {referenced.Name} ({offence})");
                }
            }
        }

        // The canary. A rule keyed on a namespace SUFFIX is one rename away from
        // examining nothing and passing forever, and this repository has already paid
        // for that once — see the note on No_module_takes_a_context_it_does_not_own.
        // The number is the thirteen entities plus the enums and the transition table
        // that sit beside them; it is a floor, not a count, so adding an entity does
        // not break it and emptying Domain/ does.
        Assert.True(examined >= 13,
            $"This test examined {examined} types. Domain/ has more than that, so the "
            + "namespace it looks for has moved and the rule below is checking nothing.");

        Assert.True(violations.Count == 0,
            "An entity names something it should not. Domain/ holds the types that survive "
            + "a service extraction unchanged; put the mapping in Persistence/ and the "
            + "behaviour in a slice:\n  " + string.Join("\n  ", violations.Distinct()));
    }

    private static bool IsGenerated(Type type)
        => type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
           || type.Name.Contains('<');

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        IEnumerable<Type> Unwrap(Type? t)
        {
            if (t is null) yield break;
            if (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType()!;
            yield return t;
            foreach (var arg in t.GetGenericArguments()) yield return arg;
        }

        foreach (var t in Unwrap(type.BaseType)) yield return t;
        foreach (var i in type.GetInterfaces()) foreach (var t in Unwrap(i)) yield return t;
        foreach (var f in type.GetFields(All)) foreach (var t in Unwrap(f.FieldType)) yield return t;
        foreach (var p in type.GetProperties(All)) foreach (var t in Unwrap(p.PropertyType)) yield return t;

        foreach (var m in type.GetMethods(All))
        {
            foreach (var t in Unwrap(m.ReturnType)) yield return t;
            foreach (var p in m.GetParameters()) foreach (var t in Unwrap(p.ParameterType)) yield return t;
        }

        foreach (var c in type.GetConstructors(All))
            foreach (var p in c.GetParameters()) foreach (var t in Unwrap(p.ParameterType)) yield return t;

        foreach (var a in type.GetCustomAttributesData()) yield return a.AttributeType;
    }
}
