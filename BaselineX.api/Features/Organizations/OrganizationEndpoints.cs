using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Organizations;

public record OrganizationDto(Guid Id, string Name, string Slug, OrganizationType Type, string? Description,
    string? LogoObjectKey, string? CoverObjectKey, string? City, string? State, string? Neighborhood,
    string? Website, string? Phone, bool IsVerified);

public record OrganizationMemberDto(Guid UserId, string DisplayName, string? AvatarObjectKey, OrganizationRole Role, DateTime CreatedAt);

public record SaveOrganizationRequest(
    [Required, MaxLength(180)] string Name,
    [MaxLength(180)] string? Slug,
    [EnumDataType(typeof(OrganizationType))] OrganizationType Type,
    string? Description,
    [MaxLength(500)] string? LogoObjectKey,
    [MaxLength(500)] string? CoverObjectKey,
    [MaxLength(120)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(120)] string? Neighborhood,
    [MaxLength(500)] string? Website,
    [MaxLength(40)] string? Phone);

public record AddMemberRequest(Guid UserId, OrganizationRole Role);

public record CoachDto(Guid Id, Guid UserId, string DisplayName, string? AvatarObjectKey, string? Bio, int? YearsExperience,
    decimal? PrivateLessonRate, string Currency, bool IsVerified, IEnumerable<string> Sports);

public record SaveCoachProfileRequest(
    string? Bio,
    [Range(0, 80)] int? YearsExperience,
    [Range(0, 100000)] decimal? PrivateLessonRate,
    [Required, MinLength(1, ErrorMessage = "Pick at least one sport.")] int[] SportIds);

public static class OrgAccess
{
    public static readonly OrganizationRole[] Managers = [OrganizationRole.Owner, OrganizationRole.Administrator];

    public static Task<bool> HasRoleAsync(this AppDbContext db, Guid organizationId, Guid userId, params OrganizationRole[] roles) =>
        db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId && roles.Contains(m.Role));

    /// <summary>True if the user owns or administers at least one organization.</summary>
    public static Task<bool> ManagesAnyOrganizationAsync(this AppDbContext db, Guid userId) =>
        db.OrganizationMembers.AnyAsync(m => m.UserId == userId && Managers.Contains(m.Role));
}

public static class OrganizationEndpoints
{
    public static void MapOrganizationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/organizations").WithTags("Organizations");

