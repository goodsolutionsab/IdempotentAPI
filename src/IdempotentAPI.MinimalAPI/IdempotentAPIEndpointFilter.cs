using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IdempotentAPI.Core;
using IdempotentAPI.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotentAPI.MinimalAPI;

public class IdempotentAPIEndpointFilter : IEndpointFilter
{
    private readonly IServiceProvider _serviceProvider;

    public IdempotentAPIEndpointFilter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var idempotency = _serviceProvider.GetRequiredService<Idempotency>();

        try
        {
            bool applyPostIdempotency = true;
            var actionContext = new ActionContext(context.HttpContext, new RouteData(), new ActionDescriptor());
            var filters = new List<IFilterMetadata>();
            var actionArguments = new Dictionary<string, object?>();

            await idempotency.PrepareMinimalApiIdempotencyAsync(context.HttpContext, context.Arguments);

            var actionExecutingContext = new ActionExecutingContext(actionContext, filters, actionArguments, null!);

            try
            {
                await idempotency.ApplyPreIdempotency(actionExecutingContext);
            }
            catch (IdempotencyKeyValidationException ex)
            {
                // Return 400 Bad Request with ProblemDetails for missing/invalid idempotency key
                return Results.Problem(
                    type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                    title: "Bad Request",
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: ex.Message);
            }

            // short-circuit to exit for async filter when result already set
            // https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/filters?view=aspnetcore-7.0#action-filters
            if (actionExecutingContext.Result != null)
            {
                applyPostIdempotency = false;
            }

            // Execute the next EndpointFilter which eventually will execute the endpoint and we will get its results.
            ObjectResult objectResult;
            if (actionExecutingContext.Result == null)
            {
                var realCallResult = await next(context);

                object? value = null;
                int? statusCode = null;

                if (realCallResult is not IResult)
                {
                    value = realCallResult;
                }
                else if (realCallResult is IValueHttpResult valueHttpResult)
                {
                    value = valueHttpResult.Value;
                }
                else
                {
                    // For IResult types that don't implement IValueHttpResult
                    // (e.g., FastEndpoints' internal handler), execute the IResult
                    // so the endpoint actually runs and writes its response.
                    await ((IResult)realCallResult).ExecuteAsync(context.HttpContext);
                    statusCode = context.HttpContext.Response.StatusCode;
                }

                objectResult = new ObjectResult(value);

                if (realCallResult is IStatusCodeHttpResult statusCodeHttpResult &&
                    statusCodeHttpResult.StatusCode.HasValue)
                {
                    objectResult.StatusCode = statusCodeHttpResult.StatusCode.Value;
                }
                else if (statusCode.HasValue)
                {
                    objectResult.StatusCode = statusCode.Value;
                }
            }
            else
            {
                object? value = null;
                if (actionExecutingContext.Result is ObjectResult valueHttpResult)
                {
                    value = valueHttpResult.Value;
                }

                objectResult = new ObjectResult(value);

                if (actionExecutingContext.Result is IStatusCodeActionResult statusCodeActionResult &&
                    statusCodeActionResult.StatusCode.HasValue)
                {
                    objectResult.StatusCode = statusCodeActionResult.StatusCode.Value;
                }
            }

            if (objectResult.StatusCode.HasValue && !context.HttpContext.Response.HasStarted)
            {
                context.HttpContext.Response.StatusCode = objectResult.StatusCode.Value;
            }

            if (!applyPostIdempotency)
            {
                // Return Results.Empty for null values to avoid writing "null" to response body
                return objectResult.Value ?? Results.Empty;
            }

            var resultExecutingContext = new ResultExecutingContext(actionContext, filters, objectResult, null!);

            await idempotency.ApplyPostIdempotency(resultExecutingContext);

            // Return Results.Empty for null values to avoid writing "null" to response body
            return objectResult.Value ?? Results.Empty;
        }
        catch
        {
            await idempotency.CancelIdempotency();
            throw;
        }

    }
}