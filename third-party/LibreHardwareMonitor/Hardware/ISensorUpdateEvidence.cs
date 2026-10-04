// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// FlowDeck addition to LibreHardwareMonitor 0.9.6.
namespace LibreHardwareMonitor.Hardware;

/// <summary>Assignment evidence, independent of history retention. This alone does not prove a physical acquisition.</summary>
public interface ISensorUpdateEvidence
{
    ulong UpdateSequence { get; }
    /// <summary>Identity of a validated shared-log sample; repeated copies do not constitute a new acquisition.</summary>
    ulong? SourceSample { get; }
}
