using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BaselineX.Api.Common;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Catalog;

[Index(nameof(Slug), IsUnique = true)]
public class Sport
{
    public int Id { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(80)] public string Slug { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

[Index(nameof(Slug), IsUnique = true)]
public class Category
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(100)] public string Slug { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

[Index(nameof(Slug), IsUnique = true)]
public class Brand
{
    public int Id { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(120)] public string Slug { get; set; } = "";
    [MaxLength(500)] public string? LogoObjectKey { get; set; }
    public bool IsActive { get; set; } = true;
}

// Canonical catalog product, e.g. "Wilson Blade 98 V9". One product, many listings.
[Index(nameof(BrandId), nameof(Name))]
[Index(nameof(SportId), nameof(CategoryId), nameof(BrandId))]
public class Product : IHasTimestamps
{
    public Guid Id { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int BrandId { get; set; }
    public Brand Brand { get; set; } = null!;
    [MaxLength(180)] public string Name { get; set; } = "";
    [MaxLength(180)] public string? Model { get; set; }
    public int? ModelYear { get; set; }
    public decimal? RetailPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Description { get; set; }
    [MaxLength(500)] public string? PrimaryImageObjectKey { get; set; }
    [Column(TypeName = "jsonb")] public string? Specifications { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
