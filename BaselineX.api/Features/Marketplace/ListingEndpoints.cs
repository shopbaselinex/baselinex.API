using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Commerce;
using BaselineX.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Validation;

namespace BaselineX.Api.Features.Marketplace;

public record ListingSummaryDto(Guid Id, string Title, decimal Price, string Currency, ListingCondition Condition,
    ListingStatus Status, string Sport, string Category, string Brand, string? Product, string? PrimaryImageObjectKey,
    bool ShippingAvailable, bool LocalPickupAvailable, string? PickupCity, DateTime CreatedAt);

public record SellerDto(Guid Id, string DisplayName, string? AvatarObjectKey);

public record ListingImageDto(Guid Id, string ObjectKey, string ContentType, int? Width, int? Height, int SortOrder, bool IsPrimary);

public record ListingDto(Guid Id, string Title, string Description, decimal Price, string Currency,
    ListingCondition Condition, ListingStatus Status, SellerInventoryType InventoryType,
    bool ShippingAvailable, bool LocalPickupAvailable, string? PickupCity, string? PickupNeighborhood,
    bool IsDemo, bool AcceptsOffers, JsonElement? ItemSpecifications,
    int SportId, int CategoryId, int BrandId, Guid? ProductId, Guid? OrganizationId,
    SellerDto Seller, IEnumerable<ListingImageDto> Images,
    DateTime CreatedAt, DateTime? PublishedAt, DateTime? SoldAt);

public record SaveListingRequest(
    int SportId,
    int CategoryId,
    int BrandId,
    Guid? ProductId,
    Guid? OrganizationId,
    [Required, MaxLength(180)] string Title,
    [Required] string Description,
    [Range(0.01, 1_000_000)] decimal Price,
    ListingCondition Condition,
    bool ShippingAvailable,
    bool LocalPickupAvailable,
    [MaxLength(120)] string? PickupCity,
    [MaxLength(120)] string? PickupNeighborhood,
    bool IsDemo,
    bool AcceptsOffers,
#pragma warning disable ASP0029 // SkipValidation is marked experimental; the validator otherwise crashes walking JsonElement
    [SkipValidation] JsonElement? ItemSpecifications);
#pragma warning restore ASP0029

// The client uploads bytes to R2 with a presigned URL, then registers the object key here.
public record AddListingImageRequest(
    [Required, MaxLength(500)] string ObjectKey,
    [Required, MaxLength(120)] string ContentType,
    long? SizeBytes,
    int? Width,
    int? Height,
    int SortOrder,
    bool IsPrimary);

public static class ListingEndpoints
{
    private static readonly OrganizationRole[] ListingRoles =
        [OrganizationRole.Owner, OrganizationRole.Administrator, OrganizationRole.Staff];

    public static void MapListingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/listings").WithTags("Listings");

        g.MapGet("/", async (AppDbContext db, int? sportId, int? categoryId, int? brandId, Guid? productId,
            Guid? organizationId, ListingCondition? condition, decimal? minPrice, decimal? maxPrice,
            bool? localPickup, string? city, string? q, string? sort, int? page, int? pageSize) =>
        {
            var query = db.Listings.AsNoTracking().Where(l => l.Status == ListingStatus.Active);
            if (sportId is not null) query = query.Where(l => l.SportId == sportId);
            if (categoryId is not null) query = query.Where(l => l.CategoryId == categoryId);
            if (brandId is not null) query = query.Where(l => l.BrandId == brandId);
            if (productId is not null) query = query.Where(l => l.ProductId == productId);
            if (organizationId is not null) query = query.Where(l => l.OrganizationId == organizationId);
            if (condition is not null) query = query.Where(l => l.Condition == condition);
            if (minPrice is not null) query = query.Where(l => l.Price >= minPrice);
            if (maxPrice is not null) query = query.Where(l => l.Price <= maxPrice);
            if (localPickup == true) query = query.Where(l => l.LocalPickupAvailable);
            if (!string.IsNullOrWhiteSpace(city)) query = query.Where(l => l.PickupCity == city);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(l => EF.Functions.ILike(l.Title, $"%{q}%"));

            query = sort switch
            {
                "price_asc" => query.OrderBy(l => l.Price),
                "price_desc" => query.OrderByDescending(l => l.Price),
                _ => query.OrderByDescending(l => l.CreatedAt),
            };
            return await query.Select(Summary).ToPagedAsync(page, pageSize);
        });

