using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Skills;

// Module wiring for Skills. One registration, no routes, no options.
//
// There is no MapSkillsModule and no Api/Endpoints/SkillsEndpoints.cs, which is
// worth saying out loud rather than leaving as an omission: the taxonomy is
// never the thing a user asks for. Skills reach the API as part of a posting, a
// resume or a demand table, and every one of those routes belongs to the module
// that owns the question. A module with no surface of its own is a legitimate
// shape — it is a shared kernel of the domain, not a missing feature.
public static class SkillsModule
{
    public static IServiceCollection AddSkillsModule(this IServiceCollection services)
    {
        // Scoped, matching the context it holds. A singleton here would capture a
        // scoped SkillsDbContext, which is the captive-dependency bug
        // ApplicationsModule.cs calls out.
        services.AddScoped<ISkillCatalog, SkillCatalog>();
        return services;
    }
}
