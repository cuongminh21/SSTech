using System.Text.Json.Serialization;
using PartnerIntegration.Api.Endpoints;
using PartnerIntegration.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();

builder.Services.AddPartnerSwagger();
builder.Services.AddPartnerSecurity(builder.Configuration);
builder.Services.AddPartnerIntegration(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(ui => ui.SwaggerEndpoint("/swagger/v1/swagger.json", "Partner Integration BFF v1"));
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapPartnerTransactionApi();
app.MapMockPartnerVerificationApi();
app.MapDevTokenEndpoint();

app.Run();

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can boot the API in integration tests.</summary>
public partial class Program;
