using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ETS2DayNightModern;

internal static class Program
{
    private static readonly MethodInfo ResolveLayout = typeof(GameTimeService).GetMethod(
        "TryResolveLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ReadMinute = typeof(GameTimeService).GetMethod(
        "ReadWorldMinute", BindingFlags.Static | BindingFlags.NonPublic)!;
    private delegate bool ResolveDelegate(GameTimeService service, nint handle, nuint imageBase,
        uint imageSize, uint pid, out GameTimeService.GameLayout layout, out int environmentMinute);

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            Console.Error.WriteLine("These native-memory regression checks require Windows x64.");
            return 2;
        }

        if (args.Length > 0 && args[0] == "--benchmark")
            return Benchmark();

        (string Name, Action Run)[] tests =
        [
            ("minor update moves the clock global without moving the known refresh site", MovedClockGlobal),
            ("patched environment still resolves the independent real-time clock", PatchedEnvironment),
            ("cached clock survives an empty world pointer and later recovers", CachedClockRecovery),
            ("conflicting clock signatures do not guess a global", ConflictingClockSignatures),
            ("module identity changes invalidate the cached clock", ModuleIdentityChange),
            ("historical offsets remain a fallback when image scanning is unavailable", HistoricalFallback),
            ("failed image scans wait before retrying and a new identity retries immediately", FailedScanBackoff),
            ("cached native clock samples allocate no managed memory", CachedClockAllocations),
            ("cached read sessions read only their original process and dispose safely", CachedReadSession),
            ("closing during concurrent probes releases the read session safely", ConcurrentDispose),
        ];

        if (args.Length > 0)
            tests = tests.Where(test => test.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}: {error.GetBaseException().Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; only this test process's allocated memory was read.");
        return failed == 0 ? 0 : 1;
    }

    private static int Benchmark()
    {
        using var image = new FakeImage();
        var service = new GameTimeService();
        var resolve = ResolveLayout.CreateDelegate<ResolveDelegate>();
        var read = ReadMinute.CreateDelegate<Func<nint, nuint, nuint, int>>();
        var handle = GetCurrentProcess();
        for (var i = 0; i < 1000; i++)
        {
            resolve(service, handle, (nuint)image.BaseAddress, image.Size, 100, out var layout, out _);
            read(handle, (nuint)image.BaseAddress, layout.GameObjectRva);
        }
        const int count = 20000;
        var stopwatch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (var i = 0; i < count; i++)
        {
            if (!resolve(service, handle, (nuint)image.BaseAddress, image.Size, 100, out var layout, out _) ||
                read(handle, (nuint)image.BaseAddress, layout.GameObjectRva) != 69)
                throw new InvalidOperationException("Benchmark could not read the synthetic clock.");
        }
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Cached native clock: {count} samples, {stopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
            $"{allocated} managed bytes ({allocated / count} bytes/sample). Only this process's allocated memory was read.");
        return 0;
    }

    private static void MovedClockGlobal()
    {
        using var image = new FakeImage();
        var service = new GameTimeService();
        var result = Resolve(service, image);
        Equal(FakeImage.NewClockRva, result.Layout.GameObjectRva, "stale known clock global took precedence over the image");
        Equal(FakeImage.RefreshRva, result.Layout.EnvironmentRefreshRva, "environment refresh moved unexpectedly");
        Equal(-1, result.EnvironmentMinute, "original environment code was not recognized");
        Equal(69, WorldMinute(image, result.Layout), "the real clock was not read through the resolved global");
    }

    private static void PatchedEnvironment()
    {
        using var image = new FakeImage(environmentMinute: 1439);
        var result = Resolve(new GameTimeService(), image);
        Equal(FakeImage.NewClockRva, result.Layout.GameObjectRva, "patched refresh hid the independent clock anchor");
        Equal(1439, result.EnvironmentMinute, "locked environment minute was not parsed");
        Equal(69, WorldMinute(image, result.Layout), "the displayed game clock used the locked environment minute");
    }

    private static void CachedClockRecovery()
    {
        using var image = new FakeImage(environmentMinute: 720);
        var service = new GameTimeService();
        var first = Resolve(service, image);
        Equal(FakeImage.NewClockRva, first.Layout.GameObjectRva, "initial clock global was not resolved");

        // Removing the independent clock code proves subsequent probes use the
        // cache, rather than scanning again while the world is not loaded.
        image.EraseClockCode();
        image.SetWorldLoaded(false);
        var empty = Resolve(service, image);
        Equal(FakeImage.NewClockRva, empty.Layout.GameObjectRva, "empty world discarded the resolved global");
        Equal(-1, WorldMinute(image, empty.Layout), "empty world must not produce a fabricated clock");

        image.SetWorldLoaded(true);
        image.SetClockMinute(24 * 60 + 70);
        var recovered = Resolve(service, image);
        Equal(FakeImage.NewClockRva, recovered.Layout.GameObjectRva, "the cached clock global was lost");
        Equal(70, WorldMinute(image, recovered.Layout), "world clock did not recover or wrap to the day");
        Equal(720, recovered.EnvironmentMinute, "clock recovery changed the locked environment");
    }

    private static void ConflictingClockSignatures()
    {
        using var image = new FakeImage();
        image.AddClockAnchor(FakeImage.ClockAnchorRva + 0x100, FakeImage.NewClockRva + 0x20);
        var header = image.Read(0, 0x1000);
        var found = GameTimeService.TryScanImageLayout(header, image.Size,
            (rva, size) => image.Read(rva, size), out var layout, out var environmentMinute);
        True(found, "the unique environment site was not recognized");
        Equal((nuint)0, layout.GameObjectRva, "conflicting clock globals must not be guessed");
        Equal(FakeImage.RefreshRva, layout.EnvironmentRefreshRva, "clock ambiguity changed the environment site");
        Equal(-1, environmentMinute, "clock ambiguity changed the parsed environment state");
    }

    private static void ModuleIdentityChange()
    {
        var service = new GameTimeService();
        using var firstImage = new FakeImage();
        var first = Resolve(service, firstImage, processKey: 101);
        Equal(FakeImage.NewClockRva, first.Layout.GameObjectRva, "initial image was not resolved");

        using var secondImage = new FakeImage(clockRva: FakeImage.NewClockRva + 0x20);
        var second = Resolve(service, secondImage, processKey: 101);
        Equal(FakeImage.NewClockRva + 0x20, second.Layout.GameObjectRva, "a different image base reused a stale clock");

        secondImage.ReplaceClockGlobal(FakeImage.NewClockRva + 0x40);
        var nextProcess = Resolve(service, secondImage, processKey: 102);
        Equal(FakeImage.NewClockRva + 0x40, nextProcess.Layout.GameObjectRva, "a different process reused a stale clock");

        secondImage.ReplaceClockGlobal(FakeImage.NewClockRva + 0x60);
        var resized = Resolve(service, secondImage, processKey: 102, moduleSize: secondImage.Size - 0x1000);
        Equal(FakeImage.NewClockRva + 0x60, resized.Layout.GameObjectRva, "a different module size reused a stale clock");
        Equal(69, WorldMinute(secondImage, resized.Layout), "the new image's clock was not readable");
    }

    private static void HistoricalFallback()
    {
        using var image = new FakeImage(clockRva: FakeImage.OldClockRva);
        Marshal.WriteByte(image.BaseAddress, 0, 0); // Make the fake PE header unavailable to the scanner.
        var result = Resolve(new GameTimeService(), image);
        Equal(FakeImage.OldClockRva, result.Layout.GameObjectRva, "historical clock fallback was lost");
        Equal(FakeImage.RefreshRva, result.Layout.EnvironmentRefreshRva, "historical refresh fallback was lost");
        Equal(69, WorldMinute(image, result.Layout), "historical clock fallback was not readable");
    }

    private static void FailedScanBackoff()
    {
        using var image = new FakeImage();
        var time = new ManualTimeProvider();
        using var service = new GameTimeService(time);
        var refresh = image.Read((uint)FakeImage.RefreshRva, 14);
        Marshal.Copy(new byte[14], 0, image.BaseAddress + (int)FakeImage.RefreshRva, 14);
        True(!TryResolve(service, image, 100, out _), "an unsupported refresh site was accepted");
        Marshal.Copy(refresh, 0, image.BaseAddress + (int)FakeImage.RefreshRva, refresh.Length);
        True(!TryResolve(service, image, 100, out _), "the failed image was rescanned on the next tick");
        time.Advance(TimeSpan.FromSeconds(15));
        True(TryResolve(service, image, 100, out _), "the retry delay prevented later recovery");

        using var nextService = new GameTimeService(time);
        Marshal.Copy(new byte[14], 0, image.BaseAddress + (int)FakeImage.RefreshRva, 14);
        True(!TryResolve(nextService, image, 101, out _), "an unsupported refresh site was accepted");
        Marshal.Copy(refresh, 0, image.BaseAddress + (int)FakeImage.RefreshRva, refresh.Length);
        True(TryResolve(nextService, image, 102, out _), "a new process identity inherited the failed scan delay");
    }

    private static void CachedClockAllocations()
    {
        using var image = new FakeImage();
        using var service = new GameTimeService();
        var resolve = ResolveLayout.CreateDelegate<ResolveDelegate>();
        var read = ReadMinute.CreateDelegate<Func<nint, nuint, nuint, int>>();
        var handle = GetCurrentProcess();
        for (var i = 0; i < 1000; i++)
        {
            resolve(service, handle, (nuint)image.BaseAddress, image.Size, 100, out var layout, out _);
            read(handle, (nuint)image.BaseAddress, layout.GameObjectRva);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            if (!resolve(service, handle, (nuint)image.BaseAddress, image.Size, 100, out var layout, out _) ||
                read(handle, (nuint)image.BaseAddress, layout.GameObjectRva) != 69)
                throw new InvalidOperationException("cached clock was not readable");
        }
        Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before,
            "normal timer samples allocated temporary arrays or delegates");
    }

    private static void CachedReadSession()
    {
        using var image = new FakeImage();
        using var service = new GameTimeService();
        var handle = SeedOwnReadSession(service, image);
        var first = service.Probe();
        Equal((uint)Environment.ProcessId, first.Pid, "the cached process identity was discarded");
        Equal(69, first.GameMinute, "the cached session did not read the test process's allocation");
        image.SetClockMinute(70);
        Equal(70, service.Probe().GameMinute, "the cached session stopped refreshing the clock");
        service.Dispose();
        True(handle.IsClosed, "the cached native process handle leaked after disposal");
        Equal(GameState.Empty, service.Probe(), "a disposed service discovered a different process");
    }

    private static void ConcurrentDispose()
    {
        using var image = new FakeImage();
        using var service = new GameTimeService();
        var handle = SeedOwnReadSession(service, image);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var state = service.Probe();
                True(state == GameState.Empty || state.GameMinute == 69,
                    "a probe observed a partially disposed read session");
            }
        })).ToArray();
        service.Dispose();
        Task.WaitAll(tasks);
        True(handle.IsClosed, "concurrent probes retained a native handle after disposal");
    }

    // Seed only this runner's own process and synthetic image. Probe therefore
    // exercises the production cache path without finding or opening a game.
    private static SafeProcessHandle SeedOwnReadSession(GameTimeService service, FakeImage image)
    {
        var nativeHandle = OpenProcess(0x00101010, false, (uint)Environment.ProcessId);
        if (nativeHandle == 0) throw new InvalidOperationException("Could not open this test process for reading.");
        var handle = new SafeProcessHandle(nativeHandle, ownsHandle: true);
        SetField(service, "_processHandle", handle);
        SetField(service, "_processPid", (uint)Environment.ProcessId);
        SetField(service, "_processBase", image.BaseAddress);
        SetField(service, "_processSize", image.Size);
        return handle;
    }

    private static void SetField(GameTimeService service, string name, object value) =>
        typeof(GameTimeService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);

    private static bool TryResolve(GameTimeService service, FakeImage image, uint processKey,
        out GameTimeService.GameLayout layout)
    {
        object?[] arguments = [GetCurrentProcess(), (nuint)image.BaseAddress, image.Size,
            processKey, default(GameTimeService.GameLayout), -1];
        var found = (bool)ResolveLayout.Invoke(service, arguments)!;
        layout = (GameTimeService.GameLayout)arguments[4]!;
        return found;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan interval) => _timestamp += (long)interval.TotalMilliseconds;
    }

    private static (GameTimeService.GameLayout Layout, int EnvironmentMinute) Resolve(
        GameTimeService service, FakeImage image, uint processKey = 100, uint? moduleSize = null)
    {
        object?[] arguments = [GetCurrentProcess(), (nuint)image.BaseAddress, moduleSize ?? image.Size,
            processKey, default(GameTimeService.GameLayout), -1];
        var found = (bool)ResolveLayout.Invoke(service, arguments)!;
        True(found, "image layout was not resolved");
        return ((GameTimeService.GameLayout)arguments[4]!, (int)arguments[5]!);
    }

    private static int WorldMinute(FakeImage image, GameTimeService.GameLayout layout) =>
        (int)ReadMinute.Invoke(null, [GetCurrentProcess(), (nuint)image.BaseAddress, layout.GameObjectRva])!;

    private static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeImage : IDisposable
    {
        public const nuint OldClockRva = 0x3714F30;
        public const nuint NewClockRva = 0x3714F50;
        public const nuint RefreshRva = 0x4F1526;
        public const uint ClockAnchorRva = 0x73F3BC;
        private const uint RefreshSectionRva = 0x4F1000;
        private const uint ClockSectionRva = 0x73F000;
        private const uint ObjectRva = 0x10000;
        private const uint EnvironmentRva = 0x20000;
        public nint BaseAddress { get; }
        public uint Size => 0x3716000;
        private nuint _clockRva;

        public FakeImage(int environmentMinute = -1, nuint clockRva = NewClockRva)
        {
            BaseAddress = VirtualAlloc(0, Size, 0x3000, 0x04); // reserve + commit, read/write, zero initialized
            if (BaseAddress == 0) throw new InvalidOperationException($"VirtualAlloc failed: {Marshal.GetLastWin32Error()}");
            _clockRva = clockRva;
            var header = new byte[0x1000];
            header[0] = (byte)'M'; header[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x3C), 0x80);
            header[0x80] = (byte)'P'; header[0x81] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x86), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x94), 0xF0);
            WriteSection(header, 0x188, RefreshSectionRva);
            WriteSection(header, 0x1B0, ClockSectionRva);
            Write(0, header);

            byte[] refresh = [0x44, 0x8B, 0x83, 0x80, 0x3E, 0, 0, 0x4C, 0x8D, 0x93, 0xAC, 0x3E, 0, 0];
            if (environmentMinute >= 0)
            {
                refresh[0] = 0x41; refresh[1] = 0xB8; refresh[6] = 0x90;
                BinaryPrimitives.WriteInt32LittleEndian(refresh.AsSpan(2, 4), environmentMinute);
            }
            Write((uint)RefreshRva, refresh);
            AddClockAnchor(ClockAnchorRva, _clockRva);
            Marshal.WriteInt64(Address(ObjectRva + 0x998), (long)Address(EnvironmentRva));
            SetWorldLoaded(true);
            SetClockMinute(69);
        }

        private static void WriteSection(byte[] header, int offset, uint rva)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 8), 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 12), rva);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 36), 0x60000020);
        }

        public void AddClockAnchor(uint rva, nuint clockRva)
        {
            var movRva = rva - 22;
            byte[] mov = [0x48, 0x8B, 0x1D, 0, 0, 0, 0];
            BinaryPrimitives.WriteInt32LittleEndian(mov.AsSpan(3), checked((int)((long)clockRva - movRva - 7)));
            Write(movRva, mov);
            Write(rva, [0x48, 0x8B, 0x9B, 0x98, 0x09, 0, 0, 0x44, 0x8B, 0x83, 0x80, 0x3E, 0, 0]);
        }

        public void ReplaceClockGlobal(nuint clockRva)
        {
            Marshal.WriteInt64(Address(_clockRva), 0);
            _clockRva = clockRva;
            AddClockAnchor(ClockAnchorRva, _clockRva);
            SetWorldLoaded(true);
        }

        public void EraseClockCode() => Write(ClockSectionRva, new byte[0x1000]);
        public void SetWorldLoaded(bool loaded) => Marshal.WriteInt64(Address(_clockRva),
            loaded ? (long)Address(ObjectRva) : 0);
        public void SetClockMinute(int value) => Marshal.WriteInt32(Address(EnvironmentRva + 0x3E80), value);
        private nint Address(nuint rva) => (nint)((nuint)BaseAddress + rva);
        private void Write(uint rva, byte[] bytes) => Marshal.Copy(bytes, 0, Address(rva), bytes.Length);

        public byte[] Read(uint rva, int size)
        {
            if ((ulong)rva + (uint)size > Size) throw new ArgumentOutOfRangeException(nameof(rva));
            var bytes = new byte[size];
            Marshal.Copy(Address(rva), bytes, 0, size);
            return bytes;
        }

        public void Dispose()
        {
            if (!VirtualFree(BaseAddress, 0, 0x8000))
                throw new InvalidOperationException($"VirtualFree failed: {Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
