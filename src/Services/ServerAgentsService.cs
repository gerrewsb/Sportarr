using System.IO.Abstractions;

namespace Sportarr.Services;

public class ServerAgentsService(IFileSystem _fileSystem)
{
    private const string C_SPORTARR_BUNDLE = "Sportarr.bundle";
    private const string C_PLEX = "plex";

    /// <summary>
    /// Copy media server agents to config directory for easy Docker access
    /// </summary>
    /// <returns></returns>
    public async Task CopyMediaServerAgents(string dataPath)
    {
        try
        {
            var agentsSourcePath = _fileSystem.Path.Combine(AppContext.BaseDirectory, "agents");
            var agentsDestPath = _fileSystem.Path.Combine(dataPath, "agents");

            Console.WriteLine($"[Sportarr] Looking for agents at: {agentsSourcePath}");

            // Check if source exists in app directory
            if (_fileSystem.Directory.Exists(agentsSourcePath))
            {
                CheckForNewerMediaServerAgents(agentsSourcePath, agentsDestPath);
                return;
            }

            await CreateMediaServerAgents(agentsDestPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sportarr] Warning: Could not setup agents directory: {ex.Message}");
        }
    }

    #region Helpers

    private void CheckForNewerMediaServerAgents(string agentsSourcePath, string agentsDestPath)
    {
        Console.WriteLine($"[Sportarr] Found agents source directory");

        var needsCopy = !_fileSystem.Directory.Exists(agentsDestPath);

        // Check if we need to update (source is newer)
        if (!needsCopy && _fileSystem.Directory.Exists(agentsDestPath))
        {
            var sourceInfo = new DirectoryInfo(agentsSourcePath);
            var destInfo = new DirectoryInfo(agentsDestPath);
            needsCopy = sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc;
        }

        if (needsCopy)
        {
            Console.WriteLine($"[Sportarr] Copying media server agents to {agentsDestPath}...");
            CopyDirectory(agentsSourcePath, agentsDestPath);
            Console.WriteLine("[Sportarr] Media server agents copied successfully");
            Console.WriteLine("[Sportarr] Plex agent available at: {0}", _fileSystem.Path.Combine(agentsDestPath, C_PLEX, C_SPORTARR_BUNDLE));
        }
        else
        {
            Console.WriteLine($"[Sportarr] Media server agents already available at {agentsDestPath}");
        }
    }

    private async Task CreateMediaServerAgents(string agentsDestPath)
    {
        // Agents not in build output - create them dynamically
        Console.WriteLine($"[Sportarr] Agents not found in build output, checking config directory...");

        var plexAgentFile = _fileSystem.Path.Combine(agentsDestPath, C_PLEX, C_SPORTARR_BUNDLE, "Contents", "Code", "__init__.py");
        var needsUpdate = !_fileSystem.Directory.Exists(agentsDestPath) || !_fileSystem.File.Exists(plexAgentFile);

        // Check if existing agent has the broken code (import os, CRLF line endings)
        if (!needsUpdate && _fileSystem.File.Exists(plexAgentFile))
        {
            var existingCode = await _fileSystem.File.ReadAllTextAsync(plexAgentFile);
            // Detect old broken agent: has "import os" or "os.environ" or CRLF line endings
            if (existingCode.Contains("import os") || existingCode.Contains("os.environ") || existingCode.Contains("\r\n"))
            {
                Console.WriteLine("[Sportarr] Detected outdated Plex agent with CRLF or import issues, updating...");
                needsUpdate = true;
            }
        }

        if (!needsUpdate)
        {
            Console.WriteLine($"[Sportarr] Media server agents already available at {agentsDestPath}");
            return;
        }

        Console.WriteLine($"[Sportarr] Creating/updating agents in {agentsDestPath}...");
        await CreateDefaultAgents(agentsDestPath);
        Console.WriteLine("[Sportarr] Agents created/updated successfully");
        Console.WriteLine("[Sportarr] Plex agent available at: {0}", Path.Combine(agentsDestPath, C_PLEX, C_SPORTARR_BUNDLE));
    }

    private void CopyDirectory(string sourceDir, string destDir)
    {
        _fileSystem.Directory.CreateDirectory(destDir);

        foreach (var file in _fileSystem.Directory.GetFiles(sourceDir))
        {
            var destFile = _fileSystem.Path.Combine(destDir, _fileSystem.Path.GetFileName(file));
            _fileSystem.File.Copy(file, destFile, true);
        }

        foreach (var dir in _fileSystem.Directory.GetDirectories(sourceDir))
        {
            var dirName = _fileSystem.Path.GetFileName(dir);
            // Skip obj and bin directories (build artifacts)
            if (dirName == "obj" || dirName == "bin")
                continue;
            CopyDirectory(dir, _fileSystem.Path.Combine(destDir, dirName));
        }
    }

