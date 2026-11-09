using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Identity;

public record MyProfileDto(Guid Id, string DisplayName, string? FirstName, string? LastName, string? AvatarObjectKey,
    string? Bio, string? City, string? State, string? Neighborhood, StripeOnboardingStatus StripeOnboardingStatus,
    DateTime CreatedAt);

public record PublicProfileDto(Guid Id, string DisplayName, string? AvatarObjectKey, string? Bio, string? City, string? State,
    DateTime CreatedAt);

public record UpdateProfileRequest(
    [Required, MaxLength(120)] string DisplayName,
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [MaxLength(500)] string? AvatarObjectKey,
    string? Bio,
    [MaxLength(120)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(120)] string? Neighborhood);

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/me").WithTags("Profile").RequireAuthorization();

        me.MapGet("/", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.UserId();
            return await db.UserProfiles.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new MyProfileDto(u.Id, u.DisplayName, u.FirstName, u.LastName, u.AvatarObjectKey,
                    u.Bio, u.City, u.State, u.Neighborhood, u.StripeOnboardingStatus, u.CreatedAt))
                .FirstAsync();
        });

        me.MapPut("/", async (UpdateProfileRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.UserId();
            var u = await db.UserProfiles.FirstAsync(x => x.Id == userId);
            (u.DisplayName, u.FirstName, u.LastName, u.AvatarObjectKey, u.Bio, u.City, u.State, u.Neighborhood) =
                (req.DisplayName, req.FirstName, req.LastName, req.AvatarObjectKey, req.Bio, req.City, req.State, req.Neighborhood);
            await db.SaveChangesAsync();
            return TypedResults.NoContent();
        });

        app.MapGet("/users/{id:guid}", async (Guid id, AppDbContext db) =>
                await db.UserProfiles.AsNoTracking().Where(u => u.Id == id)
                    .Select(u => new PublicProfileDto(u.Id, u.DisplayName, u.AvatarObjectKey, u.Bio, u.City, u.State, u.CreatedAt))
                    .FirstOrDefaultAsync() is { } p ? Results.Ok(p) : Results.NotFound())
            .WithTags("Profile");
    }
}
