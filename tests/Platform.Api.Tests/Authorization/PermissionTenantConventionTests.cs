using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Api.Modules.Auth.Controllers;
using Platform.Api.Modules.CustomerAuth.Controllers;
using Platform.Api.Modules.Customers.Controllers;
using Platform.Api.Modules.ModuleMenuItems.Controllers;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.Webhooks.Controllers;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authorization;

/// <summary>
/// Platform-mode fallback (Auth read failure → no ambient tenant → global query filter off)
/// must not reach a controller action that can read tenant data.
/// An action is compliant when it is under <c>PlatformAdminPolicy</c>, carries
/// <c>[RequirePermission]</c> / <c>[RequireAnyPermission]</c>, has an explicit tenant gate,
/// or is on the public allow-list.
/// Explicit tenant gates recognized here: <c>Authorize(Policy = "Customer")</c>
/// (customer tokens resolve <c>tenant_id</c> or throw, and never fall through to platform mode)
/// and a direct call to <c>DirectoryAuthorization.RequireTenant</c> or <c>GateAsync</c>.
/// <c>[RequireActiveModule]</c> is not a tenant gate: it skips when <c>TenantId</c> is null.
/// <c>[AllowAnonymous]</c> suppresses <c>[RequirePermission]</c>, so those actions must be allow-listed.
/// <c>GET /api/users/me</c> is the reviewed identity exception: platform mode must still
/// answer, and the directory service reads <c>TenantId</c> before any user query.
/// </summary>
public sealed class PermissionTenantConventionTests
{
    private const string CustomerPolicy = "Customer";

