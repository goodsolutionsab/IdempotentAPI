using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using IdempotentAPI.AccessCache;
using IdempotentAPI.Filters;
using IdempotentAPI.UnitTests.ApplicationServices.DTOs;
using IdempotentAPI.UnitTests.Enums;
using IdempotentAPI.UnitTests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IdempotentAPI.UnitTests.FiltersTests;

/// <summary>
/// Tests for the ContentLength bug fix.
/// When ContentLength is not set (e.g., in TestServer/WebApplicationFactory),
/// the request body should still be included in the hash calculation.
/// See: https://github.com/ikyriak/IdempotentAPI/issues/58
/// </summary>
public class IdempotencyAttribute_ContentLengthTests
{
    private readonly string _headerKeyName = "IdempotencyKey";
    private readonly string _distributedCacheKeysPrefix = "IdempAPI_";
    private readonly ILoggerFactory _loggerFactory;

    public IdempotencyAttribute_ContentLengthTests()
    {
        IServiceCollection serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder => builder.AddDebug());
        _loggerFactory = serviceCollection.BuildServiceProvider().GetService<ILoggerFactory>()!;
    }

    /// <summary>
    /// Creates an ActionContext using DefaultHttpContext where ContentLength is NOT set.
    /// This simulates the behavior in TestServer/WebApplicationFactory.
    /// </summary>
    private ActionContext ArrangeActionContextWithoutContentLength(
        string httpMethod,
        HeaderDictionary requestHeaders,
        string requestBodyString,
        HeaderDictionary responseHeaders,
        IActionResult? actionResult,
        int? statusCode)
    {
        var httpContext = new DefaultHttpContext();

        // Set up request
        httpContext.Request.Method = httpMethod;
        foreach (var header in requestHeaders)
        {
            httpContext.Request.Headers[header.Key] = header.Value;
        }

        httpContext.Request.Path = "/resource";
        httpContext.Request.QueryString = new QueryString();
        httpContext.Request.ContentType = "application/json";

        // Set up body stream - ContentLength is NOT set explicitly
        // This is the key difference from normal requests - DefaultHttpContext doesn't
        // auto-populate ContentLength when you set the Body
        var bodyBytes = Encoding.UTF8.GetBytes(requestBodyString);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        // Note: We do NOT set ContentLength to simulate TestServer/WebApplicationFactory behavior

        // Set up response headers
        foreach (var header in responseHeaders)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        return new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor()
        );
    }

    private ActionExecutedContext CreateActionExecutedContext(ActionContext actionContext)
    {
        return new ActionExecutedContext(
            actionContext,
            new List<IFilterMetadata>(),
            Mock.Of<Controller>());
    }

    private ResultExecutingContext CreateResultExecutingContext(ActionContext actionContext, IActionResult result)
    {
        return new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            result,
            Mock.Of<Controller>());
    }

    private ResultExecutedContext CreateResultExecutedContext(ActionContext actionContext, IActionResult result)
    {
        return new ResultExecutedContext(
            actionContext,
            new List<IFilterMetadata>(),
            result,
            Mock.Of<Controller>());
    }

    /// <summary>
    /// Diagnostic test: Verify that body reading works when ContentLength is null.
    /// This replicates the logic in GetRawBodyAsync to verify the test setup is correct.
    /// </summary>
    [Fact]
    public async Task Diagnostic_BodyReading_ShouldWork_WhenContentLengthIsNull()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        string expectedBody = @"{""message"":""Test message""}";
        var bodyBytes = Encoding.UTF8.GetBytes(expectedBody);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        // ContentLength is NOT set

        // Verify ContentLength is null
        Assert.Null(httpContext.Request.ContentLength);

        // Act - replicate GetRawBodyAsync logic
        var request = httpContext.Request;
        Assert.True(request.Body.CanRead);
        Assert.True(request.Body.CanSeek);
        request.Body.Position = 0;

        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var actualBody = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        // Assert
        Assert.NotNull(actualBody);
        Assert.Equal(expectedBody, actualBody);
    }

    /// <summary>
    /// Diagnostic test: Verify that body reading works through ActionExecutingContext
    /// </summary>
    [Fact]
    public async Task Diagnostic_BodyReading_ShouldWork_ThroughActionExecutingContext()
    {
        // Arrange - same setup as our actual tests
        var httpContext = new DefaultHttpContext();
        string expectedBody = @"{""message"":""Test message""}";
        var bodyBytes = Encoding.UTF8.GetBytes(expectedBody);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/resource";

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor()
        );

        var executingContext = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object>(),
            Mock.Of<Controller>());

        // Act - access body through the context hierarchy (as the filter would)
        var request = executingContext.HttpContext.Request;
        Assert.Null(request.ContentLength);
        Assert.True(request.Body.CanRead);

        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var actualBody = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        // Assert
        Assert.Equal(expectedBody, actualBody);
    }

    /// <summary>
    /// Diagnostic test: Verify that two different bodies produce different hashes
    /// </summary>
    [Fact]
    public async Task Diagnostic_DifferentBodies_ShouldProduceDifferentHashes()
    {
        // Arrange
        string body1 = @"{""message"":""First message""}";
        string body2 = @"{""message"":""Second message - different!""}";

        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body1));
        httpContext1.Request.Path = "/resource";

        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body2));
        httpContext2.Request.Path = "/resource";

        // Act - simulate what GenerateRequestsDataHashAsync does
        var hash1 = await GenerateHashLikeFilterAsync(httpContext1.Request);
        var hash2 = await GenerateHashLikeFilterAsync(httpContext2.Request);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    private async Task<string> GenerateHashLikeFilterAsync(HttpRequest httpRequest)
    {
        var requestsData = new List<object>();

        // The Request body - this is what the fixed code does
        if (httpRequest.Body != null)
        {
            // Replicate GetRawBodyAsync logic
            if (httpRequest.Body.CanRead)
            {
                if (!httpRequest.Body.CanSeek)
                {
                    httpRequest.EnableBuffering();
                }

                httpRequest.Body.Position = 0;
                using var reader = new StreamReader(httpRequest.Body, Encoding.UTF8, leaveOpen: true);
                var rawBody = await reader.ReadToEndAsync();
                httpRequest.Body.Position = 0;

                if (rawBody != null)
                {
                    requestsData.Add(rawBody);
                }
            }
        }

        // The request's URL
        if (httpRequest.Path.HasValue)
        {
            requestsData.Add(httpRequest.Path.ToString());
        }

        // Generate hash
        var json = System.Text.Json.JsonSerializer.Serialize(requestsData);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Scenario:
    /// When ContentLength is not set (null), and two requests are made with the
    /// same IdempotencyKey but DIFFERENT request bodies, the second request should
    /// return BadRequest because the body hash should be different.
    ///
    /// Bug: Previously, when ContentLength was null, the body was not included in
    /// the hash, so different bodies would have the same hash and the cached
    /// response would be incorrectly returned.
    /// </summary>
    [Theory]
    [InlineData("POST", CacheImplementation.DistributedCache, DistributedAccessLockImplementation.None)]
    [InlineData("PATCH", CacheImplementation.DistributedCache, DistributedAccessLockImplementation.None)]
    [InlineData("POST", CacheImplementation.FusionCache, DistributedAccessLockImplementation.None)]
    [InlineData("PATCH", CacheImplementation.FusionCache, DistributedAccessLockImplementation.None)]
    public async Task DifferentRequestBody_WithSameIdempotencyKey_WhenContentLengthIsNull_ShouldReturnBadRequest(
        string httpMethod,
        CacheImplementation cacheImplementation,
        DistributedAccessLockImplementation accessLockImplementation)
    {
        // Arrange
        var idempotencyKey = Guid.NewGuid().ToString();
        var requestHeaders = new HeaderDictionary
        {
            { "Content-Type", "application/json" },
            { _headerKeyName, idempotencyKey }
        };

        // Two DIFFERENT request bodies
        string firstRequestBody = @"{""message"":""First message""}";
        string secondRequestBody = @"{""message"":""Second message - different!""}";

        var controllerResult = new OkObjectResult(new ResponseModelBasic
        {
            Id = 1,
            CreatedOn = DateTime.UtcNow
        });

        // First request context (ContentLength is null)
        var firstActionContext = ArrangeActionContextWithoutContentLength(
            httpMethod, requestHeaders, firstRequestBody,
            new HeaderDictionary(), controllerResult, StatusCodes.Status200OK);

        var firstExecutingContext = new ActionExecutingContext(
            firstActionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object>(),
            Mock.Of<Controller>());
        var firstExecutedContext = CreateActionExecutedContext(firstActionContext);
        var firstResultExecutingContext = CreateResultExecutingContext(firstActionContext, controllerResult);
        var firstResultExecutedContext = CreateResultExecutedContext(firstActionContext, controllerResult);

        // Second request context with DIFFERENT body (ContentLength is null)
        var secondActionContext = ArrangeActionContextWithoutContentLength(
            httpMethod, requestHeaders, secondRequestBody,
            new HeaderDictionary(), controllerResult, StatusCodes.Status200OK);

        var secondExecutingContext = new ActionExecutingContext(
            secondActionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object>(),
            Mock.Of<Controller>());
        var secondExecutedContext = CreateActionExecutedContext(secondActionContext);

        IIdempotencyAccessCache idempotencyCache = MemoryDistributedCacheFixture.CreateCacheInstance(
            cacheImplementation, accessLockImplementation);

        var filter1 = new IdempotencyAttributeFilter(
            idempotencyCache,
            _loggerFactory,
            true,
            TimeSpan.FromHours(1).TotalMilliseconds,
            _headerKeyName,
            _distributedCacheKeysPrefix,
            distributedLockTimeout: null,
            cacheOnlySuccessResponses: true,
            isIdempotencyOptional: false);

        var filter2 = new IdempotencyAttributeFilter(
            idempotencyCache,
            _loggerFactory,
            true,
            TimeSpan.FromHours(1).TotalMilliseconds,
            _headerKeyName,
            _distributedCacheKeysPrefix,
            distributedLockTimeout: null,
            cacheOnlySuccessResponses: true,
            isIdempotencyOptional: false);

        // Act - First request should succeed
        await filter1.OnActionExecutionAsync(firstExecutingContext, () => Task.FromResult(firstExecutedContext));
        Assert.Null(firstExecutingContext.Result);

        // Cache the response
        await filter1.OnResultExecutionAsync(firstResultExecutingContext, () => Task.FromResult(firstResultExecutedContext));

        // Act - Second request with DIFFERENT body should return BadRequest
        await filter2.OnActionExecutionAsync(secondExecutingContext, () => Task.FromResult(secondExecutedContext));

        // Assert - The second request should be rejected because the body is different
        Assert.NotNull(secondExecutingContext.Result);
        Assert.IsType<BadRequestObjectResult>(secondExecutingContext.Result);
    }

    /// <summary>
    /// Scenario:
    /// When ContentLength is not set (null), and two requests are made with the
    /// same IdempotencyKey and SAME request body, the second request should
    /// return the cached response (not BadRequest).
    /// </summary>
    [Theory]
    [InlineData("POST", CacheImplementation.DistributedCache, DistributedAccessLockImplementation.None)]
    [InlineData("PATCH", CacheImplementation.DistributedCache, DistributedAccessLockImplementation.None)]
    [InlineData("POST", CacheImplementation.FusionCache, DistributedAccessLockImplementation.None)]
    [InlineData("PATCH", CacheImplementation.FusionCache, DistributedAccessLockImplementation.None)]
    public async Task SameRequestBody_WithSameIdempotencyKey_WhenContentLengthIsNull_ShouldReturnCachedResponse(
        string httpMethod,
        CacheImplementation cacheImplementation,
        DistributedAccessLockImplementation accessLockImplementation)
    {
        // Arrange
        var idempotencyKey = Guid.NewGuid().ToString();
        var requestHeaders = new HeaderDictionary
        {
            { "Content-Type", "application/json" },
            { _headerKeyName, idempotencyKey }
        };

        // SAME request body for both requests
        string requestBody = @"{""message"":""Same message""}";

        var expectedModel = new ResponseModelBasic
        {
            Id = 1,
            CreatedOn = new DateTime(2019, 10, 12, 5, 25, 25)
        };
        var controllerResult = new OkObjectResult(expectedModel);

        // First request context
        var firstActionContext = ArrangeActionContextWithoutContentLength(
            httpMethod, requestHeaders, requestBody,
            new HeaderDictionary(), controllerResult, StatusCodes.Status200OK);

        var firstExecutingContext = new ActionExecutingContext(
            firstActionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object>(),
            Mock.Of<Controller>());
        var firstExecutedContext = CreateActionExecutedContext(firstActionContext);
        var firstResultExecutingContext = CreateResultExecutingContext(firstActionContext, controllerResult);
        var firstResultExecutedContext = CreateResultExecutedContext(firstActionContext, controllerResult);

        // Second request context with SAME body
        var secondActionContext = ArrangeActionContextWithoutContentLength(
            httpMethod, requestHeaders, requestBody,
            new HeaderDictionary(), controllerResult, StatusCodes.Status200OK);

        var secondExecutingContext = new ActionExecutingContext(
            secondActionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object>(),
            Mock.Of<Controller>());
        var secondExecutedContext = CreateActionExecutedContext(secondActionContext);

        IIdempotencyAccessCache idempotencyCache = MemoryDistributedCacheFixture.CreateCacheInstance(
            cacheImplementation, accessLockImplementation);

        var filter1 = new IdempotencyAttributeFilter(
            idempotencyCache,
            _loggerFactory,
            true,
            TimeSpan.FromHours(1).TotalMilliseconds,
            _headerKeyName,
            _distributedCacheKeysPrefix,
            distributedLockTimeout: null,
            cacheOnlySuccessResponses: true,
            isIdempotencyOptional: false);

        var filter2 = new IdempotencyAttributeFilter(
            idempotencyCache,
            _loggerFactory,
            true,
            TimeSpan.FromHours(1).TotalMilliseconds,
            _headerKeyName,
            _distributedCacheKeysPrefix,
            distributedLockTimeout: null,
            cacheOnlySuccessResponses: true,
            isIdempotencyOptional: false);

        // Act - First request should succeed
        await filter1.OnActionExecutionAsync(firstExecutingContext, () => Task.FromResult(firstExecutedContext));
        Assert.Null(firstExecutingContext.Result);

        // Cache the response
        await filter1.OnResultExecutionAsync(firstResultExecutingContext, () => Task.FromResult(firstResultExecutedContext));

        // Act - Second request with SAME body should return cached response
        await filter2.OnActionExecutionAsync(secondExecutingContext, () => Task.FromResult(secondExecutedContext));

        // Assert - The second request should return the cached response (OkObjectResult)
        Assert.NotNull(secondExecutingContext.Result);
        Assert.IsType<OkObjectResult>(secondExecutingContext.Result);

        var cachedResult = secondExecutingContext.Result as OkObjectResult;
        // With System.Text.Json, cached values are JsonElement - compare the content (camelCase)
        var cachedValue = (JsonElement)cachedResult!.Value;
        Assert.Equal(expectedModel.Id, cachedValue.GetProperty("id").GetInt32());
        Assert.Equal(expectedModel.CreatedOn, cachedValue.GetProperty("createdOn").GetDateTime());
    }
}
