using BaselineX.Api.Common;
using BaselineX.Api.Features.Marketplace;
using BaselineX.Api.Features.Play;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Messaging;

[Index(nameof(ListingId))]
[Index(nameof(ActivityId))]
[Index(nameof(UpdatedAt))]
public class Conversation : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid? ListingId { get; set; }
    public Listing? Listing { get; set; }
    public Guid? ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ConversationParticipant> Participants { get; set; } = [];
    public List<Message> Messages { get; set; } = [];
}

[PrimaryKey(nameof(ConversationId), nameof(UserId))]
[Index(nameof(UserId))]
public class ConversationParticipant
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastReadAt { get; set; } // unread = Message.CreatedAt > LastReadAt
}

[Index(nameof(ConversationId), nameof(CreatedAt))]
[Index(nameof(SenderId))]
public class Message : IHasCreatedAt
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
