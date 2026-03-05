# Errors Found After .NET 10 / NuGet Update

## Issue: FastEndpoints endpoints return 200 OK instead of the expected status code

**Symptom:** All FastEndpoints endpoints wrapped with `IdempotentAPIEndpointFilter` return `200 OK` with no body, regardless of what the handler sends.

**Root cause:** FastEndpoints 8.0.1 changed how it registers endpoint handlers with ASP.NET Core routing. It now uses `Func<IResult>` (returning a `FeRequestHandler` singleton that implements `IResult`) instead of the `RequestDelegate` pattern used in v5.25.

In ASP.NET Core's endpoint filter pipeline, `IResult` objects returned by the handler are passed through all filters **before** being executed. The `IdempotentAPIEndpointFilter` receives `FeRequestHandler.Instance` from `next(context)` but doesn't recognize it — it only checks for `IValueHttpResult` and `IStatusCodeHttpResult`, neither of which `FeRequestHandler` implements. As a result:

1. The filter extracts `null` for both the value and status code.
2. It returns `Results.Empty` (200 OK, no body) to the pipeline.
3. The FastEndpoints endpoint handler **never runs**.

## Fix applied in `IdempotentAPIEndpointFilter.cs`

### 1. Execute unknown `IResult` types so the endpoint actually runs

When `next(context)` returns an `IResult` that is not `IValueHttpResult` or `IStatusCodeHttpResult`, the filter now explicitly calls `IResult.ExecuteAsync()` to run the underlying endpoint handler, then reads the status code from the HTTP response.

### 2. Guard `StatusCode` assignment against already-started responses

Since FastEndpoints writes the full response (headers + body) during `ExecuteAsync`, the response has already started by the time the filter tries to set `StatusCode`. Added a `!context.HttpContext.Response.HasStarted` guard to prevent `InvalidOperationException`.

## Remaining concern

FastEndpoints writes the response body directly to the HTTP response stream (not via a return value). After the fix, `objectResult.Value` is still `null` for FE endpoints. The idempotency caching in `ApplyPostIdempotency` reads the status code from `HttpContext.Response.StatusCode` (now correct), but the cached response body may be empty. Verify that the full idempotency cache round-trip works for FastEndpoints endpoints.

---

## Issue: Concurrent idempotency tests fail — both requests return 406 instead of one 406 + one 409

**Symptom:** `PostRequestsConcurrent_OnClusterEnvironment_WithErrorResponse_ShouldReturnTheErrorAndA409Response` and `PostJsonRequestsConcurrent_OnClusterEnvironment_WithErrorResponse_ShouldReturnTheErrorAndA409Response` fail for all WebClients. Both concurrent requests return `406 NotAcceptable` instead of one `406` and one `409 Conflict`.

**Root cause:** FusionCache was upgraded from v1.x to v2.5.0. In FusionCache v1, `IDistributedCache` was auto-discovered from DI. In FusionCache v2, **components are not auto-discovered by default** (per the [DI docs](https://github.com/ZiggyCreatures/FusionCache/blob/main/docs/DependencyInjection.md): "by default nothing is done automagically"). You must either call `TryWithAutoSetup()` on the builder or explicitly register each component.

The `AddIdempotentAPIUsingFusionCache` method called `AddFusionCache().WithOptions(...)` but never told FusionCache to use the registered `IDistributedCache`. This left FusionCache in **memory-only mode (L1 only)**. Each `WebApplicationFactory` instance had its own isolated in-memory cache, so Instance 2 could never see Instance 1's inflight marker — both requests proceeded independently.

## Fix applied in `IdempotencyFusionCacheExtensions.cs`

Added `.TryWithRegisteredDistributedCache()` to the FusionCache builder chain so it discovers the registered `IDistributedCache` from DI. We use this targeted method instead of `TryWithAutoSetup()` because the latter also picks up the DI-registered `IMemoryCache`, which may have a `SizeLimit` configured — causing `InvalidOperationException: Cache entry must specify a value for Size when SizeLimit is set`. The `IFusionCacheSerializer` is still auto-discovered by FusionCache v2 by default.
