using System.Text.Json.Serialization;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Commerce;
using BaselineX.Api.Features.Dev;
using BaselineX.Api.Features.Identity;
using BaselineX.Api.Features.Marketplace;
using BaselineX.Api.Features.Messaging;
using BaselineX.Api.Features.Organizations;
using BaselineX.Api.Features.Play;
using BaselineX.Api.Features.Reviews;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<OpenApiSecurity>());
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")).UseSnakeCaseNamingConvention());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.AddSupabaseAuth();

// The React app runs on a different origin, so the browser needs CORS to call this API.
// Auth is a Bearer header (not cookies), so credentials are not allowed.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "BaselineX API"));
    using var scope = app.Services.CreateScope();
    Seed.Run(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    app.MapDevEndpoints();
}

app.UseHttpsRedirection();
app.UseCors(); // before auth, so preflight (OPTIONS) requests are answered without a token
app.UseAuthentication();
app.UseAuthorization();
app.UseProfileUpsert();

app.MapProfileEndpoints();
app.MapOnboardingEndpoints();
app.MapCatalogEndpoints();
app.MapOrganizationEndpoints();
app.MapListingEndpoints();
app.MapOfferEndpoints();
app.MapOrderEndpoints();
app.MapPlayEndpoints();
app.MapMessagingEndpoints();
app.MapReviewEndpoints();


app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    foreach (var address in addresses)
        app.Logger.LogInformation(" BaselineX API running at {Address}", address);
    if (app.Environment.IsDevelopment())
        foreach (var address in addresses)
            app.Logger.LogInformation("Swagger UI: {Address}/swagger", address);
});

app.Run();
