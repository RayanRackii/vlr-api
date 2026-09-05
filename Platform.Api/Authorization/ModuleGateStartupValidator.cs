using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Authorization;

/// <summary>
/// Fails host startup when <see cref="RequireActiveModuleAttribute"/> uses a key that is not
/// a canonical commercial module (<see cref="PlatformModuleCatalog.Commercial"/>).
/// </summary>
public static class ModuleGateStartupValidator
{
    public const string InvalidKeyCode = "MODULE_KEY_INVALID";

    public static void Validate(IActionDescriptorCollectionProvider descriptors)
    {
        var violations = new List<string>();

        foreach (var action in descriptors.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            foreach (var attribute in EnumerateAttributes(action))
            {
                if (!IsCanonicalCommercialKey(attribute.ModuleKey))
                {
                    violations.Add(
                        $"{action.ControllerTypeInfo.FullName}.{action.ActionName} key={attribute.ModuleKey}");
                }
            }
        }

        ThrowIfInvalid(violations);
    }

    public static void Validate(Assembly assembly)
    {
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            foreach (var attribute in type.GetCustomAttributes<RequireActiveModuleAttribute>(inherit: true))
            {
                if (!IsCanonicalCommercialKey(attribute.ModuleKey))
                {
                    violations.Add($"{type.FullName}.* key={attribute.ModuleKey}");
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<RequireActiveModuleAttribute>(inherit: true))
                {
                    if (!IsCanonicalCommercialKey(attribute.ModuleKey))
                    {
                        violations.Add($"{type.FullName}.{method.Name} key={attribute.ModuleKey}");
                    }
                }
            }
        }

        ThrowIfInvalid(violations);
    }

    internal static bool IsCanonicalCommercialKey(string? moduleKey)
    {
        if (string.IsNullOrWhiteSpace(moduleKey))
        {
            return false;
        }

        if (!PlatformModuleCatalog.TryNormalize(moduleKey, out var canonical))
        {
            return false;
        }

        if (!string.Equals(moduleKey, canonical, StringComparison.Ordinal))
        {
            return false;
        }

        return PlatformModuleCatalog.Commercial.Any(module =>
            string.Equals(module.Key, canonical, StringComparison.Ordinal));
    }

    private static IEnumerable<RequireActiveModuleAttribute> EnumerateAttributes(
        ControllerActionDescriptor action)
    {
        foreach (var attribute in action.ControllerTypeInfo
                     .GetCustomAttributes<RequireActiveModuleAttribute>(inherit: true))
        {
            yield return attribute;
        }

        foreach (var attribute in action.MethodInfo
                     .GetCustomAttributes<RequireActiveModuleAttribute>(inherit: true))
        {
            yield return attribute;
        }
    }

    private static void ThrowIfInvalid(List<string> violations)
    {
        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{InvalidKeyCode}: {string.Join("; ", violations)}");
    }
}
