// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// FlowDeck addition to LibreHardwareMonitor 0.9.6.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

/// <summary>Observes driver progress without assuming a timezone or unit for the ADL update stamp.</summary>
internal sealed class AmdPmLogSnapshot
{
    private AtiAdlxx.ADLPMLogData _previous;
    private long _observedAt;
    private bool _hasBaseline;
    private bool _usable;
    public ulong SampleSequence { get; private set; }
    public bool Advanced { get; private set; }

    public void Capture(IntPtr address)
    {
        var first = Marshal.PtrToStructure<AtiAdlxx.ADLPMLogData>(address);
        var second = Marshal.PtrToStructure<AtiAdlxx.ADLPMLogData>(address);
        Observe(first, second, Stopwatch.GetTimestamp());
    }

    public void Observe(AtiAdlxx.ADLPMLogData first, AtiAdlxx.ADLPMLogData second, long timestamp)
    {
        Advanced = false;
        // Shared memory is copied twice. Mixed headers/payloads must never mark a sample as acquired.
        if (!Valid(first) || !Valid(second) || !Same(first, second))
        {
            _hasBaseline = false;
            _usable = false;
            return;
        }

        bool baseline = !_hasBaseline || timestamp < _observedAt ||
            Stopwatch.GetElapsedTime(_observedAt, timestamp) >= TimeSpan.FromSeconds(3) ||
            second.ulLastUpdated < _previous.ulLastUpdated ||
            second.ulVersion != _previous.ulVersion || second.ulActiveSampleRate != _previous.ulActiveSampleRate;
        if (baseline)
            _usable = false; // Initial/returning memory may already be old. Observe it advancing first.
        else if (second.ulLastUpdated > _previous.ulLastUpdated)
        {
            SampleSequence = checked(SampleSequence + 1);
            _usable = true;
            Advanced = true;
        }
        else if (!Same(_previous, second))
            _usable = false; // A changing payload with the same stamp has no acquisition evidence.

        _previous = second;
        _previous.ulValues = (uint[])second.ulValues.Clone();
        _observedAt = timestamp;
        _hasBaseline = true;
    }

    public bool TryRead(AtiAdlxx.ADLPMLogSensors type, out uint value)
    {
        value = 0;
        if (!_usable) return false;
        for (int i = 0; i < _previous.ulValues.Length; i += 2)
        {
            if (_previous.ulValues[i] == 0) break;
            if (_previous.ulValues[i] == (uint)type)
            {
                value = _previous.ulValues[i + 1];
                return true;
            }
        }
        return false;
    }

    private static bool Same(AtiAdlxx.ADLPMLogData a, AtiAdlxx.ADLPMLogData b) =>
        a.ulVersion == b.ulVersion && a.ulActiveSampleRate == b.ulActiveSampleRate &&
        a.ulLastUpdated == b.ulLastUpdated && a.ulValues.SequenceEqual(b.ulValues);

    private static bool Valid(AtiAdlxx.ADLPMLogData data)
    {
        if (data.ulLastUpdated == 0 || data.ulActiveSampleRate is 0 or > 86400000 ||
            data.ulValues == null || data.ulValues.Length != AtiAdlxx.ADL_PMLOG_MAX_SENSORS * 2) return false;
        var ids = new HashSet<uint>();
        for (int i = 0; i < data.ulValues.Length; i += 2)
        {
            if (data.ulValues[i] == 0) return true;
            if (!ids.Add(data.ulValues[i])) return false;
        }
        return true;
    }
}
