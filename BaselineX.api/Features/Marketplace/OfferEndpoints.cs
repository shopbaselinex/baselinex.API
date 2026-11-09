using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Marketplace;

public record OfferDto(Guid Id, Guid ListingId, string ListingTitle, Guid BuyerId, Guid SellerId, decimal Amount,
    string Currency, OfferStatus Status, Guid? ParentOfferId, DateTime ExpiresAt, DateTime CreatedAt);

public record MakeOfferRequest([Range(0.01, 1_000_000)] decimal Amount, [Range(1, 168)] int? ExpiresInHours);

public static class OfferEndpoints
{
    private const int DefaultExpiryHours = 48;

    public static void MapOfferEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Offers").RequireAuthorization();

        g.MapPost("/listings/{listingId:guid}/offers", async (Guid listingId, MakeOfferRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId);
            if (listing is null) return Results.NotFound();

            var offer = Offer.Create(listing, user.UserId(), req.Amount, ExpiresAt(req.ExpiresInHours));
            db.Offers.Add(offer);
            await db.SaveChangesAsync();
            return Results.Created($"/offers/{offer.Id}", await Get(db, offer.Id));
        });

        // role = buyer (offers I made) | seller (offers on my listings)
        g.MapGet("/me/offers", async (ClaimsPrincipal user, AppDbContext db, string? role, OfferStatus? status,
            int? page, int? pageSize) =>
        {
            var me = user.UserId();
            var query = role == "seller"
                ? db.Offers.AsNoTracking().Where(o => o.SellerId == me)
                : db.Offers.AsNoTracking().Where(o => o.BuyerId == me);
            if (status is not null) query = query.Where(o => o.Status == status);
            return await query.OrderByDescending(o => o.CreatedAt).Select(Dto).ToPagedAsync(page, pageSize);
        });

        g.MapGet("/offers/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            return await Get(db, id) is { } o && (o.BuyerId == me || o.SellerId == me) ? Results.Ok(o) : Results.NotFound();
        });

        // Accepting reserves the listing and declines the other pending offers on it.
        g.MapPost("/offers/{id:guid}/accept", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var offer = await db.Offers.Include(o => o.Listing).FirstOrDefaultAsync(o => o.Id == id);
            if (offer is null) return Results.NotFound();

            offer.Accept(me, DateTime.UtcNow);
            offer.Listing.Reserve();
            var others = await db.Offers
                .Where(o => o.ListingId == offer.ListingId && o.Id != offer.Id && o.Status == OfferStatus.Pending).ToListAsync();
            others.ForEach(o => o.Decline(me));

            await db.SaveChangesAsync();
            return Results.Ok(await Get(db, id));
        });

        g.MapPost("/offers/{id:guid}/decline", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            Act(id, db, o => o.Decline(user.UserId())));

        g.MapPost("/offers/{id:guid}/withdraw", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            Act(id, db, o => o.Withdraw(user.UserId())));

        g.MapPost("/offers/{id:guid}/counter", async (Guid id, MakeOfferRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == id);
            if (offer is null) return Results.NotFound();

            var counter = offer.Counter(user.UserId(), req.Amount, ExpiresAt(req.ExpiresInHours));
            db.Offers.Add(counter);
            await db.SaveChangesAsync();
            return Results.Created($"/offers/{counter.Id}", await Get(db, counter.Id));
        });
    }

    private static DateTime ExpiresAt(int? hours) => DateTime.UtcNow.AddHours(hours ?? DefaultExpiryHours);

    private static async Task<IResult> Act(Guid id, AppDbContext db, Action<Offer> action)
    {
        var offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == id);
        if (offer is null) return Results.NotFound();
        action(offer);
        await db.SaveChangesAsync();
        return Results.Ok(await Get(db, id));
    }

    private static Task<OfferDto?> Get(AppDbContext db, Guid id) =>
        db.Offers.AsNoTracking().Where(o => o.Id == id).Select(Dto).FirstOrDefaultAsync();

    private static readonly System.Linq.Expressions.Expression<Func<Offer, OfferDto>> Dto = o =>
        new OfferDto(o.Id, o.ListingId, o.Listing.Title, o.BuyerId, o.SellerId, o.Amount, o.Currency, o.Status,
            o.ParentOfferId, o.ExpiresAt, o.CreatedAt);
}
