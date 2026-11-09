using System.Text.Json;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Catalog;

public record LookupDto(int Id, string Name, string Slug);
public record BrandDto(int Id, string Name, string Slug, string? LogoObjectKey);
public record ProductSummaryDto(Guid Id, string Name, string? Model, int? ModelYear, decimal? RetailPrice, string Currency,
    string? PrimaryImageObjectKey, string Sport, string Category, string Brand);
public record ProductDto(Guid Id, string Name, string? Model, int? ModelYear, decimal? RetailPrice, string Currency,
    string? Description, string? PrimaryImageObjectKey, JsonElement? Specifications,
    LookupDto Sport, LookupDto Category, BrandDto Brand);

// Catalog is read-only over the API; it is curated via seed/admin tooling.
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Catalog");

        g.MapGet("/sports", async (AppDbContext db) =>
            await db.Sports.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
                .Select(x => new LookupDto(x.Id, x.Name, x.Slug)).ToListAsync());

        g.MapGet("/categories", async (AppDbContext db) =>
            await db.Categories.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
                .Select(x => new LookupDto(x.Id, x.Name, x.Slug)).ToListAsync());

        g.MapGet("/brands", async (AppDbContext db) =>
            await db.Brands.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
                .Select(x => new BrandDto(x.Id, x.Name, x.Slug, x.LogoObjectKey)).ToListAsync());

        g.MapGet("/products", async (AppDbContext db, int? sportId, int? categoryId, int? brandId, string? q,
            int? page, int? pageSize) =>
        {
            var query = db.Products.AsNoTracking();
            if (sportId is not null) query = query.Where(p => p.SportId == sportId);
            if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
            if (brandId is not null) query = query.Where(p => p.BrandId == brandId);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            return await query.OrderBy(p => p.Name)
                .Select(p => new ProductSummaryDto(p.Id, p.Name, p.Model, p.ModelYear, p.RetailPrice, p.Currency,
                    p.PrimaryImageObjectKey, p.Sport.Name, p.Category.Name, p.Brand.Name))
                .ToPagedAsync(page, pageSize);
        });

        g.MapGet("/products/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var p = await db.Products.AsNoTracking().Include(x => x.Sport).Include(x => x.Category).Include(x => x.Brand)
                .FirstOrDefaultAsync(x => x.Id == id);
            return p is null
                ? Results.NotFound()
                : Results.Ok(new ProductDto(p.Id, p.Name, p.Model, p.ModelYear, p.RetailPrice, p.Currency, p.Description,
                    p.PrimaryImageObjectKey, p.Specifications.ToJsonElement(),
                    new LookupDto(p.Sport.Id, p.Sport.Name, p.Sport.Slug),
                    new LookupDto(p.Category.Id, p.Category.Name, p.Category.Slug),
                    new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.LogoObjectKey)));
        });
    }
}
