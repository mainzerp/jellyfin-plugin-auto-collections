#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCollections.AutoDiscovery
{
    /// <summary>
    /// Represents a detected movie series with its base title and member movies.
    /// </summary>
    public class DetectedMovieSeries
    {
        public string BaseTitle { get; set; } = string.Empty;
        public string CollectionName { get; set; } = string.Empty;
        public List<Movie> Movies { get; set; } = new List<Movie>();
    }

    /// <summary>
    /// Detects movie series/franchises based on title patterns.
    /// Identifies films like "John Wick 1-4", "Star Wars Episode I-IX", etc.
    /// </summary>
    public class MovieSeriesDetector
    {
        private readonly ILogger _logger;

        // Regex patterns for detecting series numbering
        private static readonly Regex NumberAtEndRegex = new Regex(
            @"^(.+?)\s*[\s:\-]+\s*(\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RomanNumeralAtEndRegex = new Regex(
            @"^(.+?)\s*[\s:\-]*\s*(I{1,3}|IV|V|VI{0,3}|IX|X|XI{0,3})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PartNumberRegex = new Regex(
            @"^(.+?)\s*[\s:\-]+\s*(?:Part|Pt\.?|Teil)\s*(\d+|I{1,3}|IV|V|VI{0,3}|IX|X|XI{0,3})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EpisodeNumberRegex = new Regex(
            @"^(.+?)\s*[\s:\-]+\s*(?:Episode|Ep\.?)\s*(\d+|I{1,3}|IV|V|VI{0,3}|IX|X|XI{0,3})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ChapterNumberRegex = new Regex(
            @"^(.+?)\s*[\s:\-]+\s*(?:Chapter|Ch\.?|Kapitel)\s*(\d+|I{1,3}|IV|V|VI{0,3}|IX|X|XI{0,3})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SubtitleAfterColonRegex = new Regex(
            @"^(.+?)\s*:\s*.+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Pattern for spin-off detection (e.g., "Rogue One: A Star Wars Story")
        private static readonly Regex SpinoffPatternRegex = new Regex(
            @"^.+?:\s*(?:A|An|The)?\s*(.+?)\s*(?:Story|Tale|Adventure|Chronicle|Movie)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Common articles to remove for comparison
        private static readonly string[] ArticlesToRemove = { "The ", "A ", "An ", "Der ", "Die ", "Das ", "Ein ", "Eine " };

        public MovieSeriesDetector(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Detects movie series from a list of movies.
        /// </summary>
        /// <param name="movies">All movies to analyze</param>
        /// <param name="minMoviesInSeries">Minimum number of movies required to form a series</param>
        /// <param name="includeFirstWithoutNumber">If true, includes movies that might be the first in a series without numbering</param>
        /// <param name="includeSpinoffs">If true, attempts to detect spin-off movies</param>
        /// <returns>List of detected movie series</returns>
        public List<DetectedMovieSeries> DetectSeries(
            IEnumerable<Movie> movies,
            int minMoviesInSeries = 2,
            bool includeFirstWithoutNumber = true,
            bool includeSpinoffs = false)
        {
            var movieList = movies.ToList();
            _logger.LogInformation("Analyzing {Count} movies for series detection", movieList.Count);

            // Dictionary to group movies by their base title
            var seriesGroups = new Dictionary<string, List<Movie>>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in movieList)
            {
                if (string.IsNullOrWhiteSpace(movie.Name))
                    continue;

                var baseTitle = ExtractBaseTitle(movie.Name);
                if (!string.IsNullOrWhiteSpace(baseTitle))
                {
                    var normalizedTitle = NormalizeTitle(baseTitle);
                    if (!seriesGroups.ContainsKey(normalizedTitle))
                    {
                        seriesGroups[normalizedTitle] = new List<Movie>();
                    }
                    seriesGroups[normalizedTitle].Add(movie);
                }
            }

            // If includeFirstWithoutNumber, try to find the first movie without numbering
            if (includeFirstWithoutNumber)
            {
                FindFirstMoviesWithoutNumber(movieList, seriesGroups);
            }

            // If includeSpinoffs, try to add spin-off movies
            if (includeSpinoffs)
            {
                FindSpinoffMovies(movieList, seriesGroups);
            }

            // Filter groups that meet the minimum movie count
            var detectedSeries = seriesGroups
                .Where(g => g.Value.Count >= minMoviesInSeries)
                .Select(g => new DetectedMovieSeries
                {
                    BaseTitle = GetBestBaseTitle(g.Value),
                    Movies = g.Value.OrderBy(m => GetSequenceNumber(m.Name)).ToList()
                })
                .ToList();

            _logger.LogInformation("Detected {Count} movie series", detectedSeries.Count);
            foreach (var series in detectedSeries)
            {
                _logger.LogDebug("Series: {Title} with {Count} movies", series.BaseTitle, series.Movies.Count);
            }

            return detectedSeries;
        }

        /// <summary>
        /// Extracts the base title from a movie title by removing sequence indicators.
        /// </summary>
        public string ExtractBaseTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            string result = title.Trim();

            // Try each pattern to extract base title
            var patterns = new[]
            {
                EpisodeNumberRegex,
                PartNumberRegex,
                ChapterNumberRegex,
                RomanNumeralAtEndRegex,
                NumberAtEndRegex
            };

            foreach (var pattern in patterns)
            {
                var match = pattern.Match(result);
                if (match.Success && match.Groups.Count > 1)
                {
                    result = match.Groups[1].Value.Trim();
                    // Clean up trailing colons, dashes, etc.
                    result = Regex.Replace(result, @"[\s:\-]+$", "").Trim();
                    return result;
                }
            }

            // If no numbering pattern found, try to extract from subtitle pattern
            // Only if it looks like a sequel with subtitle (e.g., "John Wick: Parabellum")
            var subtitleMatch = SubtitleAfterColonRegex.Match(result);
            if (subtitleMatch.Success)
            {
                // Don't return this as a base title unless we're sure it's a series
                // This will be handled by FindFirstMoviesWithoutNumber
                return string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// Normalizes a title for comparison purposes.
        /// </summary>
        private string NormalizeTitle(string title)
        {
            var normalized = title.Trim();

            // Remove common articles at the beginning
            foreach (var article in ArticlesToRemove)
            {
                if (normalized.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(article.Length).Trim();
                    break;
                }
            }

            // Remove special characters and extra spaces
            normalized = Regex.Replace(normalized, @"[^\w\s]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            return normalized.ToLowerInvariant();
        }

        /// <summary>
        /// Finds movies that might be the first in a series without explicit numbering.
        /// </summary>
        private void FindFirstMoviesWithoutNumber(List<Movie> allMovies, Dictionary<string, List<Movie>> seriesGroups)
        {
            foreach (var group in seriesGroups.ToList())
            {
                var baseTitle = group.Key;
                var existingIds = new HashSet<Guid>(group.Value.Select(m => m.Id));

                // Look for movies that match the base title exactly (or with "The" prefix)
                foreach (var movie in allMovies)
                {
                    if (existingIds.Contains(movie.Id))
                        continue;

                    var normalizedMovieTitle = NormalizeTitle(movie.Name ?? "");
                    
                    // Check if this movie's title matches the base title
                    if (normalizedMovieTitle == baseTitle ||
                        normalizedMovieTitle == "the " + baseTitle ||
                        "the " + normalizedMovieTitle == baseTitle)
                    {
                        group.Value.Add(movie);
                        existingIds.Add(movie.Id);
                        _logger.LogDebug("Added '{Movie}' as potential first movie in series '{Series}'", 
                            movie.Name, baseTitle);
                    }
                }
            }
        }

        /// <summary>
        /// Finds spin-off movies for existing series.
        /// </summary>
        private void FindSpinoffMovies(List<Movie> allMovies, Dictionary<string, List<Movie>> seriesGroups)
        {
            var existingBaseTitles = seriesGroups.Keys.ToList();

            foreach (var movie in allMovies)
            {
                if (string.IsNullOrWhiteSpace(movie.Name))
                    continue;

                var match = SpinoffPatternRegex.Match(movie.Name);
                if (match.Success)
                {
                    var spinoffBaseTitle = NormalizeTitle(match.Groups[1].Value);
                    
                    // Check if this spin-off matches an existing series
                    foreach (var baseTitle in existingBaseTitles)
                    {
                        if (spinoffBaseTitle.Contains(baseTitle) || baseTitle.Contains(spinoffBaseTitle))
                        {
                            if (!seriesGroups[baseTitle].Any(m => m.Id == movie.Id))
                            {
                                seriesGroups[baseTitle].Add(movie);
                                _logger.LogDebug("Added '{Movie}' as spin-off for series '{Series}'", 
                                    movie.Name, baseTitle);
                            }
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the best representation of the base title from a group of movies.
        /// </summary>
        private string GetBestBaseTitle(List<Movie> movies)
        {
            // Try to find a movie without numbering - that's likely the canonical name
            foreach (var movie in movies)
            {
                var title = movie.Name ?? "";
                var baseTitle = ExtractBaseTitle(title);
                if (string.IsNullOrEmpty(baseTitle))
                {
                    // This movie doesn't have numbering, so its title is probably the series name
                    // Clean it up
                    var cleanTitle = Regex.Replace(title, @"[\s:\-]+$", "").Trim();
                    foreach (var article in ArticlesToRemove)
                    {
                        if (cleanTitle.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                        {
                            return cleanTitle;
                        }
                    }
                    return cleanTitle;
                }
            }

            // If all movies have numbering, use the extracted base title from the first one
            var firstBase = ExtractBaseTitle(movies.First().Name ?? "");
            if (!string.IsNullOrEmpty(firstBase))
            {
                // Capitalize properly
                if (firstBase.Length > 0)
                {
                    return char.ToUpper(firstBase[0]) + firstBase.Substring(1);
                }
                return firstBase;
            }

            return movies.First().Name ?? "Unknown Series";
        }

        /// <summary>
        /// Extracts a sequence number from a movie title for sorting.
        /// </summary>
        private int GetSequenceNumber(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return 0;

            // Try to find number at end
            var numberMatch = NumberAtEndRegex.Match(title);
            if (numberMatch.Success && int.TryParse(numberMatch.Groups[2].Value, out int num))
            {
                return num;
            }

            // Try Roman numerals
            var romanMatch = RomanNumeralAtEndRegex.Match(title);
            if (romanMatch.Success)
            {
                return RomanToInt(romanMatch.Groups[2].Value);
            }

            // Try Part/Episode patterns
            var patterns = new[] { PartNumberRegex, EpisodeNumberRegex, ChapterNumberRegex };
            foreach (var pattern in patterns)
            {
                var match = pattern.Match(title);
                if (match.Success)
                {
                    var numStr = match.Groups[2].Value;
                    if (int.TryParse(numStr, out int partNum))
                        return partNum;
                    return RomanToInt(numStr);
                }
            }

            // No number found - assume it's the first (original) movie
            return 0;
        }

        /// <summary>
        /// Converts a Roman numeral string to an integer.
        /// </summary>
        private int RomanToInt(string roman)
        {
            if (string.IsNullOrWhiteSpace(roman))
                return 0;

            var romanValues = new Dictionary<char, int>
            {
                {'I', 1}, {'V', 5}, {'X', 10}, {'L', 50}, 
                {'C', 100}, {'D', 500}, {'M', 1000}
            };

            roman = roman.ToUpperInvariant();
            int result = 0;
            int prevValue = 0;

            for (int i = roman.Length - 1; i >= 0; i--)
            {
                if (!romanValues.TryGetValue(roman[i], out int value))
                    return 0;

                if (value < prevValue)
                    result -= value;
                else
                    result += value;

                prevValue = value;
            }

            return result;
        }

        /// <summary>
        /// Applies the naming pattern to create a collection name.
        /// </summary>
        public string ApplyNamingPattern(string baseTitle, string pattern, string prefix = "")
        {
            var result = pattern.Replace("{Title}", baseTitle);
            if (!string.IsNullOrEmpty(prefix))
            {
                result = prefix + result;
            }
            return result;
        }
    }
}
