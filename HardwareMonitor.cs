using LibreHardwareMonitor.Hardware;

namespace HWMonitor;

/// <summary>
/// Wraps LibreHardwareMonitorLib and exposes just the readings this app needs.
/// </summary>
sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
        };
        _computer.Open();
    }

    public Reading Read()
    {
        _computer.Accept(_updateVisitor);

        float? cpuTemp = FindTemperature(HardwareType.Cpu, preferredNames: ["CPU Package", "Core (Tctl/Tdie)", "Core Average"]);
        float? gpuTemp = FindTemperature(
            hardwareType: null,
            preferredNames: ["GPU Core"],
            gpuOnly: true);

        float? cpuFanRpm = FindFanRpm([HardwareType.Motherboard], preferredNames: ["CPU Fan", "CPU"]);
        float? gpuFanRpm = FindFanRpm([HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel], preferredNames: ["GPU Fan", "Fan 1", "Fan"]);

        float? gpuLoad = FindLoad([HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel], preferredNames: ["GPU Core", "D3D 3D"]);
        float? gpuMemoryUsedMb = FindSensor([HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel], SensorType.SmallData, preferredNames: ["GPU Memory Used", "D3D Dedicated Memory Used"]);
        float? gpuMemoryTotalMb = FindSensor([HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel], SensorType.SmallData, preferredNames: ["GPU Memory Total"]);

        return new Reading(cpuTemp, gpuTemp, cpuFanRpm, gpuFanRpm, gpuLoad, gpuMemoryUsedMb, gpuMemoryTotalMb);
    }

    private float? FindLoad(HardwareType[] hardwareTypes, string[] preferredNames) =>
        FindSensor(hardwareTypes, SensorType.Load, preferredNames);

    private float? FindSensor(HardwareType[] hardwareTypes, SensorType sensorType, string[] preferredNames)
    {
        float? best = null;
        float? preferred = null;

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (Array.IndexOf(hardwareTypes, hardware.HardwareType) < 0)
            {
                continue;
            }

            ScanSensors(hardware, sensorType, preferredNames, ref best, ref preferred);
            foreach (IHardware sub in hardware.SubHardware)
            {
                ScanSensors(sub, sensorType, preferredNames, ref best, ref preferred);
            }
        }

        return preferred ?? best;
    }

    private static void ScanSensors(IHardware hardware, SensorType sensorType, string[] preferredNames, ref float? best, ref float? preferred)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != sensorType || sensor.Value is not float value)
            {
                continue;
            }

            if (preferred is null && Array.Exists(preferredNames, n => sensor.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                preferred = value;
            }

            best ??= value;
        }
    }

    private float? FindFanRpm(HardwareType[] hardwareTypes, string[] preferredNames)
    {
        float? best = null;
        float? preferred = null;

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (Array.IndexOf(hardwareTypes, hardware.HardwareType) < 0)
            {
                continue;
            }

            ScanFans(hardware, preferredNames, ref best, ref preferred);
            foreach (IHardware sub in hardware.SubHardware)
            {
                ScanFans(sub, preferredNames, ref best, ref preferred);
            }
        }

        return preferred ?? best;
    }

    private static void ScanFans(IHardware hardware, string[] preferredNames, ref float? best, ref float? preferred)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Fan || sensor.Value is not float value)
            {
                continue;
            }

            if (preferred is null && Array.Exists(preferredNames, n => sensor.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                preferred = value;
            }

            best ??= value;
        }
    }

    private float? FindTemperature(HardwareType? hardwareType, string[] preferredNames, bool gpuOnly = false)
    {
        float? best = null;
        float? preferred = null;

        foreach (IHardware hardware in _computer.Hardware)
        {
            bool matches = gpuOnly
                ? hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                : hardware.HardwareType == hardwareType;

            if (!matches)
            {
                continue;
            }

            ScanTemperatures(hardware, preferredNames, ref best, ref preferred);
            foreach (IHardware sub in hardware.SubHardware)
            {
                ScanTemperatures(sub, preferredNames, ref best, ref preferred);
            }
        }

        return preferred ?? best;
    }

    private static void ScanTemperatures(IHardware hardware, string[] preferredNames, ref float? best, ref float? preferred)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value)
            {
                continue;
            }

            if (preferred is null && Array.IndexOf(preferredNames, sensor.Name) >= 0)
            {
                preferred = value;
            }

            if (best is null || value > best)
            {
                best = value;
            }
        }
    }

    /// <summary>Dumps every hardware component and sensor the library can see, for diagnosing missing readings.</summary>
    public string DumpSensors()
    {
        _computer.Accept(_updateVisitor);
        var sb = new System.Text.StringBuilder();

        void DumpHardware(IHardware hardware, int depth)
        {
            sb.Append(' ', depth * 2).Append("[Hardware] ").Append(hardware.HardwareType).Append(": ").AppendLine(hardware.Name);
            foreach (ISensor sensor in hardware.Sensors)
            {
                sb.Append(' ', depth * 2 + 2)
                  .Append(sensor.SensorType).Append(" \"").Append(sensor.Name).Append("\" = ")
                  .AppendLine(sensor.Value is { } v ? v.ToString("0.0") : "null");
            }
            foreach (IHardware sub in hardware.SubHardware)
            {
                DumpHardware(sub, depth + 1);
            }
        }

        if (_computer.Hardware.Count == 0)
        {
            sb.AppendLine("(no hardware detected at all)");
        }

        foreach (IHardware hardware in _computer.Hardware)
        {
            DumpHardware(hardware, 0);
        }

        return sb.ToString();
    }

    public void Dispose() => _computer.Close();

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}

readonly record struct Reading(float? CpuTempC, float? GpuTempC, float? CpuFanRpm, float? GpuFanRpm, float? GpuLoadPercent, float? GpuMemoryUsedMb, float? GpuMemoryTotalMb);
