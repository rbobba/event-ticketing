using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

using Scalar.AspNetCore;

using Ticketing.Api;
using Ticketing.Api.Features.Events;
using Ticketing.Api.Features.Orders;
using Ticketing.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Ticketing")));

// RFC 9457 for every error shape. The trace id is attached centrally rather than per
// endpoint, so an id quoted in a support request can always be found in the logs.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
});

// Order matters. Handlers run in registration order and the first to return true wins,
// so the specific one is registered before the catch-all.
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

builder.Services.AddOpenApi();

// Tagged "ready" so the liveness probe below can exclude it. See the note at the
// endpoints for why that separation matters.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TicketingDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();                      // gives bare 404s a Problem Details body

if (app.Environment.IsDevelopment())
{
    // The document is generated from the code, so it cannot describe an endpoint that
    // no longer exists. Scalar renders it at /scalar/v1 for anyone wanting to click.
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Two probes, not one, because the orchestrator does different things with them.
//
// Liveness failing means "restart this instance". It deliberately checks nothing but
// the process: if it touched the database, a brief database outage would restart every
// replica at once, turning a recoverable dependency failure into a full one.
//
// Readiness failing means "stop sending traffic here, but leave it running" — which is
// the correct response to a dependency the instance cannot currently reach.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

var v1 = app.MapGroup("/v1");
v1.MapEventEndpoints();
v1.MapGetAvailability();
v1.MapSalesSummary();
v1.MapPurchaseTickets();
v1.MapGetOrder();

app.Run();

// Top-level statements compile to an internal Program; WebApplicationFactory<Program>
// needs it reachable from the test assembly.
public partial class Program { }
