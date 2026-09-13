using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Ticketing.Api;
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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();                      // gives bare 404s a Problem Details body

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var v1 = app.MapGroup("/v1");
v1.MapPurchaseTickets();

app.Run();

// Top-level statements compile to an internal Program; WebApplicationFactory<Program>
// needs it reachable from the test assembly.
public partial class Program { }
