using Microsoft.EntityFrameworkCore;
using Polly;
using Sportarr.Api.Data;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;
using Sportarr.Services;

namespace Sportarr.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSportarrHttpClients(this IServiceCollection services)
    {
        // For calling Sportarr-API
        services.AddHttpClient();

        // Configure named HttpClient for download clients with proper DNS refresh for Docker container names
        // PooledConnectionLifetime ensures DNS is re-resolved periodically (important for Docker container name resolution)
        services.AddHttpClient("DownloadClient")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Re-resolve DNS every 2 minutes (matches Sonarr behavior for Docker container names)
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                // Don't cache connections indefinitely
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                // Allow redirect following
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(100);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sportarr/1.0");
            });

        // Configure named HttpClient for download clients that need SSL certificate validation bypass
        // Used for self-signed certificates on qBittorrent/other clients behind reverse proxies
        services.AddHttpClient("DownloadClientSkipSsl")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Re-resolve DNS every 2 minutes (matches Sonarr behavior)
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                // Don't cache connections indefinitely
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                // Allow redirect following
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                // Bypass SSL certificate validation for self-signed certs
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
                }
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(100);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sportarr/1.0");
            });

        // Configure HttpClient for TRaSH Guides GitHub API with proper User-Agent
        services.AddHttpClient("TrashGuides")
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sportarr/1.0 (https://github.com/Sportarr/Sportarr)");
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Configure HttpClient for indexer searches with rate limiting and Polly retry policy
        // Rate limiting is now handled at the HTTP layer via RateLimitHandler, matching Sonarr/Radarr
        services.AddHttpClient("IndexerClient")
            .AddHttpMessageHandler<RateLimitHandler>()  // Rate limiting at HTTP layer
            .AddTransientHttpErrorPolicy(policyBuilder =>
                policyBuilder.WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        Console.WriteLine($"[Indexer] Retry {retryCount} after {timespan.TotalSeconds}s due to {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
                    }))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Sportarr/1.0");
            });

        // Configure HttpClient for IPTV stream proxying (avoids CORS issues in browser)
        services.AddHttpClient("StreamProxy")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Allow redirects for stream URLs
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10, // Increased for IPTV providers that chain redirects
                                               // Disable connection pooling for streaming to avoid stale connections
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
            })
            .ConfigureHttpClient(client =>
            {
                // Longer timeout for stream connections
                client.Timeout = TimeSpan.FromMinutes(5);
            });

        // Configure HttpClient for IPTV services (source syncing, channel testing, API calls)
        // This client properly follows HTTP 302 redirects which many IPTV providers use
        services.AddHttpClient("IptvClient")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // CRITICAL: Allow redirects - many IPTV providers (especially Xtream Codes) use 302 redirects
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                // DNS refresh for dynamic IPTV server IPs
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.18 LibVLC/3.0.18");
            });

        return services;
    }

    public static IServiceCollection AddSportarrServices(this IServiceCollection services)
    {
        // Register DownloadClientService - uses IHttpClientFactory for proper HttpClient lifecycle management
        services.AddScoped<IDownloadClientService, DownloadClientService>();
        // Register rate limiting service (Sonarr-style HTTP-level rate limiting)
        services.AddSingleton<IRateLimitService, RateLimitService>();
        // Register RateLimitHandler as transient (one per HttpClient)
        services.AddTransient<RateLimitHandler>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddScoped<UserService>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<SimpleAuthService>();
        services.AddScoped<SessionService>();
        services.AddScoped<IndexerStatusService>(); // Sonarr-style indexer health tracking and backoff
        services.AddScoped<IIndexerSearchService, IndexerSearchService>();
        services.AddScoped<ReleaseMatchingService>(); // Sonarr-style release validation to prevent downloading wrong content
        services.AddSingleton<ReleaseMatchScorer>(); // Match scoring for event-to-release matching
        services.AddScoped<ReleaseCacheService>(); // Local release cache for RSS-first search strategy
        services.AddSingleton<SearchQueueService>(); // Queue for parallel search execution
        services.AddSingleton<SearchResultCache>(); // In-memory cache for raw indexer results (reduces API calls)
        services.AddSingleton<CustomFormatMatchCache>(); // In-memory cache for CF match results (avoids repeated regex evaluation)
        services.AddScoped<IAutomaticSearchService, AutomaticSearchService>();
        services.AddScoped<DelayProfileService>();
        services.AddScoped<QualityDetectionService>();
        services.AddScoped<ReleaseEvaluator>();
        services.AddScoped<ReleaseProfileService>(); // Release profile keyword filtering (Sonarr-style)
        services.AddScoped<MediaFileParser>();
        services.AddScoped<SportsFileNameParser>(); // Sports-specific filename parsing (UFC, WWE, NFL, etc.)
        services.AddScoped<FileNamingService>();
        services.AddScoped<FileRenameService>(); // Auto-renames files when event metadata changes
        services.AddScoped<EventPartDetector>(); // Multi-part episode detection for Fighting sports
        services.AddScoped<FileFormatManager>(); // Auto-manages {Part} token in file format
        services.AddScoped<IFileImportService, FileImportService>();
        services.AddScoped<ImportMatchingService>(); // Matches external downloads to events
        services.AddScoped<CustomFormatService>();
        services.AddScoped<TrashGuideSyncService>(); // TRaSH Guides sync for custom formats and scores
        services.AddSingleton<DiskSpaceService>(); // Disk space detection (handles Docker volumes correctly)
        services.AddScoped<HealthCheckService>();
        services.AddScoped<BackupService>();
        services.AddScoped<LibraryImportService>();
        services.AddScoped<INotificationService, NotificationService>(); // Multi-provider notifications (Discord, Telegram, Pushover, Plex, Jellyfin, Emby, etc.)
        services.AddScoped<ImportListService>();
        services.AddScoped<ProvideImportItemService>(); // Provides import items with path translation
        services.AddScoped<EventQueryService>(); // Universal: Sport-aware query builder for all sports
        services.AddScoped<LeagueEventSyncService>(); // Syncs events from Sportarr API to populate leagues
        services.AddScoped<TeamLeagueDiscoveryService>(); // Discovers leagues for followed teams (cross-league team monitoring)
        services.AddScoped<SeasonSearchService>(); // Season-level search for manual season pack discovery
        services.AddScoped<EventMappingService>(); // Event mapping sync and lookup for release name matching
        services.AddScoped<PackImportService>(); // Multi-file pack import (e.g., NFL-2025-Week15 containing all games)
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<DiskScanService>(); // Periodic file existence verification (Sonarr-style disk scan)
                                                          // IPTV/DVR services for recording live streams
        services.AddScoped<M3uParserService>();
        services.AddScoped<XtreamCodesClient>();
        services.AddScoped<IptvSourceService>();
        services.AddScoped<ChannelAutoMappingService>();
        services.AddSingleton<FFmpegRecorderService>();
        services.AddSingleton<FFmpegStreamService>(); // Live stream transcoding service
        services.AddScoped<DvrRecordingService>();
        services.AddScoped<EventDvrService>();
        services.AddSingleton<DvrAutoSchedulerService>(); // DVR auto-scheduling service (singleton for background + manual trigger)
        services.AddScoped<DvrQualityScoreCalculator>(); // DVR quality score estimation
        services.AddScoped<XmltvParserService>(); // XMLTV EPG parser
        services.AddScoped<EpgService>(); // EPG management service
        services.AddScoped<EpgSchedulingService>(); // EPG-based DVR scheduling optimization
        services.AddScoped<FilteredExportService>(); // Filtered M3U/EPG export service
        services.AddSingleton<DataBaseService>();

        return services;
    }

    public static IServiceCollection AddSportarrHostedServices(this IServiceCollection services)
    {
        services.AddHostedService<TrashSyncBackgroundService>(); // TRaSH Guides auto-sync background service
        services.AddHostedService<EventMappingSyncBackgroundService>(); // Automatic event mapping sync every 12 hours (like Sonarr XEM)
        services.AddHostedService<LeagueEventAutoSyncService>(); // Background service for automatic periodic event sync
        services.AddHostedService<EnhancedDownloadMonitorService>(); // Unified download monitoring with retry, blocklist, and auto-import
        services.AddHostedService<RssSyncService>(); // Automatic RSS sync for new releases
        services.AddHostedService<TvScheduleSyncService>(); // TV schedule sync for automatic search timing
        services.AddHostedService(sp => sp.GetRequiredService<DiskScanService>());
        services.AddHostedService<FileWatcherService>(); // Real-time file monitoring via FileSystemWatcher
        services.AddHostedService<DvrSchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<DvrAutoSchedulerService>()); // Run as hosted service

        return services;
    }

    public static IServiceCollection AddSportarrDbContext(this IServiceCollection services, string dataPath)
    {
        // Configure database
        var dbPath = Path.Combine(dataPath, "sportarr.db");
        Console.WriteLine($"[Sportarr] Database path: {dbPath}");

        services.AddDbContext<SportarrDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}")
                   .ConfigureWarnings(w => w
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning)
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.FirstWithoutOrderByAndFilterWarning)));
        
        // Add DbContextFactory for concurrent database access (used by IndexerStatusService for parallel indexer searches)
        services.AddDbContextFactory<SportarrDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}")
                   .ConfigureWarnings(w => w
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning)
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)
                       .Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.FirstWithoutOrderByAndFilterWarning)), ServiceLifetime.Scoped);

        return services;
    }
}