    // Create default agents when not available in build output
    private async Task CreateDefaultAgents(string agentsDestPath)
    {
        // Create Plex agent
        var plexPath = _fileSystem.Path.Combine(agentsDestPath, C_PLEX, C_SPORTARR_BUNDLE, "Contents", "Code");
        _fileSystem.Directory.CreateDirectory(plexPath);

        // Fixed Plex agent code - no imports, hardcoded URL, uses LF line endings
        var plexAgentCode = "# -*- coding: utf-8 -*-\n\nSPORTARR_API_URL = 'https://sportarr.net'\n\n\ndef Start():\n    Log.Info(\"[Sportarr] Agent starting...\")\n    Log.Info(\"[Sportarr] API URL: %s\" % SPORTARR_API_URL)\n    HTTP.CacheTime = 3600\n\n\nclass SportarrAgent(Agent.TV_Shows):\n    name = 'Sportarr'\n    languages = ['en']\n    primary_provider = True\n    fallback_agent = False\n    accepts_from = ['com.plexapp.agents.localmedia']\n\n    def search(self, results, media, lang, manual):\n        Log.Info(\"[Sportarr] Searching for: %s\" % media.show)\n\n        try:\n            search_url = \"%s/api/metadata/plex/search?title=%s\" % (\n                SPORTARR_API_URL,\n                String.Quote(media.show, usePlus=True)\n            )\n\n            if media.year:\n                search_url = search_url + \"&year=%s\" % media.year\n\n            Log.Debug(\"[Sportarr] Search URL: %s\" % search_url)\n            response = JSON.ObjectFromURL(search_url)\n\n            if 'results' in response:\n                for idx, series in enumerate(response['results'][:10]):\n                    score = 100 - (idx * 5)\n\n                    if series.get('title', '').lower() == media.show.lower():\n                        score = 100\n\n                    results.Append(MetadataSearchResult(\n                        id=str(series.get('id')),\n                        name=series.get('title'),\n                        year=series.get('year'),\n                        score=score,\n                        lang=lang\n                    ))\n\n                    Log.Info(\"[Sportarr] Found: %s (ID: %s, Score: %d)\" % (\n                        series.get('title'), series.get('id'), score\n                    ))\n\n        except Exception as e:\n            Log.Error(\"[Sportarr] Search error: %s\" % str(e))\n\n    def update(self, metadata, media, lang, force):\n        Log.Info(\"[Sportarr] Updating metadata for ID: %s\" % metadata.id)\n\n        try:\n            series_url = \"%s/api/metadata/plex/series/%s\" % (SPORTARR_API_URL, metadata.id)\n            Log.Debug(\"[Sportarr] Series URL: %s\" % series_url)\n            series = JSON.ObjectFromURL(series_url)\n\n            if series:\n                metadata.title = series.get('title')\n                metadata.summary = series.get('summary')\n                metadata.originally_available_at = None\n\n                if series.get('year'):\n                    try:\n                        metadata.originally_available_at = Datetime.ParseDate(\"%s-01-01\" % series.get('year'))\n                    except:\n                        pass\n\n                metadata.studio = series.get('studio')\n                metadata.content_rating = series.get('content_rating')\n\n                metadata.genres.clear()\n                for genre in series.get('genres', []):\n                    metadata.genres.add(genre)\n\n                if series.get('poster_url'):\n                    try:\n                        metadata.posters[series['poster_url']] = Proxy.Media(\n                            HTTP.Request(series['poster_url']).content\n                        )\n                    except Exception as e:\n                        Log.Warn(\"[Sportarr] Failed to fetch poster: %s\" % e)\n\n                if series.get('banner_url'):\n                    try:\n                        metadata.banners[series['banner_url']] = Proxy.Media(\n                            HTTP.Request(series['banner_url']).content\n                        )\n                    except Exception as e:\n                        Log.Warn(\"[Sportarr] Failed to fetch banner: %s\" % e)\n\n                if series.get('fanart_url'):\n                    try:\n                        metadata.art[series['fanart_url']] = Proxy.Media(\n                            HTTP.Request(series['fanart_url']).content\n                        )\n                    except Exception as e:\n                        Log.Warn(\"[Sportarr] Failed to fetch fanart: %s\" % e)\n\n            seasons_url = \"%s/api/metadata/plex/series/%s/seasons\" % (SPORTARR_API_URL, metadata.id)\n            Log.Debug(\"[Sportarr] Seasons URL: %s\" % seasons_url)\n            seasons_response = JSON.ObjectFromURL(seasons_url)\n\n            if 'seasons' in seasons_response:\n                for season_data in seasons_response['seasons']:\n                    season_num = season_data.get('season_number')\n                    if season_num in media.seasons:\n                        season = metadata.seasons[season_num]\n                        season.title = season_data.get('title', \"Season %s\" % season_num)\n                        season.summary = season_data.get('summary', '')\n\n                        if season_data.get('poster_url'):\n                            try:\n                                season.posters[season_data['poster_url']] = Proxy.Media(\n                                    HTTP.Request(season_data['poster_url']).content\n                                )\n                            except Exception as e:\n                                Log.Warn(\"[Sportarr] Failed to fetch season poster: %s\" % e)\n\n                        self.update_episodes(metadata, media, season_num)\n\n        except Exception as e:\n            Log.Error(\"[Sportarr] Update error: %s\" % str(e))\n\n    def update_episodes(self, metadata, media, season_num):\n        Log.Debug(\"[Sportarr] Updating episodes for season %s\" % season_num)\n\n        try:\n            episodes_url = \"%s/api/metadata/plex/series/%s/season/%s/episodes\" % (\n                SPORTARR_API_URL, metadata.id, season_num\n            )\n            Log.Debug(\"[Sportarr] Episodes URL: %s\" % episodes_url)\n            episodes_response = JSON.ObjectFromURL(episodes_url)\n\n            if 'episodes' in episodes_response:\n                for ep_data in episodes_response['episodes']:\n                    ep_num = ep_data.get('episode_number')\n\n                    if ep_num in media.seasons[season_num].episodes:\n                        episode = metadata.seasons[season_num].episodes[ep_num]\n\n                        title = ep_data.get('title', \"Episode %s\" % ep_num)\n                        if ep_data.get('part_name'):\n                            title = \"%s - %s\" % (title, ep_data['part_name'])\n\n                        episode.title = title\n                        episode.summary = ep_data.get('summary', '')\n\n                        if ep_data.get('air_date'):\n                            try:\n                                episode.originally_available_at = Datetime.ParseDate(ep_data['air_date'])\n                            except:\n                                pass\n\n                        if ep_data.get('duration_minutes'):\n                            episode.duration = ep_data['duration_minutes'] * 60 * 1000\n\n                        if ep_data.get('thumb_url'):\n                            try:\n                                episode.thumbs[ep_data['thumb_url']] = Proxy.Media(\n                                    HTTP.Request(ep_data['thumb_url']).content\n                                )\n                            except Exception as e:\n                                Log.Warn(\"[Sportarr] Failed to fetch episode thumb: %s\" % e)\n\n                        Log.Debug(\"[Sportarr] Updated S%sE%s: %s\" % (season_num, ep_num, title))\n\n        except Exception as e:\n            Log.Error(\"[Sportarr] Episodes update error: %s\" % str(e))\n";

        await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(plexPath, "__init__.py"), plexAgentCode);

