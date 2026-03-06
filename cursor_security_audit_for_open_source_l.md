# Security audit for open source library
_Exported on 2/25/2026 at 11:40:30 GMT+1 from Cursor (2.5.20)_

# Security Audit Report: IdempotentAPI Library

**Library:** IdempotentAPI (NEOilx fork)
**Scope:** 35 source files across 9 projects
**Date:** 2026-02-24

> **Note:** This is a **fork** of the original `ikyriak/IdempotentAPI` library, published under the package name `NEOilx.IdempotentAPI`. The `.csproj` description states *"neoilx fork with ContentLength fix"*. This means you are not pulling from the original maintainer, which warrants extra scrutiny on ongoing maintenance and patch cadence.

---

## Executive Summary

The library is reasonably well-structured for its purpose (HTTP idempotency via caching). It avoids many common pitfalls (no SQL, no file system writes, no `BinaryFormatter`). However, the audit identified **2 high-severity**, **3 medium-severity**, and **several low-severity** findings that should be understood before adoption.

---

## HIGH Severity

### H1: Unsafe Type Instantiation via Reflection from Cache Data

**Location:** `Idempotency.cs` lines 352, 374-390

```349:390:IdempotentAPI/Core/Idempotency.cs
int responseStatusCode = cacheData["Response.StatusCode"].GetInt32();

Dictionary<string, object> resultObjects = cacheData["Context.Result"].ToDictionaryStringObject();
Type contextResultType = Type.GetType(resultObjects["ResultType"].GetStringValue());
// ...
ConstructorInfo ctor = contextResultType.GetConstructor(new[] { typeof(object) });
if (ctor != null && ctor.DeclaringType != typeof(ObjectResult))
{
    context.Result = (IActionResult)ctor.Invoke(new object?[] { value });
}
// ...
ConstructorInfo ctor = contextResultType.GetConstructor(Array.Empty<Type>());
if (ctor != null)
{
    context.Result = (IActionResult)ctor.Invoke(Array.Empty<object>());
}
```

The `AssemblyQualifiedName` of the original `IActionResult` type is serialized into the cache. On replay, `Type.GetType()` resolves it and `ctor.Invoke()` instantiates it. **If an attacker can poison the cache** (e.g., a compromised Redis instance, a shared cache, or a network MITM on an unencrypted Redis connection), they can control the type name and cause the application to instantiate arbitrary types. While the `(IActionResult)` cast restricts to types implementing that interface, this could still lead to unexpected side effects in constructors of attacker-chosen types.

**Risk:** Remote code execution / type confusion if cache is compromised.
**Mitigation:** Whitelist allowed `IActionResult` types instead of using `Type.GetType` on untrusted data. Ensure your cache connection is encrypted (TLS) and authenticated.

---

### H2: Non-Atomic Get-Then-Set Race Condition (Multi-Instance)

**Location:** `IdempotencyDistributedCache.cs` lines 79-85

```79:85:IdempotentAPI.Cache.DistributedCache/IdempotencyDistributedCache.cs
byte[] cachedData = _distributedCache.Get(key);
if (cachedData is null)
{
    await _distributedCache.SetAsync(key, defaultValue, (DistributedCacheEntryOptions?)options, token)
        .ConfigureAwait(false);
    return defaultValue;
}
```

The `GetOrSet` method uses a non-atomic GET-then-SET pattern. The `InProcessAccessLock` (semaphore-per-key) protects against races within a single process, but **in a multi-instance deployment without a distributed lock provider configured**, two instances can both see a cache miss and both proceed to execute the request. This **defeats the core purpose of the library** -- idempotency.

**Risk:** Duplicate request execution in multi-instance deployments if distributed locking is not configured.
**Mitigation:** Always configure a distributed lock provider (RedLock or Medalion) in any multi-instance deployment. Consider making this a required dependency or surfacing a loud warning.

---

## MEDIUM Severity

### M1: No Idempotency Key Length or Format Validation

**Location:** `Idempotency.cs` lines 707-731

```707:731:IdempotentAPI/Core/Idempotency.cs
private string GetIdempotencyKeyOrThrow(HttpRequest httpRequest)
{
    if (!httpRequest.Headers.ContainsKey(_headerKeyName))
    // ... checks for existence, single value, non-empty ...
    return idempotencyKeys.ToString();
}
```

The idempotency key is validated only for: presence, single value, and non-empty. There is **no maximum length limit** and **no format restriction**. An attacker can:
- Send extremely long keys to consume cache storage and memory
- Send keys with special characters (newlines, control characters) that could affect log integrity
- Use predictable keys to probe whether specific requests have been cached

**Risk:** DoS via cache flooding, log injection.
**Mitigation:** Enforce a maximum key length (e.g., 128 characters), restrict to alphanumeric/UUID format, or hash the key before use as cache key.

---

### M2: GZip Decompression Without Size Limits

**Location:** `Utils.cs` lines 127-150

```127:150:IdempotentAPI/Helpers/Utils.cs
public static byte[]? Decompress(byte[]? input)
{
    // ...
    using (var zip = new GZipStream(inputStream, CompressionMode.Decompress))
    {
        zip.CopyTo(outputStream);
    }
    decompressedData = outputStream.ToArray();
    // ...
}
```

Cache data is GZip-compressed before storage and decompressed on retrieval. Decompression has no size limit. If the cache is poisoned with a **GZip bomb** (a small compressed payload that expands to gigabytes), this will cause an out-of-memory condition.

**Risk:** Denial of service via OOM if cache is compromised.
**Mitigation:** Add a maximum decompressed size check during decompression.

---

### M3: Race Condition in `InProcessAccessLock.Dispose`

**Location:** `InProcessAccessLock.cs` lines 39-65

