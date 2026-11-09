using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Marketplace;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Commerce;

public enum FulfillmentMethod { Shipping, LocalPickup }

public record ShippingAddress(
    [Required, MaxLength(180)] string RecipientName,
    [Required, MaxLength(180)] string AddressLine1,
    [MaxLength(180)] string? AddressLine2,
    [Required, MaxLength(120)] string City,
    [Required, MaxLength(80)] string State,
    [Required, MaxLength(30)] string PostalCode,
    [Required, StringLength(2, MinimumLength = 2)] string CountryCode);

// Only ids are accepted from the client; every amount is computed from the DB.
public record CreateOrderRequest(Guid ListingId, Guid? OfferId, FulfillmentMethod Fulfillment, ShippingAddress? ShippingAddress);

public record ShipOrderRequest(
    [Required, MaxLength(80)] string Carrier,
    [MaxLength(120)] string? ServiceLevel,
    [Required, MaxLength(180)] string TrackingNumber,
    [MaxLength(1000)] string? LabelUrl);

public record OrderSummaryDto(Guid Id, string OrderNumber, OrderStatus Status, Guid BuyerId, Guid SellerId,
    decimal Total, string Currency, string? Title, DateTime CreatedAt);

public record OrderItemDto(Guid ListingId, string TitleSnapshot, decimal PriceSnapshot, int Quantity);
public record PaymentDto(Guid Id, decimal Amount, string Currency, PaymentStatus Status, DateTime CreatedAt);
public record ShipmentDto(Guid Id, ShipmentStatus Status, string? Carrier, string? ServiceLevel, string? TrackingNumber,
    string? RecipientName, string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode,
    string? CountryCode, DateTime? ShippedAt, DateTime? DeliveredAt);

