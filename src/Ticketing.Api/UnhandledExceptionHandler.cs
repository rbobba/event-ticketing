using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;

namespace Ticketing.Api;

/// <summary>
/// Anything that reached here is a defect, not a client mistake. The client gets a trace
/// id and nothing else; the detail goes to the log under that same id.
/// </summary>
internal sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger)
    : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogUnhandledException =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Error,
            new EventId(1, nameof(UnhandledExceptionHandler)),
            "Unhandled exception on {Method} {Path}. TraceId {TraceId}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        LogUnhandledException(
            logger,
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId,
            exception);

        await TypedResults.Problem(
            title: "An unexpected error occurred.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(httpContext);

        return true;
    }
}
