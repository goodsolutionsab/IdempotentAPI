using System.Collections.Generic;
using System.Text.Json;
using IdempotentAPI.Helpers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IdempotentAPI.UnitTests.HelpersTests
{
    public class Utils_Tests
    {
        [Fact]
        public void DederializeSerializedData_ShoultResultToTheOriginalData()
        {
            // Arrange
            Dictionary<string, object> cacheData = new Dictionary<string, object>();

            // Cache string, int, etc.
            cacheData.Add("Request.Method", "POST");
            cacheData.Add("Response.StatusCode", 200);

            // Cache a Dictionary containing a List
            Dictionary<string, List<string>> headers = new Dictionary<string, List<string>>();
            headers.Add("myHeader1", new List<string>() { "value1-1", "value1-2" });
            headers.Add("myHeader2", new List<string>() { "value2-1", "value2-1" });
            cacheData.Add("Response.Headers", headers);

            // Cache a Dictionary containing an object
            Dictionary<string, object> resultObjects = new Dictionary<string, object>();
            CreatedAtRouteResult createdAtRouteResult = new CreatedAtRouteResult("myRoute", new { id = 1 }, new { prop1 = 1, prop2 = "2" });
            resultObjects.Add("ResultType", "ResultType");
            resultObjects.Add("ResultValue", createdAtRouteResult.Value);

            // Cache a Dictionary containing string
            Dictionary<string, string> routeValues = new Dictionary<string, string>();
            routeValues.Add("route1", "routeValue1");
            routeValues.Add("route2", "routeValue2");
            resultObjects.Add("ResultRouteValues", routeValues);

            cacheData.Add("Context.Result", resultObjects);


            // Act

            // Step 1. Serialize data:
            byte[] serializedData = cacheData.Serialize();

            // Step 2. Deserialize the serialized data:
            Dictionary<string, object> cacheDataAfterSerialization =
                serializedData.DeSerialize<Dictionary<string, object>>();


            // Assert
            // With System.Text.Json, deserialized values are JsonElement when target type is object.
            // We need to verify the data can be correctly extracted using our helper methods.

            // Verify primitive values
            Assert.Equal("POST", cacheDataAfterSerialization["Request.Method"].GetStringValue());
            Assert.Equal(200, cacheDataAfterSerialization["Response.StatusCode"].GetInt32());

            // Verify headers dictionary
            var deserializedHeaders = cacheDataAfterSerialization["Response.Headers"].ToDictionaryStringListString();
            Assert.True(deserializedHeaders.ContainsKey("myHeader1"));
            Assert.Equal(new List<string> { "value1-1", "value1-2" }, deserializedHeaders["myHeader1"]);
            Assert.True(deserializedHeaders.ContainsKey("myHeader2"));
            Assert.Equal(new List<string> { "value2-1", "value2-1" }, deserializedHeaders["myHeader2"]);

            // Verify context result dictionary
            var deserializedResultObjects = cacheDataAfterSerialization["Context.Result"].ToDictionaryStringObject();
            Assert.Equal("ResultType", deserializedResultObjects["ResultType"].GetStringValue());

            // Verify route values
            var deserializedRouteValues = deserializedResultObjects["ResultRouteValues"].ToDictionaryStringString();
            Assert.Equal("routeValue1", deserializedRouteValues["route1"]);
            Assert.Equal("routeValue2", deserializedRouteValues["route2"]);

            // Verify ResultValue is preserved as JsonElement (can be serialized to response)
            // Dictionary keys are preserved as-is, but object property names use camelCase
            Assert.IsType<JsonElement>(deserializedResultObjects["ResultValue"]);
            var resultValueElement = (JsonElement)deserializedResultObjects["ResultValue"];
            Assert.Equal(1, resultValueElement.GetProperty("prop1").GetInt32());
            Assert.Equal("2", resultValueElement.GetProperty("prop2").GetString());
        }
    }
}