        g.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserIdOrNull();
            var l = await db.Listings.AsNoTracking().Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id);
            if (l is null || (l.Status is ListingStatus.Draft or ListingStatus.Removed && l.SellerUserId != me))
                return Results.NotFound();

            var seller = await db.UserProfiles.AsNoTracking().Where(u => u.Id == l.SellerUserId)
                .Select(u => new SellerDto(u.Id, u.DisplayName, u.AvatarObjectKey)).FirstAsync();
            return Results.Ok(ToDto(l, seller));
        });

        g.MapPost("/", async (SaveListingRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var listing = new Listing { SellerUserId = user.UserId() };
            if (await Validate(req, listing.SellerUserId, db) is { } error) return Results.BadRequest(error);
            Apply(listing, req);
            db.Listings.Add(listing);
            await db.SaveChangesAsync();
            return Results.Created($"/listings/{listing.Id}", new { listing.Id, listing.Status });
        }).RequireAuthorization();

        g.MapPut("/{id:guid}", async (Guid id, SaveListingRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id);
            if (listing is null) return Results.NotFound();
            if (listing.SellerUserId != user.UserId()) return Results.Forbid();
            if (listing.Status is not (ListingStatus.Draft or ListingStatus.Active))
                throw new DomainException($"A {listing.Status} listing cannot be edited.");
            if (await Validate(req, listing.SellerUserId, db) is { } error) return Results.BadRequest(error);
            Apply(listing, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapPost("/{id:guid}/publish", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            OwnerAction(id, user, db, l => l.Publish())).RequireAuthorization();

        g.MapPost("/{id:guid}/remove", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            OwnerAction(id, user, db, l => l.Remove())).RequireAuthorization();

        // Frees a listing reserved by an accepted offer that the buyer never turned into an order.
        g.MapPost("/{id:guid}/release", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            if (await OrderEndpoints.HasOpenOrder(db, id))
                throw new DomainException("Listing has an open order; cancel the order instead.");
            return await OwnerAction(id, user, db, l => l.Release());
        }).RequireAuthorization();

        g.MapPost("/{id:guid}/images", async (Guid id, AddListingImageRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var listing = await db.Listings.Include(l => l.Images).FirstOrDefaultAsync(l => l.Id == id);
            if (listing is null) return Results.NotFound();
            if (listing.SellerUserId != user.UserId()) return Results.Forbid();

            var isPrimary = req.IsPrimary || listing.Images.Count == 0;
            if (isPrimary) listing.Images.ForEach(i => i.IsPrimary = false);
            var image = new ListingImage
            {
                ObjectKey = req.ObjectKey, ContentType = req.ContentType, SizeBytes = req.SizeBytes,
                Width = req.Width, Height = req.Height, SortOrder = req.SortOrder, IsPrimary = isPrimary,
            };
            listing.Images.Add(image);
            await db.SaveChangesAsync();
            return Results.Created($"/listings/{id}/images/{image.Id}", ToDto(image));
        }).RequireAuthorization();

        g.MapDelete("/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId, ClaimsPrincipal user, AppDbContext db) =>
        {
            var listing = await db.Listings.Include(l => l.Images).FirstOrDefaultAsync(l => l.Id == id);
            if (listing is null) return Results.NotFound();
            if (listing.SellerUserId != user.UserId()) return Results.Forbid();
            if (listing.Images.FirstOrDefault(i => i.Id == imageId) is not { } image) return Results.NotFound();

            listing.Images.Remove(image);
            if (image.IsPrimary && listing.Images.OrderBy(i => i.SortOrder).FirstOrDefault() is { } next) next.IsPrimary = true;
            await db.SaveChangesAsync();
            return Results.NoContent(); // TODO: delete the R2 object once storage is wired up
        }).RequireAuthorization();

        g.MapPut("/{id:guid}/favorite", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (!await db.Listings.AnyAsync(l => l.Id == id)) return Results.NotFound();
            if (!await db.Favorites.AnyAsync(f => f.UserId == me && f.ListingId == id))
            {
                db.Favorites.Add(new Favorite { UserId = me, ListingId = id });
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapDelete("/{id:guid}/favorite", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            await db.Favorites.Where(f => f.UserId == me && f.ListingId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/me/listings", async (ClaimsPrincipal user, AppDbContext db, ListingStatus? status, int? page, int? pageSize) =>
        {
            var me = user.UserId();
            var query = db.Listings.AsNoTracking().Where(l => l.SellerUserId == me);
            if (status is not null) query = query.Where(l => l.Status == status);
            return await query.OrderByDescending(l => l.CreatedAt).Select(Summary).ToPagedAsync(page, pageSize);
        }).WithTags("Listings").RequireAuthorization();

        app.MapGet("/me/favorites", async (ClaimsPrincipal user, AppDbContext db, int? page, int? pageSize) =>
        {
            var me = user.UserId();
            return await db.Favorites.AsNoTracking().Where(f => f.UserId == me)
                .OrderByDescending(f => f.CreatedAt).Select(f => f.Listing).Select(Summary)
                .ToPagedAsync(page, pageSize);
        }).WithTags("Listings").RequireAuthorization();
    }

    private static readonly System.Linq.Expressions.Expression<Func<Listing, ListingSummaryDto>> Summary = l =>
        new ListingSummaryDto(l.Id, l.Title, l.Price, l.Currency, l.Condition, l.Status,
            l.Sport.Name, l.Category.Name, l.Brand.Name, l.Product != null ? l.Product.Name : null,
            l.Images.Where(i => i.IsPrimary).Select(i => i.ObjectKey).FirstOrDefault(),
            l.ShippingAvailable, l.LocalPickupAvailable, l.PickupCity, l.CreatedAt);

    private static async Task<IResult> OwnerAction(Guid id, ClaimsPrincipal user, AppDbContext db, Action<Listing> action)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id);
        if (listing is null) return Results.NotFound();
        if (listing.SellerUserId != user.UserId()) return Results.Forbid();
        action(listing);
        await db.SaveChangesAsync();
        return Results.Ok(new { listing.Id, listing.Status });
    }

    private static async Task<string?> Validate(SaveListingRequest req, Guid sellerId, AppDbContext db)
    {
        if (!req.ShippingAvailable && !req.LocalPickupAvailable) return "Enable shipping or local pickup.";
        if (!await db.Sports.AnyAsync(s => s.Id == req.SportId)) return "Unknown sport.";
        if (!await db.Categories.AnyAsync(c => c.Id == req.CategoryId)) return "Unknown category.";
        if (!await db.Brands.AnyAsync(b => b.Id == req.BrandId)) return "Unknown brand.";

        if (req.ProductId is { } productId)
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId);
            if (product is null) return "Unknown product.";
            if (product.SportId != req.SportId || product.CategoryId != req.CategoryId || product.BrandId != req.BrandId)
                return "Sport, category and brand must match the selected product.";
        }

        if (req.OrganizationId is { } orgId && !await db.HasRoleAsync(orgId, sellerId, ListingRoles))
            return "You cannot list items for this organization.";
        return null;
    }

    private static void Apply(Listing l, SaveListingRequest r)
    {
        (l.SportId, l.CategoryId, l.BrandId, l.ProductId, l.OrganizationId) =
            (r.SportId, r.CategoryId, r.BrandId, r.ProductId, r.OrganizationId);
        (l.Title, l.Description, l.Price, l.Condition) = (r.Title, r.Description, r.Price, r.Condition);
        (l.ShippingAvailable, l.LocalPickupAvailable, l.PickupCity, l.PickupNeighborhood) =
            (r.ShippingAvailable, r.LocalPickupAvailable, r.PickupCity, r.PickupNeighborhood);
        (l.IsDemo, l.AcceptsOffers, l.ItemSpecifications) = (r.IsDemo, r.AcceptsOffers, r.ItemSpecifications.ToJsonString());
    }

    private static ListingImageDto ToDto(ListingImage i) =>
        new(i.Id, i.ObjectKey, i.ContentType, i.Width, i.Height, i.SortOrder, i.IsPrimary);

    private static ListingDto ToDto(Listing l, SellerDto seller) => new(l.Id, l.Title, l.Description, l.Price, l.Currency,
        l.Condition, l.Status, l.InventoryType, l.ShippingAvailable, l.LocalPickupAvailable, l.PickupCity, l.PickupNeighborhood,
        l.IsDemo, l.AcceptsOffers, l.ItemSpecifications.ToJsonElement(),
        l.SportId, l.CategoryId, l.BrandId, l.ProductId, l.OrganizationId,
        seller, l.Images.OrderBy(i => i.SortOrder).Select(ToDto), l.CreatedAt, l.PublishedAt, l.SoldAt);
}
