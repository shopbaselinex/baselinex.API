using System.ComponentModel.DataAnnotations;
using BaselineX.Api.Common;
using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Organizations;

public enum OrganizationType { TennisClub, PickleballClub, ProShop, Academy }
public enum OrganizationRole { Owner, Administrator, Coach, Staff }

[Index(nameof(Slug), IsUnique = true)]
[Index(nameof(Type))]
[Index(nameof(City), nameof(State))]
[Index(nameof(IsVerified))]
[Index(nameof(StripeAccountId), IsUnique = true)]
public class Organization : IHasTimestamps
{
    public Guid Id { get; set; }
    [MaxLength(180)] public string Name { get; set; } = "";
    [MaxLength(180)] public string Slug { get; set; } = "";
    public OrganizationType Type { get; set; }
    public string? Description { get; set; }
    [MaxLength(500)] public string? LogoObjectKey { get; set; }
    [MaxLength(500)] public string? CoverObjectKey { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(80)] public string? State { get; set; }
    [MaxLength(120)] public string? Neighborhood { get; set; }
    [MaxLength(500)] public string? Website { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }
    public bool IsVerified { get; set; }
    [MaxLength(255)] public string? StripeAccountId { get; private set; } // Stripe Connect, for club payouts
    public StripeOnboardingStatus StripeOnboardingStatus { get; private set; } = StripeOnboardingStatus.NotStarted;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OrganizationMember> Members { get; set; } = [];

    public void ConnectStripeAccount(string accountId)
    {
        StripeAccountId = accountId;
        StripeOnboardingStatus = StripeOnboardingStatus.Pending;
    }

    public void UpdateStripeOnboarding(StripeOnboardingStatus status)
    {
        if (StripeAccountId is null) throw new DomainException("No Stripe account connected.");
        StripeOnboardingStatus = status;
    }
}

[Index(nameof(OrganizationId), nameof(UserId), IsUnique = true)]
[Index(nameof(UserId))]
public class OrganizationMember : IHasCreatedAt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Coach payouts use UserProfile.StripeAccountId.
[Index(nameof(UserId), IsUnique = true)]
public class CoachProfile : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Bio { get; set; }
    public int? YearsExperience { get; set; }
    public decimal? PrivateLessonRate { get; set; }
    public string Currency { get; set; } = "USD";
    public bool IsVerified { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<CoachSport> Sports { get; set; } = [];
}

[PrimaryKey(nameof(CoachProfileId), nameof(SportId))]
public class CoachSport
{
    public Guid CoachProfileId { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
}
