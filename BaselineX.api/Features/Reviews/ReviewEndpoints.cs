using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Commerce;
using BaselineX.Api.Features.Play;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Reviews;

public record ReviewDto(Guid Id, Guid ReviewerId, string ReviewerName, string? ReviewerAvatarObjectKey,
    ReviewSubjectType SubjectType, Guid SubjectId, int Rating, string? Comment, DateTime CreatedAt);

public record ReviewSummaryDto(int Count, double? AverageRating);

// SubjectId is a user id (Seller), coach profile id (Coach), organization id or activity id.
public record CreateReviewRequest(ReviewSubjectType SubjectType, Guid SubjectId, [Range(1, 5)] int Rating,
    [MaxLength(5000)] string? Comment);

public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/reviews").WithTags("Reviews");

        g.MapGet("/", async (AppDbContext db, ReviewSubjectType subjectType, Guid subjectId, int? page, int? pageSize) =>
            await db.Reviews.AsNoTracking().Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId)
                .OrderByDescending(r => r.CreatedAt)
                .Join(db.UserProfiles, r => r.ReviewerId, u => u.Id, (r, u) => new ReviewDto(r.Id, r.ReviewerId,
                    u.DisplayName, u.AvatarObjectKey, r.SubjectType, r.SubjectId, r.Rating, r.Comment, r.CreatedAt))
                .ToPagedAsync(page, pageSize));

        g.MapGet("/summary", async (AppDbContext db, ReviewSubjectType subjectType, Guid subjectId) =>
        {
            var reviews = db.Reviews.Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId);
            return new ReviewSummaryDto(await reviews.CountAsync(), await reviews.AverageAsync(r => (double?)r.Rating));
        });

        g.MapPost("/", async (CreateReviewRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (await CheckEligibility(db, me, req.SubjectType, req.SubjectId) is { } error) return Results.BadRequest(error);

            var review = new Review
            {
                ReviewerId = me, SubjectType = req.SubjectType, SubjectId = req.SubjectId,
                Rating = req.Rating, Comment = req.Comment,
            };
            db.Reviews.Add(review);
            await db.SaveChangesAsync(); // unique (reviewer, subject) -> 409 on a second review
            return Results.Created($"/reviews/{review.Id}", new { review.Id });
        }).RequireAuthorization();

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var rows = await db.Reviews.Where(r => r.Id == id && r.ReviewerId == me).ExecuteDeleteAsync();
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization();
    }

    // Polymorphic subject: no FK, so check it exists and that the reviewer finished a deal with it.
    private static async Task<string?> CheckEligibility(AppDbContext db, Guid me, ReviewSubjectType type, Guid subjectId)
    {
        var completedBookings = db.Bookings.Where(b => b.UserId == me && b.Status == BookingStatus.Completed);
        var completedOrders = db.Orders.Where(o => o.BuyerId == me && o.Status == OrderStatus.Completed);

        switch (type)
        {
            case ReviewSubjectType.Seller:
                if (subjectId == me) return "You cannot review yourself.";
                if (!await db.UserProfiles.AnyAsync(u => u.Id == subjectId)) return "Unknown seller.";
                return await completedOrders.AnyAsync(o => o.SellerId == subjectId)
                    ? null : "You can review a seller after a completed order.";

            case ReviewSubjectType.Coach:
                var coachUserId = await db.CoachProfiles.Where(c => c.Id == subjectId).Select(c => (Guid?)c.UserId).FirstOrDefaultAsync();
                if (coachUserId is null) return "Unknown coach.";
                if (coachUserId == me) return "You cannot review yourself.";
                return await completedBookings.AnyAsync(b => b.ActivitySession.Activity.HostUserId == coachUserId)
                    ? null : "You can review a coach after a completed session with them.";

            case ReviewSubjectType.Organization:
                if (!await db.Organizations.AnyAsync(o => o.Id == subjectId)) return "Unknown organization.";
                return await completedOrders.AnyAsync(o => o.OrganizationId == subjectId) ||
                       await completedBookings.AnyAsync(b => b.ActivitySession.Activity.OrganizationId == subjectId)
                    ? null : "You can review an organization after a completed order or session with it.";

            case ReviewSubjectType.Activity:
                if (!await db.Activities.AnyAsync(a => a.Id == subjectId)) return "Unknown activity.";
                return await completedBookings.AnyAsync(b => b.ActivitySession.ActivityId == subjectId)
                    ? null : "You can review an activity after a completed session.";

            default:
                return "Unknown subject type.";
        }
    }
}