        // Create Info.plist for Plex (using LF line endings)
        var infoPlistPath = _fileSystem.Path.Combine(agentsDestPath, C_PLEX, C_SPORTARR_BUNDLE, "Contents");
        var infoPlist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n    <key>CFBundleIdentifier</key>\n    <string>com.sportarr.agents.sportarr</string>\n\n    <key>PlexPluginClass</key>\n    <string>Agent</string>\n\n    <key>PlexClientPlatforms</key>\n    <string>*</string>\n\n    <key>PlexClientPlatformExclusions</key>\n    <string></string>\n\n    <key>PlexFrameworkVersion</key>\n    <string>2</string>\n\n    <key>PlexPluginCodePolicy</key>\n    <string>Elevated</string>\n\n    <key>PlexBundleVersion</key>\n    <string>1</string>\n\n    <key>CFBundleVersion</key>\n    <string>1.0.0</string>\n\n    <key>PlexAgentAttributionText</key>\n    <string>Metadata provided by Sportarr</string>\n</dict>\n</plist>\n";
        await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(infoPlistPath, "Info.plist"), infoPlist);

        // Create Jellyfin agent placeholder
        var jellyfinPath = _fileSystem.Path.Combine(agentsDestPath, "jellyfin");
        _fileSystem.Directory.CreateDirectory(jellyfinPath);
        var jellyfinReadme = @"# Sportarr Jellyfin Plugin

The Jellyfin plugin needs to be built from source or downloaded from releases.

## Building from Source

```bash
cd agents/jellyfin/Sportarr
dotnet build -c Release
```

## Installation

Copy the built DLL to your Jellyfin plugins directory:
- Docker: /config/plugins/Sportarr/
- Windows: %APPDATA%\Jellyfin\Server\plugins\Sportarr\
- Linux: ~/.local/share/jellyfin/plugins/Sportarr/

Then restart Jellyfin.
";
        await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(jellyfinPath, "README.md"), jellyfinReadme);

        // Create a README for the agents folder
        var agentsReadme = @"# Sportarr Media Server Agents

This folder contains metadata agents for media servers.

## Plex

The `plex/Sportarr.bundle` folder is a Plex metadata agent.
Copy it to your Plex plugins directory and restart Plex.

## Jellyfin

See `jellyfin/README.md` for Jellyfin plugin instructions.
";
        await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(agentsDestPath, "README.md"), agentsReadme);
    }

    #endregion
}