    private static readonly HashSet<string> PublicAllowList = new(StringComparer.Ordinal)
    {
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.GetBranding)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.GetRegistrationSchema)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.Register)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.VerifyEmail)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.Login)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.RequestOtp)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.VerifyOtp)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.ResendVerification)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.RequestPasswordReset)),
        Endpoint<CustomerAuthController>(nameof(CustomerAuthController.ConfirmPasswordReset)),
        Endpoint<PasswordRecoveryController>(nameof(PasswordRecoveryController.ForgotPassword)),
        Endpoint<WhatsAppWebhookController>(nameof(WhatsAppWebhookController.Verify)),
        Endpoint<WhatsAppWebhookController>(nameof(WhatsAppWebhookController.Receive)),
        Endpoint<ReservationsController>(nameof(ReservationsController.CheckAvailability)),
        Endpoint<ReservationsController>(nameof(ReservationsController.ListPublicAssets)),
        Endpoint<ScheduleController>(nameof(ScheduleController.GetDayPublic)),
        Endpoint<RentalLayoutsController>(nameof(RentalLayoutsController.ListPublic)),
        Endpoint<BookingPolicyController>(nameof(BookingPolicyController.GetPublic)),
        Endpoint<ModuleMenuItemsController>(nameof(ModuleMenuItemsController.GetPublicMenu)),
        Endpoint<InvitesController>(nameof(InvitesController.Preview)),
        Endpoint<InvitesController>(nameof(InvitesController.Accept)),
    };

    /// <summary>
    /// Not public. Super-Admin with no tenant still calls <c>GET /api/users/me</c>.
    /// <c>[RequirePermission]</c> would 403 that call. Membership list and select
    /// sit here too: they authorize the Auth user, not a permission in the token's
    /// current tenant, so a stale tenant claim cannot block the picker.
    /// </summary>
    private static readonly HashSet<string> PlatformIdentityActions = new(StringComparer.Ordinal)
    {
        Endpoint<UsersController>(nameof(UsersController.GetCurrent)),
        Endpoint<StaffTenantMembershipsController>(nameof(StaffTenantMembershipsController.ListMemberships)),
        Endpoint<StaffTenantMembershipsController>(nameof(StaffTenantMembershipsController.SelectMembership)),
    };

    [Fact]
    public void Non_platform_admin_actions_are_permission_or_tenant_gated_or_public()
    {
        using var host = StartHost(typeof(AssetsController).Assembly);
        var descriptors = ReadActions(host);

        Assert.True(
            descriptors.Count > 0,
            "IActionDescriptorCollectionProvider did not discover Platform.Api controllers.");

        var ids = descriptors.Select(EndpointId).ToList();
        var unique = ids.ToHashSet(StringComparer.Ordinal);
        var ambiguous = descriptors
            .GroupBy(EndpointId, StringComparer.Ordinal)
            .Where(group => group.Select(action => action.MethodInfo).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.True(
            ambiguous.Count == 0,
            "Two different methods share an action name, so the allow-list cannot tell them apart: "
            + string.Join(", ", ambiguous));

        Assert.Contains(Endpoint<UsersController>(nameof(UsersController.GetCurrent)), unique);
        Assert.Contains(Endpoint<AdminTenantsController>(nameof(AdminTenantsController.List)), unique);
        Assert.Contains(Endpoint<TenantCustomersController>(nameof(TenantCustomersController.List)), unique);

        var violations = new List<string>();
        foreach (var action in descriptors)
        {
            violations.AddRange(Inspect(action));
        }

        foreach (var id in PublicAllowList)
        {
            if (!unique.Contains(id))
            {
                violations.Add($"Public allow-list entry {id} does not match a discovered action.");
            }
        }

        foreach (var id in PlatformIdentityActions)
        {
            if (!unique.Contains(id))
            {
                violations.Add($"Platform identity entry {id} does not match a discovered action.");
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Tenant_customers_list_calls_the_directory_tenant_gate()
    {
        var method = typeof(TenantCustomersController).GetMethod(nameof(TenantCustomersController.List));
        Assert.NotNull(method);
        Assert.True(CallsDirectoryTenantGate(method));
    }

    [Fact]
    public void Current_user_lookup_reads_the_ambient_tenant()
    {
        var method = typeof(UserDirectoryService).GetMethod(nameof(UserDirectoryService.GetCurrentAsync));
        Assert.NotNull(method);
        Assert.True(ReadsTenantId(method));
    }

    [Fact]
    public void Inspector_rejects_an_ungated_action_and_accepts_each_gate()
    {
        using var host = StartHost(
            typeof(UngatedProbeController).Assembly,
            typeof(UngatedProbeController),
            typeof(PermissionProbeController),
            typeof(AnyPermissionProbeController),
            typeof(PlatformAdminProbeForConventionController),
            typeof(CustomerProbeController),
            typeof(DirectoryGateProbeController),
            typeof(AnonymousPermissionProbeController),
            typeof(PlatformAdminAnonymousProbeController));
        var byId = ReadActions(host).ToDictionary(EndpointId, StringComparer.Ordinal);

        AssertViolation(byId, typeof(UngatedProbeController), nameof(UngatedProbeController.Get));
        AssertClean(byId, typeof(PermissionProbeController), nameof(PermissionProbeController.Get));
        AssertClean(byId, typeof(AnyPermissionProbeController), nameof(AnyPermissionProbeController.Get));
        AssertClean(
            byId,
            typeof(PlatformAdminProbeForConventionController),
            nameof(PlatformAdminProbeForConventionController.Get));
        AssertClean(byId, typeof(CustomerProbeController), nameof(CustomerProbeController.Get));
        AssertClean(byId, typeof(DirectoryGateProbeController), nameof(DirectoryGateProbeController.Get));
        AssertViolation(
            byId,
            typeof(AnonymousPermissionProbeController),
            nameof(AnonymousPermissionProbeController.Get));
        AssertViolation(
            byId,
            typeof(PlatformAdminAnonymousProbeController),
            nameof(PlatformAdminAnonymousProbeController.Get));
    }

    private static void AssertClean(
        IReadOnlyDictionary<string, ControllerActionDescriptor> byId,
        Type controller,
        string action)
    {
        var id = $"{controller.Name}.{action}";
        var violations = Inspect(byId[id]);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static void AssertViolation(
        IReadOnlyDictionary<string, ControllerActionDescriptor> byId,
        Type controller,
        string action)
    {
        var id = $"{controller.Name}.{action}";
        Assert.NotEmpty(Inspect(byId[id]));
    }

    private static IReadOnlyList<string> Inspect(ControllerActionDescriptor action)
    {
        var id = EndpointId(action);

        if (IsAllowAnonymous(action))
        {
            return PublicAllowList.Contains(id)
                ? []
                : [$"{id} is anonymous and is not on the public allow-list."];
        }

        if (PublicAllowList.Contains(id))
        {
            return [$"{id} is on the public allow-list but is not AllowAnonymous."];
        }

        if (HasPlatformAdminPolicy(action))
        {
            return [];
        }

        if (HasPermissionPolicy(action))
        {
            if (PlatformIdentityActions.Contains(id))
            {
                return [$"{id} now carries a permission. Remove it from the platform identity list."];
            }

            return [];
        }

        if (HasCustomerPolicy(action))
        {
            return [];
        }

        try
        {
            if (CallsDirectoryTenantGate(action.MethodInfo))
            {
                return [];
            }
        }
        catch (InvalidOperationException ex)
        {
            return [$"{id} IL could not be read: {ex.Message}"];
        }

        if (PlatformIdentityActions.Contains(id))
        {
            var currentUser = typeof(UserDirectoryService).GetMethod(
                nameof(UserDirectoryService.GetCurrentAsync));
            if (currentUser is not null && ReadsTenantId(currentUser))
            {
                return [];
            }

            return
            [
                $"{id} is the platform identity action, but UserDirectoryService.GetCurrentAsync no longer reads TenantId.",
            ];
        }

        return
        [
            $"{id} is outside PlatformAdminPolicy and has no RequirePermission, explicit tenant gate, or public allow-list entry.",
        ];
    }

    private static bool HasPlatformAdminPolicy(ControllerActionDescriptor action) =>
        EnumeratePolicies(action).Any(policy =>
            string.Equals(
                policy,
                SupabaseAuthenticationExtensions.PlatformAdminPolicy,
                StringComparison.Ordinal));

    private static bool HasCustomerPolicy(ControllerActionDescriptor action) =>
        EnumeratePolicies(action).Any(policy =>
            string.Equals(policy, CustomerPolicy, StringComparison.Ordinal));

    private static bool HasPermissionPolicy(ControllerActionDescriptor action)
    {
        if (HasAttribute<RequirePermissionAttribute>(action)
            || HasAttribute<RequireAnyPermissionAttribute>(action))
        {
            return true;
        }

        foreach (var policy in EnumeratePolicies(action))
        {
            if (PermissionPolicies.TryParse(policy, out _)
                || PermissionPolicies.TryParseAny(policy, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAttribute<TAttribute>(ControllerActionDescriptor action)
        where TAttribute : Attribute =>
        action.MethodInfo.GetCustomAttribute<TAttribute>(inherit: true) is not null
        || action.ControllerTypeInfo.GetCustomAttribute<TAttribute>(inherit: true) is not null
        || action.EndpointMetadata.OfType<TAttribute>().Any();

    private static bool IsAllowAnonymous(ControllerActionDescriptor action) =>
        action.MethodInfo.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || action.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || action.EndpointMetadata.OfType<IAllowAnonymous>().Any();

    private static IEnumerable<string> EnumeratePolicies(ControllerActionDescriptor action)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var data in action.EndpointMetadata.OfType<IAuthorizeData>())
        {
            if (TryAdd(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }

        foreach (var data in action.MethodInfo.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
        {
            if (TryAdd(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }

        foreach (var data in action.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>())
        {
            if (TryAdd(seen, data.Policy, out var policy))
            {
                yield return policy;
            }
        }
    }

    private static bool TryAdd(HashSet<string> seen, string? policy, out string added)
    {
        added = string.Empty;
        if (string.IsNullOrWhiteSpace(policy) || !seen.Add(policy))
        {
            return false;
        }

        added = policy;
        return true;
    }

    private static bool CallsDirectoryTenantGate(MethodInfo method) =>
        CalledMethods(method).Any(called =>
            called.DeclaringType == typeof(DirectoryAuthorization)
            && called.Name is nameof(DirectoryAuthorization.RequireTenant)
                or nameof(DirectoryAuthorization.GateAsync));

    private static bool ReadsTenantId(MethodInfo method) =>
        CalledMethods(method).Any(called =>
            called.DeclaringType == typeof(ITenantProvider)
            && called.Name == "get_" + nameof(ITenantProvider.TenantId));

    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        foreach (var body in ExecutionMethods(method))
        {
            foreach (var called in ReadCalls(body))
            {
                yield return called;
            }
        }
    }

    private static IEnumerable<MethodBase> ExecutionMethods(MethodInfo method)
    {
        yield return method;

        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = stateMachine?.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (moveNext is not null)
        {
            yield return moveNext;
        }
    }

    private static IEnumerable<MethodBase> ReadCalls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null || il.Length == 0)
        {
            yield break;
        }

        var module = method.Module;
        var index = 0;
        while (index < il.Length)
        {
            int opcodeValue;
            if (il[index] == 0xFE)
            {
                if (index + 1 >= il.Length)
                {
                    throw new InvalidOperationException($"Truncated two-byte opcode in {Describe(method)}.");
                }

                opcodeValue = unchecked((short)(0xFE00 | il[index + 1]));
                index += 2;
            }
            else
            {
                opcodeValue = il[index];
                index += 1;
            }

            if (!OpcodeShapes.TryGetValue(opcodeValue, out var shape))
            {
                throw new InvalidOperationException(
                    $"Unknown IL opcode 0x{opcodeValue & 0xFFFF:X} in {Describe(method)}.");
            }

            if (shape.OperandSize < 0)
            {
                if (index + 4 > il.Length)
                {
                    throw new InvalidOperationException($"Truncated switch in {Describe(method)}.");
                }

                var count = BitConverter.ToInt32(il, index);
                index += 4 + (count * 4);
                continue;
            }

            if (shape.IsMethodToken)
            {
                if (index + 4 > il.Length)
                {
                    throw new InvalidOperationException($"Truncated method token in {Describe(method)}.");
                }

                var token = BitConverter.ToInt32(il, index);
                index += 4;
                MethodBase? resolved = TryResolveMethod(module, method, token);
                if (resolved is not null)
                {
                    yield return resolved;
                }

                continue;
            }

            if (index + shape.OperandSize > il.Length)
            {
                throw new InvalidOperationException($"Truncated operand in {Describe(method)}.");
            }

            index += shape.OperandSize;
        }
    }

    private static MethodBase? TryResolveMethod(Module module, MethodBase caller, int token)
    {
        var typeArgs = caller.DeclaringType?.GetGenericArguments();
        var methodArgs = caller is MethodInfo info && info.IsGenericMethod
            ? info.GetGenericArguments()
            : null;

        if (typeArgs is { Length: 0 })
        {
            typeArgs = null;
        }

        if (methodArgs is { Length: 0 })
        {
            methodArgs = null;
        }

        try
        {
            return module.ResolveMethod(token, typeArgs, methodArgs);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Describe(MethodBase method) =>
        $"{method.DeclaringType?.FullName}.{method.Name}";

    private static string EndpointId(ControllerActionDescriptor action) =>
        $"{action.ControllerTypeInfo.Name}.{action.ActionName}";

    private static string Endpoint<TController>(string action)
        where TController : ControllerBase =>
        $"{typeof(TController).Name}.{action}";

    private static List<ControllerActionDescriptor> ReadActions(IHost host) =>
        host.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToList();

    private static IHost StartHost(Assembly assembly, params Type[] onlyControllers) =>
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
                            if (onlyControllers.Length > 0)
                            {
                                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                                {
                                    manager.FeatureProviders.Remove(provider);
                                }

                                manager.FeatureProviders.Add(new SelectedControllerFeatureProvider(onlyControllers));
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(assembly));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private static readonly Dictionary<int, OpcodeShape> OpcodeShapes = BuildOpcodeShapes();

    private static Dictionary<int, OpcodeShape> BuildOpcodeShapes()
    {
        var shapes = new Dictionary<int, OpcodeShape>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
            {
                continue;
            }

            var size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget
                    or OperandType.ShortInlineI
                    or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI
                    or OperandType.InlineBrTarget
                    or OperandType.InlineField
                    or OperandType.InlineMethod
                    or OperandType.InlineSig
                    or OperandType.InlineString
                    or OperandType.InlineTok
                    or OperandType.InlineType
                    or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                _ => throw new InvalidOperationException(
                    $"Unsupported operand type {opcode.OperandType} on {opcode.Name}."),
            };

            shapes[opcode.Value] = new OpcodeShape(
                size,
                opcode.OperandType == OperandType.InlineMethod);
        }

        return shapes;
    }

    private readonly record struct OpcodeShape(int OperandSize, bool IsMethodToken);

    private sealed class SelectedControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }
}

[ApiController]
[Route("convention/ungated")]
internal sealed class UngatedProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiController]
[Route("convention/permission")]
internal sealed class PermissionProbeController : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Core.UsersRead)]
    public IActionResult Get() => Ok();
}

[ApiController]
[Route("convention/any-permission")]
internal sealed class AnyPermissionProbeController : ControllerBase
{
    [HttpGet]
    [RequireAnyPermission(Permissions.Core.UsersRead, Permissions.Core.RolesRead)]
    public IActionResult Get() => Ok();
}

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("convention/platform-admin")]
internal sealed class PlatformAdminProbeForConventionController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiController]
[Authorize(Policy = "Customer")]
[Route("convention/customer")]
internal sealed class CustomerProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiController]
[Route("convention/directory-gate")]
internal sealed class DirectoryGateProbeController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var denied = await DirectoryAuthorization.GateAsync(
            this,
            null!,
            null!,
            User,
            Permissions.Core.CustomersRead);
        return denied ?? Ok();
    }
}

[ApiController]
[Route("convention/anonymous-permission")]
internal sealed class AnonymousPermissionProbeController : ControllerBase
{
    [AllowAnonymous]
    [RequirePermission(Permissions.Core.UsersRead)]
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("convention/platform-admin-anonymous")]
internal sealed class PlatformAdminAnonymousProbeController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public IActionResult Get() => Ok();
}
