using Microsoft.AspNetCore.Diagnostics;

using Ticketing.Domain;

namespace Ticketing.Api;

/// <summary>
/// Maps a broken domain rule to 422: the request parsed fine and then failed a business
/// rule. Centralised here so no endpoint has to remember to translate it.
/// </summary>
internal sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger)
    : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogDomainRejection =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(DomainExceptionHandler)),
            "Domain rule rejected {Method} {Path}: {Reason}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not DomainException domainException)
            return false;   // not ours — let the next handler decide

        // Warning, not Error: the client asked for something the rules forbid. Logging
        // this at Error would make the error rate meaningless to alert on. No exception
        // passed either — the stack trace of an expected rejection is noise.
        LogDomainRejection(
            logger,
            httpContext.Request.Method,
            httpContext.Request.Path,
            domainException.Message,
            null);

        await TypedResults.Problem(
            title: "The request was well formed but could not be processed.",
            detail: domainException.Message,
            statusCode: StatusCodes.Status422UnprocessableEntity)
            .ExecuteAsync(httpContext);

        return true;
    }
}
