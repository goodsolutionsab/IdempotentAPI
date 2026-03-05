# Integration Tests

## Prerequisites

**Redis must be running on `localhost:6379`.** The tests use Redis for both FusionCache and MadelsonDistributedLock. The simplest way to start one:

```bash
docker run -d -p 6379:6379 redis
```

## Running the tests

Run the tests directly against the project:

```bash
dotnet test tests/IdempotentAPI.IntegrationTests/IdempotentAPI.IntegrationTests.csproj
```

Or to see verbose output per test:

```bash
dotnet test tests/IdempotentAPI.IntegrationTests/IdempotentAPI.IntegrationTests.csproj --logger "console;verbosity=detailed"
```

Run a specific test class:

```bash
dotnet test tests/IdempotentAPI.IntegrationTests/IdempotentAPI.IntegrationTests.csproj --filter "FullyQualifiedName~SingleApiTests"
```

## What the tests cover

| Test class | What it tests |
|---|---|
| `SingleApiTests` | Single-instance idempotency: cached responses, different request bodies, HTTP errors, concurrent requests, optional idempotency |
| `ContentTypeTests` | Content-Type handling for cached responses (charset, 406 NotAcceptable, duplicate headers) |
| `TestWebAPIsConcurrentTests` | Cluster-style concurrent requests across two app instances, error responses |

## How they work

The tests use `WebApplicationFactory<T>` (from `Microsoft.AspNetCore.Mvc.Testing`) to spin up in-memory test servers for three different API styles: standard Web API, Minimal API, and FastEndpoints. Each factory configures the app to use **FusionCache** and **MadelsonDistributedLock** via in-memory configuration — no `appsettings.json` or environment variables needed beyond having Redis available.
