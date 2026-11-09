using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Messaging;

public record MessageDto(Guid Id, Guid ConversationId, Guid SenderId, string Body, DateTime CreatedAt);

public record ConversationDto(Guid Id, Guid? ListingId, string? ListingTitle, Guid? ActivityId, string? ActivityTitle,
    IEnumerable<Guid> OtherUserIds, MessageDto? LastMessage, int UnreadCount, DateTime UpdatedAt);

// Opens (or reuses) a 1:1 conversation with RecipientId, optionally about a listing or an activity.
public record StartConversationRequest(Guid RecipientId, Guid? ListingId, Guid? ActivityId, [Required, MaxLength(5000)] string Body);

public record SendMessageRequest([Required, MaxLength(5000)] string Body);

public static class MessagingEndpoints
{
    public static void MapMessagingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Messaging").RequireAuthorization();

        g.MapGet("/me/conversations", async (ClaimsPrincipal user, AppDbContext db, int? page, int? pageSize) =>
        {
            var me = user.UserId();
            return await db.Conversations.AsNoTracking()
                .Where(c => c.Participants.Any(p => p.UserId == me))
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new ConversationDto(c.Id, c.ListingId, c.Listing != null ? c.Listing.Title : null,
                    c.ActivityId, c.Activity != null ? c.Activity.Title : null,
                    c.Participants.Where(p => p.UserId != me).Select(p => p.UserId),
                    c.Messages.OrderByDescending(m => m.CreatedAt)
                        .Select(m => new MessageDto(m.Id, m.ConversationId, m.SenderId, m.Body, m.CreatedAt)).FirstOrDefault(),
                    c.Messages.Count(m => m.SenderId != me && c.Participants.Any(p => p.UserId == me &&
                        (p.LastReadAt == null || m.CreatedAt > p.LastReadAt))),
                    c.UpdatedAt))
                .ToPagedAsync(page, pageSize);
        });

        g.MapPost("/conversations", async (StartConversationRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (req.RecipientId == me) return Results.BadRequest("You cannot message yourself.");
            if (req.ListingId is not null && req.ActivityId is not null) return Results.BadRequest("Pick a listing or an activity, not both.");
            if (!await db.UserProfiles.AnyAsync(u => u.Id == req.RecipientId)) return Results.BadRequest("Unknown recipient.");
            if (req.ListingId is { } listingId && !await db.Listings.AnyAsync(l => l.Id == listingId)) return Results.BadRequest("Unknown listing.");
            if (req.ActivityId is { } activityId && !await db.Activities.AnyAsync(a => a.Id == activityId)) return Results.BadRequest("Unknown activity.");

            var conversation = await db.Conversations
                .Where(c => c.ListingId == req.ListingId && c.ActivityId == req.ActivityId && c.Participants.Count == 2 &&
                            c.Participants.Any(p => p.UserId == me) && c.Participants.Any(p => p.UserId == req.RecipientId))
                .FirstOrDefaultAsync();
            if (conversation is null)
            {
                conversation = new Conversation
                {
                    ListingId = req.ListingId, ActivityId = req.ActivityId,
                    Participants = [new ConversationParticipant { UserId = me }, new ConversationParticipant { UserId = req.RecipientId }],
                };
                db.Conversations.Add(conversation);
            }

            var message = await Send(db, conversation, me, req.Body);
            return Results.Created($"/conversations/{conversation.Id}/messages", ToDto(message));
        });

        // Newest first; pass the oldest CreatedAt you have as "before" to page back.
        g.MapGet("/conversations/{id:guid}/messages", async (Guid id, ClaimsPrincipal user, AppDbContext db,
            DateTimeOffset? before, int? limit) =>
        {
            if (!await IsParticipant(db, id, user.UserId())) return Results.NotFound();
            var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == id);
            if (before is not null) query = query.Where(m => m.CreatedAt < before.Value.UtcDateTime);
            return Results.Ok(await query.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(m => new MessageDto(m.Id, m.ConversationId, m.SenderId, m.Body, m.CreatedAt)).ToListAsync());
        });

        g.MapPost("/conversations/{id:guid}/messages", async (Guid id, SendMessageRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.Participants.Any(p => p.UserId == me));
            if (conversation is null) return Results.NotFound();
            var message = await Send(db, conversation, me, req.Body);
            return Results.Created($"/conversations/{id}/messages", ToDto(message));
        });

        g.MapPost("/conversations/{id:guid}/read", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var now = DateTime.UtcNow;
            var rows = await db.ConversationParticipants.Where(p => p.ConversationId == id && p.UserId == me)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.LastReadAt, now));
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    private static async Task<Message> Send(AppDbContext db, Conversation conversation, Guid senderId, string body)
    {
        var message = new Message { ConversationId = conversation.Id, SenderId = senderId, Body = body.Trim() };
        db.Messages.Add(message);
        if (db.Entry(conversation).State == EntityState.Unchanged)
            db.Entry(conversation).Property(c => c.UpdatedAt).IsModified = true; // bump for inbox ordering

        // Sending implies the sender has read the conversation.
        var sender = conversation.Participants.FirstOrDefault(p => p.UserId == senderId)
            ?? await db.ConversationParticipants.FirstAsync(p => p.ConversationId == conversation.Id && p.UserId == senderId);
        sender.LastReadAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return message;
    }

    private static Task<bool> IsParticipant(AppDbContext db, Guid conversationId, Guid userId) =>
        db.ConversationParticipants.AnyAsync(p => p.ConversationId == conversationId && p.UserId == userId);

    private static MessageDto ToDto(Message m) => new(m.Id, m.ConversationId, m.SenderId, m.Body, m.CreatedAt);
}
