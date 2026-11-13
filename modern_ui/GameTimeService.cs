using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ETS2DayNightModern;

public readonly record struct GameState(
    bool Alive,
    bool Patchable,
    uint Pid,
    nint ExeBase,
    int GameMinute,
    int EnvironmentMinute,
    nuint EnvironmentRefreshRva = 0,
    bool AccessDenied = false)
{
    public static GameState Empty => new(false, false, 0, 0, -1, -1, 0, false);
}

public readonly record struct OperationResult(bool Success, string Message);

public sealed class GameTimeService
{
    public readonly record struct GameLayout(
        nuint GameObjectRva,
        nuint EnvironmentRefreshRva,
        bool RequirePostAnchor);

    private const string TargetProcess = "eurotrucks2.exe";
    private const nuint EnvironmentOffset = 0x998;
    private const nuint WorldTimeOffset = 0x3E80;
    private const int DayMinutes = 1440;
    private const uint ImageScnMemExecute = 0x20000000;
    private const uint MaxScanSectionBytes = 64 * 1024 * 1024;
    private const int ErrorAccessDenied = 5;

    private static readonly byte[] Original = { 0x44, 0x8B, 0x83, 0x80, 0x3E, 0x00, 0x00 };
    private static readonly byte[] RefreshPostAnchor = { 0x4C, 0x8D, 0x93, 0xAC, 0x3E, 0x00, 0x00 };
    private static readonly byte[] GameObjectAnchor =
    {
        0x48, 0x8B, 0x9B, 0x98, 0x09, 0x00, 0x00,
        0x44, 0x8B, 0x83, 0x80, 0x3E, 0x00, 0x00,
    };

    private static readonly GameLayout[] KnownLayouts =
    {
        // ETS2 1.62.x (verified on 1.62.1.0)
        new(0x3714F30, 0x4F1526, RequirePostAnchor: true),
        // ETS2 1.61.x
        new(0x36AE6D8, 0x4E5FA6, RequirePostAnchor: false),
    };

    private readonly object _cacheLock = new();
    private uint _cachedPid;
    private nint _cachedBase;
    private uint _cachedSize;
    private GameLayout _cachedLayout;

    public GameState Probe()
    {
        if (!OperatingSystem.IsWindows()) return GameState.Empty;
        var pid = FindProcessId();
        if (pid == 0) return GameState.Empty;
        if (!FindModuleInfo(pid, out var baseAddress, out var moduleSize, out var moduleAccessDenied) ||
            baseAddress == 0)
        {
            return new GameState(true, false, pid, 0, -1, -1, 0, moduleAccessDenied);
        }

        var handle = OpenProcess(ProcessAccess.QueryLimitedInformation | ProcessAccess.VmRead, false, pid);
        if (handle == 0)
            handle = OpenProcess(ProcessAccess.QueryInformation | ProcessAccess.VmRead, false, pid);
        if (handle == 0)
        {
            var denied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
            return new GameState(true, false, pid, baseAddress, -1, -1, 0, denied);
        }

        try
        {
            if (!TryResolveLayout(handle, (nuint)baseAddress, moduleSize, pid,
                                  out var layout, out var environmentMinute))
            {
                return new GameState(true, false, pid, baseAddress, -1, -1, 0, false);
            }

            var gameMinute = layout.GameObjectRva != 0
                ? ReadWorldMinute(handle, (nuint)baseAddress, layout.GameObjectRva)
                : -1;
            return new GameState(true, true, pid, baseAddress, gameMinute, environmentMinute,
                                 layout.EnvironmentRefreshRva, false);
        }
        finally { CloseHandle(handle); }
    }

    public OperationResult Apply(GameState state, int minute)
    {
        if (!state.Alive || !state.Patchable || state.ExeBase == 0 || state.EnvironmentRefreshRva == 0)
            return new(false, "当前 ETS2 状态不可写入。");
        if (minute is < 0 or >= DayMinutes)
            return new(false, "时间值超出范围。");
        var handle = OpenProcess(ProcessAccess.QueryInformation | ProcessAccess.VmRead |
                                 ProcessAccess.VmWrite | ProcessAccess.VmOperation,
                                 false, state.Pid);
        if (handle == 0) return new(false, "无法打开 ETS2 进程，请以管理员运行。");
        try
        {
            var address = (nuint)state.ExeBase + state.EnvironmentRefreshRva;
            if (!ReadExact(handle, address, 7, out var current))
                return new(false, "读取游戏代码失败。");
            var next = MakePatch(minute);
            if (current.SequenceEqual(next))
                return new(true, $"环境已切到 {FormatMinute(minute)}。\n无需重复写入。");
            if (!IsKnownCode(current))
                return new(false, "游戏代码特征不匹配，已停止写入。");
            var threads = SuspendProcessThreads(state.Pid);
            if (threads.Count == 0) return new(false, "暂停 ETS2 渲染线程失败。");
            try
            {
                if (!ReadExact(handle, address, 7, out current) || !IsKnownCode(current))
                    return new(false, "写入前校验失败，未修改游戏代码。");
                return WriteBytes(handle, address, next)
                    ? new(true, $"环境已切到 {FormatMinute(minute)}。\n时钟不变 · 仅本机可见")
                    : new(false, "写入失败，游戏代码未完成更新。");
            }
            finally { ResumeProcessThreads(threads); }
        }
        finally { CloseHandle(handle); }
    }

