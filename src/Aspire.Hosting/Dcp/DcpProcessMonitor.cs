// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SystemProcess = System.Diagnostics.Process;

namespace Aspire.Hosting.Dcp;

internal sealed record DcpProcessIdentity(int ProcessId, DateTime Timestamp);

internal interface IDcpProcessMonitor
{
    DcpProcessIdentity? GetMonitorProcess();
}

internal sealed partial class DcpProcessMonitor(IConfiguration configuration, ILogger<DcpProcessMonitor> logger) : IDcpProcessMonitor
{
    private const int DefaultLinuxClockTicksPerSecond = 100;
    private const int LinuxClockTicksPerSecondConfigName = 2; // _SC_CLK_TCK

    private bool _initialized;
    private DcpProcessIdentity? _monitorProcess;

    public DcpProcessIdentity? GetMonitorProcess()
    {
        if (!_initialized)
        {
            _monitorProcess = CreateMonitorProcess();
            _initialized = true;
        }

        return _monitorProcess;
    }

    private DcpProcessIdentity? CreateMonitorProcess()
    {
        var monitorProcessId = GetConfiguredCliProcessId() ?? GetParentProcessId();
        if (monitorProcessId is null)
        {
            logger.LogDebug("No monitor process ID could be determined for persistent resources.");
            return null;
        }

        var timestamp = GetProcessIdentityTimestamp(monitorProcessId.Value);
        if (timestamp is null)
        {
            logger.LogDebug("No monitor process timestamp could be determined for process {MonitorProcessId}.", monitorProcessId);
            return null;
        }

        return new(monitorProcessId.Value, timestamp.Value);
    }

    private int? GetConfiguredCliProcessId()
    {
        if (configuration[KnownConfigNames.CliProcessId] is not { } value)
        {
            return null;
        }

        if (int.TryParse(value, CultureInfo.InvariantCulture, out var processId) && processId > 0)
        {
            return processId;
        }

        logger.LogDebug("Configured CLI process ID '{ConfiguredCliProcessId}' is invalid.", value);
        return null;
    }

    private static int? GetParentProcessId()
    {
        if (!OperatingSystem.IsWindows())
        {
            var parentProcessId = getppid();
            return parentProcessId > 0 ? parentProcessId : null;
        }

        return GetWindowsParentProcessId();
    }

    private DateTime? GetProcessIdentityTimestamp(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxProcessIdentityTimestamp(processId);
        }

        try
        {
            using var process = SystemProcess.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            logger.LogDebug("Monitor process {MonitorProcessId} no longer exists.", processId);
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogDebug("Monitor process {MonitorProcessId} exited before its timestamp could be read.", processId);
            return null;
        }
    }

    private DateTime? GetLinuxProcessIdentityTimestamp(int processId)
    {
        var statPath = Path.Combine(
            Environment.GetEnvironmentVariable("HOST_PROC") ?? "/proc",
            processId.ToString(CultureInfo.InvariantCulture),
            "stat");

        string contents;
        try
        {
            contents = File.ReadAllText(statPath);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not read monitor process stat file '{StatPath}'.", statPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Could not read monitor process stat file '{StatPath}'.", statPath);
            return null;
        }

        // /proc/<pid>/stat fields start as:
        //   12345 (process name may contain spaces or parentheses) S 1 2 3 ...
        // The process start time is field 22, in clock ticks since boot. Match DCP's
        // Linux identity time by converting that monotonic value into a DateTime
        // offset from DateTime.MinValue instead of estimating a wall-clock time.
        var closeParenIndex = contents.LastIndexOf(')');
        if (closeParenIndex < 0 || closeParenIndex + 2 >= contents.Length)
        {
            logger.LogDebug("Monitor process stat file '{StatPath}' was malformed.", statPath);
            return null;
        }

        var fields = contents[(closeParenIndex + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], CultureInfo.InvariantCulture, out var startTicks))
        {
            logger.LogDebug("Monitor process stat file '{StatPath}' did not contain a valid start time.", statPath);
            return null;
        }

        var startTimeMilliseconds = (startTicks * 1000) / (ulong)GetLinuxClockTicksPerSecond();
        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc).AddMilliseconds(startTimeMilliseconds);
    }

    private static int GetLinuxClockTicksPerSecond()
    {
        var result = sysconf(LinuxClockTicksPerSecondConfigName);
        return result > 0 ? (int)result : DefaultLinuxClockTicksPerSecond;
    }

    private static int? GetWindowsParentProcessId()
    {
        var basicInformation = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            SystemProcess.GetCurrentProcess().Handle,
            processInformationClass: 0,
            ref basicInformation,
            Marshal.SizeOf<ProcessBasicInformation>(),
            returnLength: IntPtr.Zero);

        if (status != 0 || basicInformation.InheritedFromUniqueProcessId <= 0 || basicInformation.InheritedFromUniqueProcessId > int.MaxValue)
        {
            return null;
        }

        return (int)basicInformation.InheritedFromUniqueProcessId;
    }

    [LibraryImport("libc", SetLastError = true, EntryPoint = "getppid")]
    private static partial int getppid();

    [LibraryImport("libc", SetLastError = true, EntryPoint = "sysconf")]
    private static partial long sysconf(int name);

    [LibraryImport("ntdll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
