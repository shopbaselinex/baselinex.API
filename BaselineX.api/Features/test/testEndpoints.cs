namespace BaselineX.Api.Features.Test;


using Microsoft.EntityFrameworkCore;
using BaselineX.Api.Data;


public static class TestEndpoints
{
    // "this WebApplication app" makes this an extension method,
    // so Program.cs can call it as app.MapTestEndpoints()
    public static void MapTestEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/test").WithTags("Test"); // the tag groups it in Swagger

        g.MapGet("/", () => "Hello World");


        g.MapGet("/sports", async (AppDbContext db) =>
    await db.Sports.Select(s => s.Name).ToListAsync());
    }
}
