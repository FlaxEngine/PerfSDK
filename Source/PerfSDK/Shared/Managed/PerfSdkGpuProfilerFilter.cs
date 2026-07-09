#if USE_PROFILER

using FlaxEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NVIDIA
{
    /// <summary>
    /// Removes engine UI profiler scopes (GUI / Render2D) from PerfSDK views and timings.
    /// </summary>
    internal static class PerfSdkGpuProfilerFilter
    {
        static readonly FieldInfo CpuEventNameField = typeof(ProfilerCPU.Event).GetField("Name0", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static bool IsExcludedRegionName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return string.Equals(name, "GUI", StringComparison.Ordinal)
                || string.Equals(name, "Render2D", StringComparison.Ordinal);
        }

        public static unsafe ProfilerGPU.Event[] FilterGpuEvents(ProfilerGPU.Event[] events)
        {
            if (events == null || events.Length == 0)
                return Array.Empty<ProfilerGPU.Event>();

            var filtered = new List<ProfilerGPU.Event>(events.Length);
            int excludeDepth = -1;

            for (int i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (e.Name == null)
                    continue;

                while (excludeDepth >= 0 && e.Depth <= excludeDepth)
                    excludeDepth = -1;

                if (excludeDepth >= 0)
                    continue;

                if (IsExcludedRegionName(new string(e.Name)))
                {
                    excludeDepth = e.Depth;
                    continue;
                }

                filtered.Add(e);
            }

            return filtered.ToArray();
        }

        public static unsafe float ComputeExcludedGpuTimeMs(ProfilerGPU.Event[] events)
        {
            if (events == null || events.Length == 0)
                return 0.0f;

            float excluded = 0.0f;
            int excludeDepth = -1;

            for (int i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (e.Name == null)
                    continue;

                while (excludeDepth >= 0 && e.Depth <= excludeDepth)
                    excludeDepth = -1;

                if (excludeDepth >= 0)
                    continue;

                if (!IsExcludedRegionName(new string(e.Name)))
                    continue;

                excluded += Math.Max(e.Time, 0.0f);
                excludeDepth = e.Depth;
            }

            return excluded;
        }

        public static float GetAdjustedDrawGpuTimeMs(float rawDrawGpuTimeMs, ProfilerGPU.Event[] events)
        {
            return Math.Max(rawDrawGpuTimeMs - ComputeExcludedGpuTimeMs(events), 0.001f);
        }

        public static float GetAdjustedDrawCpuTimeMs(float rawDrawCpuTimeMs)
        {
            float excluded = 0.0f;
            var threads = ProfilingTools.EventsCPU;
            if (threads == null)
                return Math.Max(rawDrawCpuTimeMs, 0.0f);

            for (int t = 0; t < threads.Length; t++)
            {
                var threadEvents = threads[t].Events;
                if (threadEvents == null)
                    continue;
                excluded += ComputeExcludedCpuTimeMs(threadEvents);
            }

            return Math.Max(rawDrawCpuTimeMs - excluded, 0.0f);
        }

        public static unsafe float ComputeExcludedCpuTimeMs(ProfilerCPU.Event[] events)
        {
            if (events == null || events.Length == 0)
                return 0.0f;

            float excluded = 0.0f;
            int excludeDepth = -1;

            for (int i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];

                while (excludeDepth >= 0 && e.Depth <= excludeDepth)
                    excludeDepth = -1;

                if (excludeDepth >= 0)
                    continue;

                string name = GetCpuEventName(e);
                if (!IsExcludedRegionName(name))
                    continue;

                excluded += Math.Max((float)((e.End - e.Start) * 1000.0), 0.0f);
                excludeDepth = e.Depth;
            }

            return excluded;
        }

        static string GetCpuEventName(ProfilerCPU.Event e)
        {
            if (CpuEventNameField == null)
                return string.Empty;

            try
            {
                object value = CpuEventNameField.GetValue(e);
                if (value == null)
                    return string.Empty;

                if (value is string text)
                    return text;

                if (value is char[] chars)
                    return ReadNullTerminatedChars(chars);

                if (value is short[] shorts)
                    return ReadNullTerminatedShorts(shorts);
            }
            catch
            {
            }

            return string.Empty;
        }

        static string ReadNullTerminatedChars(char[] chars)
        {
            if (chars.Length == 0)
                return string.Empty;

            int length = Array.IndexOf(chars, '\0');
            if (length < 0)
                length = chars.Length;
            if (length == 0)
                return string.Empty;

            return new string(chars, 0, length);
        }

        static unsafe string ReadNullTerminatedShorts(short[] shorts)
        {
            if (shorts.Length == 0)
                return string.Empty;

            fixed (short* name = shorts)
            {
                if (name == null || name[0] == 0)
                    return string.Empty;
                return new string((char*)name);
            }
        }
    }
}
#endif
