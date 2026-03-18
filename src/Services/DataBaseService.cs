using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using Sportarr.Api.Models;
using Sportarr.Api.Services;
using Sportarr.Api.Services.Interfaces;

namespace Sportarr.Services;

public class DataBaseService(IServiceProvider _provider)
{
    public async Task RunMigrations()
    {
        Console.WriteLine("[Sportarr] Applying database migrations...");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();

        // Check if database exists and has tables but no migration history
        // This happens when database was created with EnsureCreated() instead of Migrate()
        var canConnect = await db.Database.CanConnectAsync();
        var hasMigrationHistory = canConnect && (await db.Database.GetAppliedMigrationsAsync()).Any();

        // Check if AppSettings table exists (core table that should always be present)
        bool hasTables = false;

        if (canConnect)
        {
            using var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AppSettings'";
            var result = await command.ExecuteScalarAsync();
            hasTables = Convert.ToInt32(result) > 0;
        }

        if (canConnect && hasTables && !hasMigrationHistory)
        {
            // Database was created with EnsureCreated() - we need to seed the migration history
            // to prevent migrations from trying to recreate existing tables
            Console.WriteLine("[Sportarr] Detected database created without migrations. Seeding migration history...");

            // Get all migrations that exist in the codebase
            var allMigrations = db.Database.GetMigrations().ToList();

            // Mark all existing migrations as applied (since tables already exist)
            // We'll use a raw SQL approach since the history table doesn't exist yet
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL,
                    ""ProductVersion"" TEXT NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                )");

            // Insert all migrations as applied (using parameterized query to prevent SQL injection)
            foreach (var migration in allMigrations)
            {
                try
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, '8.0.0')");
                    Console.WriteLine($"[Sportarr] Marked migration as applied: {migration}");
                }
                catch
                {
                    // Migration might already be in history, skip
                }
            }
        }

        // Now apply any new migrations
        await db.Database.MigrateAsync();      
        await CheckMonitoredPartsColumn(db);
        await CheckDisableSslCertificateValidationColumn(db);
        await CheckDownloadClientsTable(db);
        await CheckDownloadQueueTable(db);
        await CheckEventFilesTable(db);
        await CheckPendingImportsTable(db);
        await CheckMediaManagementSettingsTable(db);
        await CheckAppSettingsTable(db);
        await CheckGrabHistoryTable(db);
        await RecalculateQualityScores(db);
        await CheckTagsColumns(db);
        await CleanupOrphanedEvents(db);
        await CleanupIncompleteTasksOnStartup(db);

        Console.WriteLine("[Sportarr] Database migrations applied successfully");
    }

    /// <summary>
    /// Ensure StandardFileFormat is updated to new default format (backwards compatibility fix)
    /// This runs AFTER migrations so EnableMultiPartEpisodes column exists
    /// </summary>
    public async Task UpdateStandardFileFormat()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();

        try
        {
            var mediaSettings = await db.MediaManagementSettings.FirstOrDefaultAsync();

            if (mediaSettings is null)
            {
                Console.WriteLine("[Sportarr] Warning: MediaManagementSettings not found - will be created on first use");
                return;
            }

            const string correctFormat = "{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full}";
            const string correctFormatNoPart = "{Series} - {Season}{Episode} - {Event Title} - {Quality Full}";

            // Check if StandardFileFormat needs to be updated
            var currentFormat = mediaSettings.StandardFileFormat ?? "";

            // Only update if it's NOT already in the correct format
            if (currentFormat.Equals(correctFormat, StringComparison.OrdinalIgnoreCase) ||
                currentFormat.Equals(correctFormatNoPart, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Sportarr] StandardFileFormat is already correct: '{currentFormat}'");
                return;
            }

            // Check if this is an old format that should be replaced
            var oldFormats = new[]
            {
                "{Event Title} - {Event Date} - {League}",
                "{Event Title} - {Air Date} - {Quality Full}",
                "{League}/{Event Title}",
                "{Event Title}",
                ""
            };

            if (!oldFormats.Any(f => f.Equals(currentFormat, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(currentFormat))
            {
                // User has a custom format - log but don't update
                Console.WriteLine($"[Sportarr] StandardFileFormat is custom: '{currentFormat}' - not updating automatically");
                return;
            }

            Console.WriteLine($"[Sportarr] Updating StandardFileFormat from '{currentFormat}' to new Plex-style format...");
            mediaSettings.StandardFileFormat = correctFormat;
            await db.SaveChangesAsync();
            Console.WriteLine("[Sportarr] StandardFileFormat updated successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not update StandardFileFormat: {ex.Message}");
        }
    }

    /// <summary>
    /// CRITICAL: Sync SecuritySettings from config.xml to database on startup
    /// This ensures the DynamicAuthenticationMiddleware has the correct auth settings
    /// Previously, settings were only saved to config.xml but middleware reads from database
    /// </summary>
    public async Task SyncSecuritySettings()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportarrDbContext>();
        var configService = scope.ServiceProvider.GetRequiredService<IConfigService>();
        var config = await configService.GetConfigAsync();

        Console.WriteLine($"[Sportarr] Syncing SecuritySettings to database (AuthMethod={config.AuthenticationMethod}, AuthRequired={config.AuthenticationRequired})");

        var appSettings = await db.AppSettings.FirstOrDefaultAsync();

        if (appSettings == null)
        {
            appSettings = new AppSettings { Id = 1 };
            db.AppSettings.Add(appSettings);
        }

        // Check if we have a plaintext password but no hash - need to hash it
        var passwordHash = config.PasswordHash ?? "";
        var passwordSalt = config.PasswordSalt ?? "";
        var passwordIterations = config.PasswordIterations > 0 ? config.PasswordIterations : 10000;

        if (!string.IsNullOrWhiteSpace(config.Password) && string.IsNullOrWhiteSpace(passwordHash))
        {
            Console.WriteLine("[Sportarr] Found plaintext password without hash - hashing now...");

            // Generate salt and hash the password
            var salt = new byte[128 / 8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            var hashedBytes = Microsoft.AspNetCore.Cryptography.KeyDerivation.KeyDerivation.Pbkdf2(
                password: config.Password,
                salt: salt,
                prf: Microsoft.AspNetCore.Cryptography.KeyDerivation.KeyDerivationPrf.HMACSHA512,
                iterationCount: passwordIterations,
                numBytesRequested: 256 / 8);

            passwordHash = Convert.ToBase64String(hashedBytes);
            passwordSalt = Convert.ToBase64String(salt);

            // Save hashed credentials back to config.xml (clear plaintext)
            await configService.UpdateConfigAsync(c =>
            {
                c.Password = ""; // Clear plaintext
                c.PasswordHash = passwordHash;
                c.PasswordSalt = passwordSalt;
                c.PasswordIterations = passwordIterations;
            });

            Console.WriteLine("[Sportarr] Password hashed and saved to config.xml");
        }

        // Create SecuritySettings JSON for database
        var dbSecuritySettings = new SecuritySettings
        {
            AuthenticationMethod = config.AuthenticationMethod?.ToLower() ?? "none",
            AuthenticationRequired = config.AuthenticationRequired?.ToLower() ?? "disabledforlocaladdresses",
            Username = config.Username ?? "",
            Password = "", // Never store plaintext
            ApiKey = config.ApiKey ?? "",
            CertificateValidation = config.CertificateValidation?.ToLower() ?? "enabled",
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
            PasswordIterations = passwordIterations
        };

        appSettings.SecuritySettings = System.Text.Json.JsonSerializer.Serialize(dbSecuritySettings);
        await db.SaveChangesAsync();

        Console.WriteLine("[Sportarr] SecuritySettings synced to database successfully");
    }

    #region Migrations

    private async Task CheckMonitoredPartsColumn(SportarrDbContext db)
    {
        // Ensure MonitoredParts column exists in Leagues table (backwards compatibility fix)
        // This handles cases where migrations were applied but column wasn't created
        try
        {
            var checkColumnSql = "SELECT COUNT(*) FROM pragma_table_info('Leagues') WHERE name='MonitoredParts'";
            var columnExists = db.Database.SqlQueryRaw<int>(checkColumnSql).AsEnumerable().FirstOrDefault();

            if (columnExists == 0)
            {
                Console.WriteLine("[Sportarr] Leagues.MonitoredParts column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Leagues ADD COLUMN MonitoredParts TEXT");
                Console.WriteLine("[Sportarr] Leagues.MonitoredParts column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify Leagues.MonitoredParts column: {ex.Message}");
        }

        // Ensure MonitoredParts column exists in Events table (backwards compatibility fix)
        try
        {
            var checkEventColumnSql = "SELECT COUNT(*) FROM pragma_table_info('Events') WHERE name='MonitoredParts'";
            var eventColumnExists = db.Database.SqlQueryRaw<int>(checkEventColumnSql).AsEnumerable().FirstOrDefault();

            if (eventColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] Events.MonitoredParts column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Events ADD COLUMN MonitoredParts TEXT");
                Console.WriteLine("[Sportarr] Events.MonitoredParts column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify Events.MonitoredParts column: {ex.Message}");
        }
    }

    private async Task CheckDisableSslCertificateValidationColumn(SportarrDbContext db)
    {
        // Ensure DisableSslCertificateValidation column exists in DownloadClients table (backwards compatibility fix)
        try
        {
            var checkSslColumnSql = "SELECT COUNT(*) FROM pragma_table_info('DownloadClients') WHERE name='DisableSslCertificateValidation'";
            var sslColumnExists = db.Database.SqlQueryRaw<int>(checkSslColumnSql).AsEnumerable().FirstOrDefault();

            if (sslColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadClients.DisableSslCertificateValidation column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadClients ADD COLUMN DisableSslCertificateValidation INTEGER NOT NULL DEFAULT 0");
                Console.WriteLine("[Sportarr] DownloadClients.DisableSslCertificateValidation column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify DownloadClients.DisableSslCertificateValidation column: {ex.Message}");
        }
    }

    private async Task CheckDownloadClientsTable(SportarrDbContext db)
    {
        // Ensure SequentialDownload and FirstAndLastFirst columns exist in DownloadClients table (debrid service support)
        try
        {
            var checkSeqColumnSql = "SELECT COUNT(*) FROM pragma_table_info('DownloadClients') WHERE name='SequentialDownload'";
            var seqColumnExists = db.Database.SqlQueryRaw<int>(checkSeqColumnSql).AsEnumerable().FirstOrDefault();

            if (seqColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadClients.SequentialDownload column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadClients ADD COLUMN SequentialDownload INTEGER NOT NULL DEFAULT 0");
                Console.WriteLine("[Sportarr] DownloadClients.SequentialDownload column added successfully");
            }

            var checkFirstLastColumnSql = "SELECT COUNT(*) FROM pragma_table_info('DownloadClients') WHERE name='FirstAndLastFirst'";
            var firstLastColumnExists = db.Database.SqlQueryRaw<int>(checkFirstLastColumnSql).AsEnumerable().FirstOrDefault();

            if (firstLastColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadClients.FirstAndLastFirst column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadClients ADD COLUMN FirstAndLastFirst INTEGER NOT NULL DEFAULT 0");
                Console.WriteLine("[Sportarr] DownloadClients.FirstAndLastFirst column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify DownloadClients sequential download columns: {ex.Message}");
        }
    }

    private async Task CheckDownloadQueueTable(SportarrDbContext db)
    {
        // Ensure ImportRetryCount column exists in DownloadQueue table (backwards compatibility fix)
        // This column was added but EF Core migrations may not have run properly on some databases
        try
        {
            var checkImportRetryColumnSql = "SELECT COUNT(*) FROM pragma_table_info('DownloadQueue') WHERE name='ImportRetryCount'";
            var importRetryColumnExists = db.Database.SqlQueryRaw<int>(checkImportRetryColumnSql)
                .AsEnumerable()
                .FirstOrDefault();

            if (importRetryColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadQueue.ImportRetryCount column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadQueue ADD COLUMN ImportRetryCount INTEGER NOT NULL DEFAULT 0");
                Console.WriteLine("[Sportarr] DownloadQueue.ImportRetryCount column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify DownloadQueue.ImportRetryCount column: {ex.Message}");
        }

        // Ensure IndexerId column exists in DownloadQueue table (backwards compatibility fix)
        // This column was added for seed config lookup but may be missing on older databases
        try
        {
            var checkIndexerIdColumnSql = "SELECT COUNT(*) FROM pragma_table_info('DownloadQueue') WHERE name='IndexerId'";
            var indexerIdColumnExists = db.Database.SqlQueryRaw<int>(checkIndexerIdColumnSql).AsEnumerable().FirstOrDefault();

            if (indexerIdColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadQueue.IndexerId column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadQueue ADD COLUMN IndexerId INTEGER NULL");
                Console.WriteLine("[Sportarr] DownloadQueue.IndexerId column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify DownloadQueue.IndexerId column: {ex.Message}");
        }

        // Ensure IsManualSearch column exists in DownloadQueue (added in download settings rework)
        try
        {
            var checkIsManualSearchCol = "SELECT COUNT(*) FROM pragma_table_info('DownloadQueue') WHERE name='IsManualSearch'";
            var isManualSearchExists = db.Database.SqlQueryRaw<int>(checkIsManualSearchCol).AsEnumerable().FirstOrDefault();

            if (isManualSearchExists == 0)
            {
                Console.WriteLine("[Sportarr] DownloadQueue.IsManualSearch column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE DownloadQueue ADD COLUMN IsManualSearch INTEGER NOT NULL DEFAULT 0");
                Console.WriteLine("[Sportarr] DownloadQueue.IsManualSearch column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify DownloadQueue.IsManualSearch column: {ex.Message}");
        }
    }

    private async Task CheckEventFilesTable(SportarrDbContext db)
    {
        // Ensure EventFiles table exists (backwards compatibility fix for file tracking)
        // This handles cases where migration history was seeded before EventFiles migration existed
        try
        {
            var checkTableSql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EventFiles'";
            var tableExists = db.Database.SqlQueryRaw<int>(checkTableSql).AsEnumerable().FirstOrDefault();

            if (tableExists == 0)
            {
                Console.WriteLine("[Sportarr] EventFiles table missing - creating it now...");

                // Create EventFiles table with all columns and indexes
                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE ""EventFiles"" (
                        ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        ""EventId"" INTEGER NOT NULL,
                        ""FilePath"" TEXT NOT NULL,
                        ""Size"" INTEGER NOT NULL,
                        ""Quality"" TEXT NULL,
                        ""PartName"" TEXT NULL,
                        ""PartNumber"" INTEGER NULL,
                        ""Added"" TEXT NOT NULL,
                        ""LastVerified"" TEXT NULL,
                        ""Exists"" INTEGER NOT NULL DEFAULT 1,
                        CONSTRAINT ""FK_EventFiles_Events_EventId"" FOREIGN KEY (""EventId"") REFERENCES ""Events"" (""Id"") ON DELETE CASCADE
                    )");

                // Create indexes
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_EventFiles_EventId"" ON ""EventFiles"" (""EventId"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_EventFiles_PartNumber"" ON ""EventFiles"" (""PartNumber"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_EventFiles_Exists"" ON ""EventFiles"" (""Exists"")");

                Console.WriteLine("[Sportarr] EventFiles table created successfully");
                Console.WriteLine("[Sportarr] File tracking is now enabled for all sports");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify EventFiles table: {ex.Message}");
        }

        // Ensure ReleaseGroup column exists in EventFiles table (for file renaming with {Release Group} token)
        try
        {
            var checkRgColumnSql = "SELECT COUNT(*) FROM pragma_table_info('EventFiles') WHERE name='ReleaseGroup'";
            var rgColumnExists = db.Database.SqlQueryRaw<int>(checkRgColumnSql).AsEnumerable().FirstOrDefault();

            if (rgColumnExists != 0)
            {
                return;
            }

            Console.WriteLine("[Sportarr] EventFiles.ReleaseGroup column missing - adding it now...");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE EventFiles ADD COLUMN ReleaseGroup TEXT");
            Console.WriteLine("[Sportarr] EventFiles.ReleaseGroup column added successfully");

            // Backfill release groups from existing OriginalTitle values
            var filesWithOriginalTitle = await db.EventFiles
                .Where(ef => ef.OriginalTitle != null && ef.OriginalTitle != "")
                .ToListAsync();

            int backfilled = 0;

            foreach (var ef in filesWithOriginalTitle)
            {
                var rgMatch = System.Text.RegularExpressions.Regex.Match(
                    ef.OriginalTitle!, @"-([A-Za-z0-9]+)(?:\.[a-z]{2,4})?$");

                if (rgMatch.Success)
                {
                    var group = rgMatch.Groups[1].Value;
                    var excluded = new[] { "DL", "WEB", "HD", "SD", "UHD" };
                    if (!excluded.Contains(group.ToUpper()))
                    {
                        ef.ReleaseGroup = group;
                        backfilled++;
                    }
                }
            }

            if (backfilled > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"[Sportarr] Backfilled ReleaseGroup for {backfilled} existing files");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify EventFiles.ReleaseGroup column: {ex.Message}");
        }
    }

    private async Task CheckPendingImportsTable(SportarrDbContext db)
    {
        // Ensure PendingImports table exists (for external download detection feature)
        try
        {
            var checkTableSql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingImports'";
            var tableExists = db.Database.SqlQueryRaw<int>(checkTableSql)
                .AsEnumerable()
                .FirstOrDefault();

            if (tableExists == 0)
            {
                Console.WriteLine("[Sportarr] PendingImports table missing - creating it now...");

                // Create PendingImports table with all columns
                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE ""PendingImports"" (
                        ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        ""DownloadClientId"" INTEGER NULL,
                        ""DownloadId"" TEXT NOT NULL,
                        ""Title"" TEXT NOT NULL,
                        ""FilePath"" TEXT NOT NULL,
                        ""Size"" INTEGER NOT NULL DEFAULT 0,
                        ""Quality"" TEXT NULL,
                        ""QualityScore"" INTEGER NOT NULL DEFAULT 0,
                        ""Status"" INTEGER NOT NULL DEFAULT 0,
                        ""ErrorMessage"" TEXT NULL,
                        ""SuggestedEventId"" INTEGER NULL,
                        ""SuggestedPart"" TEXT NULL,
                        ""SuggestionConfidence"" INTEGER NOT NULL DEFAULT 0,
                        ""Detected"" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""ResolvedAt"" TEXT NULL,
                        ""Protocol"" TEXT NULL,
                        ""TorrentInfoHash"" TEXT NULL,
                        CONSTRAINT ""FK_PendingImports_DownloadClients_DownloadClientId"" FOREIGN KEY (""DownloadClientId"") REFERENCES ""DownloadClients"" (""Id"") ON DELETE CASCADE,
                        CONSTRAINT ""FK_PendingImports_Events_SuggestedEventId"" FOREIGN KEY (""SuggestedEventId"") REFERENCES ""Events"" (""Id"") ON DELETE SET NULL
                    )");

                // Create indexes
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_DownloadClientId"" ON ""PendingImports"" (""DownloadClientId"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_SuggestedEventId"" ON ""PendingImports"" (""SuggestedEventId"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_Status"" ON ""PendingImports"" (""Status"")");

                Console.WriteLine("[Sportarr] PendingImports table created successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify PendingImports table: {ex.Message}");
        }

        // Ensure PendingImports has IsPack, FileCount, MatchedEventsCount columns (added for pack import support)
        try
        {
            var checkTableSql2 = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingImports'";
            var table2Exists = db.Database.SqlQueryRaw<int>(checkTableSql2)
                .AsEnumerable()
                .FirstOrDefault();

            if (table2Exists > 0)
            {
                var checkIsPack = "SELECT COUNT(*) FROM pragma_table_info('PendingImports') WHERE name='IsPack'";
                var isPackExists = db.Database.SqlQueryRaw<int>(checkIsPack)
                    .AsEnumerable()
                    .FirstOrDefault();

                if (isPackExists == 0)
                {
                    Console.WriteLine("[Sportarr] Adding IsPack/FileCount/MatchedEventsCount columns to PendingImports...");
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""PendingImports"" ADD COLUMN ""IsPack"" INTEGER NOT NULL DEFAULT 0");
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""PendingImports"" ADD COLUMN ""FileCount"" INTEGER NOT NULL DEFAULT 0");
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""PendingImports"" ADD COLUMN ""MatchedEventsCount"" INTEGER NOT NULL DEFAULT 0");
                    Console.WriteLine("[Sportarr] PendingImports columns added successfully");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not add PendingImports columns: {ex.Message}");
        }

        // Ensure PendingImports.DownloadClientId is nullable (for disk-discovered files with no download client)
        // SQLite doesn't support ALTER COLUMN, so we rebuild the table if needed
        try
        {
            var checkNullableSql = "SELECT COUNT(*) FROM pragma_table_info('PendingImports') WHERE name='DownloadClientId' AND \"notnull\" = 1";
            var isNotNull = db.Database.SqlQueryRaw<int>(checkNullableSql).AsEnumerable().FirstOrDefault();

            if (isNotNull > 0) // Column is NOT NULL, needs to be nullable
            {
                Console.WriteLine("[Sportarr] Rebuilding PendingImports table to make DownloadClientId nullable...");

                await db.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE ""PendingImports_new"" (
                        ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        ""DownloadClientId"" INTEGER NULL,
                        ""DownloadId"" TEXT NOT NULL,
                        ""Title"" TEXT NOT NULL,
                        ""FilePath"" TEXT NOT NULL,
                        ""Size"" INTEGER NOT NULL DEFAULT 0,
                        ""Quality"" TEXT NULL,
                        ""QualityScore"" INTEGER NOT NULL DEFAULT 0,
                        ""Status"" INTEGER NOT NULL DEFAULT 0,
                        ""ErrorMessage"" TEXT NULL,
                        ""SuggestedEventId"" INTEGER NULL,
                        ""SuggestedPart"" TEXT NULL,
                        ""SuggestionConfidence"" INTEGER NOT NULL DEFAULT 0,
                        ""Detected"" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        ""ResolvedAt"" TEXT NULL,
                        ""Protocol"" TEXT NULL,
                        ""TorrentInfoHash"" TEXT NULL,
                        ""IsPack"" INTEGER NOT NULL DEFAULT 0,
                        ""FileCount"" INTEGER NOT NULL DEFAULT 0,
                        ""MatchedEventsCount"" INTEGER NOT NULL DEFAULT 0,
                        CONSTRAINT ""FK_PendingImports_DownloadClients_DownloadClientId"" FOREIGN KEY (""DownloadClientId"") REFERENCES ""DownloadClients"" (""Id"") ON DELETE CASCADE,
                        CONSTRAINT ""FK_PendingImports_Events_SuggestedEventId"" FOREIGN KEY (""SuggestedEventId"") REFERENCES ""Events"" (""Id"") ON DELETE SET NULL
                    )");

                await db.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""PendingImports_new"" (""Id"", ""DownloadClientId"", ""DownloadId"", ""Title"", ""FilePath"", ""Size"", ""Quality"", ""QualityScore"", ""Status"", ""ErrorMessage"", ""SuggestedEventId"", ""SuggestedPart"", ""SuggestionConfidence"", ""Detected"", ""ResolvedAt"", ""Protocol"", ""TorrentInfoHash"", ""IsPack"", ""FileCount"", ""MatchedEventsCount"")
                    SELECT ""Id"", CASE WHEN ""DownloadClientId"" = 0 THEN NULL ELSE ""DownloadClientId"" END, ""DownloadId"", ""Title"", ""FilePath"", ""Size"", ""Quality"", ""QualityScore"", ""Status"", ""ErrorMessage"", ""SuggestedEventId"", ""SuggestedPart"", ""SuggestionConfidence"", ""Detected"", ""ResolvedAt"", ""Protocol"", ""TorrentInfoHash"", ""IsPack"", ""FileCount"", ""MatchedEventsCount""
                    FROM ""PendingImports""");

                await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""PendingImports""");
                await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""PendingImports_new"" RENAME TO ""PendingImports""");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_DownloadClientId"" ON ""PendingImports"" (""DownloadClientId"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_SuggestedEventId"" ON ""PendingImports"" (""SuggestedEventId"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_Status"" ON ""PendingImports"" (""Status"")");
                await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX ""IX_PendingImports_DownloadId"" ON ""PendingImports"" (""DownloadId"")");

                Console.WriteLine("[Sportarr] PendingImports table rebuilt successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify PendingImports.DownloadClientId nullability: {ex.Message}");
        }
    }

    private async Task CheckMediaManagementSettingsTable(SportarrDbContext db)
    {
        // Ensure EnableMultiPartEpisodes column exists in MediaManagementSettings (backwards compatibility fix)
        // This handles cases where migration history was seeded before the column was added
        try
        {
            var checkColumnSql = "SELECT COUNT(*) FROM pragma_table_info('MediaManagementSettings') WHERE name='EnableMultiPartEpisodes'";
            var columnExists = db.Database.SqlQueryRaw<int>(checkColumnSql)
                .AsEnumerable()
                .FirstOrDefault();

            if (columnExists == 0)
            {
                Console.WriteLine("[Sportarr] EnableMultiPartEpisodes column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""MediaManagementSettings"" ADD COLUMN ""EnableMultiPartEpisodes"" INTEGER NOT NULL DEFAULT 1");
                Console.WriteLine("[Sportarr] EnableMultiPartEpisodes column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify EnableMultiPartEpisodes column: {ex.Message}");
        }

        // Ensure granular folder format/creation columns exist in MediaManagementSettings
        // These were added after some installs and may be missing from older databases
        try
        {
            var columnsToAdd = new[]
            {
                ("LeagueFolderFormat", "TEXT NOT NULL DEFAULT '{Series}'"),
                ("SeasonFolderFormat", "TEXT NOT NULL DEFAULT 'Season {Season}'"),
                ("CreateLeagueFolders", "INTEGER NOT NULL DEFAULT 1"),
                ("CreateSeasonFolders", "INTEGER NOT NULL DEFAULT 1"),
                ("ReorganizeFolders", "INTEGER NOT NULL DEFAULT 0"),
            };

            foreach (var (columnName, columnDef) in columnsToAdd)
            {
                var checkSql = $"SELECT COUNT(*) FROM pragma_table_info('MediaManagementSettings') WHERE name='{columnName}'";
                var exists = db.Database.SqlQueryRaw<int>(checkSql)
                    .AsEnumerable()
                    .FirstOrDefault();

                if (exists == 0)
                {
                    Console.WriteLine($"[Sportarr] Adding missing column {columnName} to MediaManagementSettings...");
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"MediaManagementSettings\" ADD COLUMN \"" + columnName + "\" " + columnDef);
                    Console.WriteLine($"[Sportarr] Column {columnName} added successfully");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not add missing MediaManagementSettings columns: {ex.Message}");
        }

        // Remove deprecated StandardEventFormat column if it exists (backwards compatibility fix)
        // This column was removed but migration may not have run properly on some databases
        try
        {
            var checkColumnSql = "SELECT COUNT(*) FROM pragma_table_info('MediaManagementSettings') WHERE name='StandardEventFormat'";
            var columnExists = db.Database.SqlQueryRaw<int>(checkColumnSql)
                .AsEnumerable()
                .FirstOrDefault();

            if (columnExists > 0)
            {
                Console.WriteLine("[Sportarr] Removing deprecated StandardEventFormat column from MediaManagementSettings...");
                // SQLite doesn't support DROP COLUMN directly, so we need to recreate the table
                // Note: Using single quotes for SQL string literals (not C# interpolation)
                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS MediaManagementSettings_new (
                        Id INTEGER PRIMARY KEY,
                        RenameFiles INTEGER NOT NULL DEFAULT 1,
                        StandardFileFormat TEXT NOT NULL DEFAULT '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full}',
                        EventFolderFormat TEXT NOT NULL DEFAULT '{Event Title}',
                        LeagueFolderFormat TEXT NOT NULL DEFAULT '{Series}',
                        SeasonFolderFormat TEXT NOT NULL DEFAULT 'Season {Season}',
                        CreateEventFolder INTEGER NOT NULL DEFAULT 1,
                        RenameEvents INTEGER NOT NULL DEFAULT 0,
                        ReplaceIllegalCharacters INTEGER NOT NULL DEFAULT 1,
                        CreateLeagueFolders INTEGER NOT NULL DEFAULT 1,
                        CreateSeasonFolders INTEGER NOT NULL DEFAULT 1,
                        CreateEventFolders INTEGER NOT NULL DEFAULT 1,
                        ReorganizeFolders INTEGER NOT NULL DEFAULT 0,
                        DeleteEmptyFolders INTEGER NOT NULL DEFAULT 0,
                        SkipFreeSpaceCheck INTEGER NOT NULL DEFAULT 0,
                        MinimumFreeSpace INTEGER NOT NULL DEFAULT 100,
                        UseHardlinks INTEGER NOT NULL DEFAULT 1,
                        ImportExtraFiles INTEGER NOT NULL DEFAULT 0,
                        ExtraFileExtensions TEXT NOT NULL DEFAULT 'srt,nfo',
                        ChangeFileDate TEXT NOT NULL DEFAULT 'None',
                        RecycleBin TEXT NOT NULL DEFAULT '',
                        RecycleBinCleanup INTEGER NOT NULL DEFAULT 7,
                        SetPermissions INTEGER NOT NULL DEFAULT 0,
                        FileChmod TEXT NOT NULL DEFAULT '644',
                        ChmodFolder TEXT NOT NULL DEFAULT '755',
                        ChownUser TEXT NOT NULL DEFAULT '',
                        ChownGroup TEXT NOT NULL DEFAULT '',
                        CopyFiles INTEGER NOT NULL DEFAULT 0,
                        Created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        LastModified TEXT,
                        EnableMultiPartEpisodes INTEGER NOT NULL DEFAULT 1,
                        RootFolders TEXT NOT NULL DEFAULT '[]'
                    )";

                using var connection = db.Database.GetDbConnection();

                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = createTableSql;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO MediaManagementSettings_new (
                            Id, RenameFiles, StandardFileFormat, EventFolderFormat,
                            LeagueFolderFormat, SeasonFolderFormat,
                            CreateEventFolder, RenameEvents, ReplaceIllegalCharacters,
                            CreateLeagueFolders, CreateSeasonFolders, CreateEventFolders, ReorganizeFolders,
                            DeleteEmptyFolders, SkipFreeSpaceCheck, MinimumFreeSpace, UseHardlinks,
                            ImportExtraFiles, ExtraFileExtensions, ChangeFileDate, RecycleBin, RecycleBinCleanup,
                            SetPermissions, FileChmod, ChmodFolder, ChownUser, ChownGroup,
                            CopyFiles, Created, LastModified,
                            EnableMultiPartEpisodes, RootFolders
                        )
                        SELECT
                            Id, RenameFiles, StandardFileFormat, EventFolderFormat,
                            COALESCE(LeagueFolderFormat, '{Series}'), COALESCE(SeasonFolderFormat, 'Season {Season}'),
                            CreateEventFolder, RenameEvents, ReplaceIllegalCharacters,
                            COALESCE(CreateLeagueFolders, 1), COALESCE(CreateSeasonFolders, 1), CreateEventFolders, COALESCE(ReorganizeFolders, 0),
                            DeleteEmptyFolders, SkipFreeSpaceCheck, MinimumFreeSpace, UseHardlinks,
                            ImportExtraFiles, ExtraFileExtensions, ChangeFileDate, RecycleBin, RecycleBinCleanup,
                            SetPermissions, FileChmod, ChmodFolder, ChownUser, ChownGroup,
                            CopyFiles, Created, LastModified,
                            COALESCE(EnableMultiPartEpisodes, 1), COALESCE(RootFolders, '[]')
                        FROM MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DROP TABLE MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "ALTER TABLE MediaManagementSettings_new RENAME TO MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                Console.WriteLine("[Sportarr] StandardEventFormat column removed successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not remove StandardEventFormat column: {ex.Message}");
        }

        // Remove deprecated RemoveCompletedDownloads/RemoveFailedDownloads from MediaManagementSettings
        // These were moved to per-client settings but initial migration created them as NOT NULL without DEFAULT
        // The StandardEventFormat migration above handles this for fresh installs, but users who updated
        // through intermediate versions may have had StandardEventFormat removed while these columns remained
        try
        {
            var checkRemoveCol = "SELECT COUNT(*) FROM pragma_table_info('MediaManagementSettings') WHERE name='RemoveCompletedDownloads'";
            var removeColExists = db.Database.SqlQueryRaw<int>(checkRemoveCol)
                .AsEnumerable()
                .FirstOrDefault();

            if (removeColExists > 0)
            {
                Console.WriteLine("[Sportarr] Removing deprecated RemoveCompletedDownloads/RemoveFailedDownloads columns from MediaManagementSettings...");

                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS MediaManagementSettings_new (
                        Id INTEGER PRIMARY KEY,
                        RenameFiles INTEGER NOT NULL DEFAULT 1,
                        StandardFileFormat TEXT NOT NULL DEFAULT '{Series} - {Season}{Episode}{Part} - {Event Title} - {Quality Full}',
                        EventFolderFormat TEXT NOT NULL DEFAULT '{Event Title}',
                        LeagueFolderFormat TEXT NOT NULL DEFAULT '{Series}',
                        SeasonFolderFormat TEXT NOT NULL DEFAULT 'Season {Season}',
                        CreateEventFolder INTEGER NOT NULL DEFAULT 1,
                        RenameEvents INTEGER NOT NULL DEFAULT 0,
                        ReplaceIllegalCharacters INTEGER NOT NULL DEFAULT 1,
                        CreateLeagueFolders INTEGER NOT NULL DEFAULT 1,
                        CreateSeasonFolders INTEGER NOT NULL DEFAULT 1,
                        CreateEventFolders INTEGER NOT NULL DEFAULT 1,
                        ReorganizeFolders INTEGER NOT NULL DEFAULT 0,
                        DeleteEmptyFolders INTEGER NOT NULL DEFAULT 0,
                        SkipFreeSpaceCheck INTEGER NOT NULL DEFAULT 0,
                        MinimumFreeSpace INTEGER NOT NULL DEFAULT 100,
                        UseHardlinks INTEGER NOT NULL DEFAULT 1,
                        ImportExtraFiles INTEGER NOT NULL DEFAULT 0,
                        ExtraFileExtensions TEXT NOT NULL DEFAULT 'srt,nfo',
                        ChangeFileDate TEXT NOT NULL DEFAULT 'None',
                        RecycleBin TEXT NOT NULL DEFAULT '',
                        RecycleBinCleanup INTEGER NOT NULL DEFAULT 7,
                        SetPermissions INTEGER NOT NULL DEFAULT 0,
                        FileChmod TEXT NOT NULL DEFAULT '644',
                        ChmodFolder TEXT NOT NULL DEFAULT '755',
                        ChownUser TEXT NOT NULL DEFAULT '',
                        ChownGroup TEXT NOT NULL DEFAULT '',
                        CopyFiles INTEGER NOT NULL DEFAULT 0,
                        Created TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        LastModified TEXT,
                        EnableMultiPartEpisodes INTEGER NOT NULL DEFAULT 1,
                        RootFolders TEXT NOT NULL DEFAULT '[]'
                    )";

                using var connection2 = db.Database.GetDbConnection();
                if (connection2.State != System.Data.ConnectionState.Open)
                    await connection2.OpenAsync();

                using (var cmd = connection2.CreateCommand())
                {
                    cmd.CommandText = createTableSql;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection2.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO MediaManagementSettings_new (
                            Id, RenameFiles, StandardFileFormat, EventFolderFormat,
                            LeagueFolderFormat, SeasonFolderFormat,
                            CreateEventFolder, RenameEvents, ReplaceIllegalCharacters,
                            CreateLeagueFolders, CreateSeasonFolders, CreateEventFolders, ReorganizeFolders,
                            DeleteEmptyFolders, SkipFreeSpaceCheck, MinimumFreeSpace, UseHardlinks,
                            ImportExtraFiles, ExtraFileExtensions, ChangeFileDate, RecycleBin, RecycleBinCleanup,
                            SetPermissions, FileChmod, ChmodFolder, ChownUser, ChownGroup,
                            CopyFiles, Created, LastModified,
                            EnableMultiPartEpisodes, RootFolders
                        )
                        SELECT
                            Id, RenameFiles, StandardFileFormat, EventFolderFormat,
                            COALESCE(LeagueFolderFormat, '{Series}'), COALESCE(SeasonFolderFormat, 'Season {Season}'),
                            CreateEventFolder, RenameEvents, ReplaceIllegalCharacters,
                            COALESCE(CreateLeagueFolders, 1), COALESCE(CreateSeasonFolders, 1), CreateEventFolders, COALESCE(ReorganizeFolders, 0),
                            DeleteEmptyFolders, SkipFreeSpaceCheck, MinimumFreeSpace, UseHardlinks,
                            ImportExtraFiles, ExtraFileExtensions, ChangeFileDate, RecycleBin, RecycleBinCleanup,
                            SetPermissions, FileChmod, ChmodFolder, ChownUser, ChownGroup,
                            CopyFiles, Created, LastModified,
                            COALESCE(EnableMultiPartEpisodes, 1), COALESCE(RootFolders, '[]')
                        FROM MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection2.CreateCommand())
                {
                    cmd.CommandText = "DROP TABLE MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = connection2.CreateCommand())
                {
                    cmd.CommandText = "ALTER TABLE MediaManagementSettings_new RENAME TO MediaManagementSettings";
                    await cmd.ExecuteNonQueryAsync();
                }

                Console.WriteLine("[Sportarr] Deprecated download columns removed successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not remove deprecated download columns: {ex.Message}");
        }
    }

    private async Task CheckAppSettingsTable(SportarrDbContext db)
    {
        // Ensure RedownloadFailedFromInteractiveSearch column exists in AppSettings (added in download settings rework)
        try
        {
            var checkRedownloadInteractiveCol = "SELECT COUNT(*) FROM pragma_table_info('AppSettings') WHERE name='RedownloadFailedFromInteractiveSearch'";
            var redownloadInteractiveExists = db.Database.SqlQueryRaw<int>(checkRedownloadInteractiveCol)
                .AsEnumerable()
                .FirstOrDefault();

            if (redownloadInteractiveExists == 0)
            {
                Console.WriteLine("[Sportarr] AppSettings.RedownloadFailedFromInteractiveSearch column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE AppSettings ADD COLUMN RedownloadFailedFromInteractiveSearch INTEGER NOT NULL DEFAULT 1");
                Console.WriteLine("[Sportarr] AppSettings.RedownloadFailedFromInteractiveSearch column added successfully");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify AppSettings.RedownloadFailedFromInteractiveSearch column: {ex.Message}");
        }
    }

    private async Task CheckGrabHistoryTable(SportarrDbContext db)
    {
        // Ensure DownloadId column exists in GrabHistory table (for external download detection)
        try
        {
            var checkGhColumnSql = "SELECT COUNT(*) FROM pragma_table_info('GrabHistory') WHERE name='DownloadId'";
            var ghColumnExists = db.Database.SqlQueryRaw<int>(checkGhColumnSql)
                .AsEnumerable()
                .FirstOrDefault();

            if (ghColumnExists == 0)
            {
                Console.WriteLine("[Sportarr] GrabHistory.DownloadId column missing - adding it now...");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE GrabHistory ADD COLUMN DownloadId TEXT");

                // Backfill for torrents: qBittorrent uses TorrentInfoHash as DownloadId
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE GrabHistory SET DownloadId = TorrentInfoHash WHERE TorrentInfoHash IS NOT NULL AND DownloadId IS NULL");

                var backfilledCount = db.Database.SqlQueryRaw<int>("SELECT COUNT(*) FROM GrabHistory WHERE DownloadId IS NOT NULL")
                    .AsEnumerable()
                    .FirstOrDefault();

                Console.WriteLine($"[Sportarr] GrabHistory.DownloadId column added (backfilled {backfilledCount} torrent grabs)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify GrabHistory.DownloadId column: {ex.Message}");
        }
    }

    private async Task RecalculateQualityScores(SportarrDbContext db)
    {
        // Recalculate QualityScore for all EventFiles and DownloadQueueItems
        // Previous scoring used inverted profile-index logic (SDTV scored higher than 1080p)
        // Now uses deterministic resolution + source scoring
        try
        {
            var filesToFix = await db.EventFiles.Where(f => f.Quality != null).ToListAsync();
            var fixedFiles = 0;
            foreach (var file in filesToFix)
            {
                var correctScore = ReleaseEvaluator.CalculateQualityScoreFromName(file.Quality);
                if (file.QualityScore != correctScore)
                {
                    file.QualityScore = correctScore;
                    fixedFiles++;
                }
            }

            var queueToFix = await db.DownloadQueue.Where(d => d.Quality != null).ToListAsync();
            var fixedQueue = 0;
            foreach (var item in queueToFix)
            {
                var correctScore = ReleaseEvaluator.CalculateQualityScoreFromName(item.Quality);
                if (item.QualityScore != correctScore)
                {
                    item.QualityScore = correctScore;
                    fixedQueue++;
                }
            }

            if (fixedFiles > 0 || fixedQueue > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"[Sportarr] Recalculated QualityScore: {fixedFiles} files, {fixedQueue} queue items updated");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not recalculate QualityScore: {ex.Message}");
        }
    }

    private async Task CheckTagsColumns(SportarrDbContext db)
    {
        // Ensure Tags columns exist for tag-based filtering support
        try
        {
            var tagsTables = new[] { ("Leagues", "Tags"), ("DownloadClients", "Tags"), ("Notifications", "Tags"), ("Indexers", "Tags") };
            foreach (var (table, column) in tagsTables)
            {
                var checkSql = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
                var exists = db.Database.SqlQueryRaw<int>(checkSql)
                    .AsEnumerable()
                    .FirstOrDefault();

                if (exists == 0)
                {
                    Console.WriteLine($"[Sportarr] {table}.{column} column missing - adding it now...");
                    await db.Database.ExecuteSqlAsync($"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT '[]'");
                    Console.WriteLine($"[Sportarr] {table}.{column} column added successfully");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not verify Tags columns: {ex.Message}");
        }

    }

    private async Task CleanupOrphanedEvents(SportarrDbContext db)
    {
        // Clean up orphaned events (events whose leagues no longer exist)
        try
        {
            var orphanedEvents = await db.Events
                .Where(e => e.LeagueId == null || !db.Leagues.Any(l => l.Id == e.LeagueId))
                .ToListAsync();

            if (orphanedEvents.Count > 0)
            {
                Console.WriteLine($"[Sportarr] Found {orphanedEvents.Count} orphaned events (no league) - cleaning up...");
                db.Events.RemoveRange(orphanedEvents);
                await db.SaveChangesAsync();
                Console.WriteLine($"[Sportarr] Successfully removed {orphanedEvents.Count} orphaned events");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not clean up orphaned events: {ex.Message}");
        }
    }

    private async Task CleanupIncompleteTasksOnStartup(SportarrDbContext db)
    {
        // Clean up incomplete tasks on startup (Sonarr-style behavior)
        // Tasks that were Queued or Running when the app shut down should be cleared
        // This prevents old queued searches from unexpectedly executing after restart
        try
        {
            var incompleteTasks = await db.Tasks
                .Where(t => t.Status == Sportarr.Api.Models.TaskStatus.Queued ||
                           t.Status == Sportarr.Api.Models.TaskStatus.Running ||
                           t.Status == Sportarr.Api.Models.TaskStatus.Aborting)
                .ToListAsync();

            if (incompleteTasks.Count > 0)
            {
                Console.WriteLine($"[Sportarr] Found {incompleteTasks.Count} incomplete tasks from previous session - cleaning up...");
                foreach (var task in incompleteTasks)
                {
                    task.Status = Sportarr.Api.Models.TaskStatus.Cancelled;
                    task.Ended = DateTime.UtcNow;
                    task.Message = "Cancelled: Application was restarted";
                }
                await db.SaveChangesAsync();
                Console.WriteLine($"[Sportarr] Marked {incompleteTasks.Count} tasks as cancelled");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not clean up incomplete tasks: {ex.Message}");
        }
    }

    #endregion
}
