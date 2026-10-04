// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware.Battery;
using LibreHardwareMonitor.Hardware.Controller.AeroCool;
using LibreHardwareMonitor.Hardware.Controller.AquaComputer;
using LibreHardwareMonitor.Hardware.Controller.Arctic;
using LibreHardwareMonitor.Hardware.Controller.Heatmaster;
using LibreHardwareMonitor.Hardware.Controller.MSI;
using LibreHardwareMonitor.Hardware.Controller.Nzxt;
using LibreHardwareMonitor.Hardware.Controller.Razer;
using LibreHardwareMonitor.Hardware.Controller.TBalancer;
using LibreHardwareMonitor.Hardware.Cpu;
using LibreHardwareMonitor.Hardware.Gpu;
using LibreHardwareMonitor.Hardware.Memory;
using LibreHardwareMonitor.Hardware.Motherboard;
using LibreHardwareMonitor.Hardware.Network;
using LibreHardwareMonitor.Hardware.PowerMonitor;
using LibreHardwareMonitor.Hardware.Psu.Corsair;
using LibreHardwareMonitor.Hardware.Psu.Msi;
using LibreHardwareMonitor.Hardware.Storage;

namespace LibreHardwareMonitor.Hardware;

/// <summary>
/// Stores all hardware groups and decides which devices should be enabled and updated.
/// </summary>
public class Computer : IComputer
{
    private readonly List<IGroup> _groups = new();
    private readonly List<IGroup> _pendingClose = new();
    private readonly object _lock = new();
    private readonly ISettings _settings;
    private readonly Func<SMBios> _createBios;
    private readonly Action _openMutexes, _closeMutexes, _openOpCode, _closeOpCode, _addGroups;
    private bool _mutexesStarted, _opCodeStarted, _transitioning;

    private bool _batteryEnabled;
    private bool _controllerEnabled;
    private bool _cpuEnabled;
    private bool _gpuEnabled;
    private bool _powerMonitorEnabled;
    private bool _memoryEnabled;
    private bool _motherboardEnabled;
    private bool _networkEnabled;
    private bool _open;
    private bool _psuEnabled;
    private SMBios _smbios;
    private bool _storageEnabled;

    /// <summary>
    /// Creates a new <see cref="IComputer" /> instance with basic initial <see cref="Settings" />.
    /// </summary>
    public Computer() : this(null) { }

    /// <summary>
    /// Creates a new <see cref="IComputer" /> instance with additional <see cref="ISettings" />.
    /// </summary>
    /// <param name="settings">Computer settings that will be transferred to each <see cref="IHardware" />.</param>
    public Computer(ISettings settings)
        : this(settings, () => new SMBios(), Mutexes.Open, Mutexes.Close, OpCode.Open, OpCode.Close, computer => computer.AddGroups())
    { }

    internal Computer(ISettings settings, Func<SMBios> createBios, Action openMutexes, Action closeMutexes,
        Action openOpCode, Action closeOpCode, Action<Computer> addGroups)
    {
        _settings = settings ?? new Settings();
        _createBios = createBios ?? throw new ArgumentNullException(nameof(createBios));
        _openMutexes = openMutexes ?? throw new ArgumentNullException(nameof(openMutexes));
        _closeMutexes = closeMutexes ?? throw new ArgumentNullException(nameof(closeMutexes));
        _openOpCode = openOpCode ?? throw new ArgumentNullException(nameof(openOpCode));
        _closeOpCode = closeOpCode ?? throw new ArgumentNullException(nameof(closeOpCode));
        if (addGroups == null) throw new ArgumentNullException(nameof(addGroups));
        _addGroups = () => addGroups(this);
    }

    /// <inheritdoc />
    public event HardwareEventHandler HardwareAdded;

    /// <inheritdoc />
    public event HardwareEventHandler HardwareRemoved;

    /// <inheritdoc />
    public IList<IHardware> Hardware
    {
        get
        {
            lock (_lock)
            {
                List<IHardware> list = new();

                foreach (IGroup group in _groups)
                    list.AddRange(group.Hardware);

                return list;
            }
        }
    }

