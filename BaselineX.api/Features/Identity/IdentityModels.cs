using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BaselineX.Api.Common;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Identity;

public enum StripeOnboardingStatus { NotStarted, Pending, Complete, Restricted }
public enum Gender { Female, Male, NonBinary, PreferNotToSay }

/// <summary>What the user said they're here for (the onboarding "How will you use BaselineX?" step).
/// Self-chosen, so it only personalizes the app. It grants nothing: coach and business tools check
/// coach_profiles and organization_members.</summary>
public enum ProfileRole { ShopAndPlay, Coach, Business }

// Passwords and email live in Supabase. Profile is upserted on first API call.
[Index(nameof(StripeAccountId), IsUnique = true)]
public class UserProfile : IHasTimestamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; } // = Supabase JWT "sub"
    [MaxLength(120)] public string DisplayName { get; set; } = "";
    [MaxLength(100)] public string? FirstName { get; set; }
    [MaxLength(100)] public string? LastName { get; set; }
    [MaxLength(500)] public string? AvatarObjectKey { get; set; }
    [MaxLength(80)] public string? State { get; set; } // 2-letter US state code (CA). 80 leaves room for other countries' regions later.
    [Column(TypeName = "char(2)")] public string? Country { get; set; } // ISO 3166-1 alpha-2. Only "US" for now.
    public Gender? Gender { get; set; } // private: never in PublicProfileDto
    public List<ProfileRole> Roles { get; set; } = []; // empty until onboarding asks
    [MaxLength(255)] public string? StripeAccountId { get; private set; } // Stripe Connect, for payouts
    public StripeOnboardingStatus StripeOnboardingStatus { get; private set; } = StripeOnboardingStatus.NotStarted;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

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
