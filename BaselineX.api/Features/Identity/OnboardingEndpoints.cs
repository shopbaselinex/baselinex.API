using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Organizations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Identity;

/// <summary>The answers from the onboarding steps after "Your details".
/// Coach is needed when Roles has Coach, Business when Roles has Business (unless they already exist).</summary>
public record FinishOnboardingRequest(
    [Required, MinLength(1, ErrorMessage = "Pick at least one.")] List<ProfileRole> Roles,
    SaveCoachProfileRequest? Coach,
    SaveOrganizationRequest? Business);

public static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // The last onboarding step. Saves the roles, and the coach profile and business they need, in one
        // transaction: all of it is saved or none of it. Safe to retry: an existing coach profile or business
        // is kept, so a retry never creates a second business.
        app.MapPost("/me/onboarding", async Task<Results<NoContent, ValidationProblem>> (
            FinishOnboardingRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            // JSON may carry a role as a number (e.g. 9); only named roles are allowed.
            if (req.Roles.Any(r => !Enum.IsDefined(r)))
                return Invalid("Roles", "Unknown role. Use ShopAndPlay, Coach or Business.");

            var userId = user.UserId();
            var roles = req.Roles.Distinct().Order().ToList();

            if (roles.Contains(ProfileRole.Coach) && !await db.CoachProfiles.AnyAsync(c => c.UserId == userId))
            {
                if (req.Coach is null) return Invalid("Coach", "Add your coach details.");
                await OrganizationEndpoints.UpsertCoachProfileAsync(db, userId, req.Coach);
            }

            if (roles.Contains(ProfileRole.Business) && !await db.ManagesAnyOrganizationAsync(userId))
            {
                if (req.Business is null) return Invalid("Business", "Add your business details.");
                await OrganizationEndpoints.AddOrganizationAsync(db, userId, req.Business);
            }

            var profile = await db.UserProfiles.FirstAsync(u => u.Id == userId);
            profile.Roles = roles;
            await db.SaveChangesAsync(); // one transaction for everything above
            return TypedResults.NoContent();
        }).WithTags("Profile").RequireAuthorization();
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
