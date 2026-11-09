using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Identity;
using BaselineX.Api.Features.Marketplace;

namespace BaselineX.Api.Data;

public static class Seed
{
    // Stand-in for a Supabase user until auth is wired up.
    public static readonly Guid DevUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void Run(AppDbContext db)
    {
        if (db.Sports.Any()) return;

        var tennis = new Sport { Name = "Tennis", Slug = "tennis" };
        var pickleball = new Sport { Name = "Pickleball", Slug = "pickleball" };
        var padel = new Sport { Name = "Padel", Slug = "padel" };
        var squash = new Sport { Name = "Squash", Slug = "squash" };

        var racquets = new Category { Name = "Racquets", Slug = "racquets" };
        var paddles = new Category { Name = "Paddles", Slug = "paddles" };
        var shoes = new Category { Name = "Shoes", Slug = "shoes" };
        var bags = new Category { Name = "Bags", Slug = "bags" };

        var wilson = new Brand { Name = "Wilson", Slug = "wilson" };

        var blade = new Product
        {
            Name = "Wilson Blade 98 V9", Model = "Blade 98", ModelYear = 2024, RetailPrice = 269.00m,
            Sport = tennis, Category = racquets, Brand = wilson,
            Specifications = """{"weight_grams": 305, "head_size_sq_in": 98}""",
        };

        var seller = new UserProfile { Id = DevUserId, DisplayName = "Dev Seller", City = "Austin", State = "TX" };

        var listing = new Listing
        {
            SellerUserId = seller.Id, Product = blade, Sport = tennis, Category = racquets, Brand = wilson,
            Title = "Used Blade 98 V9, 4 3/8", Description = "Lightly used, fresh strings.",
            Price = 159.00m, Condition = ListingCondition.Good,
            ShippingAvailable = true, LocalPickupAvailable = true, PickupCity = "Austin",
        };
        listing.Publish();

        db.AddRange(tennis, pickleball, padel, squash, racquets, paddles, shoes, bags, wilson, blade, seller, listing);
        db.SaveChanges();
    }
}
