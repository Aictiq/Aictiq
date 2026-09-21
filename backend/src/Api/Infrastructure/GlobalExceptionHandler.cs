using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// Last-resort exception handler: logs the failure and returns problem+json without
/// leaking internals. Expected errors are returned as ProblemDetails by the endpoints
/// themselves; only unhandled exceptions reach this.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, type) = exception switch
        {
            // Malformed requests (bad JSON, missing required parameters) are client
            // errors, not server faults — keep their status and don't log them as errors.
            BadHttpRequestException badRequest =>
                (badRequest.StatusCode, "Malformed request.", ProblemTypes.Validation),

            // A tenant-scoped write with no organization in scope is a programming error,
            // never something a client can cause — so it stays a 500 and gets logged, but
            // with a title that names the actual fault instead of "unexpected".
            TenantMissingException =>
                (StatusCodes.Status500InternalServerError, "Tenant context was not established.", (string?)null),

            // The database refused a write that would have left an organization with no
            // Owner. It is the database's refusal and not the endpoint's because two
            // Owners demoting each other at the same instant each pass every check the
            // application can make — neither can see the other's uncommitted work.
            DbUpdateException
            {
                InnerException: PostgresException
                {
                    SqlState: PostgresErrorCodes.RaiseException,
                    MessageText: DatabaseSignals.LastOwner
                }
            } =>
                (StatusCodes.Status409Conflict,
                 "An organization must always have at least one owner.", ProblemTypes.LastOwner),

            // Optimistic-concurrency conflicts are expected under parallel edits (xmin token).
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict,
                 "The record was modified by someone else — refresh and try again.", ProblemTypes.Conflict),

            // Unique-constraint violations that slipped past endpoint checks (races) are
            // conflicts, not server faults.
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict,
                 "A record with the same unique value already exists.", ProblemTypes.Conflict),

            // Referential-integrity races: the parent row was deleted between the
            // endpoint's lookup and the write, or a RESTRICT-ed lookup is still in use.
            // Both are "someone else changed the world under you" — a conflict, not a fault.
            DbUpdateException
            {
                InnerException: PostgresException
                {
                    SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation
                }
            } =>
                (StatusCodes.Status409Conflict,
                 "A referenced record was changed or is still in use — refresh and try again.", ProblemTypes.Conflict),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", (string?)null)
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        // IProblemDetailsService sets application/problem+json and runs
        // CustomizeProblemDetails (which is where traceId gets attached).
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Type = type }
        });
    }
}
