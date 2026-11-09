using System;
using NAudio.CoreAudioApi;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 播报时临时调整系统音量：播报前调到目标值，播完还原。
    /// 只动默认播放设备的「主音量」，不碰静音开关的其它状态。
    /// </summary>
    public static class SystemVolume
    {
        /// <summary>把系统音量设成 volume(0-100)，返回可还原的句柄；失败返回 null。</summary>
        public static float? Apply(int volume)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null) return null;

                var vol = device.AudioEndpointVolume;
                float old = vol.MasterVolumeLevelScalar;

                float target = Math.Clamp(volume, 0, 100) / 100f;
                if (Math.Abs(old - target) > 0.005f)
                {
                    vol.MasterVolumeLevelScalar = target;
                    Log.Write($"播报音量已调整: {Math.Round(old * 100)}% → {Math.Round(target * 100)}%");
                }

                return old;
            }
            catch (Exception ex)
            {
                Log.Write("调整系统音量失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>还原到调整前的音量（用户中途手动改过就不覆盖）。</summary>
        public static void Restore(float? previous, int applied)
        {
            if (previous == null) return;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device == null) return;

                var vol = device.AudioEndpointVolume;
                float now = vol.MasterVolumeLevelScalar;
                float appliedValue = Math.Clamp(applied, 0, 100) / 100f;

                // 只有当前仍然是「我们设的那个值」才还原，避免盖掉别人刚做的调整
                if (Math.Abs(now - appliedValue) > 0.02f) return;

                vol.MasterVolumeLevelScalar = previous.Value;
                Log.Write($"播报音量已还原: {Math.Round(previous.Value * 100)}%");
            }
            catch (Exception ex)
            {
                Log.Write("还原系统音量失败: " + ex.Message);
            }
        }
    }
}
