#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AutoCollections.Configuration;

namespace Jellyfin.Plugin.AutoCollections.AutoDiscovery
{
    /// <summary>
    /// Represents an auto-discovered collection with its items.
    /// </summary>
    public class DiscoveredCollection
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty; // "MovieSeries", "Genre", "Studio", "Decade"
        public List<BaseItem> Items { get; set; } = new List<BaseItem>();
    }

    /// <summary>
    /// Manages the auto-discovery of collections based on library content.
    /// </summary>
    public class AutoDiscoveryManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly MovieSeriesDetector _movieSeriesDetector;

        public AutoDiscoveryManager(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _movieSeriesDetector = new MovieSeriesDetector(logger);
        }

        /// <summary>
        /// Discovers all collections based on configuration settings.
        /// </summary>
        public List<DiscoveredCollection> DiscoverCollections(PluginConfiguration config)
        {
            var discoveries = new List<DiscoveredCollection>();

            if (!config.EnableAutoDiscovery)
            {
                _logger.LogDebug("Auto-Discovery is disabled");
                return discoveries;
            }

            _logger.LogInformation("Starting Auto-Discovery of collections");

            // Get all movies and series from library
            var allMovies = GetAllMovies();
            var allSeries = GetAllSeries();

            _logger.LogInformation("Found {MovieCount} movies and {SeriesCount} series in library", 
                allMovies.Count, allSeries.Count);

            // Discover Movie Series
            if (config.DetectMovieSeries)
            {
                var movieSeriesCollections = DiscoverMovieSeries(
                    allMovies,
                    config.MinMoviesInSeries,
                    config.IncludeFirstMovieWithoutNumber,
                    config.IncludeSpinoffs,
                    config.MovieSeriesNamingPattern,
                    config.AutoDiscoveryPrefix);
                discoveries.AddRange(movieSeriesCollections);
            }

            // Discover Genre Collections
            if (config.CreateGenreCollections)
            {
                var genreCollections = DiscoverGenreCollections(
                    allMovies,
                    allSeries,
                    config.MinItemsPerGenre,
                    config.GenreNamingPattern,
                    config.AutoDiscoveryPrefix);
                discoveries.AddRange(genreCollections);
            }

            // Discover Studio Collections
            if (config.CreateStudioCollections)
            {
                var studioCollections = DiscoverStudioCollections(
                    allMovies,
                    allSeries,
                    config.MinItemsPerStudio,
                    config.StudioNamingPattern,
                    config.AutoDiscoveryPrefix);
                discoveries.AddRange(studioCollections);
            }

            // Discover Decade Collections
            if (config.CreateDecadeCollections)
            {
                var decadeCollections = DiscoverDecadeCollections(
                    allMovies,
                    allSeries,
                    config.MinItemsPerDecade,
                    config.DecadeNamingPattern,
                    config.AutoDiscoveryPrefix);
                discoveries.AddRange(decadeCollections);
            }

            _logger.LogInformation("Auto-Discovery complete. Found {Count} collections", discoveries.Count);
            return discoveries;
        }

        /// <summary>
        /// Gets all movies from the library.
        /// </summary>
        private List<Movie> GetAllMovies()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();
        }

        /// <summary>
        /// Gets all TV series from the library.
        /// </summary>
        private List<MediaBrowser.Controller.Entities.TV.Series> GetAllSeries()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<MediaBrowser.Controller.Entities.TV.Series>().ToList();
        }

        /// <summary>
        /// Discovers movie series collections.
        /// </summary>
        private List<DiscoveredCollection> DiscoverMovieSeries(
            List<Movie> movies,
            int minMoviesInSeries,
            bool includeFirstWithoutNumber,
            bool includeSpinoffs,
            string namingPattern,
            string prefix)
        {
            _logger.LogInformation("Detecting movie series (min: {Min} movies)", minMoviesInSeries);

            var detectedSeries = _movieSeriesDetector.DetectSeries(
                movies,
                minMoviesInSeries,
                includeFirstWithoutNumber,
                includeSpinoffs);

            var collections = new List<DiscoveredCollection>();
            foreach (var series in detectedSeries)
            {
                var collectionName = _movieSeriesDetector.ApplyNamingPattern(
                    series.BaseTitle, namingPattern, prefix);

                collections.Add(new DiscoveredCollection
                {
                    Name = collectionName,
                    Category = "MovieSeries",
                    Items = series.Movies.Cast<BaseItem>().ToList()
                });

                _logger.LogDebug("Discovered movie series: {Name} with {Count} movies", 
                    collectionName, series.Movies.Count);
            }

            return collections;
        }

        /// <summary>
        /// Discovers genre-based collections.
        /// </summary>
        private List<DiscoveredCollection> DiscoverGenreCollections(
            List<Movie> movies,
            List<MediaBrowser.Controller.Entities.TV.Series> series,
            int minItems,
            string namingPattern,
            string prefix)
        {
            _logger.LogInformation("Detecting genre collections (min: {Min} items)", minItems);

            // Collect all genres from movies and series
            var genreGroups = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in movies)
            {
                if (movie.Genres != null)
                {
                    foreach (var genre in movie.Genres)
                    {
                        if (string.IsNullOrWhiteSpace(genre))
                            continue;

                        if (!genreGroups.ContainsKey(genre))
                            genreGroups[genre] = new List<BaseItem>();
                        
                        genreGroups[genre].Add(movie);
                    }
                }
            }

            foreach (var show in series)
            {
                if (show.Genres != null)
                {
                    foreach (var genre in show.Genres)
                    {
                        if (string.IsNullOrWhiteSpace(genre))
                            continue;

                        if (!genreGroups.ContainsKey(genre))
                            genreGroups[genre] = new List<BaseItem>();
                        
                        genreGroups[genre].Add(show);
                    }
                }
            }

            // Filter by minimum items and create collections
            var collections = new List<DiscoveredCollection>();
            foreach (var group in genreGroups.Where(g => g.Value.Count >= minItems))
            {
                var collectionName = namingPattern.Replace("{Genre}", group.Key);
                if (!string.IsNullOrEmpty(prefix))
                    collectionName = prefix + collectionName;

                collections.Add(new DiscoveredCollection
                {
                    Name = collectionName,
                    Category = "Genre",
                    Items = group.Value
                });

                _logger.LogDebug("Discovered genre collection: {Name} with {Count} items", 
                    collectionName, group.Value.Count);
            }

            return collections;
        }

        /// <summary>
        /// Discovers studio-based collections.
        /// </summary>
        private List<DiscoveredCollection> DiscoverStudioCollections(
            List<Movie> movies,
            List<MediaBrowser.Controller.Entities.TV.Series> series,
            int minItems,
            string namingPattern,
            string prefix)
        {
            _logger.LogInformation("Detecting studio collections (min: {Min} items)", minItems);

            var studioGroups = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in movies)
            {
                if (movie.Studios != null)
                {
                    foreach (var studio in movie.Studios)
                    {
                        if (string.IsNullOrWhiteSpace(studio))
                            continue;

                        if (!studioGroups.ContainsKey(studio))
                            studioGroups[studio] = new List<BaseItem>();
                        
                        studioGroups[studio].Add(movie);
                    }
                }
            }

            foreach (var show in series)
            {
                if (show.Studios != null)
                {
                    foreach (var studio in show.Studios)
                    {
                        if (string.IsNullOrWhiteSpace(studio))
                            continue;

                        if (!studioGroups.ContainsKey(studio))
                            studioGroups[studio] = new List<BaseItem>();
                        
                        studioGroups[studio].Add(show);
                    }
                }
            }

            var collections = new List<DiscoveredCollection>();
            foreach (var group in studioGroups.Where(g => g.Value.Count >= minItems))
            {
                var collectionName = namingPattern.Replace("{Studio}", group.Key);
                if (!string.IsNullOrEmpty(prefix))
                    collectionName = prefix + collectionName;

                collections.Add(new DiscoveredCollection
                {
                    Name = collectionName,
                    Category = "Studio",
                    Items = group.Value
                });

                _logger.LogDebug("Discovered studio collection: {Name} with {Count} items", 
                    collectionName, group.Value.Count);
            }

            return collections;
        }

        /// <summary>
        /// Discovers decade-based collections.
        /// </summary>
        private List<DiscoveredCollection> DiscoverDecadeCollections(
            List<Movie> movies,
            List<MediaBrowser.Controller.Entities.TV.Series> series,
            int minItems,
            string namingPattern,
            string prefix)
        {
            _logger.LogInformation("Detecting decade collections (min: {Min} items)", minItems);

            var decadeGroups = new Dictionary<int, List<BaseItem>>();

            foreach (var movie in movies)
            {
                if (movie.PremiereDate.HasValue)
                {
                    int decade = (movie.PremiereDate.Value.Year / 10) * 10;
                    if (!decadeGroups.ContainsKey(decade))
                        decadeGroups[decade] = new List<BaseItem>();
                    
                    decadeGroups[decade].Add(movie);
                }
                else if (movie.ProductionYear.HasValue)
                {
                    int decade = (movie.ProductionYear.Value / 10) * 10;
                    if (!decadeGroups.ContainsKey(decade))
                        decadeGroups[decade] = new List<BaseItem>();
                    
                    decadeGroups[decade].Add(movie);
                }
            }

            foreach (var show in series)
            {
                if (show.PremiereDate.HasValue)
                {
                    int decade = (show.PremiereDate.Value.Year / 10) * 10;
                    if (!decadeGroups.ContainsKey(decade))
                        decadeGroups[decade] = new List<BaseItem>();
                    
                    decadeGroups[decade].Add(show);
                }
                else if (show.ProductionYear.HasValue)
                {
                    int decade = (show.ProductionYear.Value / 10) * 10;
                    if (!decadeGroups.ContainsKey(decade))
                        decadeGroups[decade] = new List<BaseItem>();
                    
                    decadeGroups[decade].Add(show);
                }
            }

            var collections = new List<DiscoveredCollection>();
            foreach (var group in decadeGroups.Where(g => g.Value.Count >= minItems).OrderBy(g => g.Key))
            {
                var decadeStr = group.Key.ToString();
                var collectionName = namingPattern.Replace("{Decade}", decadeStr);
                if (!string.IsNullOrEmpty(prefix))
                    collectionName = prefix + collectionName;

                collections.Add(new DiscoveredCollection
                {
                    Name = collectionName,
                    Category = "Decade",
                    Items = group.Value
                });

                _logger.LogDebug("Discovered decade collection: {Name} with {Count} items", 
                    collectionName, group.Value.Count);
            }

            return collections;
        }
    }
}
