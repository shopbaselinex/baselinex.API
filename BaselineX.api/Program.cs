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
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<OpenApiSecurity>());
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.AddSupabaseAuth();

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
app.UseAuthentication();
app.UseAuthorization();
app.UseProfileUpsert();

app.MapProfileEndpoints();
app.MapCatalogEndpoints();
app.MapOrganizationEndpoints();
app.MapListingEndpoints();
app.MapOfferEndpoints();
app.MapOrderEndpoints();
app.MapPlayEndpoints();
app.MapMessagingEndpoints();
app.MapReviewEndpoints();
app.Run();
