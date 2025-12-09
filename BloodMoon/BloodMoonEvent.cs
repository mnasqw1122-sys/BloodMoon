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

        private const string SaveKey = "BloodMoonEventData";
        private long _offsetTicks;

        private static readonly TimeSpan SleepTime = TimeSpan.FromHours(160);
        private static readonly TimeSpan ActiveTime = TimeSpan.FromHours(24);

        private long Period => SleepTime.Ticks + ActiveTime.Ticks;

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

        public void Save()
        {
            var sd = new SaveData { valid = true, offsetTicks = _offsetTicks };
            SavesSystem.Save(SaveKey, sd);
        }

        private void SetRandomOffset()
        {
            _offsetTicks = UnityEngine.Random.Range(0, 1000000);
        }

        public bool IsActive(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            return pos >= SleepTime.Ticks;
        }

        public TimeSpan GetETA(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            if (pos < SleepTime.Ticks)
            {
                return TimeSpan.FromTicks(SleepTime.Ticks - pos);
            }
            return TimeSpan.Zero;
        }

        public TimeSpan GetOverETA(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            return TimeSpan.FromTicks(Period - pos);
        }

        public float GetSleepPercent(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period;
            return (float)pos / SleepTime.Ticks;
        }

        public float GetActiveRemainPercent(TimeSpan now)
        {
            long pos = (now.Ticks + _offsetTicks) % Period - SleepTime.Ticks;
            return 1f - (float)pos / ActiveTime.Ticks;
        }
    }
}

