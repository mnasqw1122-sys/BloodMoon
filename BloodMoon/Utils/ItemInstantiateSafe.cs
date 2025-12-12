using Cysharp.Threading.Tasks;
using ItemStatsSystem;
using UnityEngine;

namespace BloodMoon.Utils
{
    public static class ItemInstantiateSafe
    {
        public static async UniTask<Item?> SafeInstantiateById(int id, int maxTries = 3)
        {
            for (int i = 0; i < maxTries; i++)
            {
                try
                {
                    var item = await ItemAssetsCollection.InstantiateAsync(id);
                    if (item != null) return item;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[BloodMoon] SafeInstantiateById failed id={id} try={i+1}: {e.Message}");
                }
                await UniTask.Yield();
            }
            return null;
        }

        public static async UniTask<Item?> SafeInstantiateRandom(int[] ids, int maxTries = 5)
        {
            if (ids == null || ids.Length == 0) return null;
            for (int i = 0; i < maxTries; i++)
            {
                int id = ids[UnityEngine.Random.Range(0, ids.Length)];
                var it = await SafeInstantiateById(id, 1);
                if (it != null) return it;
                await UniTask.Yield();
            }
            return null;
        }

        public static async UniTask<Item?> SafeInstantiateFilter(ItemFilter filter, int maxTries = 5)
        {
            var ids = ItemAssetsCollection.Search(filter);
            return await SafeInstantiateRandom(ids, maxTries);
        }
    }
}
