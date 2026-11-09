using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BaselineX.Api.Common;
using BaselineX.Api.Features.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Commerce;

public enum OrderStatus { PendingPayment, Paid, AwaitingShipment, Shipped, Delivered, Completed, Cancelled, Refunded }
public enum PaymentStatus { Pending, RequiresAction, Succeeded, Failed, Cancelled, PartiallyRefunded, Refunded }
public enum ShipmentStatus { Pending, LabelCreated, Shipped, InTransit, Delivered, Returned, Cancelled }
public enum StripeEventStatus { Received, Processed, Ignored, Failed }

// One order per seller. Prices are always computed from the DB, never from client input.
[Index(nameof(OrderNumber), IsUnique = true)]
[Index(nameof(OrganizationId))]
[Index(nameof(Status))]
[Index(nameof(BuyerId), nameof(CreatedAt))]
[Index(nameof(SellerId), nameof(CreatedAt))]
public class Order : IHasTimestamps
{
    public Guid Id { get; set; }
    [MaxLength(60)] public string OrderNumber { get; set; } = "";
    public Guid BuyerId { get; set; }
    public Guid SellerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public OrderStatus Status { get; private set; } = OrderStatus.PendingPayment;

    public decimal Subtotal { get; private set; }
    public decimal ShippingAmount { get; private set; }
    public decimal PlatformFee { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; set; } = "USD";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }

    public uint Version { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<Shipment> Shipments { get; set; } = [];

    // Listing (and offer) must be loaded from the DB; they are the only source of the price.
    // Buy-now reserves an active listing; an accepted offer has already reserved it.
    public static Order CreateForListing(Listing listing, Guid buyerId, string orderNumber, Offer? acceptedOffer,
        decimal shippingAmount, decimal platformFeePercent, decimal taxAmount)
    {
        if (buyerId == listing.SellerUserId) throw new DomainException("Buyer cannot purchase their own listing.");

        if (acceptedOffer is null)
            listing.Reserve();
        else if (acceptedOffer.ListingId != listing.Id || acceptedOffer.BuyerId != buyerId ||
                 acceptedOffer.Status != OfferStatus.Accepted || listing.Status != ListingStatus.Reserved)
            throw new DomainException("Offer is not an accepted offer for this buyer and listing.");

        var price = acceptedOffer?.Amount ?? listing.Price;
        var platformFee = Math.Round(price * platformFeePercent / 100m, 2);
        var order = new Order
        {
            OrderNumber = orderNumber, BuyerId = buyerId, SellerId = listing.SellerUserId,
            OrganizationId = listing.OrganizationId, Currency = listing.Currency,
            Subtotal = price, ShippingAmount = shippingAmount, PlatformFee = platformFee, TaxAmount = taxAmount,
            Total = price + shippingAmount + platformFee + taxAmount,
        };
        order.Items.Add(new OrderItem { ListingId = listing.Id, TitleSnapshot = listing.Title, PriceSnapshot = price });
        return order;
    }

    public void MarkPaid()
    {
        Status = Transition.To(Status, OrderStatus.Paid, OrderStatus.PendingPayment);
        PaidAt = DateTime.UtcNow;
    }

    public void MarkAwaitingShipment() =>
        Status = Transition.To(Status, OrderStatus.AwaitingShipment, OrderStatus.Paid);

    public void MarkShipped() =>
        Status = Transition.To(Status, OrderStatus.Shipped, OrderStatus.Paid, OrderStatus.AwaitingShipment);

    public void MarkDelivered() =>
        Status = Transition.To(Status, OrderStatus.Delivered, OrderStatus.Shipped);

    // Paid -> Completed covers local pickup.
    public void Complete()
    {
        Status = Transition.To(Status, OrderStatus.Completed, OrderStatus.Paid, OrderStatus.Delivered);
        CompletedAt = DateTime.UtcNow;
    }

    public void Cancel()
    {
        Status = Transition.To(Status, OrderStatus.Cancelled,
            OrderStatus.PendingPayment, OrderStatus.Paid, OrderStatus.AwaitingShipment);
        CancelledAt = DateTime.UtcNow;
    }

    public void MarkRefunded() =>
        Status = Transition.To(Status, OrderStatus.Refunded,
            OrderStatus.Paid, OrderStatus.AwaitingShipment, OrderStatus.Shipped,
            OrderStatus.Delivered, OrderStatus.Completed, OrderStatus.Cancelled);
}

// Immutable snapshots.
[Index(nameof(OrderId))]
[Index(nameof(ListingId))]
public class OrderItem : IHasCreatedAt
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid ListingId { get; init; }
    public Listing Listing { get; init; } = null!;
    [MaxLength(250)] public string TitleSnapshot { get; init; } = "";
    public decimal PriceSnapshot { get; init; }
    public int Quantity { get; init; } = 1;
    public DateTime CreatedAt { get; set; }
}

