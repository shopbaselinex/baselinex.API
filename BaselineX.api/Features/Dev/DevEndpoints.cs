using BaselineX.Api.Data;
using BaselineX.Api.Features.Commerce;
using BaselineX.Api.Features.Play;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Dev;

// Development only: stands in for the Stripe webhook until payments are wired up.
public static class DevEndpoints
{
    public static void MapDevEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/dev").WithTags("Dev (simulated payments)");

        g.MapPost("/orders/{id:guid}/mark-paid", async (Guid id, AppDbContext db) =>
        {
            var order = await OrderEndpoints.Load(db, id);
            if (order is null) return Results.NotFound();

            order.MarkPaid();
            order.Payments.Where(p => p.Status is PaymentStatus.Pending or PaymentStatus.RequiresAction).ToList()
                .ForEach(p => p.MarkSucceeded("dev_simulated"));
            foreach (var listing in await OrderEndpoints.ListingsOf(db, order)) listing.MarkSold();
            if (order.Shipments.Any(s => s.Status == ShipmentStatus.Pending)) order.MarkAwaitingShipment();

            await db.SaveChangesAsync();
            return Results.Ok(await OrderEndpoints.Get(db, id));
        });

        g.MapPost("/bookings/{id:guid}/mark-paid", async (Guid id, AppDbContext db) =>
        {
            var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id);
            if (booking is null) return Results.NotFound();
            booking.Confirm();
            await db.SaveChangesAsync();
            return Results.Ok(await PlayEndpoints.GetBooking(db, id));
        });
    }
}
