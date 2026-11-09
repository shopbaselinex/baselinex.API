using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BaselineX.Api.Common;

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public static partial class HttpExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, int? page, int? pageSize)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var total = await query.CountAsync();
        var items = await query.Skip((p - 1) * size).Take(size).ToListAsync();
        return new PagedResult<T>(items, p, size, total);
    }

    // Supabase user id ("sub" claim). Only call on endpoints that RequireAuthorization().
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing sub claim."));

    public static Guid? UserIdOrNull(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    public static string? ToJsonString(this JsonElement? element) => element?.GetRawText();

    public static JsonElement? ToJsonElement(this string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    public static string Slugify(string value) =>
        NonSlugChars().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}

// Maps domain and persistence failures to ProblemDetails instead of 500s.
public class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        (int status, string title)? mapped = exception switch
        {
            DomainException => (StatusCodes.Status400BadRequest, exception.Message),
            BadHttpRequestException bad => (bad.StatusCode, bad.Message), // malformed JSON / route values
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The record was changed by someone else. Reload and retry."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "A record with the same unique value already exists."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } =>
                (StatusCodes.Status400BadRequest, "A referenced record does not exist."),
            _ => null,
        };
        if (mapped is not { } m) return false;

        context.Response.StatusCode = m.status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = m.status, Title = m.title },
        });
    }
}
