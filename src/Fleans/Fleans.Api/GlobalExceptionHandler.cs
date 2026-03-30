using Fleans.Domain.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Fleans.Api;

public partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = MapException(exception);

        if (statusCode >= 500)
            LogUnhandledException(exception);

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        }, cancellationToken);

        return true;
    }

    private static (int StatusCode, string Title, string Detail) MapException(Exception exception) =>
        exception switch
        {
            BadRequestActivityException ex => (400, "Bad Request", ex.GetActivityErrorState().Message),
            ArgumentException ex           => (400, "Bad Request", ex.Message),
            KeyNotFoundException ex        => (404, "Not Found", ex.Message),
            InvalidOperationException ex   => (409, "Conflict", ex.Message),
            _                              => (500, "Internal Server Error",
                                               "An unexpected error occurred. See server logs for details.")
        };

    [LoggerMessage(EventId = 8100, Level = LogLevel.Error,
        Message = "Unhandled exception in request pipeline")]
    private partial void LogUnhandledException(Exception exception);
}
