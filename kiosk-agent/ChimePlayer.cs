using System;
using System.IO;
using System.Media;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 播报提示音：现场合成 WAV，不依赖外部音频文件。
    ///
    /// 三款都是「客舱广播」风格的钟声，区别在音程与音色浓度：
    ///   cabin —— 真实客舱感：非谐泛音 + 轻微失谐拍频 + 击打瞬态 + 长余音
    ///   air   —— 叮—咚（高到低，中等长度）
    ///   airup —— 低沉版，音区更低、余音更长
    /// </summary>
    public static class ChimePlayer
    {
        private const int SampleRate = 44100;

        /// <summary>可选音效 id（与前端下拉框一致）</summary>
        public static readonly string[] Presets = { "cabin", "air", "airup" };

        public static bool IsValid(string id) =>
            !string.IsNullOrWhiteSpace(id) && Array.IndexOf(Presets, id.Trim().ToLowerInvariant()) >= 0;

        public static void PlayBefore(string preset) => Play(Render(preset, true));

        public static void PlayAfter(string preset) => Play(Render(preset, false));

        private static byte[] Render(string preset, bool before)
        {
            switch ((preset ?? "").Trim().ToLowerInvariant())
            {
                // 真实客舱广播：下行纯四度（E6 → B5），钟体非谐泛音 + 余音
                case "cabin":
                    return before
                        ? BuildRich(new[] { (1318.51, 1150), (987.77, 1650) }, 70)
                        : BuildRich(new[] { (987.77, 1150), (1318.51, 1650) }, 70);

                // 叮—咚：高到低，长度适中
                case "air":
                    return before
                        ? BuildBell(new[] { (987.77, 620), (659.25, 900) }, 55)
                        : BuildBell(new[] { (659.25, 620), (987.77, 900) }, 55);

                // 低沉版
                case "airup":
                    return before
                        ? BuildBell(new[] { (783.99, 700), (523.25, 950) }, 55)
                        : BuildBell(new[] { (523.25, 700), (783.99, 950) }, 55);

                default:
                    return BuildBell(new[] { (783.99, 700), (523.25, 950) }, 55);
            }
        }

        // ------------------------------------------------------------------
        // 钟体泛音：基频 + 谐波 + 非谐分音（真实金属钟/钟琴的关键）
        // ------------------------------------------------------------------
        private static readonly (double mult, double amp, double decay)[] BellPartials =
        {
            (1.00, 0.85, 1.00),
            (2.00, 0.45, 1.40),
            (3.01, 0.22, 1.85),
            (4.20, 0.12, 2.40),
            (5.43, 0.07, 3.00)
        };

        // 真实钟声的分音：低八度托底 + 基频厚实，高次分音收得很轻 → 醇厚不刺耳
        private static readonly (double mult, double amp, double decay)[] InharmonicPartials =
        {
            (0.50, 0.55, 0.55),   // 低八度嗡音：给"厚"的底盘
            (0.75, 0.18, 0.70),   // 半音区填充，让声音更饱满
            (1.00, 0.10, 0.90),   // 额外基频叠层（配合失谐产生厚度）
            (2.76, 0.10, 2.20),   // 一点钟体分音，保留金属感但不抢
            (5.40, 0.03, 4.00)    // 极轻的高频，只为"亮"一点点
        };

        /// <summary>普通钟声（谐波为主）</summary>
        private static byte[] BuildBell((double freq, int ms)[] notes, int gapMs) =>
            BuildNotes(notes, gapMs, BellPartials, attackMs: 8, tauDiv: 3.2, gain: 0.22);

        /// <summary>真实客舱钟声：谐波 + 非谐分音 + 失谐拍频 + 击打瞬态 + 长余音</summary>
        private static byte[] BuildRich((double freq, int ms)[] notes, int gapMs) =>
            BuildNotes(notes, gapMs, BothPartials, attackMs: 14, tauDiv: 5.2, gain: 0.24,
                       detune: 0.0013, strike: true);

        private static (double mult, double amp, double decay)[] BothPartials
        {
            get
            {
                var list = new (double, double, double)[BellPartials.Length + InharmonicPartials.Length];
                BellPartials.CopyTo(list, 0);
                InharmonicPartials.CopyTo(list, BellPartials.Length);
                return list;
            }
        }

        private static byte[] BuildNotes((double freq, int ms)[] notes, int gapMs,
                                         (double mult, double amp, double decay)[] partials,
                                         int attackMs, double tauDiv, double gain,
                                         double detune = 0, bool strike = false)
        {
            int gap = SampleRate * gapMs / 1000;
            int total = gap * Math.Max(0, notes.Length - 1);
            foreach (var n in notes) total += SampleRate * n.ms / 1000;

            var pcm = new short[total];
            var rnd = new Random(20260101);
            int pos = 0;

            for (int n = 0; n < notes.Length; n++)
            {
                var (freq, ms) = notes[n];
                int samples = SampleRate * ms / 1000;
                int attack = Math.Max(1, SampleRate * attackMs / 1000);
                double tau = samples / tauDiv;

                for (int i = 0; i < samples && pos < total; i++)
                {
                    double t = (double)i / SampleRate;
                    double env = Math.Exp(-i / tau);
                    if (i < attack) env *= (double)i / attack;

                    double s = 0;
                    foreach (var (mult, amp, decay) in partials)
                    {
                        double f = freq * mult;
                        if (f > SampleRate / 2.0 - 200) continue;

                        double pd = Math.Exp(-i / (tau / decay));

                        if (detune > 0)
                        {
                            // 两个略微失谐的振荡器叠加 → 缓慢的拍频，听起来"厚"且不呆板
                            double a = Math.Sin(2 * Math.PI * f * (1 - detune) * t);
                            double b = Math.Sin(2 * Math.PI * f * (1 + detune) * t);
                            s += amp * pd * (a + b) * 0.5;
                        }
                        else
                        {
                            s += amp * pd * Math.Sin(2 * Math.PI * f * t);
                        }
                    }

                    // 击打瞬态：前几毫秒加一点噪声，模拟槌子敲击
                    if (strike && i < SampleRate * 0.006)
                    {
                        double k = 1.0 - i / (SampleRate * 0.006);
                        s += (rnd.NextDouble() * 2 - 1) * 0.05 * k;
                    }

                    double v = s * gain / 2.6;
                    if (v > 1) v = 1; else if (v < -1) v = -1;

                    pcm[pos++] = (short)(v * short.MaxValue);
                }

                if (n < notes.Length - 1) pos += gap;
            }

            return WrapPcm(pcm, Math.Min(pos, total));
        }

        private static byte[] WrapPcm(short[] pcm, int used)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            const short channels = 1, bits = 16;
            int dataLen = used * 2;

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write(channels);
            w.Write(SampleRate);
            w.Write(SampleRate * channels * bits / 8);
            w.Write((short)(channels * bits / 8));
            w.Write(bits);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataLen);

            for (int i = 0; i < used; i++)
            {
                w.Write((byte)(pcm[i] & 0xFF));
                w.Write((byte)((pcm[i] >> 8) & 0xFF));
            }

            w.Flush();
            return ms.ToArray();
        }

        private static void Play(byte[] wav)
        {
            try
            {
                using var ms = new MemoryStream(wav);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                Log.Write("提示音播放失败: " + ex.Message);
            }
        }
    }
}