    /// <inheritdoc />
    public bool IsBatteryEnabled
    {
        get { return _batteryEnabled; }
        set
        {
            if (_open && value != _batteryEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                {
                    Add(new BatteryGroup(_settings));
                }
                else
                {
                    RemoveType<BatteryGroup>();
                }
            }

            _batteryEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsControllerEnabled
    {
        get { return _controllerEnabled; }
        set
        {
            if (_open && value != _controllerEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                {
                    Add(new TBalancerGroup(_settings));
                    Add(new HeatmasterGroup(_settings));
                    Add(new AquaComputerGroup(_settings));
                    Add(new AeroCoolGroup(_settings));
                    Add(new NzxtGroup(_settings));
                    Add(new RazerGroup(_settings));
                    Add(new ArcticGroup(_settings));
                    Add(new MsiGroup(_settings));
                }
                else
                {
                    RemoveType<TBalancerGroup>();
                    RemoveType<HeatmasterGroup>();
                    RemoveType<AquaComputerGroup>();
                    RemoveType<AeroCoolGroup>();
                    RemoveType<NzxtGroup>();
                    RemoveType<RazerGroup>();
                    RemoveType<ArcticGroup>();
                    RemoveType<MsiGroup>();
                }
            }

            _controllerEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsCpuEnabled
    {
        get { return _cpuEnabled; }
        set
        {
            if (_open && value != _cpuEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new CpuGroup(_settings));
                else
                    RemoveType<CpuGroup>();
            }

            _cpuEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsGpuEnabled
    {
        get { return _gpuEnabled; }
        set
        {
            if (_open && value != _gpuEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                {
                    Add(new AmdGpuGroup(_settings));
                    Add(new NvidiaGroup(_settings));

                    if (_cpuEnabled)
                        Add(new IntelGpuGroup(GetIntelCpus(), _settings));
                }
                else
                {
                    RemoveType<AmdGpuGroup>();
                    RemoveType<NvidiaGroup>();
                    RemoveType<IntelGpuGroup>();
                }
            }

            _gpuEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsPowerMonitorEnabled
    {
        get { return _powerMonitorEnabled; }
        set
        {
            if (_open && value != _powerMonitorEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new PowerMonitorGroup(_settings));
                else
                    RemoveType<PowerMonitorGroup>();
            }

            _powerMonitorEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsMemoryEnabled
    {
        get { return _memoryEnabled; }
        set
        {
            if (_open && value != _memoryEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new MemoryGroup(_settings));
                else
                    RemoveType<MemoryGroup>();
            }

            _memoryEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsMotherboardEnabled
    {
        get { return _motherboardEnabled; }
        set
        {
            if (_open && value != _motherboardEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new MotherboardGroup(_smbios, _settings));
                else
                    RemoveType<MotherboardGroup>();
            }

            _motherboardEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsNetworkEnabled
    {
        get { return _networkEnabled; }
        set
        {
            if (_open && value != _networkEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new NetworkGroup(_settings));
                else
                    RemoveType<NetworkGroup>();
            }

            _networkEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsPsuEnabled
    {
        get { return _psuEnabled; }
        set
        {
            if (_open && value != _psuEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                {
                    Add(new CorsairPsuGroup(_settings));
                    Add(new MsiPsuGroup(_settings));
                }
                else
                {
                    RemoveType<CorsairPsuGroup>();
                    RemoveType<MsiPsuGroup>();
                }
            }

            _psuEnabled = value;
        }
    }

    /// <inheritdoc />
    public bool IsStorageEnabled
    {
        get { return _storageEnabled; }
        set
        {
            if (_open && value != _storageEnabled)
            {
                EnsureNoPendingClose();
                if (value)
                    Add(new StorageGroup(_settings));
                else
                    RemoveType<StorageGroup>();
            }

            _storageEnabled = value;
        }
    }

    /// <summary>
    /// Contains computer information table read in accordance with <see href="https://www.dmtf.org/standards/smbios">System Management BIOS (SMBIOS) Reference Specification</see>.
    /// </summary>
    public SMBios SMBios
    {
        get
        {
            if (!_open)
                throw new InvalidOperationException("SMBIOS cannot be accessed before opening.");

            return _smbios;
        }
    }

    //// <inheritdoc />
    public string GetReport()
    {
        lock (_lock)
        {
            using StringWriter w = new(CultureInfo.InvariantCulture);

            w.WriteLine();
            w.WriteLine(nameof(LibreHardwareMonitor) + " Report");
            w.WriteLine();

            Version version = typeof(Computer).Assembly.GetName().Version;

            NewSection(w);
            w.Write("Version: ");
            w.WriteLine(version.ToString());
            w.WriteLine();

            NewSection(w);
            w.Write("Common Language Runtime: ");
            w.WriteLine(Environment.Version.ToString());
            w.Write("Operating System: ");
            w.WriteLine(Environment.OSVersion.ToString());
            w.Write("Process Type: ");
            w.WriteLine(IntPtr.Size == 4 ? "32-Bit" : "64-Bit");
            w.WriteLine();

            NewSection(w);
            w.WriteLine("Sensors");
            w.WriteLine();

            foreach (IGroup group in _groups)
            {
                foreach (IHardware hardware in group.Hardware)
                    ReportHardwareSensorTree(hardware, w, string.Empty);
            }

            w.WriteLine();

            NewSection(w);
            w.WriteLine("Parameters");
            w.WriteLine();

            foreach (IGroup group in _groups)
            {
                foreach (IHardware hardware in group.Hardware)
                    ReportHardwareParameterTree(hardware, w, string.Empty);
            }

            w.WriteLine();

            foreach (IGroup group in _groups)
            {
                string report = group.GetReport();
                if (!string.IsNullOrEmpty(report))
                {
                    NewSection(w);
                    w.Write(report);
                }

                foreach (IHardware hardware in group.Hardware)
                    ReportHardware(hardware, w);
            }

            return w.ToString();
        }
    }

    /// <summary>
    /// Triggers the <see cref="IVisitor.VisitComputer" /> method for the given observer.
    /// </summary>
    /// <param name="visitor">Observer who call to devices.</param>
    public void Accept(IVisitor visitor)
    {
        if (visitor == null)
            throw new ArgumentNullException(nameof(visitor));

        visitor.VisitComputer(this);
    }

    /// <summary>
    /// Triggers the <see cref="IElement.Accept" /> method with the given visitor for each device in each group.
    /// </summary>
    /// <param name="visitor">Observer who call to devices.</param>
    public void Traverse(IVisitor visitor)
    {
        lock (_lock)
        {
            // Use a for-loop instead of foreach to avoid a collection modified exception after sleep, even though everything is under a lock.
            for (int i = 0; i < _groups.Count; i++)
            {
                IGroup group = _groups[i];

                for (int j = 0; j < group.Hardware.Count; j++)
                    group.Hardware[j].Accept(visitor);
            }
        }
    }

    private void HardwareAddedEvent(IHardware hardware)
    {
        HardwareAdded?.Invoke(hardware);
    }

    private void HardwareRemovedEvent(IHardware hardware)
    {
        HardwareRemoved?.Invoke(hardware);
    }

    private void Add(IGroup group)
    {
        if (group == null)
            return;

        lock (_lock)
        {
            if (_groups.Contains(group))
                return;

            _groups.Add(group);

            if (group is IHardwareChanged hardwareChanged)
            {
                hardwareChanged.HardwareAdded += HardwareAddedEvent;
                hardwareChanged.HardwareRemoved += HardwareRemovedEvent;
            }
        }

        if (HardwareAdded != null)
        {
            foreach (IHardware hardware in group.Hardware)
                HardwareAdded(hardware);
        }
    }

    private void Remove(IGroup group)
    {
        var errors = new List<Exception>();
        bool notify;
        lock (_lock)
        {
            notify = _groups.Remove(group);
            if (!notify && !_pendingClose.Contains(group)) return;
            if (!_pendingClose.Contains(group)) _pendingClose.Add(group);

            if (notify && group is IHardwareChanged hardwareChanged)
            {
                try { hardwareChanged.HardwareAdded -= HardwareAddedEvent; } catch (Exception error) { errors.Add(error); }
                try { hardwareChanged.HardwareRemoved -= HardwareRemovedEvent; } catch (Exception error) { errors.Add(error); }
            }
        }

        if (notify && HardwareRemoved is { } removed)
        {
            try
            {
                foreach (IHardware hardware in group.Hardware)
                    foreach (HardwareEventHandler handler in removed.GetInvocationList())
                        try { handler(hardware); } catch (Exception error) { errors.Add(error); }
            }
            catch (Exception error) { errors.Add(error); }
        }

        try
        {
            group.Close();
            lock (_lock) _pendingClose.Remove(group);
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Hardware group removal failed", errors);
    }

    private void RemoveType<T>() where T : IGroup
    {
        List<T> list = [];

        lock (_lock)
        {
            foreach (IGroup group in _groups)
            {
                if (group is T t)
                    list.Add(t);
            }
        }

        var errors = new List<Exception>();
        foreach (T group in list)
            try { Remove(group); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Hardware groups removal failed", errors);
    }

    private void EnsureNoPendingClose()
    {
        if (_pendingClose.Count > 0) throw new InvalidOperationException("Hardware cleanup must complete before changing groups.");
    }

    /// <summary>
    /// If hasn't been opened before, opens <see cref="SMBios" />, <see cref="OpCode" /> and triggers the private <see cref="AddGroups" /> method depending on which categories are
    /// enabled.
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_transitioning) throw new InvalidOperationException("A hardware lifecycle operation is already in progress.");
            if (_open) return;
            if (_pendingClose.Count > 0 || _mutexesStarted || _opCodeStarted)
                throw new InvalidOperationException("Previous hardware cleanup must complete before opening.");
            _transitioning = true;
            try
            {
                _smbios = _createBios();
                _mutexesStarted = true; _openMutexes();
                _opCodeStarted = true; _openOpCode();
                _addGroups();
                _open = true;
            }
            catch (Exception error)
            {
                var cleanup = CloseCore();
                if (cleanup.Count > 0) throw new AggregateException("Hardware initialization and cleanup failed", new[] { error }.Concat(cleanup));
                throw;
            }
            finally { _transitioning = false; }
        }
    }

    private void AddGroups()
    {
        if (_motherboardEnabled)
            Add(new MotherboardGroup(_smbios, _settings));

        if (_cpuEnabled)
            Add(new CpuGroup(_settings));

        if (_memoryEnabled)
            Add(new MemoryGroup(_settings));

        if (_gpuEnabled)
        {
            Add(new AmdGpuGroup(_settings));
            Add(new NvidiaGroup(_settings));

            if (_cpuEnabled)
                Add(new IntelGpuGroup(GetIntelCpus(), _settings));
        }

        if (_powerMonitorEnabled)
            Add(new PowerMonitorGroup(_settings));

        if (_controllerEnabled)
        {
            Add(new TBalancerGroup(_settings));
            Add(new HeatmasterGroup(_settings));
            Add(new AquaComputerGroup(_settings));
            Add(new AeroCoolGroup(_settings));
            Add(new NzxtGroup(_settings));
            Add(new RazerGroup(_settings));
            Add(new ArcticGroup(_settings));
            Add(new MsiGroup(_settings));
        }

        if (_storageEnabled)
            Add(new StorageGroup(_settings));

        if (_networkEnabled)
            Add(new NetworkGroup(_settings));

        if (_psuEnabled)
        {
            Add(new CorsairPsuGroup(_settings));
            Add(new MsiPsuGroup(_settings));
        }

        if (_batteryEnabled)
            Add(new BatteryGroup(_settings));
    }

    private static void NewSection(TextWriter writer)
    {
        for (int i = 0; i < 8; i++)
            writer.Write("----------");

        writer.WriteLine();
        writer.WriteLine();
    }

    private static int CompareSensor(ISensor a, ISensor b)
    {
        int c = a.SensorType.CompareTo(b.SensorType);
        if (c == 0)
            return a.Index.CompareTo(b.Index);

        return c;
    }

    private static void ReportHardwareSensorTree(IHardware hardware, TextWriter w, string space)
    {
        w.WriteLine("{0}|", space);
        w.WriteLine("{0}+- {1} ({2})", space, hardware.Name, hardware.Identifier);

        ISensor[] sensors = hardware.Sensors;
        Array.Sort(sensors, CompareSensor);

        foreach (ISensor sensor in sensors)
            w.WriteLine("{0}|  +- {1,-14} : {2,8:G6} {3,8:G6} {4,8:G6} ({5})", space, sensor.Name, sensor.Value, sensor.Min, sensor.Max, sensor.Identifier);

        foreach (IHardware subHardware in hardware.SubHardware)
            ReportHardwareSensorTree(subHardware, w, "|  ");
    }

    private static void ReportHardwareParameterTree(IHardware hardware, TextWriter w, string space)
    {
        w.WriteLine("{0}|", space);
        w.WriteLine("{0}+- {1} ({2})", space, hardware.Name, hardware.Identifier);

        ISensor[] sensors = hardware.Sensors;
        Array.Sort(sensors, CompareSensor);

        foreach (ISensor sensor in sensors)
        {
            string innerSpace = space + "|  ";
            if (sensor.Parameters.Count > 0)
            {
                w.WriteLine("{0}|", innerSpace);
                w.WriteLine("{0}+- {1} ({2})", innerSpace, sensor.Name, sensor.Identifier);

                foreach (IParameter parameter in sensor.Parameters)
                {
                    string innerInnerSpace = innerSpace + "|  ";
                    w.WriteLine("{0}+- {1} : {2}", innerInnerSpace, parameter.Name, string.Format(CultureInfo.InvariantCulture, "{0} : {1}", parameter.DefaultValue, parameter.Value));
                }
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
            ReportHardwareParameterTree(subHardware, w, "|  ");
    }

    private static void ReportHardware(IHardware hardware, TextWriter w)
    {
        string hardwareReport = hardware.GetReport();
        if (!string.IsNullOrEmpty(hardwareReport))
        {
            NewSection(w);
            w.Write(hardwareReport);
        }

        foreach (IHardware subHardware in hardware.SubHardware)
            ReportHardware(subHardware, w);
    }

    /// <summary>
    /// If opened before, removes all <see cref="IGroup" /> and triggers <see cref="OpCode.Close" />.
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_transitioning) throw new InvalidOperationException("A hardware lifecycle operation is already in progress.");
            _transitioning = true;
            try
            {
                var errors = CloseCore();
                if (errors.Count > 0) throw new AggregateException("Hardware shutdown failed", errors);
            }
            finally { _transitioning = false; }
        }
    }

    // Keep failed closures reachable for retry. They may still need the shared ISA/PCI/EC mutexes.
    private List<Exception> CloseCore()
    {
        _open = false;
        var errors = new List<Exception>();
        var closing = _groups.AsEnumerable().Reverse().Concat(_pendingClose.AsEnumerable().Reverse()).Distinct().ToArray();
        foreach (var group in closing)
            try { Remove(group); } catch (Exception error) { errors.Add(error); }
        if (_pendingClose.Count == 0)
        {
            if (_opCodeStarted)
                try { _closeOpCode(); _opCodeStarted = false; } catch (Exception error) { errors.Add(error); }
            if (_mutexesStarted)
                try { _closeMutexes(); _mutexesStarted = false; } catch (Exception error) { errors.Add(error); }
        }
        _smbios = null;
        return errors;
    }

    /// <summary>
    /// If opened before, removes all <see cref="IGroup" /> and recreates it.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_transitioning) throw new InvalidOperationException("A hardware lifecycle operation is already in progress.");
            if (!_open) return;
            Close();
            Open();
        }
    }

    private List<IntelCpu> GetIntelCpus()
    {
        // Create a temporary cpu group if one has not been added.
        lock (_lock)
        {
            IGroup cpuGroup = _groups.Find(x => x is CpuGroup) ?? new CpuGroup(_settings);
            return cpuGroup.Hardware.Select(x => x as IntelCpu).ToList();
        }
    }

    /// <summary>
    /// <see cref="Computer" /> specific additional settings passed to its <see cref="IHardware" />.
    /// </summary>
    private class Settings : ISettings
    {
        public bool Contains(string name)
        {
            return false;
        }

        public void SetValue(string name, string value)
        { }

        public string GetValue(string name, string value)
        {
            return value;
        }

        public void Remove(string name)
        { }
    }
}