        g.MapGet("/", async (AppDbContext db, OrganizationType? type, string? city, string? state, string? q,
            int? page, int? pageSize) =>
        {
            var query = db.Organizations.AsNoTracking();
            if (type is not null) query = query.Where(o => o.Type == type);
            if (!string.IsNullOrWhiteSpace(city)) query = query.Where(o => o.City == city);
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(o => o.State == state);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(o => EF.Functions.ILike(o.Name, $"%{q}%"));
            return await query.OrderByDescending(o => o.IsVerified).ThenBy(o => o.Name)
                .Select(o => ToDto(o)).ToPagedAsync(page, pageSize);
        });

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id) is { } o
                ? Results.Ok(ToDto(o)) : Results.NotFound());

        g.MapPost("/", async (SaveOrganizationRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var org = await AddOrganizationAsync(db, user.UserId(), req);
            await db.SaveChangesAsync();
            return TypedResults.Created($"/organizations/{org.Id}", ToDto(org));
        }).RequireAuthorization();

        g.MapPut("/{id:guid}", async (Guid id, SaveOrganizationRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
            if (org is null) return Results.NotFound();
            if (!await db.HasRoleAsync(id, user.UserId(), OrgAccess.Managers)) return Results.Forbid();
            Apply(org, req);
            if (req.Slug is not null) org.Slug = HttpExtensions.Slugify(req.Slug);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(org));
        }).RequireAuthorization();

        g.MapGet("/{id:guid}/members", async (Guid id, AppDbContext db) =>
            await (from m in db.OrganizationMembers.AsNoTracking()
                   join u in db.UserProfiles on m.UserId equals u.Id
                   where m.OrganizationId == id
                   orderby m.Role, u.DisplayName
                   select new OrganizationMemberDto(m.UserId, u.DisplayName, u.AvatarObjectKey, m.Role, m.CreatedAt))
                .ToListAsync());

        g.MapPost("/{id:guid}/members", async (Guid id, AddMemberRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (!await db.Organizations.AnyAsync(o => o.Id == id)) return Results.NotFound();
            if (!await db.HasRoleAsync(id, me, OrgAccess.Managers)) return Results.Forbid();
            if (req.Role == OrganizationRole.Owner && !await db.HasRoleAsync(id, me, OrganizationRole.Owner)) return Results.Forbid();
            if (!await db.UserProfiles.AnyAsync(u => u.Id == req.UserId)) return Results.BadRequest("Unknown user.");

            var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == req.UserId);
            if (member is null) db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = id, UserId = req.UserId, Role = req.Role });
            else member.Role = req.Role;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (me != userId && !await db.HasRoleAsync(id, me, OrgAccess.Managers)) return Results.Forbid();

            var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == userId);
            if (member is null) return Results.NotFound();
            if (member.Role == OrganizationRole.Owner &&
                await db.OrganizationMembers.CountAsync(m => m.OrganizationId == id && m.Role == OrganizationRole.Owner) == 1)
                throw new DomainException("An organization must keep at least one owner.");

            db.OrganizationMembers.Remove(member);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        MapCoachEndpoints(app);
    }

    private static void MapCoachEndpoints(WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Coaches");

        g.MapGet("/coaches", async (AppDbContext db, int? sportId, int? page, int? pageSize) =>
        {
            var query = db.CoachProfiles.AsNoTracking();
            if (sportId is not null) query = query.Where(c => c.Sports.Any(s => s.SportId == sportId));
            return await query.OrderByDescending(c => c.IsVerified).ThenByDescending(c => c.YearsExperience)
                .Join(db.UserProfiles, c => c.UserId, u => u.Id, (c, u) => new CoachDto(c.Id, c.UserId, u.DisplayName,
                    u.AvatarObjectKey, c.Bio, c.YearsExperience, c.PrivateLessonRate, c.Currency, c.IsVerified,
                    c.Sports.Select(s => s.Sport.Name)))
                .ToPagedAsync(page, pageSize);
        });

        g.MapGet("/coaches/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.CoachProfiles.AsNoTracking().Where(c => c.Id == id)
                .Join(db.UserProfiles, c => c.UserId, u => u.Id, (c, u) => new CoachDto(c.Id, c.UserId, u.DisplayName,
                    u.AvatarObjectKey, c.Bio, c.YearsExperience, c.PrivateLessonRate, c.Currency, c.IsVerified,
                    c.Sports.Select(s => s.Sport.Name)))
                .FirstOrDefaultAsync() is { } c ? Results.Ok(c) : Results.NotFound());

        g.MapPut("/me/coach-profile", async (SaveCoachProfileRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var coach = await UpsertCoachProfileAsync(db, user.UserId(), req);
            await db.SaveChangesAsync();
            return Results.Ok(new { coach.Id });
        }).RequireAuthorization();
    }

    // Shared with onboarding (POST /me/onboarding). Both only stage changes; the caller saves.

    /// <summary>Creates or updates the user's coach profile.</summary>
    internal static async Task<CoachProfile> UpsertCoachProfileAsync(AppDbContext db, Guid userId, SaveCoachProfileRequest req)
    {
        var sportIds = req.SportIds.Distinct().ToArray();
        if (await db.Sports.CountAsync(s => sportIds.Contains(s.Id)) != sportIds.Length)
            throw new DomainException("Unknown sport id.");

        var coach = await db.CoachProfiles.Include(c => c.Sports).FirstOrDefaultAsync(c => c.UserId == userId);
        if (coach is null) db.CoachProfiles.Add(coach = new CoachProfile { UserId = userId });
        (coach.Bio, coach.YearsExperience, coach.PrivateLessonRate) = (req.Bio, req.YearsExperience, req.PrivateLessonRate);
        coach.Sports.RemoveAll(s => !sportIds.Contains(s.SportId));
        coach.Sports.AddRange(sportIds.Where(id => coach.Sports.All(s => s.SportId != id))
            .Select(id => new CoachSport { SportId = id }));
        return coach;
    }

    /// <summary>Creates an organization owned by the user.</summary>
    internal static async Task<Organization> AddOrganizationAsync(AppDbContext db, Guid ownerId, SaveOrganizationRequest req)
    {
        var org = new Organization { Slug = await UniqueSlugAsync(db, req.Slug ?? req.Name) };
        Apply(org, req);
        org.Members.Add(new OrganizationMember { UserId = ownerId, Role = OrganizationRole.Owner });
        db.Organizations.Add(org);
        return org;
    }

    // "Baseline Tennis Club" → baseline-tennis-club, or baseline-tennis-club-2 if that one is taken.
    private static async Task<string> UniqueSlugAsync(AppDbContext db, string value)
    {
        var slug = HttpExtensions.Slugify(value);
        slug = slug[..Math.Min(slug.Length, 170)].Trim('-'); // room for a suffix within 180
        if (slug == "") slug = "organization";
        var taken = await db.Organizations.Where(o => o.Slug == slug || o.Slug.StartsWith(slug + "-"))
            .Select(o => o.Slug).ToListAsync();
        var candidate = slug;
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{slug}-{i}";
        return candidate;
    }

    private static void Apply(Organization o, SaveOrganizationRequest r) =>
        (o.Name, o.Type, o.Description, o.LogoObjectKey, o.CoverObjectKey, o.City, o.State, o.Neighborhood, o.Website, o.Phone) =
        (r.Name, r.Type, r.Description, r.LogoObjectKey, r.CoverObjectKey, r.City, r.State, r.Neighborhood, r.Website, r.Phone);

    private static OrganizationDto ToDto(Organization o) => new(o.Id, o.Name, o.Slug, o.Type, o.Description,
        o.LogoObjectKey, o.CoverObjectKey, o.City, o.State, o.Neighborhood, o.Website, o.Phone, o.IsVerified);
}
