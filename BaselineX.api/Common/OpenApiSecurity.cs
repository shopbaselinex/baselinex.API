using BaselineX.Api.Features.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BaselineX.Api.Common;

// Adds the "Authorize" button to Swagger UI: a Supabase Bearer token, and in dev the X-Dev-User header.
public class OpenApiSecurity(IWebHostEnvironment env) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            Description = "Supabase access token",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });

        if (env.IsDevelopment())
        {
            document.Components.SecuritySchemes["DevUser"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = AuthSetup.DevUserHeader,
                Description = "Dev only (Auth:DevBypass): act as this user id. Defaults to the seeded dev user.",
            };
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("DevUser", document)] = [] });
        }
        return Task.CompletedTask;
    }
}