    public OperationResult Restore(GameState state)
    {
        if (!state.Alive || !state.Patchable || state.ExeBase == 0 || state.EnvironmentRefreshRva == 0)
            return new(false, "当前 ETS2 状态不可恢复。");
        var handle = OpenProcess(ProcessAccess.QueryInformation | ProcessAccess.VmRead |
                                 ProcessAccess.VmWrite | ProcessAccess.VmOperation,
                                 false, state.Pid);
        if (handle == 0) return new(false, "无法打开 ETS2 进程，请以管理员运行。");
        try
        {
            var address = (nuint)state.ExeBase + state.EnvironmentRefreshRva;
            if (!ReadExact(handle, address, 7, out var current))
                return new(false, "读取游戏代码失败。");
            if (current.SequenceEqual(Original))
                return new(true, "游戏原始环境代码已经恢复。");
            if (!IsKnownCode(current)) return new(false, "代码特征不匹配，未执行恢复。");
            var threads = SuspendProcessThreads(state.Pid);
            if (threads.Count == 0) return new(false, "暂停 ETS2 渲染线程失败。");
            try
            {
                return WriteBytes(handle, address, Original)
                    ? new(true, "已恢复原样，天空/光照重新跟随游戏时间。")
                    : new(false, "恢复失败，游戏代码未完成更新。");
            }
            finally { ResumeProcessThreads(threads); }
        }
        finally { CloseHandle(handle); }
    }

    private bool TryResolveLayout(nint handle, nuint baseAddress, uint moduleSize, uint pid,
                                  out GameLayout layout, out int environmentMinute)
    {
        GameLayout cached = default;
        var hasCached = false;
        lock (_cacheLock)
        {
            if (_cachedPid == pid && _cachedBase == (nint)baseAddress &&
                _cachedSize == moduleSize && _cachedLayout.EnvironmentRefreshRva != 0)
            {
                cached = _cachedLayout;
                hasCached = true;
            }
        }

        if (hasCached &&
            TryInspectRefreshSite(handle, baseAddress + cached.EnvironmentRefreshRva,
                                  cached.RequirePostAnchor, out environmentMinute))
        {
            layout = cached;
            return true;
        }

        // Minor game updates can move the world-time global while leaving the
        // environment patch at the same RVA. Resolve both from the image before
        // falling back to historical offsets, then cache the resolved addresses.
        // A temporarily empty world pointer must not trigger a scan every tick.
        if (moduleSize >= 0x1000 &&
            ReadExact(handle, baseAddress, 0x1000, out var header) &&
            TryScanImageLayout(
                header,
                moduleSize,
                (rva, size) => ReadExact(handle, baseAddress + rva, size, out var sec) ? sec : null,
                out var scanned,
                out environmentMinute))
        {
            CacheLayout(pid, (nint)baseAddress, moduleSize, scanned);
            layout = scanned;
            return true;
        }

        foreach (var candidate in KnownLayouts)
        {
            if (moduleSize != 0 && candidate.EnvironmentRefreshRva + 14 > moduleSize)
                continue;
            if (TryInspectRefreshSite(handle, baseAddress + candidate.EnvironmentRefreshRva,
                                      candidate.RequirePostAnchor, out environmentMinute))
            {
                CacheLayout(pid, (nint)baseAddress, moduleSize, candidate);
                layout = candidate;
                return true;
            }
        }

        layout = default;
        environmentMinute = -1;
        return false;
    }

    private void CacheLayout(uint pid, nint baseAddress, uint moduleSize, GameLayout layout)
    {
        lock (_cacheLock)
        {
            _cachedPid = pid;
            _cachedBase = baseAddress;
            _cachedSize = moduleSize;
            _cachedLayout = layout;
        }
    }

    private static bool TryInspectRefreshSite(nint handle, nuint address, bool requirePostAnchor,
                                              out int environmentMinute)
    {
        environmentMinute = -1;
        var readLength = requirePostAnchor ? 14 : 7;
        if (!ReadExact(handle, address, readLength, out var bytes)) return false;
        if (!TryParseRefreshCode(bytes.AsSpan(0, 7), out environmentMinute)) return false;
        return !requirePostAnchor || bytes.AsSpan(7, 7).SequenceEqual(RefreshPostAnchor);
    }