public record OrderDto(Guid Id, string OrderNumber, OrderStatus Status, Guid BuyerId, Guid SellerId, Guid? OrganizationId,
    decimal Subtotal, decimal ShippingAmount, decimal PlatformFee, decimal TaxAmount, decimal Total, string Currency,
    IEnumerable<OrderItemDto> Items, IEnumerable<PaymentDto> Payments, IEnumerable<ShipmentDto> Shipments,
    DateTime CreatedAt, DateTime? PaidAt, DateTime? CompletedAt, DateTime? CancelledAt);

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Orders").RequireAuthorization();

        g.MapPost("/orders", async (CreateOrderRequest req, ClaimsPrincipal user, AppDbContext db, IConfiguration config) =>
        {
            var me = user.UserId();
            var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == req.ListingId);
            if (listing is null) return Results.NotFound();

            if (req.Fulfillment == FulfillmentMethod.Shipping && (!listing.ShippingAvailable || req.ShippingAddress is null))
                return Results.BadRequest("Shipping is not available for this listing or the address is missing.");
            if (req.Fulfillment == FulfillmentMethod.LocalPickup && !listing.LocalPickupAvailable)
                return Results.BadRequest("Local pickup is not available for this listing.");
            if (await HasOpenOrder(db, listing.Id))
                throw new DomainException("This listing already has an open order.");

            var offer = req.OfferId is { } offerId ? await db.Offers.FirstOrDefaultAsync(o => o.Id == offerId) : null;
            if (req.OfferId is not null && offer is null) return Results.BadRequest("Unknown offer.");

            // TODO: shipping rates and tax (e.g. Stripe Tax) are not wired up yet.
            var order = Order.CreateForListing(listing, me, NewOrderNumber(), offer,
                shippingAmount: 0m, config.GetValue("Fees:MarketplacePercent", 0m), taxAmount: 0m);
            order.Payments.Add(new Payment { Amount = order.Total, Currency = order.Currency });
            if (req.ShippingAddress is { } a && req.Fulfillment == FulfillmentMethod.Shipping)
                order.Shipments.Add(new Shipment
                {
                    RecipientName = a.RecipientName, AddressLine1 = a.AddressLine1, AddressLine2 = a.AddressLine2,
                    City = a.City, State = a.State, PostalCode = a.PostalCode, CountryCode = a.CountryCode.ToUpperInvariant(),
                });

            db.Orders.Add(order);
            await db.SaveChangesAsync();
            // TODO: create the Stripe PaymentIntent and return its client secret.
            return Results.Created($"/orders/{order.Id}", await Get(db, order.Id));
        });

        // role = buyer (my purchases) | seller (my sales)
        g.MapGet("/me/orders", async (ClaimsPrincipal user, AppDbContext db, string? role, OrderStatus? status,
            int? page, int? pageSize) =>
        {
            var me = user.UserId();
            var query = role == "seller"
                ? db.Orders.AsNoTracking().Where(o => o.SellerId == me)
                : db.Orders.AsNoTracking().Where(o => o.BuyerId == me);
            if (status is not null) query = query.Where(o => o.Status == status);
            return await query.OrderByDescending(o => o.CreatedAt)
                .Select(o => new OrderSummaryDto(o.Id, o.OrderNumber, o.Status, o.BuyerId, o.SellerId, o.Total, o.Currency,
                    o.Items.Select(i => i.TitleSnapshot).FirstOrDefault(), o.CreatedAt))
                .ToPagedAsync(page, pageSize);
        });

        g.MapGet("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            return await Get(db, id) is { } o && (o.BuyerId == me || o.SellerId == me) ? Results.Ok(o) : Results.NotFound();
        });

        // Unpaid orders only; cancelling a paid order needs a Stripe refund.
        g.MapPost("/orders/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var order = await Load(db, id);
            if (order is null || (order.BuyerId != me && order.SellerId != me)) return Results.NotFound();
            if (order.Status != OrderStatus.PendingPayment)
                throw new DomainException("Only unpaid orders can be cancelled here.");

            order.Cancel();
            order.Payments.Where(p => p.Status is PaymentStatus.Pending or PaymentStatus.RequiresAction).ToList().ForEach(p => p.Cancel());
            order.Shipments.Where(s => s.Status == ShipmentStatus.Pending).ToList().ForEach(s => s.Cancel());
            foreach (var listing in await ListingsOf(db, order))
                if (listing.Status == ListingStatus.Reserved) listing.Release();

            await db.SaveChangesAsync();
            return Results.Ok(await Get(db, id));
        });

        g.MapPost("/orders/{id:guid}/ship", async (Guid id, ShipOrderRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var order = await Load(db, id);
            if (order is null || order.SellerId != user.UserId()) return Results.NotFound();
            var shipment = order.Shipments.FirstOrDefault(s => s.Status is ShipmentStatus.Pending or ShipmentStatus.LabelCreated)
                ?? throw new DomainException("This order has no pending shipment.");

            if (shipment.Status == ShipmentStatus.Pending)
                shipment.MarkLabelCreated(req.Carrier, req.ServiceLevel, req.TrackingNumber, req.LabelUrl);
            shipment.MarkShipped();
            order.MarkShipped();

            await db.SaveChangesAsync();
            return Results.Ok(await Get(db, id));
        });

        // Buyer confirms they received the item (shipped or picked up).
        g.MapPost("/orders/{id:guid}/confirm-receipt", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var order = await Load(db, id);
            if (order is null || order.BuyerId != user.UserId()) return Results.NotFound();

            if (order.Status == OrderStatus.Shipped)
            {
                order.Shipments.Where(s => s.Status is ShipmentStatus.Shipped or ShipmentStatus.InTransit).ToList()
                    .ForEach(s => s.MarkDelivered());
                order.MarkDelivered();
            }
            else if (order.Shipments.Any(s => s.Status != ShipmentStatus.Cancelled))
                throw new DomainException("This order has not shipped yet.");
            order.Complete();

            await db.SaveChangesAsync();
            return Results.Ok(await Get(db, id));
        });
    }

    internal static Task<Order?> Load(AppDbContext db, Guid id) =>
        db.Orders.Include(o => o.Items).Include(o => o.Payments).Include(o => o.Shipments).FirstOrDefaultAsync(o => o.Id == id);

    internal static Task<List<Listing>> ListingsOf(AppDbContext db, Order order)
    {
        var listingIds = order.Items.Select(i => i.ListingId).ToList();
        return db.Listings.Where(l => listingIds.Contains(l.Id)).ToListAsync();
    }

    internal static Task<bool> HasOpenOrder(AppDbContext db, Guid listingId) =>
        db.OrderItems.AnyAsync(i => i.ListingId == listingId && db.Orders.Any(o => o.Id == i.OrderId &&
            o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded));

    private static string NewOrderNumber() =>
        $"BX-{DateTime.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetHexString(6)}";

    internal static Task<OrderDto?> Get(AppDbContext db, Guid id) =>
        db.Orders.AsNoTracking().Where(o => o.Id == id)
            .Select(o => new OrderDto(o.Id, o.OrderNumber, o.Status, o.BuyerId, o.SellerId, o.OrganizationId,
                o.Subtotal, o.ShippingAmount, o.PlatformFee, o.TaxAmount, o.Total, o.Currency,
                o.Items.Select(i => new OrderItemDto(i.ListingId, i.TitleSnapshot, i.PriceSnapshot, i.Quantity)),
                o.Payments.OrderBy(p => p.CreatedAt).Select(p => new PaymentDto(p.Id, p.Amount, p.Currency, p.Status, p.CreatedAt)),
                o.Shipments.Select(s => new ShipmentDto(s.Id, s.Status, s.Carrier, s.ServiceLevel, s.TrackingNumber,
                    s.RecipientName, s.AddressLine1, s.AddressLine2, s.City, s.State, s.PostalCode, s.CountryCode,
                    s.ShippedAt, s.DeliveredAt)),
                o.CreatedAt, o.PaidAt, o.CompletedAt, o.CancelledAt))
            .FirstOrDefaultAsync();
}
