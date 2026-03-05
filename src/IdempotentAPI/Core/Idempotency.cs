#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IdempotentAPI.AccessCache;
using IdempotentAPI.AccessCache.Exceptions;
using IdempotentAPI.Exceptions;
using IdempotentAPI.Extensions;
using IdempotentAPI.Helpers;
using IdempotentAPI.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace IdempotentAPI.Core
{
    public class Idempotency
    {
        private const string FastEndpointsResponseItemKey = "FastEndpointsResponse";
        private const string FastEndpointsEndpointFactory = "FastEndpoints.EndpointFactory";
        private readonly object _cacheEntryOptions;
        private readonly bool _cacheOnlySuccessResponses;
        private readonly IIdempotencyAccessCache _distributedCache;
        private readonly string _distributedCacheKeysPrefix;
        private readonly TimeSpan? _distributedLockTimeout;
        /// <summary>
        /// The read-only list of HTTP Header Keys will be handled from the selected HTTP Server and
        /// not included in the cache.
        /// </summary>
        /// <remarks>
        /// Content-Type is excluded because storing it with charset (e.g., "application/json; charset=utf-8")
        /// can cause 406 NotAcceptable errors due to content negotiation conflicts when the cached response
        /// is returned. ASP.NET Core's output formatters will set the correct Content-Type anyway.
        /// See: https://github.com/ikyriak/IdempotentAPI/issues/78
        /// </remarks>
        private readonly IReadOnlyList<string> _excludeHttpHeaderKeys = new List<string>() { "Transfer-Encoding", "Content-Type" };

        private readonly double _expiresInMilliseconds;
        private readonly HashAlgorithm _hashAlgorithm;
        private readonly string _headerKeyName;
        private readonly ILogger<Idempotency>? _logger;
        private readonly bool _isIdempotencyOptional;
        private readonly JsonSerializerOptions? _serializerOptions = null;
        private readonly List<Type>? _excludeRequestSpecialTypes = null;
        private readonly bool _useProblemDetailsForErrors;
        private readonly IIdempotencyMetrics _metrics;

        private string _idempotencyKey = string.Empty;
        private bool _isPreIdempotencyApplied = false;
        private bool _isPreIdempotencyCacheReturned = false;

        [Obsolete("Use the double Milliseconds overload")]
        public Idempotency(
            IIdempotencyAccessCache distributedCache,
            ILogger<Idempotency> logger,
            int expireHours,
            string headerKeyName,
            string distributedCacheKeysPrefix,
            TimeSpan? distributedLockTimeout,
            bool cacheOnlySuccessResponses,
            bool isIdempotencyOptional) : this(distributedCache, logger, TimeSpan.FromHours(expireHours).TotalMilliseconds, headerKeyName, distributedCacheKeysPrefix, distributedLockTimeout, cacheOnlySuccessResponses, isIdempotencyOptional)
        {
        }

        public Idempotency(
             IIdempotencyAccessCache distributedCache,
             ILogger<Idempotency> logger,
             double expiresInMilliseconds,
             string headerKeyName,
             string distributedCacheKeysPrefix,
             TimeSpan? distributedLockTimeout,
             bool cacheOnlySuccessResponses,
             bool isIdempotencyOptional,
             JsonSerializerOptions? serializerOptions = null,
             List<Type>? excludeRequestSpecialTypes = null,
             bool useProblemDetailsForErrors = false,
             IIdempotencyMetrics? metrics = null)
        {
            _distributedCache = distributedCache ?? throw new ArgumentNullException($"An {nameof(IIdempotencyAccessCache)} is not configured. You should register the required services by using the \"AddIdempotentAPIUsing{{YourCacheProvider}}\" function.");
            _expiresInMilliseconds = expiresInMilliseconds;
            _headerKeyName = headerKeyName;
            _distributedCacheKeysPrefix = distributedCacheKeysPrefix;
            _distributedLockTimeout = distributedLockTimeout;
            _logger = logger;
            _metrics = metrics ?? NullIdempotencyMetrics.Instance;

            _hashAlgorithm = new SHA256CryptoServiceProvider();
            _cacheEntryOptions = _distributedCache.CreateCacheEntryOptions(_expiresInMilliseconds);
            _cacheOnlySuccessResponses = cacheOnlySuccessResponses;
            _isIdempotencyOptional = isIdempotencyOptional;
            _serializerOptions = serializerOptions;
            _excludeRequestSpecialTypes = excludeRequestSpecialTypes;
            _useProblemDetailsForErrors = useProblemDetailsForErrors;
        }

        private string DistributedCacheKey
        {
            set
            {
                _idempotencyKey = value;
            }
            get
            {
                return _distributedCacheKeysPrefix + _idempotencyKey;
            }
        }

        /// <summary>
        /// Cache the Response in relation to the provided idempotencyKey
        /// </summary>
        /// <param name="context"></param>
        public async Task ApplyPostIdempotency(ResultExecutingContext context)
        {
            if (IsLoggerEnabled(LogLevel.Information))
            {
                _logger.LogInformation("IdempotencyFilterAttribute [After Controller execution]: Response for {HttpContextResponseStatusCode} sent ({HttpContextResponseContentLength} bytes)", context.HttpContext.Response.StatusCode, context.HttpContext.Response.ContentLength ?? 0);
            }

            if (!_isPreIdempotencyApplied || _isPreIdempotencyCacheReturned)
            {
                if (IsLoggerEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("IdempotencyFilterAttribute [After Controller execution]: SKIPPED (isPreIdempotencyApplied:{_isPreIdempotencyApplied}, isPreIdempotencyCacheReturned:{_isPreIdempotencyCacheReturned})", _isPreIdempotencyApplied, _isPreIdempotencyCacheReturned);
                }
                return;
            }

            // Return when the current response is unsuccessful, and we should accept only the success status codes.
            if (_cacheOnlySuccessResponses
                && !context.HttpContext.Response.IsSuccessStatusCode())
            {
                try
                {
                    await _distributedCache.Remove(DistributedCacheKey, _distributedLockTimeout)
                        .ConfigureAwait(false);
                }
                catch (DistributedLockNotAcquiredException distributedLockNotAcquiredException)
                {
                    LogDistributedLockNotAcquiredException("After Controller execution", distributedLockNotAcquiredException);
                    _metrics.RecordLockFailure("remove");
                }

                _metrics.RecordNonSuccessSkip(
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value ?? string.Empty,
                    context.HttpContext.Response.StatusCode);

                if (IsLoggerEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("IdempotencyFilterAttribute [After Controller execution]: SKIPPED (Status Code {StatusCode} is not success 2xx).", context.HttpContext.Response.StatusCode);
                }
                return;
            }

            // Generate the data to be cached
            byte[]? cacheDataBytes = GenerateCacheData(context);

            // Save to cache:
            try
            {
                await _distributedCache.Set(DistributedCacheKey, cacheDataBytes, _cacheEntryOptions, _distributedLockTimeout)
                    .ConfigureAwait(false);
            }
            catch (DistributedLockNotAcquiredException distributedLockNotAcquiredException)
            {
                LogDistributedLockNotAcquiredException("After Controller execution", distributedLockNotAcquiredException);
                _metrics.RecordLockFailure("set");
                return;
            }

            _metrics.RecordCacheStore(
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path.Value ?? string.Empty);

            if (IsLoggerEnabled(LogLevel.Information))
            {
                _logger.LogInformation("IdempotencyFilterAttribute [After Controller execution]: Result is cached for idempotencyKey: {idempotencyKey}", _idempotencyKey);
            }
        }

        public async Task PrepareIdempotency(ResourceExecutingContext context)
        {
            // Check if Idempotency can be applied:
            if (!CanPerformIdempotency(context.HttpContext.Request))
            {
                return;
            }

            string requestsDataHash = await GenerateRequestsDataHashAsync(context.HttpContext.Request);

            context.HttpContext.SetRequestsDataHash(requestsDataHash);
        }

        public async Task PrepareMinimalApiIdempotencyAsync(HttpContext httpContext, IList<object?> arguments)
        {
            // Check if Idempotency can be applied:
            if (!CanPerformIdempotency(httpContext.Request))
            {
                return;
            }

            // Remove the special types because they causes a self-referencing loop when serializing.
            // In addition, they are unnecessary to generate the request's data hash.
            // Reference: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-8.0#special-types
            var filteredArguments = arguments.Where(a =>
                a is not null
                && (a is not HttpRequest
                    and not HttpContext
                    and not HttpResponse
                    and not ClaimsPrincipal
                    and not CancellationToken)
                && (_excludeRequestSpecialTypes is null || 
                    (_excludeRequestSpecialTypes is not null && 
                     !_excludeRequestSpecialTypes.Any(t => a.GetType() == t || a.GetType().IsSubclassOf(t))))
                ).ToList();

            string requestsDataHash = await GenerateRequestsDataHashMinimalApiAsync(filteredArguments, httpContext.Request);

            httpContext.SetRequestsDataHash(requestsDataHash);
        }

        /// <summary>
        /// Return the cached response based on the provided idempotencyKey
        /// </summary>
        /// <param name="context"></param>
        public async Task ApplyPreIdempotency(ActionExecutingContext context)
        {
            if (IsLoggerEnabled(LogLevel.Information))
            {
                _logger.LogInformation("IdempotencyFilterAttribute [Before Controller execution]: Request for {HttpContextRequestMethod}: {HttpContextRequestPath} received ({HttpContextRequestContentLength} bytes)", context.HttpContext.Request.Method, context.HttpContext.Request.Path, context.HttpContext.Request.ContentLength ?? 0);
            }

            // Check if Idempotency can be applied:
            if (!CanPerformIdempotency(context.HttpContext.Request))
            {
                if (IsLoggerEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("IdempotencyFilterAttribute [Before Controller execution]: Idempotency SKIPPED, httpRequest Method is: {httpRequestMethod}", context.HttpContext.Request.Method.ToString());
                }

                return;
            }

            // Try to get the IdempotencyKey value from header. If its optional and not found just return.
            if (_isIdempotencyOptional
                && !TryGetIdempotencyKey(context.HttpContext.Request, out _idempotencyKey))
            {
                if (IsLoggerEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("IdempotencyFilterAttribute [Before Controller execution]: Idempotency SKIPPED, because it is optional, and a unique IdempotencyKey was not found.");
                }

                return;
            }
            else
            {
                _idempotencyKey = GetIdempotencyKeyOrThrow(context.HttpContext.Request);
            }

            // Generate the request data hash for the current request
            // This is needed for both:
            // 1. Comparing with cached hash when cache hit occurs
            // 2. Storing in cache when the request completes
            // Note: For Minimal APIs, the hash is already set by PrepareMinimalApiIdempotencyAsync
            // so we only generate it here if not already present
            string requestsDataHash = context.HttpContext.GetRequestsDataHash();
            if (string.IsNullOrEmpty(requestsDataHash))
            {
                requestsDataHash = await GenerateRequestsDataHashAsync(context.HttpContext.Request);
                context.HttpContext.SetRequestsDataHash(requestsDataHash);
            }

            // Check if idempotencyKey exists in cache and return value:
            Guid uniqueRequestId = Guid.NewGuid();
            byte[] cacheDataBytes;

            try
            {
                cacheDataBytes = await _distributedCache.GetOrSet(
                    DistributedCacheKey,
                    defaultValue: GenerateRequestInFlightCacheData(uniqueRequestId),
                    options: _cacheEntryOptions,
                    distributedLockTimeout: _distributedLockTimeout)
                    .ConfigureAwait(false);
            }
            catch (DistributedLockNotAcquiredException distributedLockNotAcquiredException)
            {
                LogDistributedLockNotAcquiredException("Before Controller", distributedLockNotAcquiredException);
                _metrics.RecordLockFailure("get_or_set");

                context.Result = new ConflictResult();
                return;
            }

            IReadOnlyDictionary<string, object>? cacheData = cacheDataBytes.DeSerialize<IReadOnlyDictionary<string, object>>(_serializerOptions);
            if (cacheData is null)
            {
                throw new Exception("Cannot DeSerialize cached data.");
            }

            // RPG - 2021-07-05 - Check if there is a copy of this request in flight, if so return a 409 HTTP Conflict response.
            if (cacheData.ContainsKey("Request.Inflight")
                && uniqueRequestId.ToString().ToLower() != cacheData["Request.Inflight"].ToString().ToLower())
            {
                context.Result = new ConflictResult();
                return;
            }

            if (!cacheData.ContainsKey("Request.Inflight"))
            {
                // 2019-07-06: Evaluate the "Request.DataHash" in order to be sure that the cached
                // response is returned for the same combination of IdempotencyKey and Request
                string cachedRequestDataHash = cacheData["Request.DataHash"].ToString();
                string currentRequestDataHash = context.HttpContext.GetRequestsDataHash();
                if (cachedRequestDataHash != currentRequestDataHash)
                {
                    _metrics.RecordHashMismatch();

                    if (_useProblemDetailsForErrors)
                    {
                        context.Result = new BadRequestObjectResult(new ProblemDetails
                        {
                            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                            Title = "Bad Request",
                            Status = StatusCodes.Status400BadRequest,
                            Detail = $"The Idempotency header key value '{_idempotencyKey}' was used in a different request.",
                            Instance = context.HttpContext.Request.Path
                        });
                    }
                    else
                    {
                        context.Result = new BadRequestObjectResult($"The Idempotency header key value '{_idempotencyKey}' was used in a different request.");
                    }
                    return;
                }

                // Set the StatusCode and Response result (based on the IActionResult type)
                // The response body will be created from a .NET middle-ware in a following step.
                int responseStatusCode = cacheData["Response.StatusCode"].GetInt32();

                Dictionary<string, object> resultObjects = cacheData["Context.Result"].ToDictionaryStringObject();
                Type contextResultType = Type.GetType(resultObjects["ResultType"].GetStringValue());
                if (contextResultType == null)
                {
                    throw new NotImplementedException($"ApplyPreIdempotency, ResultType {resultObjects["ResultType"]} is not recognized");
                }


                // Initialize the IActionResult based on its type:
                if (contextResultType == typeof(CreatedAtRouteResult))
                {
                    // Normalize JsonElement null to C# null to avoid returning "null" string in response body
                    object? value = resultObjects["ResultValue"].NormalizeJsonNull();
                    string routeName = resultObjects["ResultRouteName"].GetStringValue() ?? string.Empty;
                    Dictionary<string, string> RouteValues = resultObjects["ResultRouteValues"].ToDictionaryStringString();

                    context.Result = new CreatedAtRouteResult(routeName, RouteValues, value);
                }
                else if (contextResultType.BaseType == typeof(ObjectResult)
                    || contextResultType == typeof(ObjectResult))
                {
                    // Normalize JsonElement null to C# null to avoid returning "null" string in response body
                    object? value = resultObjects["ResultValue"].NormalizeJsonNull();
                    ConstructorInfo ctor = contextResultType.GetConstructor(new[] { typeof(object) });
                    if (ctor != null && ctor.DeclaringType != typeof(ObjectResult))
                    {
                        context.Result = (IActionResult)ctor.Invoke(new object?[] { value });
                    }
                    else
                    {
                        context.Result = new ObjectResult(value) { StatusCode = responseStatusCode };
                    }
                }
                else if (contextResultType.BaseType == typeof(StatusCodeResult)
                    || contextResultType.BaseType == typeof(ActionResult))
                {
                    ConstructorInfo ctor = contextResultType.GetConstructor(Array.Empty<Type>());
                    if (ctor != null)
                    {
                        context.Result = (IActionResult)ctor.Invoke(Array.Empty<object>());
                    }
                }
                else
                {
                    throw new NotImplementedException($"ApplyPreIdempotency is not implemented for IActionResult type {contextResultType}");
                }

                // Include cached headers (if does not exist) at the response:
                Dictionary<string, List<string>>? headerKeyValues = cacheData["Response.Headers"].ToDictionaryStringListString();
                if (headerKeyValues != null)
                {
                    foreach (KeyValuePair<string, List<string>> headerKeyValue in headerKeyValues)
                    {
                        if (!context.HttpContext.Response.Headers.ContainsKey(headerKeyValue.Key))
                        {
                            context.HttpContext.Response.Headers.Add(headerKeyValue.Key, headerKeyValue.Value.ToArray());
                        }
                    }
                }

                _metrics.RecordCacheHit(
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value ?? string.Empty);

                if (IsLoggerEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("IdempotencyFilterAttribute [Before Controller]: Return result from idempotency cache (of type {contextResultType})", contextResultType.ToString());
                }
                _isPreIdempotencyCacheReturned = true;
            }
            else
            {
                // Cache data contains "Request.Inflight" with matching uniqueRequestId
                // This means we just set the inflight marker - it's a cache miss (new request)
                _metrics.RecordCacheMiss(
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value ?? string.Empty);
            }

            if (IsLoggerEnabled(LogLevel.Information))
            {
                _logger.LogInformation("IdempotencyFilterAttribute [Before Controller]: End");
            }
            _isPreIdempotencyApplied = true;
        }

        /// <summary>
        /// Cancel the idempotency by removing the related cached data. For example, this function
        /// can be used when exceptions occur.
        /// </summary>
        public async Task CancelIdempotency()
        {
            _metrics.RecordIdempotencyCancelled();

            try
            {
                await _distributedCache.Remove(DistributedCacheKey, _distributedLockTimeout)
                    .ConfigureAwait(false);
            }
            catch (DistributedLockNotAcquiredException distributedLockNotAcquiredException)
            {
                LogDistributedLockNotAcquiredException("After Controller execution", distributedLockNotAcquiredException);
                _metrics.RecordLockFailure("remove");
            }

            if (IsLoggerEnabled(LogLevel.Information))
            {
                _logger.LogInformation("IdempotencyFilterAttribute [After Controller execution]: SKIPPED (An exception occurred).");
            }
        }

        private bool CanPerformIdempotency(HttpRequest httpRequest)
        {
            // If distributedCache is not configured
            if (_distributedCache == null)
            {
                throw new Exception("An IDistributedCache is not configured.");
            }

            // Idempotency is applied on Post, Put, Patch & Delete HTTP methods:
            // Note: DELETE is included because when used with If-Match headers for optimistic
            // concurrency, retries would get 404 (resource gone) instead of the original 204.
            if (httpRequest.Method != HttpMethods.Post
                && httpRequest.Method != HttpMethods.Put
                && httpRequest.Method != HttpMethods.Patch
                && httpRequest.Method != HttpMethods.Delete)
            {
                return false;
            }

            // For multiple executions of the PreStep:
            if (_isPreIdempotencyApplied)
            {
                return false;
            }

            return true;
        }

        private byte[] GenerateCacheData(ResultExecutingContext context)
        {
            Dictionary<string, object> cacheData = new();
            // Cache Request params:
            cacheData.Add("Request.Method", context.HttpContext.Request.Method);
            cacheData.Add("Request.Path", context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : string.Empty);
            cacheData.Add("Request.QueryString", context.HttpContext.Request.QueryString.ToUriComponent());
            cacheData.Add("Request.DataHash", context.HttpContext.GetRequestsDataHash());

            //Cache Response params:
            cacheData.Add("Response.StatusCode", context.HttpContext.Response.StatusCode);
            cacheData.Add("Response.ContentType", context.HttpContext.Response.ContentType);

            Dictionary<string, List<string>> headers = context.HttpContext.Response.Headers
                .Where(h => !_excludeHttpHeaderKeys.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToList());

            cacheData.Add("Response.Headers", headers);


            // 2019-07-05: Response.Body cannot be accessed because its not yet created.
            // We are saving the Context.Result, because based on this the Response.Body is created.
            Dictionary<string, object> resultObjects = new();
            var contextResult = context.Result;
            resultObjects.Add("ResultType", contextResult.GetType().AssemblyQualifiedName);

            if (context.HttpContext.Items.ContainsKey(FastEndpointsResponseItemKey))
            {
                resultObjects.Add("ResultValue", context.HttpContext.Items[FastEndpointsResponseItemKey]);
            }
            else if (contextResult is CreatedAtRouteResult route)
            {
                //CreatedAtRouteResult.CreatedAtRouteResult(string routeName, object routeValues, object value)
                resultObjects.Add("ResultValue", route.Value);
                resultObjects.Add("ResultRouteName", route.RouteName);

                Dictionary<string, string> RouteValues = route.RouteValues.ToDictionary(r => r.Key, r => r.Value.ToString());
                resultObjects.Add("ResultRouteValues", RouteValues);
            }
            else if (contextResult is ObjectResult objectResult)
            {
                if (objectResult.Value.IsAnonymousType())
                {
                    resultObjects.Add("ResultValue", Utils.AnonymousObjectToDictionary(objectResult.Value, Convert.ToString));
                }
                else
                {
                    resultObjects.Add("ResultValue", objectResult.Value);
                }
            }
            else if (contextResult is StatusCodeResult || contextResult is ActionResult)
            {
                // Known types that do not need additional data
            }
            else
            {
                throw new NotImplementedException($"ApplyPostIdempotency.generateCacheData is not implement for IActionResult type {contextResult.GetType()}");
            }

            cacheData.Add("Context.Result", resultObjects);


            byte[]? serializedCacheData = cacheData.Serialize(_serializerOptions);

            if (serializedCacheData is null)
                throw new Exception("Cannot Serialize the inFlightCacheData.");

            return serializedCacheData;
        }

        private byte[] GenerateRequestInFlightCacheData(Guid guid)
        {
            Dictionary<string, object> inFlightCacheData = new()
            {
                { "Request.Inflight", guid }
            };

            byte[]? serializedCacheData = inFlightCacheData.Serialize(_serializerOptions);
            if (serializedCacheData is null)
                throw new Exception("Cannot Serialize the inFlightCacheData.");

            return serializedCacheData;
        }

        private async Task<string> GenerateRequestsDataHashMinimalApiAsync(IList<object?> arguments, HttpRequest httpRequest)
        {
            List<object?> requestsData = new(arguments);

            // The request's URL:
            if (httpRequest.Path.HasValue)
            {
                requestsData.Add(httpRequest.Path.ToString());
            }

            // Read the post data from the request body when the arguments don't
            // already contain body data. This covers FastEndpoints (v8.0+) where the
            // route delegate has no parameters so arguments is empty, as well as older
            // FE versions that passed an EndpointFactory instance.
            if (arguments.Count == 0
                || arguments.Any(a => a?.GetType().ToString() == FastEndpointsEndpointFactory))
            {
                string requestBodyHash = await GenerateRequestsBodyHashAsync(httpRequest);
                requestsData.Add(requestBodyHash);
            }

            return Utils.GetHash(_hashAlgorithm, requestsData.SerializeToJson(_serializerOptions));
        }

        private async Task<string> GenerateRequestsBodyHashAsync(HttpRequest httpRequest)
        {
            httpRequest.EnableBuffering();
            httpRequest.Body.Position = 0;

            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            int bytesRead;

            string requestBodyHash;
            using (var ms = new MemoryStream())
            {
                while ((bytesRead = await httpRequest.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, bytesRead);
                }

                requestBodyHash = Utils.GetHash(_hashAlgorithm, ms.ToArray());
            }

            ArrayPool<byte>.Shared.Return(buffer);

            httpRequest.Body.Position = 0;
            return requestBodyHash;
        }

        private async Task<string> GenerateRequestsDataHashAsync(HttpRequest httpRequest)
        {
            List<object> requestsData = new();

            // The Request body:
            // Note: We don't check ContentLength.HasValue because in TestServer/WebApplicationFactory
            // scenarios, ContentLength may be null even when the body has content. GetRawBodyAsync()
            // already handles EnableBuffering() and checking if the body can be read.
            if (httpRequest.Body != null)
            {
                string? rawBody = await httpRequest.GetRawBodyAsync();
                if (rawBody != null)
                {
                    requestsData.Add(rawBody);
                }
            }

            // The Form data:
            if (httpRequest.HasFormContentType
                && httpRequest.Form != null)
            {
                requestsData.Add(httpRequest.Form);
            }

            // Form-Files data:
            if (httpRequest.HasFormContentType
                && httpRequest.Form != null
                && httpRequest.Form.Files != null
                && httpRequest.Form.Files.Count > 0)
            {
                foreach (IFormFile formFile in httpRequest.Form.Files)
                {
                    Stream fileStream = formFile.OpenReadStream();
                    if (fileStream.CanRead
                        && fileStream.CanSeek
                        && fileStream.Length > 0)
                    {
                        using (MemoryStream memoryStream = new())
                        {
                            fileStream.Position = 0;
                            await fileStream.CopyToAsync(memoryStream)
                                .ConfigureAwait(false);
                            requestsData.Add(memoryStream.ToArray());
                        }
                    }
                    else
                    {
                        requestsData.Add(formFile.FileName + "_" + formFile.Length.ToString() + "_" + formFile.Name);
                    }
                }
            }

            // The request's URL:
            if (httpRequest.Path.HasValue)
            {
                requestsData.Add(httpRequest.Path.ToString());
            }

            return Utils.GetHash(_hashAlgorithm, requestsData.SerializeToJson(_serializerOptions));
        }

        private bool IsLoggerEnabled(LogLevel logLevel)
        {
            return _logger?.IsEnabled(logLevel) ?? false;
        }

        private void LogDistributedLockNotAcquiredException(
            string message,
            DistributedLockNotAcquiredException distributedLockNotAcquiredException)
        {
            if (IsLoggerEnabled(LogLevel.Error) && distributedLockNotAcquiredException.InnerException is not null)
            {
                _logger.LogError(
                    distributedLockNotAcquiredException.InnerException,
                    $"IdempotencyFilterAttribute [{message}]: DistributedLockNotAcquired. {distributedLockNotAcquiredException.Message}");
            }
            else if (IsLoggerEnabled(LogLevel.Warning))
            {
                _logger.LogWarning($"IdempotencyFilterAttribute [{message}]: DistributedLockNotAcquired. {distributedLockNotAcquiredException.Message}");
            }
        }

        /// <summary>
        /// Get the IdempotencyKey from Headers or Throw Exception.
        /// </summary>
        /// <param name="httpRequest"></param>
        /// <returns></returns>
        /// <exception cref="IdempotencyKeyValidationException"></exception>
        private string GetIdempotencyKeyOrThrow(HttpRequest httpRequest)
        {
            // The "headerKeyName" must be provided as a Header:
            if (!httpRequest.Headers.ContainsKey(_headerKeyName))
            {
                throw new IdempotencyKeyValidationException($"The '{_headerKeyName}' header is required for idempotent requests.");
            }

            if (!httpRequest.Headers.TryGetValue(_headerKeyName, out StringValues idempotencyKeys))
            {
                throw new IdempotencyKeyValidationException($"The '{_headerKeyName}' header value could not be read.");
            }

            if (idempotencyKeys.Count > 1)
            {
                throw new IdempotencyKeyValidationException($"Multiple '{_headerKeyName}' header values were found. Only one value is allowed.");
            }

            if (idempotencyKeys.Count <= 0 || string.IsNullOrEmpty(idempotencyKeys.First()))
            {
                throw new IdempotencyKeyValidationException($"The '{_headerKeyName}' header value is required and cannot be empty.");
            }

            return idempotencyKeys.ToString();
        }

        /// <summary>
        /// Try to get the IdempotencyKey from Headers.
        /// </summary>
        /// <param name="httpRequest"></param>
        /// <param name="idempotencyKey"></param>
        /// <returns></returns>
        private bool TryGetIdempotencyKey(HttpRequest httpRequest, out string idempotencyKey)
        {
            idempotencyKey = string.Empty;

            // The "headerKeyName" must be provided as a Header:
            if (!httpRequest.Headers.ContainsKey(_headerKeyName))
            {
                return false;
            }

            if (!httpRequest.Headers.TryGetValue(_headerKeyName, out StringValues idempotencyKeys))
            {
                return false;
            }

            if (idempotencyKeys.Count > 1)
            {
                return false;
            }

            if (idempotencyKeys.Count <= 0 || string.IsNullOrEmpty(idempotencyKeys.First()))
            {
                return false;
            }

            idempotencyKey = idempotencyKeys.ToString();
            return true;
        }
    }
}
