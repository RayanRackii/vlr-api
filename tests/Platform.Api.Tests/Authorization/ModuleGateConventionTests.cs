using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Authorization;

public sealed class ModuleGateConventionTests
{
    private static readonly IReadOnlyDictionary<string, string> NamespaceModules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Platform.Api.Modules.Assets.Controllers"] = PlatformModules.Inventory,
            ["Platform.Api.Modules.Rentals.Controllers"] = PlatformModules.Rentals,
            ["Platform.Api.Modules.Pmoc.Controllers"] = PlatformModules.Pmoc,
            ["Platform.Api.Modules.WorkOrders.Controllers"] = PlatformModules.WorkOrders,
            ["Platform.Api.Modules.Catalog.Controllers"] = PlatformModules.Catalog,
        };

    private static readonly HashSet<string> CommercialModuleKeys =
        PlatformModuleCatalog.Commercial
            .Select(module => module.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static readonly Dictionary<string, PermissionDefinition> PermissionsByKey =
        PermissionCatalog.All.ToDictionary(item => item.Key, StringComparer.Ordinal);

    [Fact]
    public void Commercial_endpoints_declare_the_matching_RequireActiveModule()
    {
        using var host = StartPlatformApiHost();
        var descriptors = host.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToList();

        Assert.True(
            descriptors.Count > 0,
            "IActionDescriptorCollectionProvider did not discover Platform.Api controllers.");

        var controllerNames = descriptors
            .Select(action => action.ControllerTypeInfo.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("AssetsController", controllerNames);
        Assert.Contains("UsersController", controllerNames);
        Assert.Contains("AdminTenantsController", controllerNames);

        var violations = new List<string>();
        foreach (var action in descriptors)
        {
            violations.AddRange(Inspect(action));
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> Inspect(ControllerActionDescriptor action)
    {
        var expected = ResolveExpectedModule(action, out var conflict);
        if (conflict is not null)
        {
            yield return conflict;
            yield break;
        }

        if (expected is null)
        {
            yield break;
        }

        var declared = ResolveDeclaredModule(action);
        var endpoint = $"{action.ControllerTypeInfo.Name}.{action.ActionName}";

        if (declared is null)
        {
            yield return
                $"Commercial endpoint {endpoint} belongs to module {expected} but does not declare RequireActiveModule({expected}).";
            yield break;
        }

        if (!string.Equals(declared, expected, StringComparison.Ordinal))
        {
            yield return
                $"Commercial endpoint {endpoint} belongs to module {expected} but declares RequireActiveModule({declared}) instead of {expected}.";
        }
    }

    private static string? ResolveExpectedModule(ControllerActionDescriptor action, out string? conflict)
    {
        conflict = null;
        var nsModule = ResolveNamespaceModule(action.ControllerTypeInfo.Namespace);
        var permissionModules = ResolvePermissionModules(action).ToList();

        if (permissionModules.Count > 1)
        {
            conflict =
                $"Commercial endpoint {action.ControllerTypeInfo.Name}.{action.ActionName} " +
                $"declares commercial permissions for multiple modules: {string.Join(", ", permissionModules)}.";
            return null;
        }

        var permissionModule = permissionModules.Count == 1 ? permissionModules[0] : null;

        if (nsModule is not null
            && permissionModule is not null
            && !string.Equals(nsModule, permissionModule, StringComparison.Ordinal))
        {
            conflict =
                $"Commercial endpoint {action.ControllerTypeInfo.Name}.{action.ActionName} " +
                $"belongs to module {nsModule} from namespace but permission catalog requires {permissionModule}.";
            return null;
        }

        return nsModule ?? permissionModule;
    }

    private static string? ResolveNamespaceModule(string? ns) =>
        ns is not null && NamespaceModules.TryGetValue(ns, out var module) ? module : null;

    private static IEnumerable<string> ResolvePermissionModules(ControllerActionDescriptor action)
    {
        var modules = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var policy in EnumerateAuthorizePolicies(action))
        {
            if (!PermissionPolicies.TryParse(policy, out var permissionKey))
            {
                continue;
            }

            if (!PermissionsByKey.TryGetValue(permissionKey, out var definition))
            {
                continue;
            }

            if (definition.ModuleKey is not null && CommercialModuleKeys.Contains(definition.ModuleKey))
            {
                modules.Add(definition.ModuleKey);
            }
        }

        return modules;
    }

    private static IEnumerable<string> EnumerateAuthorizePolicies(ControllerActionDescriptor action)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var data in action.EndpointMetadata.OfType<IAuthorizeData>())
        {
            if (TryAddPolicy(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }

        foreach (var data in action.MethodInfo.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
        {
            if (TryAddPolicy(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }

        foreach (var data in action.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
        {
            if (TryAddPolicy(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }
    }

    private static bool TryAddPolicy(HashSet<string> seen, string? policy, out string added)
    {
        added = string.Empty;
        if (string.IsNullOrWhiteSpace(policy) || !seen.Add(policy))
        {
            return false;
        }

        added = policy;
        return true;
    }

    private static string? ResolveDeclaredModule(ControllerActionDescriptor action)
    {
        var onAction = action.MethodInfo.GetCustomAttribute<RequireActiveModuleAttribute>(inherit: true);
        if (onAction is not null)
        {
            return onAction.ModuleKey;
        }

        return action.ControllerTypeInfo
            .GetCustomAttribute<RequireActiveModuleAttribute>(inherit: true)
            ?.ModuleKey;
    }

    private static IHost StartPlatformApiHost() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(AssetsController).Assembly));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
}
