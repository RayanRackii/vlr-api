using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogOrderStateMachineTests
{
    [Fact]
    public void Happy_path_and_invalid_transitions()
    {
        var order = NewOrder();
        order.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        order.StartPreparing(CatalogActorType.B2BUser, Guid.NewGuid());
        order.MarkReady(CatalogActorType.B2BUser, Guid.NewGuid());
        order.Complete(CatalogActorType.B2BUser, Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Completed, order.Status);
        Assert.Throws<InvalidCatalogOrderTransitionException>(
            () => order.Cancel(CatalogActorType.B2BUser, "late", Guid.NewGuid()));
    }

    [Fact]
    public void Reject_and_b2b_cancel_require_reason()
    {
        var order = NewOrder();
        Assert.Throws<ArgumentException>(() => order.Reject(" ", CatalogActorType.B2BUser, Guid.NewGuid()));
        order.Reject("sem estoque", CatalogActorType.B2BUser, Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Rejected, order.Status);
        Assert.Equal("sem estoque", order.RejectedReason);

        var preparing = NewOrder();
        preparing.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        preparing.StartPreparing(CatalogActorType.B2BUser, Guid.NewGuid());
        Assert.Throws<ArgumentException>(
            () => preparing.Cancel(CatalogActorType.B2BUser, "", Guid.NewGuid()));
        preparing.Cancel(CatalogActorType.B2BUser, "máquina parada", Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Cancelled, preparing.Status);
    }

    [Fact]
    public void Customer_can_cancel_requested_only()
    {
        var order = NewOrder();
        order.Cancel(CatalogActorType.Customer, reason: null, Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Cancelled, order.Status);

        var approved = NewOrder();
        approved.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        Assert.Throws<InvalidCatalogOrderTransitionException>(
            () => approved.Cancel(CatalogActorType.Customer, reason: null, Guid.NewGuid()));
    }

    [Fact]
    public void Customer_cannot_cancel_terminal_orders_even_with_widest_policy()
    {
        var rejected = NewOrder();
        rejected.Reject("sem estoque", CatalogActorType.B2BUser, Guid.NewGuid());

        var cancelled = NewOrder();
        cancelled.Cancel(CatalogActorType.Customer, reason: null, Guid.NewGuid());

        var completed = NewOrder();
        completed.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        completed.StartPreparing(CatalogActorType.B2BUser, Guid.NewGuid());
        completed.MarkReady(CatalogActorType.B2BUser, Guid.NewGuid());
        completed.Complete(CatalogActorType.B2BUser, Guid.NewGuid());

        foreach (var order in new[] { rejected, cancelled, completed })
        {
            var before = order.Status;
            Assert.Throws<InvalidCatalogOrderTransitionException>(
                () => order.Cancel(CatalogActorType.Customer, reason: null, Guid.NewGuid(), CatalogOrderStatus.Ready));
            Assert.Equal(before, order.Status);
        }
    }

    [Fact]
    public void B2B_can_cancel_preparing_and_ready()
    {
        var preparing = NewOrder();
        preparing.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        preparing.StartPreparing(CatalogActorType.B2BUser, Guid.NewGuid());
        preparing.Cancel(CatalogActorType.B2BUser, "falta peça", Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Cancelled, preparing.Status);

        var ready = NewOrder();
        ready.Approve(CatalogActorType.B2BUser, Guid.NewGuid());
        ready.StartPreparing(CatalogActorType.B2BUser, Guid.NewGuid());
        ready.MarkReady(CatalogActorType.B2BUser, Guid.NewGuid());
        ready.Cancel(CatalogActorType.B2BUser, "cliente desistiu", Guid.NewGuid());
        Assert.Equal(CatalogOrderStatus.Cancelled, ready.Status);
    }

    private static CatalogOrder NewOrder() =>
        new()
        {
            TenantId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            OrderNumber = 1,
            CustomerNameSnapshot = "Ana",
        };
}
