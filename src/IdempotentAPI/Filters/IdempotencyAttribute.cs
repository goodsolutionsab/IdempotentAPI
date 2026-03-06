using System;
using System.Collections.Generic;
using System.Text.Json;
using IdempotentAPI.AccessCache;
using IdempotentAPI.Core;
using IdempotentAPI.Telemetry;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdempotentAPI.Filters
{
    /// <summary>
    /// Use Idempotent operations on POST, PUT, PATCH and DELETE HTTP methods
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class IdempotentAttribute : Attribute, IFilterFactory, IIdempotencyOptions
    {
        private TimeSpan _expiresIn = DefaultIdempotencyOptions.ExpiresIn;
        private bool _expiresInSpecified;
        private string _distributedCacheKeysPrefix = DefaultIdempotencyOptions.DistributedCacheKeysPrefix;
        private bool _distributedCacheKeysPrefixSpecified;
        private string _headerKeyName = DefaultIdempotencyOptions.HeaderKeyName;
        private bool _headerKeyNameSpecified;
        private bool _cacheOnlySuccessResponses = DefaultIdempotencyOptions.CacheOnlySuccessResponses;
        private double _distributedLockTimeoutMilli = DefaultIdempotencyOptions.DistributedLockTimeoutMilli;
        private bool _distributedLockTimeoutMilliSpecified;
        private bool _isIdempotencyOptional = DefaultIdempotencyOptions.IsIdempotencyOptional;
        private bool _useProblemDetailsForErrors = DefaultIdempotencyOptions.UseProblemDetailsForErrors;
        private bool _useProblemDetailsForErrorsSpecified;

        public bool IsReusable => false;

        public bool Enabled { get; set; } = true;

        ///<inheritdoc/>
        public int ExpireHours
        {
            get => Convert.ToInt32(_expiresIn.TotalHours);
            set
            {
                _expiresIn = TimeSpan.FromHours(value);
                _expiresInSpecified = true;
            }
        }

        ///<inheritdoc/>
        public double ExpiresInMilliseconds
        {
            get => _expiresIn.TotalMilliseconds;
            set
            {
                _expiresIn = TimeSpan.FromMilliseconds(value);
                _expiresInSpecified = true;
            }
        }

        ///<inheritdoc/>
        public string DistributedCacheKeysPrefix
        {
            get => _distributedCacheKeysPrefix;
            set
            {
                _distributedCacheKeysPrefix = value;
                _distributedCacheKeysPrefixSpecified = true;
            }
        }

        ///<inheritdoc/>
        public string HeaderKeyName
        {
            get => _headerKeyName;
            set
            {
                _headerKeyName = value;
                _headerKeyNameSpecified = true;
            }
        }

        ///<inheritdoc/>
        public bool CacheOnlySuccessResponses
        {
            get => _cacheOnlySuccessResponses;
            set
            {
                _cacheOnlySuccessResponses = value;
                CacheOnlySuccessResponsesSpecified = true;
            }
        }

        ///<inheritdoc/>
        public bool CacheOnlySuccessResponsesSpecified { get; private set; }

        ///<inheritdoc/>
        public double DistributedLockTimeoutMilli
        {
            get => _distributedLockTimeoutMilli;
            set
            {
                _distributedLockTimeoutMilli = value;
                _distributedLockTimeoutMilliSpecified = true;
            }
        }

        ///<inheritdoc/>
        public bool IsIdempotencyOptional
        {
            get => _isIdempotencyOptional;
            set
            {
                _isIdempotencyOptional = value;
                IsIdempotencyOptionalSpecified = true;
            }
        }

        ///<inheritdoc/>
        public bool IsIdempotencyOptionalSpecified { get; private set; }

        /// <summary>
        /// By default, idempotency settings are taken from the registered <see cref="IIdempotencyOptions"/>
        /// in the ServiceCollection.
        /// Set this flag to false to use only values defined on this attribute.
        /// </summary>
        public bool UseIdempotencyOption { get; set; } = true;

        public JsonSerializerOptions? SerializerOptions { get => null; set => throw new NotImplementedException(); }
        public List<Type>? ExcludeRequestSpecialTypes { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        ///<inheritdoc/>
        public bool UseProblemDetailsForErrors
        {
            get => _useProblemDetailsForErrors;
            set
            {
                _useProblemDetailsForErrors = value;
                _useProblemDetailsForErrorsSpecified = true;
            }
        }

        public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        {
            var distributedCache = (IIdempotencyAccessCache)serviceProvider.GetService(typeof(IIdempotencyAccessCache));
            var loggerFactory = (ILoggerFactory)serviceProvider.GetService(typeof(ILoggerFactory));
            var metrics = serviceProvider.GetService<IIdempotencyMetrics>();

            var generalIdempotencyOptions = serviceProvider.GetService<IIdempotencyOptions>();
            var useGlobalOptions = UseIdempotencyOption && generalIdempotencyOptions != null;
            var expiresInMilliseconds = useGlobalOptions && !_expiresInSpecified
                ? generalIdempotencyOptions!.ExpiresInMilliseconds
                : ExpiresInMilliseconds;
            var headerKeyName = useGlobalOptions && !_headerKeyNameSpecified
                ? generalIdempotencyOptions!.HeaderKeyName
                : HeaderKeyName;
            var distributedCacheKeysPrefix = useGlobalOptions && !_distributedCacheKeysPrefixSpecified
                ? generalIdempotencyOptions!.DistributedCacheKeysPrefix
                : DistributedCacheKeysPrefix;
            var distributedLockTimeoutMilli = useGlobalOptions && !_distributedLockTimeoutMilliSpecified
                ? generalIdempotencyOptions!.DistributedLockTimeoutMilli
                : DistributedLockTimeoutMilli;
            var distributedLockTimeout = distributedLockTimeoutMilli >= 0
                ? (TimeSpan?)TimeSpan.FromMilliseconds(distributedLockTimeoutMilli)
                : null;

            // Use global options as the base when enabled, and override with explicitly set attribute properties.
            var cacheOnlySuccessResponses = useGlobalOptions && !CacheOnlySuccessResponsesSpecified
                ? generalIdempotencyOptions!.CacheOnlySuccessResponses
                : CacheOnlySuccessResponses;

            var isIdempotencyOptional = useGlobalOptions && !IsIdempotencyOptionalSpecified
                ? generalIdempotencyOptions!.IsIdempotencyOptional
                : IsIdempotencyOptional;

            var useProblemDetailsForErrors = useGlobalOptions && !_useProblemDetailsForErrorsSpecified
                ? generalIdempotencyOptions!.UseProblemDetailsForErrors
                : UseProblemDetailsForErrors;

            return new IdempotencyAttributeFilter(
                distributedCache,
                loggerFactory,
                Enabled,
                expiresInMilliseconds,
                headerKeyName,
                distributedCacheKeysPrefix,
                distributedLockTimeout,
                cacheOnlySuccessResponses,
                isIdempotencyOptional,
                generalIdempotencyOptions?.SerializerOptions,
                useProblemDetailsForErrors,
                metrics);
        }
    }
}