// No payout id: Stripe payouts are batched per account, not per payment.
[Index(nameof(OrderId))]
[Index(nameof(ProviderPaymentIntentId), IsUnique = true)]
[Index(nameof(ProviderChargeId))]
[Index(nameof(Status))]
public class Payment : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    [MaxLength(50)] public string Provider { get; set; } = "stripe";
    [MaxLength(255)] public string? ProviderPaymentIntentId { get; set; }
    [MaxLength(255)] public string? ProviderChargeId { get; private set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;
    [MaxLength(255)] public string? StripeConnectedAccountId { get; set; }
    [MaxLength(255)] public string? TransferId { get; set; }
    [MaxLength(255)] public string? RefundId { get; private set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }

    public void MarkRequiresAction() =>
        Status = Transition.To(Status, PaymentStatus.RequiresAction, PaymentStatus.Pending);

    public void MarkSucceeded(string? chargeId)
    {
        Status = Transition.To(Status, PaymentStatus.Succeeded, PaymentStatus.Pending, PaymentStatus.RequiresAction);
        ProviderChargeId = chargeId;
    }

    public void MarkFailed() =>
        Status = Transition.To(Status, PaymentStatus.Failed, PaymentStatus.Pending, PaymentStatus.RequiresAction);

    public void Cancel() =>
        Status = Transition.To(Status, PaymentStatus.Cancelled, PaymentStatus.Pending, PaymentStatus.RequiresAction);

    public void MarkRefunded(string refundId, bool partial)
    {
        Status = Transition.To(Status, partial ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded,
            PaymentStatus.Succeeded, PaymentStatus.PartiallyRefunded);
        RefundId = refundId;
    }
}

[Index(nameof(OrderId))]
[Index(nameof(TrackingNumber))]
[Index(nameof(Status))]
public class Shipment : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    [MaxLength(80)] public string? Carrier { get; private set; }
    [MaxLength(120)] public string? ServiceLevel { get; private set; }
    [MaxLength(180)] public string? TrackingNumber { get; private set; }
    [MaxLength(1000)] public string? LabelUrl { get; private set; }
    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Pending;
    [MaxLength(180)] public string? RecipientName { get; set; }
    [MaxLength(180)] public string? AddressLine1 { get; set; }
    [MaxLength(180)] public string? AddressLine2 { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(80)] public string? State { get; set; }
    [MaxLength(30)] public string? PostalCode { get; set; }
    [Column(TypeName = "char(2)")] public string? CountryCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ShippedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public uint Version { get; set; }

    public void MarkLabelCreated(string carrier, string? serviceLevel, string trackingNumber, string? labelUrl)
    {
        Status = Transition.To(Status, ShipmentStatus.LabelCreated, ShipmentStatus.Pending);
        (Carrier, ServiceLevel, TrackingNumber, LabelUrl) = (carrier, serviceLevel, trackingNumber, labelUrl);
    }

    public void MarkShipped()
    {
        Status = Transition.To(Status, ShipmentStatus.Shipped, ShipmentStatus.Pending, ShipmentStatus.LabelCreated);
        ShippedAt = DateTime.UtcNow;
    }

    public void MarkInTransit() =>
        Status = Transition.To(Status, ShipmentStatus.InTransit, ShipmentStatus.Shipped);

    public void MarkDelivered()
    {
        Status = Transition.To(Status, ShipmentStatus.Delivered, ShipmentStatus.Shipped, ShipmentStatus.InTransit);
        DeliveredAt = DateTime.UtcNow;
    }

    public void MarkReturned() =>
        Status = Transition.To(Status, ShipmentStatus.Returned,
            ShipmentStatus.Shipped, ShipmentStatus.InTransit, ShipmentStatus.Delivered);

    public void Cancel() =>
        Status = Transition.To(Status, ShipmentStatus.Cancelled, ShipmentStatus.Pending, ShipmentStatus.LabelCreated);
}

// Idempotency: insert first; a unique violation on StripeEventId means already handled, return 200.
[Index(nameof(StripeEventId), IsUnique = true)]
[Index(nameof(Status))]
[Index(nameof(ReceivedAt))]
public class StripeWebhookEvent
{
    public Guid Id { get; set; }
    [MaxLength(255)] public string StripeEventId { get; set; } = "";
    [MaxLength(180)] public string EventType { get; set; } = "";
    public StripeEventStatus Status { get; private set; } = StripeEventStatus.Received;
    [MaxLength(128)] public string? PayloadHash { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void MarkProcessed()
    {
        Status = Transition.To(Status, StripeEventStatus.Processed, StripeEventStatus.Received, StripeEventStatus.Failed);
        ProcessedAt = DateTime.UtcNow;
        ErrorMessage = null;
    }

    public void MarkIgnored()
    {
        Status = Transition.To(Status, StripeEventStatus.Ignored, StripeEventStatus.Received);
        ProcessedAt = DateTime.UtcNow;
    }

    public void MarkFailed(string error)
    {
        Status = Transition.To(Status, StripeEventStatus.Failed, StripeEventStatus.Received, StripeEventStatus.Failed);
        ErrorMessage = error;
    }
}
