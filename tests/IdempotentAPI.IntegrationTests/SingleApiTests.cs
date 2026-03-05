using System.Net;
using System.Net.Http.Json;
using IdempotentAPI.TestWebAPIs.DTOs;
using Xunit;
using Xunit.Abstractions;

namespace IdempotentAPI.IntegrationTests;

/// <summary>
/// Used for testing a single API
/// NOTE: The API project needs to be running prior to running this test
/// </summary>
public class SingleApiTests : IClassFixture<WebApi1ApplicationFactory>
    , IClassFixture<WebMinimalApi1ApplicationFactory>
    , IClassFixture<WebTestFastEndpointsAPI1ApplicationFactory>
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly HttpClient[] _httpClients;

    private const int WebApiClientIndex = 0;
    private const int WebMinimalApiClientIndex = 1;
    private const int WebFastEndpointsAPIClientIndex = 2;

    public SingleApiTests(
        WebApi1ApplicationFactory api1ApplicationFactory,
        WebMinimalApi1ApplicationFactory minimalApi1ApplicationFactory,
        WebTestFastEndpointsAPI1ApplicationFactory webTestFastEndpointsAPI1ApplicationFactory,
        ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _httpClients = new[]
            {
                api1ApplicationFactory.CreateClient(),
                minimalApi1ApplicationFactory.CreateClient(),
                webTestFastEndpointsAPI1ApplicationFactory.CreateClient(),
            };
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostTest_ShouldReturnCachedResponse(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/test", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/test", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        Assert.Equal(content1, content2);
    }


    [Fact]
    public async Task PostTest_WhenUsingIdempotencyOptionOnWebApiClient_ShouldReturnCachedResponse()
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        int httpClientIndex = WebApiClientIndex;
        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPIPerMethod/testUseIdempotencyOption", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPIPerMethod/testUseIdempotencyOption", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        Assert.Equal(content1, content2);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostTestObject_ShouldReturnCachedResponse(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/testobject", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/testobject", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        Assert.NotNull(content1);
        Assert.NotEqual("null", content1);
        Assert.Equal(content1, content2);
    }


    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostTestDifferentRequestObject_WithSameIdempotencyKey_ShouldReturnBadRequestResponse(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        var requestDTO1 = new RequestDTOs() { Description = "A request body." };
        var requestDTO2 = new RequestDTOs() { Description = "A different request body with the same IdempotencyKey." };

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsJsonAsync("v6/TestingIdempotentAPI/testobjectbody", requestDTO1);
        var response2 = await _httpClients[httpClientIndex].PostAsJsonAsync("v6/TestingIdempotentAPI/testobjectbody", requestDTO2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);

        var content2 = await response2.Content.ReadAsStringAsync();
        Assert.Matches("The Idempotency header key value '.*' was used in a different request\\.", content2);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    public async Task PostTestObjectWithHttpError_ShouldReturnExpectedStatusCode_NotCaching(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        const HttpStatusCode expectedhttpStatusCode = HttpStatusCode.BadGateway;
        const int delaySeconds = 1;
        var response1 = await _httpClients[httpClientIndex].PostAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(expectedhttpStatusCode, response1.StatusCode);
        Assert.Equal(expectedhttpStatusCode, response2.StatusCode);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostJsonTestObjectWithHttpError_ShouldReturnExpectedStatusCode_NotCaching(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        var dummyRequest = new RequestDTOs() { Description = "Empty Body!" };

        // Act
        const HttpStatusCode expectedhttpStatusCode = HttpStatusCode.BadGateway;
        const int delaySeconds = 1;
        var response1 = await _httpClients[httpClientIndex].PostAsJsonAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", dummyRequest);
        var response2 = await _httpClients[httpClientIndex].PostAsJsonAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", dummyRequest);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(expectedhttpStatusCode, response1.StatusCode);
        Assert.Equal(expectedhttpStatusCode, response2.StatusCode);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    public async Task PostTestObjectWithHttpError_ShouldReturnExpectedStatusCode_Cached(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        const HttpStatusCode expectedhttpStatusCode = HttpStatusCode.Created;
        const int delaySeconds = 1;
        var response1 = await _httpClients[httpClientIndex].PostAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(expectedhttpStatusCode, response1.StatusCode);
        Assert.Equal(expectedhttpStatusCode, response2.StatusCode);

        Assert.Equal(string.Empty, content1);
        Assert.Equal(string.Empty, content2);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostJsonTestObjectWithHttpError_ShouldReturnExpectedStatusCode_Cached(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        var dummyRequest = new RequestDTOs() { Description = "Empty Body!" };

        // Act
        const HttpStatusCode expectedhttpStatusCode = HttpStatusCode.Created;
        const int delaySeconds = 1;
        var response1 = await _httpClients[httpClientIndex].PostAsJsonAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", dummyRequest);
        var response2 = await _httpClients[httpClientIndex].PostAsJsonAsync($"v6/TestingIdempotentAPI/testobjectWithHttpError?delaySeconds={delaySeconds}&httpErrorCode={(int)expectedhttpStatusCode}", dummyRequest);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(expectedhttpStatusCode, response1.StatusCode);
        Assert.Equal(expectedhttpStatusCode, response2.StatusCode);

        Assert.True(string.IsNullOrEmpty(content1) || content1 == "null");
        Assert.True(string.IsNullOrEmpty(content2) || content2 == "null");
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    public async Task PostRequestsConcurrent_OnSameAPI_WithErrorResponse_ShouldReturnTheErrorAndA409Response(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        const int delaySeconds = 1;
        var httpPostTask1 = _httpClients[httpClientIndex]
            .PostAsync($"v6/TestingIdempotentAPI/customNotAcceptable406?delaySeconds={delaySeconds}", null);
        var httpPostTask2 = _httpClients[httpClientIndex]
            .PostAsync($"v6/TestingIdempotentAPI/customNotAcceptable406?delaySeconds={delaySeconds}", null);

        await Task.WhenAll(httpPostTask1, httpPostTask2);

        var content1 = await httpPostTask1.Result.Content.ReadAsStringAsync();
        var content2 = await httpPostTask2.Result.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        // Assert
        var resultStatusCodes = new List<HttpStatusCode>
        {
            httpPostTask1.Result.StatusCode,
            httpPostTask2.Result.StatusCode
        };
        Assert.Contains(HttpStatusCode.NotAcceptable, resultStatusCodes);
        Assert.Contains(HttpStatusCode.Conflict, resultStatusCodes);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostJsonRequestsConcurrent_OnSameAPI_WithErrorResponse_ShouldReturnTheErrorAndA409Response(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        var dummyRequest = new RequestDTOs() { Description = "Empty Body!" };

        // Act
        const int delaySeconds = 1;
        var httpPostTask1 = _httpClients[httpClientIndex]
            .PostAsJsonAsync($"v6/TestingIdempotentAPI/customNotAcceptable406?delaySeconds={delaySeconds}", dummyRequest);
        var httpPostTask2 = _httpClients[httpClientIndex]
            .PostAsJsonAsync($"v6/TestingIdempotentAPI/customNotAcceptable406?delaySeconds={delaySeconds}", dummyRequest);

        await Task.WhenAll(httpPostTask1, httpPostTask2);

        var content1 = await httpPostTask1.Result.Content.ReadAsStringAsync();
        var content2 = await httpPostTask2.Result.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        // Assert
        var resultStatusCodes = new List<HttpStatusCode>
        {
            httpPostTask1.Result.StatusCode,
            httpPostTask2.Result.StatusCode
        };
        Assert.Contains(HttpStatusCode.NotAcceptable, resultStatusCodes);
        Assert.Contains(HttpStatusCode.Conflict, resultStatusCodes);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    [InlineData(WebMinimalApiClientIndex)]
    [InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task Post_DifferentEndpoints_SameIdempotentKey_ShouldReturnFailure(int httpClientIndex)
    {
        // Arrange
        var guid = Guid.NewGuid().ToString();

        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();
        _httpClients[httpClientIndex].DefaultRequestHeaders.Add("IdempotencyKey", guid);

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/test", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentAPI/testobject", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    //[InlineData(WebMinimalApiClientIndex)]
    //[InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostTest_WhenIdempotencyIsOptional_ShouldReturnResponse(int httpClientIndex)
    {
        // Arrange
        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentOptionalAPI/test", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentOptionalAPI/test", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        Assert.NotNull(content1);
        Assert.NotEqual("null", content1);

        Assert.NotNull(content2);
        Assert.NotEqual("null", content2);

        Assert.NotEqual(content1, content2);
    }

    [Theory]
    [InlineData(WebApiClientIndex)]
    //[InlineData(WebMinimalApiClientIndex)]
    //[InlineData(WebFastEndpointsAPIClientIndex)]
    public async Task PostTestObject_WhenIdempotencyIsOptional__ShouldReturnResponse(int httpClientIndex)
    {
        // Arrange
        _httpClients[httpClientIndex].DefaultRequestHeaders.Clear();

        // Act
        var response1 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentOptionalAPI/testobject", null);
        var response2 = await _httpClients[httpClientIndex].PostAsync("v6/TestingIdempotentOptionalAPI/testobject", null);

        // Assert
        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();
        _testOutputHelper.WriteLine($"content1: {Environment.NewLine}{content1}");
        _testOutputHelper.WriteLine($"content2: {Environment.NewLine}{content2}");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        Assert.NotNull(content1);
        Assert.NotEqual("null", content1);

        Assert.NotNull(content2);
        Assert.NotEqual("null", content2);

        Assert.NotEqual(content1, content2);
    }
}
