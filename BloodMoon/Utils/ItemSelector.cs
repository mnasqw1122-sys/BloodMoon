using System;
using System.Collections.Generic;
using System.Linq;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;

namespace BloodMoon.Utils
{
    public static class ItemSelector
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<int>> _cache = new Dictionary<string, List<int>>();

        public static void ClearCache()
        {
            lock (_lock)
            {
                _cache.Clear();
            }
        }

        public static List<int> GetBestItems(ItemFilter filter, int count, int minValue = 0)
        {
            lock (_lock)
            {
                // Search using the filter (this handles tags, caliber, quality)
                try
                {
                    var candidates = ItemAssetsCollection.Search(filter);
                    if (candidates == null || candidates.Length == 0) return new List<int>();

                    // Get metadata for all candidates to check value
                    var itemsWithValues = new List<(int ID, int Price)>();
                    for(int i=0; i<candidates.Length; i++)
                    {
                        int id = candidates[i];
                        var meta = ItemAssetsCollection.GetMetaData(id);
                        if (meta.id != 0 && meta.priceEach >= minValue)
                        {
                            itemsWithValues.Add((id, meta.priceEach));
                        }
                    }

                    if (itemsWithValues.Count == 0) return new List<int>();

                    // Just take top count for now as per "High Value" requirement.
                    return itemsWithValues
                        .OrderByDescending(x => x.Price)
                        .Take(count)
                        .Select(x => x.ID)
                        .ToList();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BloodMoon] GetBestItems Error: {e.Message}");
                    return new List<int>();
                }
            }
        }

        public static List<int> GetRandomHighValueItems(ItemFilter filter, int count, int minValue)
        {
            lock (_lock)
            {
                try
                {
                    var candidates = ItemAssetsCollection.Search(filter);
                    if (candidates == null || candidates.Length == 0) return new List<int>();

                    var highValueItems = new List<int>();
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        int id = candidates[i];
                        var meta = ItemAssetsCollection.GetMetaData(id);
                        if (meta.id != 0 && meta.priceEach >= minValue)
                        {
                            highValueItems.Add(id);
                        }
                    }

                    if (highValueItems.Count == 0) return new List<int>();

                    var result = new List<int>();
                    for (int i = 0; i < count; i++)
                    {
                        result.Add(highValueItems[UnityEngine.Random.Range(0, highValueItems.Count)]);
                    }
                    return result;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BloodMoon] GetRandomHighValueItems Error: {e.Message}");
                    return new List<int>();
                }
            }
        }

        public static List<int> GetBestItemsByTags(string[] tagNames, int count, int minQuality = 0, int minValue = 0)
        {
            lock (_lock)
            {
                if (tagNames == null || tagNames.Length == 0) return new List<int>();

                // Cache Check - REMOVED count from key to allow reuse
                var sortedTags = tagNames.Where(t => !string.IsNullOrEmpty(t)).OrderBy(t => t).ToArray();
                string key = $"Tags:{string.Join("|", sortedTags)}_Q:{minQuality}_V:{minValue}";
                
                List<int> cachedList;
                if (!_cache.TryGetValue(key, out cachedList))
                {
                    // Compute full list
                    cachedList = new List<int>();
                    var tags = new List<Tag>();
                    foreach (var name in sortedTags)
                    {
                        try
                        {
                            var t = TagUtilities.TagFromString(name);
                            if (t != null) tags.Add(t);
                        }
                        catch { } 
                    }

                    if (tags.Count > 0) 
                    {
                        var allCandidates = new HashSet<int>();
                        foreach (var tag in tags)
                        {
                            try
                            {
                                // OR Logic: Combine results of each tag
                                var filter = new ItemFilter 
                                { 
                                    requireTags = new Tag[] { tag }, 
                                    minQuality = minQuality,
                                    maxQuality = 6 
                                };
                                var ids = ItemAssetsCollection.Search(filter);
                                if (ids != null)
                                {
                                    for (int i = 0; i < ids.Length; i++) allCandidates.Add(ids[i]);
                                }
                            }
                            catch { }
                        }

                        var itemsWithValues = new List<(int ID, int Price)>();
                        foreach (var id in allCandidates)
                        {
                            try
                            {
                                var meta = ItemAssetsCollection.GetMetaData(id);
                                if (meta.id != 0 && meta.priceEach >= minValue)
                                {
                                    itemsWithValues.Add((id, meta.priceEach));
                                }
                            }
                            catch { }
                        }
                        
                        // Sort descending by price
                        itemsWithValues.Sort((a, b) => b.Price.CompareTo(a.Price));
                        cachedList = itemsWithValues.Select(x => x.ID).ToList();
                    }
                    
                    _cache[key] = cachedList;
                }

                // Now just take what we need from cache
                if (cachedList.Count == 0) return new List<int>();
                
                if (count >= cachedList.Count) return new List<int>(cachedList);
                return cachedList.GetRange(0, count);
            }
        }
        
        public static int GetBestItem(ItemFilter filter)
        {
            var best = GetBestItems(filter, 1);
            return best.Count > 0 ? best[0] : 0;
        }
    }
}
