using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BaselineX.Api.Common;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Identity;

public enum StripeOnboardingStatus { NotStarted, Pending, Complete, Restricted }

// Passwords and email live in Supabase. Profile is upserted on first API call.
[Index(nameof(City), nameof(State))]
[Index(nameof(StripeAccountId), IsUnique = true)]
public class UserProfile : IHasTimestamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; } // = Supabase JWT "sub"
    [MaxLength(120)] public string DisplayName { get; set; } = "";
    [MaxLength(100)] public string? FirstName { get; set; }
    [MaxLength(100)] public string? LastName { get; set; }
    [MaxLength(500)] public string? AvatarObjectKey { get; set; }
    public string? Bio { get; set; }
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(80)] public string? State { get; set; }
    [MaxLength(120)] public string? Neighborhood { get; set; }
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
