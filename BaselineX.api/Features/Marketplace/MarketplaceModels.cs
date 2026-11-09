using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BaselineX.Api.Common;
using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Marketplace;

public enum ListingCondition { New, LikeNew, Excellent, Good, Fair }
public enum ListingStatus { Draft, Active, Reserved, Sold, Removed }
public enum SellerInventoryType { SelfService, ManagedResale, BaselinexOwned }
public enum OfferStatus { Pending, Accepted, Declined, Countered, Expired, Withdrawn }

// A specific item for sale. If ProductId is set, sport/category/brand must match the product (enforce in handler).
[Index(nameof(ProductId))]
[Index(nameof(OrganizationId))]
[Index(nameof(Price))]
[Index(nameof(Status), nameof(CreatedAt))]
[Index(nameof(SportId), nameof(CategoryId), nameof(Status))]
[Index(nameof(BrandId), nameof(Status))]
[Index(nameof(SellerUserId), nameof(Status))]
[Index(nameof(OrganizationId), nameof(Status))]
[Index(nameof(LocalPickupAvailable), nameof(PickupCity))]
public class Listing : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid SellerUserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid? ProductId { get; set; } // null = custom/manual listing
    public Product? Product { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int BrandId { get; set; }
    public Brand Brand { get; set; } = null!;

    [MaxLength(180)] public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";

    public ListingCondition Condition { get; set; }
    public ListingStatus Status { get; private set; } = ListingStatus.Draft;

    public bool ShippingAvailable { get; set; }
    public bool LocalPickupAvailable { get; set; }
    [MaxLength(120)] public string? PickupCity { get; set; }
    [MaxLength(120)] public string? PickupNeighborhood { get; set; }

    public bool IsDemo { get; set; }
    public bool AcceptsOffers { get; set; } = true;
    public SellerInventoryType InventoryType { get; set; } = SellerInventoryType.SelfService;
    [Column(TypeName = "jsonb")] public string? ItemSpecifications { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; private set; }
    public DateTime? SoldAt { get; private set; }

    public uint Version { get; set; } // concurrency (Postgres xmin)
    public List<ListingImage> Images { get; set; } = [];

    public void Publish()
    {
        Status = Transition.To(Status, ListingStatus.Active, ListingStatus.Draft);
        PublishedAt = DateTime.UtcNow;
    }

    public void Reserve() =>
        Status = Transition.To(Status, ListingStatus.Reserved, ListingStatus.Active);

    // Back on the market after a cancelled order or an accepted offer that was never paid.
    public void Release() =>
        Status = Transition.To(Status, ListingStatus.Active, ListingStatus.Reserved);

    public void MarkSold()
    {
        Status = Transition.To(Status, ListingStatus.Sold, ListingStatus.Active, ListingStatus.Reserved);
        SoldAt = DateTime.UtcNow;
    }

    public void Remove() =>
        Status = Transition.To(Status, ListingStatus.Removed,
            ListingStatus.Draft, ListingStatus.Active, ListingStatus.Reserved, ListingStatus.Removed);
}

// Bytes live in R2; upload via presigned URL directly from the client.
[Index(nameof(ObjectKey), IsUnique = true)]
[Index(nameof(ListingId), nameof(SortOrder))]
public class ListingImage : IHasCreatedAt
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    [MaxLength(500)] public string ObjectKey { get; set; } = ""; // R2 key, not the file
    [MaxLength(120)] public string ContentType { get; set; } = "";
    public long? SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
}

[PrimaryKey(nameof(UserId), nameof(ListingId))]
[Index(nameof(ListingId))]
public class Favorite : IHasCreatedAt
{
    public Guid UserId { get; set; }
    public Guid ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

// Only the listing owner can accept. Expired offers cannot be accepted. Buyer != seller.
[Index(nameof(BuyerId))]
[Index(nameof(SellerId))]
[Index(nameof(ExpiresAt))]
[Index(nameof(ListingId), nameof(Status))]
public class Offer : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public Guid BuyerId { get; set; }
    public Guid SellerId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public OfferStatus Status { get; private set; } = OfferStatus.Pending;
    public Guid? ParentOfferId { get; set; } // set on counter-offers
    public Offer? ParentOffer { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }

    public static Offer Create(Listing listing, Guid buyerId, decimal amount, DateTime expiresAt)
    {
        if (buyerId == listing.SellerUserId) throw new DomainException("Sellers cannot make offers on their own listing.");
        if (listing.Status != ListingStatus.Active || !listing.AcceptsOffers) throw new DomainException("Listing is not accepting offers.");
        if (amount <= 0) throw new DomainException("Offer amount must be positive.");

        return new Offer
        {
            ListingId = listing.Id, BuyerId = buyerId, SellerId = listing.SellerUserId,
            Amount = amount, Currency = listing.Currency, ExpiresAt = expiresAt,
        };
    }

    public void Accept(Guid actingUserId, DateTime nowUtc)
    {
        EnsureActor(actingUserId, SellerId);
        if (nowUtc >= ExpiresAt) throw new DomainException("Offer has expired.");
        Status = Transition.To(Status, OfferStatus.Accepted, OfferStatus.Pending);
    }

    public void Decline(Guid actingUserId)
    {
        EnsureActor(actingUserId, SellerId);
        Status = Transition.To(Status, OfferStatus.Declined, OfferStatus.Pending);
    }

    public Offer Counter(Guid actingUserId, decimal amount, DateTime expiresAt)
    {
        EnsureActor(actingUserId, SellerId);
        Status = Transition.To(Status, OfferStatus.Countered, OfferStatus.Pending);
        return new Offer
        {
            ListingId = ListingId, BuyerId = BuyerId, SellerId = SellerId,
            Amount = amount, Currency = Currency, ExpiresAt = expiresAt, ParentOfferId = Id,
        };
    }

    public void Withdraw(Guid actingUserId)
    {
        EnsureActor(actingUserId, BuyerId);
        Status = Transition.To(Status, OfferStatus.Withdrawn, OfferStatus.Pending);
    }

    public void Expire(DateTime nowUtc)
    {
        if (nowUtc < ExpiresAt) throw new DomainException("Offer has not expired yet.");
        Status = Transition.To(Status, OfferStatus.Expired, OfferStatus.Pending);
    }

    private static void EnsureActor(Guid actingUserId, Guid requiredUserId)
    {
        if (actingUserId != requiredUserId) throw new DomainException("Not allowed to act on this offer.");
    }
}
