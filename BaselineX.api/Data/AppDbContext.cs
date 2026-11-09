using BaselineX.Api.Common;
using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Commerce;
using BaselineX.Api.Features.Identity;
using BaselineX.Api.Features.Marketplace;
using BaselineX.Api.Features.Messaging;
using BaselineX.Api.Features.Organizations;
using BaselineX.Api.Features.Play;
using BaselineX.Api.Features.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BaselineX.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // Identity
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    // Catalog
    public DbSet<Sport> Sports => Set<Sport>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Product> Products => Set<Product>();

    // Organizations
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<CoachProfile> CoachProfiles => Set<CoachProfile>();
    public DbSet<CoachSport> CoachSports => Set<CoachSport>();

    // Marketplace
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<ListingImage> ListingImages => Set<ListingImage>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Offer> Offers => Set<Offer>();

    // Commerce
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<StripeWebhookEvent> StripeWebhookEvents => Set<StripeWebhookEvent>();

    // Play
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ActivitySession> ActivitySessions => Set<ActivitySession>();
    public DbSet<Booking> Bookings => Set<Booking>();

    // Messaging
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();
    public DbSet<Message> Messages => Set<Message>();

    // Reviews
    public DbSet<Review> Reviews => Set<Review>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // User FKs (no navigation to UserProfile; it lives at the edge of the model)
        b.Entity<OrganizationMember>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<CoachProfile>().HasOne<UserProfile>().WithOne().HasForeignKey<CoachProfile>(x => x.UserId);
        b.Entity<Listing>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.SellerUserId);
        b.Entity<Offer>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.BuyerId);
        b.Entity<Offer>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.SellerId);
        b.Entity<Order>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.BuyerId);
        b.Entity<Order>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.SellerId);
        b.Entity<Order>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
        b.Entity<Activity>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.HostUserId);
        b.Entity<Booking>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<ConversationParticipant>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId);
        b.Entity<Message>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.SenderId);
        b.Entity<Review>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.ReviewerId);

        b.Entity<CoachProfile>().HasMany(x => x.Sports).WithOne().HasForeignKey(x => x.CoachProfileId);

        // The only cascading deletes in the ERD; every other FK is Restrict (below).
        b.Entity<Listing>().HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Favorite>().HasOne<UserProfile>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Favorite>().HasOne(x => x.Listing).WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Order>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Conversation>().HasMany(x => x.Participants).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Conversation>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);

        // Check constraints (raw SQL uses snake_case column names)
        b.Entity<Activity>().ToTable(t => t.HasCheckConstraint(
            "ck_activities_host_or_organization", "host_user_id IS NOT NULL OR organization_id IS NOT NULL"));
        b.Entity<ActivitySession>().ToTable(t =>
        {
            t.HasCheckConstraint("ck_activity_sessions_booked_count", "booked_count >= 0 AND booked_count <= capacity");
            t.HasCheckConstraint("ck_activity_sessions_time_range", "end_time_utc > start_time_utc");
        });
        b.Entity<Review>().ToTable(t => t.HasCheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5"));

        ApplyModelWideRules(b);
    }

    private static void ApplyModelWideRules(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties().ToList())
            {
                // Enums are varchar(30), not native Postgres enums.
                if (p.ClrType.IsEnum)
                    b.Entity(entity.ClrType).Property(p.Name).HasConversion<string>().HasMaxLength(30);

                if (p.Name == "Currency")
                    b.Entity(entity.ClrType).Property(p.Name).HasColumnType("char(3)");

                // Optimistic concurrency via Postgres xmin.
                if (p.Name == "Version" && p.ClrType == typeof(uint))
                    b.Entity(entity.ClrType).Property(p.Name).IsRowVersion();
            }

            foreach (var fk in entity.GetForeignKeys())
                if (((IConventionForeignKey)fk).GetDeleteBehaviorConfigurationSource() != ConfigurationSource.Explicit)
                    fk.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added && entry.Entity is IHasCreatedAt created && created.CreatedAt == default)
                created.CreatedAt = now;

            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is IHasTimestamps updated)
                updated.UpdatedAt = now;
        }
    }
}
