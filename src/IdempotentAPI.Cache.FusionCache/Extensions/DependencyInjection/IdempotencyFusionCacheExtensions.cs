using System;
using IdempotentAPI.Cache.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace IdempotentAPI.Cache.FusionCache.Extensions.DependencyInjection
{
    public static class IdempotencyFusionCacheExtensions
    {
        /// <summary>
        /// Register and configure the FusionCache services that the IdempotentAPI library needs.
        /// <list type="bullet">
        ///     <item>TIP: If the FusionCache services are already registered, then you should use the <see cref="AddIdempotentAPIUsingRegisteredFusionCache"/>.</item>
        /// </list>
        /// </summary>
        /// <param name="serviceCollection"></param>
        /// <param name="cacheEntryOptions">Read the following URL for instructions: https://github.com/jodydonetti/ZiggyCreatures.FusionCache/blob/main/docs/StepByStep.md</param>
        /// <param name="distributedCacheCircuitBreakerDuration">To temporarily disable the distributed cache in case of hard errors so that, if the distributed cache is having issues, it will have less requests to handle and maybe it will be able to get back on its feet.</param>
        /// <returns></returns>
        public static IServiceCollection AddIdempotentAPIUsingFusionCache(
            this IServiceCollection serviceCollection,
            FusionCacheEntryOptions? cacheEntryOptions = null,
            TimeSpan? distributedCacheCircuitBreakerDuration = null)
        {
            // Register the FusionCache implementation of for the IIdempotencyCache
            serviceCollection.AddSingleton<IIdempotencyCache, IdempotencyFusionCache>();

            // Register the FusionCache.
            // In FusionCache v2+, components are NOT auto-discovered by default.
            // We explicitly discover the distributed cache (L2) so that cross-instance
            // idempotency detection works. We use TryWithRegisteredDistributedCache()
            // (not TryWithAutoSetup()) to avoid picking up an IMemoryCache from DI
            // that may have a SizeLimit configured, which would cause
            // "Cache entry must specify a value for Size when SizeLimit is set" errors.
            // The serializer is still auto-discovered by FusionCache v2 by default.
            serviceCollection.AddFusionCache()
                .TryWithRegisteredDistributedCache()
                .WithOptions(options =>
                {
                    if (cacheEntryOptions != null)
                        options.DefaultEntryOptions = cacheEntryOptions;

                    if (distributedCacheCircuitBreakerDuration.HasValue)
                        options.DistributedCacheCircuitBreakerDuration = distributedCacheCircuitBreakerDuration.Value;
                });

            return serviceCollection;
        }

        /// <summary>
        /// Register the <see cref="IdempotencyFusionCache"/> implementation that uses the already registered <see cref="IFusionCache"/>.
        /// </summary>
        /// <param name="serviceCollection"></param>
        /// <returns></returns>
        public static IServiceCollection AddIdempotentAPIUsingRegisteredFusionCache(this IServiceCollection serviceCollection)
        {
            // Register the FusionCache implementation of for the IIdempotencyCache
            serviceCollection.AddSingleton<IIdempotencyCache, IdempotencyFusionCache>();

            return serviceCollection;
        }
    }
}
