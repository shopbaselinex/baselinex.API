using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Organizations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Identity;

public record MyProfileDto(Guid Id, string DisplayName, string? FirstName, string? LastName, string? AvatarObjectKey,
    string? State, string? Country, Gender? Gender, List<ProfileRole> Roles,
    bool HasCoachProfile, bool HasBusiness, bool ProfileComplete, StripeOnboardingStatus StripeOnboardingStatus, DateTime CreatedAt);

public record PublicProfileDto(Guid Id, string DisplayName, string? AvatarObjectKey, string? State, DateTime CreatedAt);

public record UpdateProfileRequest(
    [Required, MaxLength(120)] string DisplayName,
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [MaxLength(500)] string? AvatarObjectKey,
    [RegularExpression("^[A-Z]{2}$", ErrorMessage = "Use a 2-letter state code, e.g. CA.")] string? State,
    // We only serve the US for now. To open another country, allow its code here.
    [RegularExpression("^US$", ErrorMessage = "BaselineX is only available in the United States for now.")] string? Country,
    [EnumDataType(typeof(Gender))] Gender? Gender,
    List<ProfileRole>? Roles);

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/me").WithTags("Profile").RequireAuthorization();

        me.MapGet("/", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.UserId();
            return await db.UserProfiles.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new
                {
                    u,
                    HasCoachProfile = db.CoachProfiles.Any(c => c.UserId == u.Id),
                    HasBusiness = db.OrganizationMembers.Any(m => m.UserId == u.Id && OrgAccess.Managers.Contains(m.Role)),
                })
                .Select(x => new MyProfileDto(x.u.Id, x.u.DisplayName, x.u.FirstName, x.u.LastName, x.u.AvatarObjectKey,
                    x.u.State, x.u.Country, x.u.Gender, x.u.Roles, x.HasCoachProfile, x.HasBusiness,
                    // Onboarding is done when these are filled. The frontend's /onboard steps ask for exactly these:
                    // details, at least one role, and the coach profile / business that the chosen roles need.
                    x.u.FirstName != null && x.u.LastName != null && x.u.Country != null && x.u.State != null
                        && x.u.Gender != null && x.u.Roles.Count > 0
                        && (!x.u.Roles.Contains(ProfileRole.Coach) || x.HasCoachProfile)
                        && (!x.u.Roles.Contains(ProfileRole.Business) || x.HasBusiness),
                    x.u.StripeOnboardingStatus, x.u.CreatedAt))
                .FirstAsync();
        });

        me.MapPut("/", async Task<Results<NoContent, ValidationProblem>> (UpdateProfileRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            // JSON may carry a role as a number (e.g. 9); only named roles are allowed.
            if (req.Roles?.Any(r => !Enum.IsDefined(r)) == true)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    { ["Roles"] = ["Unknown role. Use ShopAndPlay, Coach or Business."] });

            var userId = user.UserId();
            var u = await db.UserProfiles.FirstAsync(x => x.Id == userId);
            // Blank strings are stored as null, so "filled in" checks stay simple.
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            u.DisplayName = req.DisplayName.Trim();
            (u.FirstName, u.LastName, u.AvatarObjectKey) = (Clean(req.FirstName), Clean(req.LastName), Clean(req.AvatarObjectKey));
            (u.State, u.Country, u.Gender) = (req.State, req.Country, req.Gender);
            u.Roles = req.Roles?.Distinct().Order().ToList() ?? [];
            await db.SaveChangesAsync();
            return TypedResults.NoContent();
        });

        app.MapGet("/users/{id:guid}", async (Guid id, AppDbContext db) =>
                await db.UserProfiles.AsNoTracking().Where(u => u.Id == id)
                    .Select(u => new PublicProfileDto(u.Id, u.DisplayName, u.AvatarObjectKey, u.State, u.CreatedAt))
                    .FirstOrDefaultAsync() is { } p ? Results.Ok(p) : Results.NotFound())
            .WithTags("Profile");
    }
}