    public static bool TryScanImageLayout(
        ReadOnlySpan<byte> header,
        uint moduleSize,
        Func<uint, int, byte[]?> readSection,
        out GameLayout layout,
        out int environmentMinute)
    {
        layout = default;
        environmentMinute = -1;

        if (header.Length < 0x100 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return false;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C, 4));
        if (peOffset < 0x40 || peOffset > header.Length - 24)
            return false;
        if (header[peOffset] != (byte)'P' || header[peOffset + 1] != (byte)'E' ||
            header[peOffset + 2] != 0 || header[peOffset + 3] != 0)
            return false;

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(peOffset + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(peOffset + 20, 2));
        var sectionTable = peOffset + 24 + optionalSize;
        if (sectionCount == 0 || sectionCount > 32 ||
            sectionTable < 0 || sectionTable + sectionCount * 40 > header.Length)
            return false;

        nuint foundRefreshRva = 0;
        var foundEnvMinute = -1;
        var refreshHits = 0;
        nuint foundGameObjectRva = 0;
        var gameObjectConflict = false;

        for (var i = 0; i < sectionCount; i++)
        {
            var sec = header.Slice(sectionTable + i * 40, 40);
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(8, 4));
            var rva = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(12, 4));
            var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(36, 4));

            if ((characteristics & ImageScnMemExecute) == 0 || rva >= moduleSize)
                continue;
            var maxRemaining = moduleSize - rva;
            if (virtualSize == 0 || virtualSize > maxRemaining)
                virtualSize = maxRemaining;
            if (virtualSize < 32 || virtualSize > MaxScanSectionBytes)
                continue;

            var code = readSection(rva, (int)virtualSize);
            if (code is null) continue;
            var span = code.AsSpan();

            var searchOffset = 0;
            while (searchOffset <= span.Length - 7)
            {
                var rel = span.Slice(searchOffset).IndexOf(RefreshPostAnchor);
                if (rel < 0) break;
                var pos = searchOffset + rel;
                if (pos >= 7 && TryParseRefreshCode(span.Slice(pos - 7, 7), out var candidateMinute))
                {
                    refreshHits++;
                    if (refreshHits > 1) return false;
                    foundRefreshRva = rva + (uint)(pos - 7);
                    foundEnvMinute = candidateMinute;
                }
                searchOffset = pos + 1;
            }

            var objOffset = 0;
            while (objOffset <= span.Length - GameObjectAnchor.Length)
            {
                var rel = span.Slice(objOffset).IndexOf(GameObjectAnchor);
                if (rel < 0) break;
                var pos = objOffset + rel;
                for (var back = 7; back <= 32 && back <= pos; back++)
                {
                    var movPos = pos - back;
                    if (span[movPos] == 0x48 && span[movPos + 1] == 0x8B && span[movPos + 2] == 0x1D)
                    {
                        var disp = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(movPos + 3, 4));
                        var targetRva = (long)rva + movPos + 7 + disp;
                        if (targetRva > 0 && targetRva < moduleSize)
                        {
                            var candidateObj = (nuint)targetRva;
                            if (foundGameObjectRva == 0) foundGameObjectRva = candidateObj;
                            else if (foundGameObjectRva != candidateObj) gameObjectConflict = true;
                        }
                        break;
                    }
                }
                objOffset = pos + 1;
            }
        }

        if (refreshHits != 1 || foundRefreshRva == 0)
            return false;

        layout = new GameLayout(
            gameObjectConflict ? 0 : foundGameObjectRva,
            foundRefreshRva,
            RequirePostAnchor: true);
        environmentMinute = foundEnvMinute;
        return true;
    }

    private static int ReadWorldMinute(nint handle, nuint baseAddress, nuint gameObjectRva)
    {
        if (!ReadExact(handle, baseAddress + gameObjectRva, IntPtr.Size, out var objectBytes)) return -1;
        var objectAddress = IntPtr.Size == 8 ? BitConverter.ToUInt64(objectBytes) : BitConverter.ToUInt32(objectBytes);
        if (objectAddress == 0 || !ReadExact(handle, (nuint)objectAddress + EnvironmentOffset,
                                               IntPtr.Size, out var environmentBytes)) return -1;
        var environmentAddress = IntPtr.Size == 8 ? BitConverter.ToUInt64(environmentBytes) : BitConverter.ToUInt32(environmentBytes);
        if (environmentAddress == 0 || !ReadExact(handle, (nuint)environmentAddress + WorldTimeOffset,
                                                    4, out var minuteBytes)) return -1;
        return (int)(BitConverter.ToUInt32(minuteBytes) % DayMinutes);
    }

    private static byte[] MakePatch(int minute)
    {
        var bytes = new byte[7] { 0x41, 0xB8, 0, 0, 0, 0, 0x90 };
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), minute);
        return bytes;
    }

    private static bool TryParseRefreshCode(ReadOnlySpan<byte> bytes, out int environmentMinute)
    {
        environmentMinute = -1;
        if (bytes.Length != 7) return false;
        if (bytes.SequenceEqual(Original)) return true;
        if (bytes[0] == 0x41 && bytes[1] == 0xB8 && bytes[6] == 0x90)
        {
            var minute = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(2, 4));
            if (minute is >= 0 and < DayMinutes)
            {
                environmentMinute = minute;
                return true;
            }
        }
        return false;
    }

    private static bool IsKnownCode(ReadOnlySpan<byte> bytes) => TryParseRefreshCode(bytes, out _);

    private static string FormatMinute(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private static uint FindProcessId()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
        if (snapshot == InvalidHandleValue) return 0;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return 0;
            do
            {
                if (string.Equals(entry.ExeFile, TargetProcess, StringComparison.OrdinalIgnoreCase))
                    return entry.ProcessId;
            } while (Process32Next(snapshot, ref entry));
            return 0;
        }
        finally { CloseHandle(snapshot); }
    }

    private static bool FindModuleInfo(uint pid, out nint baseAddress, out uint baseSize, out bool accessDenied)
    {
        baseAddress = 0;
        baseSize = 0;
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Module | SnapshotFlags.Module32, pid);
        if (snapshot == InvalidHandleValue)
        {
            accessDenied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
            return false;
        }
        accessDenied = false;
        try
        {
            var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
            if (!Module32First(snapshot, ref entry)) return false;
            baseAddress = entry.BaseAddress;
            baseSize = entry.BaseSize;
            return baseAddress != 0;
        }
        finally { CloseHandle(snapshot); }
    }

    private static bool ReadExact(nint process, nuint address, int size, out byte[] bytes)
    {
        bytes = new byte[size];
        return ReadProcessMemory(process, (nint)address, bytes, size, out var read) && read == size;
    }

    private static bool WriteBytes(nint process, nuint address, byte[] bytes)
    {
        if (!VirtualProtectEx(process, (nint)address, (nuint)bytes.Length,
                              PageExecuteReadWrite, out var oldProtect)) return false;
        try
        {
            if (!WriteProcessMemory(process, (nint)address, bytes, bytes.Length, out var written) || written != bytes.Length)
                return false;
            FlushInstructionCache(process, (nint)address, (nuint)bytes.Length);
            return ReadExact(process, address, bytes.Length, out var verify) && verify.SequenceEqual(bytes);
        }
        finally { VirtualProtectEx(process, (nint)address, (nuint)bytes.Length, oldProtect, out _); }
    }

    private static List<nint> SuspendProcessThreads(uint pid)
    {
        var handles = new List<nint>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Thread, 0);
        if (snapshot == InvalidHandleValue) return handles;
        try
        {
            var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
            if (!Thread32First(snapshot, ref entry)) return handles;
            do
            {
                if (entry.OwnerProcessId != pid) continue;
                var thread = OpenThread(ThreadSuspendResume | ThreadQueryInformation, false, entry.ThreadId);
                if (thread == 0) continue;
                if (SuspendThread(thread) == uint.MaxValue) CloseHandle(thread);
                else handles.Add(thread);
            } while (Thread32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return handles;
    }

    private static void ResumeProcessThreads(List<nint> handles)
    {
        for (var i = handles.Count - 1; i >= 0; i--)
        {
            ResumeThread(handles[i]);
            CloseHandle(handles[i]);
        }
    }

    private const nint InvalidHandleValue = -1;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadQueryInformation = 0x0040;

    [Flags] private enum ProcessAccess : uint
    {
        QueryInformation = 0x0400,
        QueryLimitedInformation = 0x1000,
        VmRead = 0x0010,
        VmWrite = 0x0020,
        VmOperation = 0x0008,
    }
    [Flags] private enum SnapshotFlags : uint { Process = 0x2, Thread = 0x4, Module = 0x8, Module32 = 0x10 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExeFile;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint Size, ModuleId, ProcessId, GlobalUsage, ProcessUsage;
        public nint BaseAddress;
        public uint BaseSize;
        public nint ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Path;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size, Usage, ThreadId, OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32First(nint snapshot, ref ModuleEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(nint snapshot, ref ThreadEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(nint snapshot, ref ThreadEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(ProcessAccess access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, int size, out int read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(nint process, nint address, nuint size,
                                                uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