```39:65:IdempotentAPI.AccessCache/Lockers/InProcessAccessLock.cs
public void Dispose()
{
    while (true)
    {
        if (!lockValueMapper.TryGetValue(_lockedValue, out var minlockValueMapperiLock))
            break;
        var updatedLock = minlockValueMapperiLock with { Count = minlockValueMapperiLock.Count - 1 };
        if (lockValueMapper.TryUpdate(_lockedValue, updatedLock, minlockValueMapperiLock))
        {
            if (updatedLock.Count == 0 && lockValueMapper.TryRemove(_lockedValue, out var removedLock))
            {
                removedLock.Semaphore.Value.Release();
                removedLock.Semaphore.Value.Dispose();
            }
            // ...
        }
    }
}
```

Between `TryUpdate` (setting Count=0) and `TryRemove`, a new thread can call `AddOrUpdate`, creating a new entry with Count=1 that reuses the same key. The subsequent `TryRemove` will remove the new entry and **dispose its semaphore while another thread is waiting on it**, causing an `ObjectDisposedException`.

**Risk:** Intermittent exceptions under high concurrency on the same idempotency key, potentially breaking the locking mechanism.
**Mitigation:** This is difficult to trigger but possible under load. Monitor for `ObjectDisposedException` from `SemaphoreSlim`.

---

## LOW Severity

### L1: Obsolete Cryptographic API

**Location:** `Idempotency.cs` line 97

```csharp
_hashAlgorithm = new SHA256CryptoServiceProvider();
```

`SHA256CryptoServiceProvider` is marked obsolete. Use `SHA256.Create()` instead. The algorithm (SHA-256) is fine for this use case (request fingerprinting, not password hashing).

---

### L2: String Interpolation in Log Messages

**Location:** `Idempotency.cs` lines 693, 697

```csharp
_logger.LogError(distributedLockNotAcquiredException.InnerException,
    $"IdempotencyFilterAttribute [{message}]: DistributedLockNotAcquired. {distributedLockNotAcquiredException.Message}");
```

String interpolation is used instead of structured logging placeholders. This bypasses the logger's ability to defer string formatting (minor performance issue) and could contribute to log injection if `message` or exception messages contain crafted text.

---

### L3: User-Controlled Data Reflected in Error Responses

**Location:** `Idempotency.cs` line 336, 342

```csharp
Detail = $"The Idempotency header key value '{_idempotencyKey}' was used in a different request."
```

The user-supplied idempotency key is reflected in the error response body. For a JSON API this is low risk, but if the response were rendered as HTML, this would be an XSS vector.

---

### L4: Entire Request Body Read into Memory for Hashing

**Location:** `Idempotency.cs` lines 619-678

The full request body (including uploaded files) is read into `MemoryStream` for hash computation. For very large request bodies, this could be a DoS vector. The library does use `ArrayPool` for body reading in the Minimal API path, but the MVC path reads form files fully into memory.

---

### L5: Cache Data Not Encrypted at Rest

Cached data (serialized response bodies, headers, status codes) is GZip-compressed but **not encrypted**. If the cache backend (Redis, etc.) is compromised, all cached API responses are readable.

---

## INFORMATIONAL

### I1: Dependency Versions

| Package | Version Used | Notes |
|---------|-------------|-------|
| `Microsoft.AspNetCore.Mvc.Core` | 2.2.5 | Very old (2019). Only for netstandard2.0/2.1 targets. |
| `System.Text.Json` | 8.0.5 | Reasonably current. |
| `ZiggyCreatures.FusionCache` | 0.13.0 | Very outdated. Current is 1.x+. Potential bugs/CVEs. |
| `RedLock.net` | 2.3.2 | Acceptable. |
| `DistributedLock.Core` | 1.0.5 | Acceptable. |
| `Microsoft.Extensions.*` | 6.0.0 | End of support. Consider 8.0.x. |

### I2: Default Configuration Concerns

- Default cache expiry: **24 hours** -- long-lived cached responses increase the window for stale data
- Default distributed lock timeout: **-1** (disabled) -- distributed locking is opt-in, which is dangerous in multi-instance deployments (see H2)
- `IsIdempotencyOptional` defaults to **false** (good)
- `CacheOnlySuccessResponses` defaults to **true** (good)

### I3: No CSRF/Anti-Forgery Integration

The library does not interact with anti-forgery tokens, which is expected since it targets API endpoints. Just ensure your API endpoints have proper authentication before the idempotency filter runs.

---

## Recommendations Summary

| Priority | Action |
|----------|--------|
| **Must** | Configure a distributed lock provider (RedLock/Medalion) for any multi-instance deployment |
| **Must** | Secure your cache connection with TLS and authentication (Redis AUTH + TLS) |
| **Should** | Add maximum length validation to idempotency keys before adoption (wrapper or PR) |
| **Should** | Be aware of the `Type.GetType` reflection risk; consider forking and adding a type whitelist |
| **Should** | Update `ZiggyCreatures.FusionCache` to 1.x if using FusionCache |
| **Should** | Update `Microsoft.Extensions.*` packages to 8.0.x |
| **Could** | Add decompression size limits to `Utils.Decompress` |
| **Could** | Replace `SHA256CryptoServiceProvider` with `SHA256.Create()` |
| **Could** | Switch log messages to structured logging format |

---

**Overall Assessment:** The library is suitable for use with mitigations. The most critical action item is ensuring your cache infrastructure is secured (TLS + auth) and that distributed locking is properly configured. The reflection-based type instantiation from cache data (H1) is the most concerning code-level finding and warrants monitoring or a whitelist patch.

Let me know if you'd like me to dive deeper into any specific finding, propose concrete code fixes for any of these issues, or help you set up a secure configuration for your deployment.

