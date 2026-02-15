using System;
using Saves;
using UnityEngine;

namespace BloodMoon
{
    [Serializable]
    public class BloodMoonEvent
    {
        [Serializable]
        private struct SaveData
        {
            public bool valid;
            public long offsetTicks;
            public void Setup(BloodMoonEvent e)
            {
                e._offsetTicks = offsetTicks;
            }
        }

        private const string SaveKey = "BloodMoon_EventData";
        private long _offsetTicks;

        private TimeSpan SleepTime => TimeSpan.FromHours(BloodMoon.Utils.ModConfig.Instance.SleepHours);
        private TimeSpan ActiveTime => TimeSpan.FromHours(BloodMoon.Utils.ModConfig.Instance.ActiveHours);

        private long Period => SleepTime.Ticks + ActiveTime.Ticks;

        /// <summary>
        /// 从存档系统加载事件数据
        /// </summary>
        public void Load()
        {
            var sd = SavesSystem.Load<SaveData>(SaveKey);
            if (!sd.valid)
            {
                SetRandomOffset();
            }
            else
            {
                sd.Setup(this);
            }
        }

        /// <summary>
        /// 保存事件数据到存档系统
        /// </summary>
        public void Save()
        {
            var sd = new SaveData { valid = true, offsetTicks = _offsetTicks };
            SavesSystem.Save(SaveKey, sd);
        }

        /// <summary>
        /// 设置随机的时间偏移
        /// </summary>
        private void SetRandomOffset()
        {
            // 使用完整周期范围以获得更好的随机化
            // 在较旧的Unity/System版本中，Random.Range不支持long类型
            // 因此我们构造它
            double rnd = UnityEngine.Random.value;
            _offsetTicks = (long)(rnd * Period);
        }

        /// <summary>
        /// 检查血月事件是否当前是否激活
        /// </summary>
        /// <param name="now">当前游戏时间</param>
        /// <returns>如果事件激活返回true</returns>
        public bool IsActive(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            bool active = pos >= SleepTime.Ticks;
            return active;
        }

        /// <summary>
        /// 获取到血月来临的预计时间
        /// </summary>
        /// <param name="now">当前游戏时间</param>
        /// <returns>距离事件开始的时间跨度</returns>
        public TimeSpan GetETA(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            if (pos < SleepTime.Ticks)
            {
                return TimeSpan.FromTicks(SleepTime.Ticks - pos);
            }
            return TimeSpan.Zero;
        }

        /// <summary>
        /// 获取血月结束的预计时间
        /// </summary>
        /// <param name="now">当前游戏时间</param>
        /// <returns>事件结束的时间跨度</returns>
        public TimeSpan GetOverETA(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            return TimeSpan.FromTicks(Period - pos);
        }

        /// <summary>
        /// 获取休眠阶段的进度百分比
        /// </summary>
        /// <param name="now">当前游戏时间</param>
        /// <returns>进度百分比（0.0到1.0</returns>
        public float GetSleepPercent(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            return (float)pos / SleepTime.Ticks;
        }

        /// <summary>
        /// 获取激活阶段的剩余时间百分比
        /// </summary>
        /// <param name="now">当前游戏时间</param>
        /// <returns>剩余时间百分比（0.0到1.0</returns>
        public float GetActiveRemainPercent(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period - SleepTime.Ticks;
            return 1f - (float)pos / ActiveTime.Ticks;
        }
    }
}

