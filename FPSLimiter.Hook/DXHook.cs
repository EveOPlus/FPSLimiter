using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FPSLimiter.Hook;

public unsafe class DxHook
{
    private const int PAGE_EXECUTE_READWRITE = 0x40;

    // See signature https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present plus "this" pointer.
    private static delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int> _originalPresent; // 8

    private static delegate* unmanaged[Stdcall]<IntPtr, void> _dx11Flush;
    private static delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int> _dxGetDevice; // 7
    private static delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, void> _dx11GetContext;
    private static delegate* unmanaged[Stdcall]<IntPtr, uint> _comRelease;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr> _dx12GetWaitHandle;

    private static IntPtr _cachedRealContext = IntPtr.Zero;

    // See signature https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiswapchain1-present1 "plus" this pointer.
    private static delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int> _originalPresent1;

    private static int _targetFpsInFocus = 60;
    private static int _targetFpsInBackground = 5;
    private static double _perFrameTargetMsInFocus = 1000.0 / _targetFpsInFocus;
    private static double _perFrameTargetMsInBackground = 1000.0 / _targetFpsInBackground;

    private static bool _isOurWindowInFocus = true;
    private static bool _isFpsThrottleActive = true;
    private static bool _isNamedPipeRunning = true;

    // We're going to assume that the MainWindowHandle is the one we care about.
    // This may not be true for every game, but it should hold true most the time, and it should do what I need for now...
    private static IntPtr _thisClientsHandle = Process.GetCurrentProcess().MainWindowHandle; 

    private static NamedPipeServerStream _namedPipeServerStream = null!;

    private static readonly uint CurrentPid = (uint)Process.GetCurrentProcess().Id;
    private static readonly Stopwatch LastFrameSw = Stopwatch.StartNew();
    private static readonly Stopwatch LastCheckedFocusSw = Stopwatch.StartNew();
    private static readonly PrecisionSleep PrecisionSleep = new ();
    
    // Name the pipe based on the MainWindowHandle so clients don't conflict and so it's easy to find, we can also use this like a mutex which should work on linux too.
    private static readonly string _fpsLimiterPipeName = "FpsLimiter_" + _thisClientsHandle;
    
    private static readonly Action<string> _log = x => DebugLogger.WriteLine(x, _thisClientsHandle);

    [UnmanagedCallersOnly(EntryPoint = "Initialize", CallConvs = [typeof(CallConvStdcall)])]
    public static void Initialize()
    {
        try
        {
            try
            {
                _namedPipeServerStream = CreateNamedPipeServer();
            }
            catch (Exception ex)
            {
                // Double on using the named pipe like a cross-platform mutex. If the named pipe is already taken then don't hook again.
                _log($"Failed to create named pipe with error: {ex}");
                return;
            }

            // Setup a named pipe so we can manage the target fps from another process such as Eve-O Preview.
            Task.Run(StartPipeServer);

            _log($"Subscribing to EVENT_SYSTEM_FOREGROUND");
            WinEventHook.StartListening(HandleForegroundChangedEvent);

            _log($"Setup dummy DXGI objects to find the VTable address");
            using var factory = new Factory1();
            using var device = new SharpDX.Direct3D11.Device(DriverType.Hardware, DeviceCreationFlags.None);
            using var swapChain = new SwapChain(factory, device, new SwapChainDescription()
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(1, 1, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                Usage = Usage.RenderTargetOutput,
                OutputHandle = Process.GetCurrentProcess().MainWindowHandle,
                SampleDescription = new SampleDescription(1, 0),
                IsWindowed = true
            });

            void** vTablePointer = *(void***)swapChain.NativePointer;

            _log($"Locating Present should at index 8 for DirectX 11");
            void** presentEntryPtr = &vTablePointer[8];
            _originalPresent = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)*presentEntryPtr;

            // Get some more pointers so we can try and clear the cache and avoid spikes.
            _dxGetDevice = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)(*(void***)swapChain.NativePointer)[7];
            void** deviceVtable = *(void***)device.NativePointer;
            _dx11GetContext = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, void>)deviceVtable[40];
            void** contextVtable = *(void***)device.ImmediateContext.NativePointer;
            _dx11Flush = (delegate* unmanaged[Stdcall]<IntPtr, void>)contextVtable[111];
            _comRelease = (delegate* unmanaged[Stdcall]<IntPtr, uint>)deviceVtable[2];

            // Grant access to the memory address
            if (VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtect))
            {
                _log($"Hooking into Present");
                *presentEntryPtr = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&HookedPresent;
                // Set the protection back to what it was before we got here.
                VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), oldProtect, out _);
            }
            
            bool isDx12Game = false;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (module.ModuleName?.ToLower() == "d3d12.dll")
                {
                    _log($"Located DirectX 12 module is loaded.");
                    isDx12Game = true;
                    break;
                }
            }

            if (isDx12Game)
            {
                _log($"Locating Present1 should at index 22 for DirectX 12");
                void** presentEntry22 = &vTablePointer[22];
                _originalPresent1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)*presentEntry22;
                
                // Get some more pointers so we can try and clear the cache and avoid spikes.
                _dx12GetWaitHandle = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(void***)swapChain.NativePointer)[28];
                
                if (VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtectDx12))
                {
                    _log($"Hooking into Present1");
                    *presentEntry22 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)&HookedPresent1;

                    VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), oldProtectDx12, out _);
                }
            }
            _log("0");

            if (QueryPerformanceFrequency(out _qpcFrequency))
            {
                // Now you can also accurately set your 16ms target based on real hardware
                _qpcTicksPer16ms = (long)(_qpcFrequency * 0.016666);
            }

            //InstallQPCHook();
            //InstallIATHook();
        }
        catch (Exception ex)
        {
            _log(ex.ToString());

            try
            {
                _namedPipeServerStream.Dispose();
            }
            catch
            {
                // Just some housekeeping, nothing to do.
            }
        }

        _log("Finished Initialization");
    }

    private static unsafe void InstallIATHook()
    {
        // 1. Get the base address of the current game's .exe
        IntPtr baseAddr = GetModuleHandle(null);
        byte* basePtr = (byte*)baseAddr;

        // 2. Navigate PE headers (x64)
        // DOS Header -> e_lfanew points to NT Headers
        int e_lfanew = *(int*)(basePtr + 0x3C);
        byte* ntHeaders = basePtr + e_lfanew;

        // Optional Header -> Data Directory [1] is the Import Table
        // Offset for x64: NTHeader + 0x18 (OptionalHeader) + 0x70 (DataDirectory)
        int importTableRVA = *(int*)(ntHeaders + 0x18 + 0x70 + 8);
        if (importTableRVA == 0) return;

        byte* importDesc = basePtr + importTableRVA;

        // 3. Iterate through imported DLL descriptors
        while (*(int*)(importDesc + 12) != 0) // Name RVA
        {
            string dllName = Marshal.PtrToStringAnsi((IntPtr)(basePtr + *(int*)(importDesc + 12)));
            if (dllName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase) ||
                dllName.Equals("KernelBase.dll", StringComparison.OrdinalIgnoreCase))
            {
                // FirstThunk (IAT) is at offset 16
                int iatRVA = *(int*)(importDesc + 16);
                long* iatEntry = (long*)(basePtr + iatRVA);

                // 4. Find the QPC address in the IAT
                IntPtr realQPC = GetProcAddress(GetModuleHandle(dllName), "QueryPerformanceCounter");

                while (*iatEntry != 0)
                {
                    if (*iatEntry == (long)realQPC)
                    {
                        // Found it! Save the original and swap
                        _originalQPC = (delegate* unmanaged[Stdcall]<long*, bool>)*iatEntry;

                        if (VirtualProtect((IntPtr)iatEntry, (UIntPtr)8, PAGE_EXECUTE_READWRITE, out uint old))
                        {
                            // Use Interlocked for a 100% thread-safe atomic swap
                            Interlocked.Exchange(ref *iatEntry, (long)(delegate* unmanaged[Stdcall]<long*, bool>)&HookedQPC);
                            VirtualProtect((IntPtr)iatEntry, (UIntPtr)8, old, out _);
                            _log($"IAT Hooked QPC in {dllName}");
                            return;
                        }
                    }
                    iatEntry++;
                }
            }
            importDesc += 20; // Size of IMAGE_IMPORT_DESCRIPTOR
        }
    }

    private static IntPtr AllocateNear(IntPtr target)
    {
        // Scan memory within 2GB range to allow a 5-byte JMP (0xE9)
        long startAddr = (long)target;
        for (int i = 1; i < 500; i++)
        {
            // Try +ve and -ve offsets (64KB steps)
            long[] tries = { startAddr + (i * 0x10000), startAddr - (i * 0x10000) };
            foreach (long addr in tries)
            {
                IntPtr allocated = VirtualAlloc((IntPtr)addr, (UIntPtr)1024, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (allocated != IntPtr.Zero) return allocated;
            }
        }
        return IntPtr.Zero;
    }

    private static void InstallQPCHook()
    {
        _log("Locating QPC for patching...");
        IntPtr hModule = GetModuleHandle("KernelBase.dll");
        if (hModule == IntPtr.Zero) hModule = GetModuleHandle("kernel32.dll");
        IntPtr qpcAddr = GetProcAddress(hModule, "QueryPerformanceCounter");

        QueryPerformanceFrequency(out long freq);
        _qpcTicksPer16ms = freq / 60;

        // 1. Create a Relay near QPC to bridge the 64-bit address gap
        IntPtr relay = AllocateNear(qpcAddr);
        if (relay == IntPtr.Zero) { _log("Failed to allocate relay near QPC"); return; }

        // 2. Build Trampoline in the relay: [16-bytes stolen] + [JMP back to QPC+16]
        byte* r = (byte*)relay;
        Marshal.Copy(qpcAddr, new byte[16], 0, 16); // Buffer check
        byte[] stolen = new byte[16];
        Marshal.Copy(qpcAddr, stolen, 0, 16);
        Marshal.Copy(stolen, 0, relay, 16);

        // JMP back to QPC + 16 (14-byte absolute JMP)
        byte* jmpBack = r + 16;
        jmpBack[0] = 0xFF; jmpBack[1] = 0x25;
        *(int*)(jmpBack + 2) = 0;
        *(long*)(jmpBack + 6) = (long)qpcAddr + 16;

        _originalQPC = (delegate* unmanaged[Stdcall]<long*, bool>)relay;

        // 3. Build the Detour JMP in the relay (offset 128) to our C# HookedQPC
        byte* detour = r + 128;
        detour[0] = 0xFF; detour[1] = 0x25;
        *(int*)(detour + 2) = 0;
        *(long*)(detour + 6) = (long)(delegate* unmanaged[Stdcall]<long*, bool>)&HookedQPC;

        // 4. ATOMIC 5-BYTE JMP Patch
        // This is the ONLY part touching game code. Using 5-bytes avoids splitting instructions.
        if (VirtualProtect(qpcAddr, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out uint old))
        {
            byte[] jmp = new byte[5];
            jmp[0] = 0xE9; // JMP relative
            int relAddr = (int)((long)detour - (long)qpcAddr - 5);
            fixed (byte* p = &jmp[1]) *(int*)p = relAddr;

            Marshal.Copy(jmp, 0, qpcAddr, 5);
            VirtualProtect(qpcAddr, (UIntPtr)5, old, out _);
            _log("QPC Hook installed using 5-byte atomic relay.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInFocus()
    {
        if (LastCheckedFocusSw.ElapsedMilliseconds < 3000)
        {
            return _isOurWindowInFocus;
        }

        // Just as a safe guard lets manually check if we're in focus if it's been a while since anything happened.
        // We depend mostly on events updating our focus now so we most the time we can trust it but checking once every few seconds is a trivial backup.
        // This is mostly to protect us accidentally thinking we have focus e.g. if the named pipe gives us focus, but then we lost it somehow in a race condition.
        IntPtr foregroundHandle = GetForegroundWindow();
        SetOurWindowInFocus(IsThisOurHandle(foregroundHandle));

        return _isOurWindowInFocus;
    }

    private static void HandleForegroundChangedEvent(IntPtr handleTakingFocus)
    {
        SetOurWindowInFocus(IsThisOurHandle(handleTakingFocus));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetOurWindowInFocus(bool isInFocus)
    {
        _isOurWindowInFocus = isInFocus;
        LastCheckedFocusSw.Restart();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsThisOurHandle(IntPtr theHandleToCheck)
    {
        // If we know the handle, just check it directly
        // Note: We should always know this because we've started being lazy and assuming it's the main window. but leaving code for future changes if needed.
        if (_thisClientsHandle != IntPtr.Zero)
        {
            return _thisClientsHandle == theHandleToCheck;
        }

        // If there's no handle, it can't be ours.
        if (theHandleToCheck == IntPtr.Zero)
        {
            return false;
        }

        // If we don't know the handle yet, compare the process Id until we find it.
        GetWindowThreadProcessId(theHandleToCheck, out uint foregroundPid);

        if (foregroundPid == CurrentPid)
        {
            _thisClientsHandle = theHandleToCheck;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrottleTheFrame()
    {
        if (_isFpsThrottleActive)
        {
            bool inFocusAtStartOfFrame = IsInFocus();

            var perFrameTargetMs = inFocusAtStartOfFrame ? _perFrameTargetMsInFocus : _perFrameTargetMsInBackground;

            double elapsed = LastFrameSw.Elapsed.TotalMilliseconds;
            if (elapsed < perFrameTargetMs)
            {
                double timeLeftToWait = perFrameTargetMs - elapsed;

                // First lets wait out some big chunks, but still small enough to break out if we take focus.
                // This should only really be needed if we're going super slow fps and need to snap back to high speed.
                while (timeLeftToWait > 16.0)
                {
                    Thread.Sleep(1); // This might sleep for around 15ms by default. so only do it while waiting out big chunks.

                    // If we received focus, then break out of this frame to reduce lag when switching.
                    if (!inFocusAtStartOfFrame && IsInFocus())
                    {
                        LastFrameSw.Restart();
                        return;
                    }

                    // Refresh remaining time left to wait.
                    timeLeftToWait = perFrameTargetMs - LastFrameSw.Elapsed.TotalMilliseconds;
                }

                // Wait out the final big chunk to save the CPU a bit longer. We're so close to the end theres no point trying to escape.
                if (timeLeftToWait > 1.0)
                {
                    PrecisionSleep.Sleep(timeLeftToWait - 0.5);
                }

                // Busy wait for the final high-precision micro-seconds
                while (LastFrameSw.Elapsed.TotalMilliseconds < perFrameTargetMs)
                {
                    Thread.SpinWait(10);
                }
            }

            // Start timing the next frame so we know how much extra delay we need to add.
            LastFrameSw.Restart();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HookedPresent(IntPtr swapChain, uint syncInterval, uint flags)
    {
        if (_isFpsThrottleActive)
        {
            // Use your existing Stopwatch to measure the "Real" time spent
            double msBefore = LastFrameSw.Elapsed.TotalMilliseconds;

            ThrottleTheFrame(); // Your sleep logic

            double msAfter = LastFrameSw.Elapsed.TotalMilliseconds;
            double msSlept = msAfter - msBefore;

            // Convert MS to Ticks using the frequency you found in Initialize
            // _qpcFrequency should be a static long set during QueryPerformanceFrequency
            long sleptTicks = (long)((msSlept / 1000.0) * _qpcFrequency);

            if (sleptTicks > _qpcTicksPer16ms)
            {
                long excessTicks = sleptTicks - _qpcTicksPer16ms;
                Interlocked.Add(ref _qpcOffset, excessTicks);
            }

            return _originalPresent(swapChain, 0, flags);
        }

        return _originalPresent(swapChain, syncInterval, flags);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HookedPresent1(IntPtr swapChain, uint syncInterval, uint flags, IntPtr present1Parameters)
    {
        var result = _originalPresent1(swapChain, _isFpsThrottleActive ? 0 : syncInterval, flags, present1Parameters);
        ThrottleTheFrame();

        // Sync CPU/GPU to prevent the burst
        IntPtr handle = _dx12GetWaitHandle(swapChain);
        if (handle != IntPtr.Zero)
        {
            WaitForSingleObject(handle, 0xFFFFFFFF);
        }

        // Let DirectX 12 do its thing to generate the next frame.
        return result;
    }

    private static long _qpcOffset = 0;
    private static byte[] _originalBytes = new byte[14];
    private static long _qpcCallCount = 0;
    private static long _qpcFrequency;
    private static long _lastReportedTicks = 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static unsafe bool HookedQPC(long* lpPerformanceCount)
    {
        // Use the pointer you saved during IAT Hook
        bool result = _originalQPC(lpPerformanceCount);

        if (result)
        {
            long adjustedTime = *lpPerformanceCount - _qpcOffset;

            // Ensure time NEVER moves backwards
            if (adjustedTime <= _lastReportedTicks)
            {
                adjustedTime = _lastReportedTicks + 1;
            }

            _lastReportedTicks = adjustedTime;
            *lpPerformanceCount = adjustedTime;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NamedPipeServerStream CreateNamedPipeServer()
    {
        _log($"Creating named pipe server: {_fpsLimiterPipeName}");
        return new NamedPipeServerStream(_fpsLimiterPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }

    // Just making up a custom mini messaging protocol just randomly really.
    private const byte PipeFpsPrefixByteFocused = 0xF1; // F1 is a prefix before an int (4 bytes) for the target focused FPS rate.
    private const byte PipeFpsPrefixByteBackground = 0xF2; // F2 is a prefix before an int (4 bytes) for the target background FPS rate.
    private const byte PipeQueryDirection = 0xA1; // A1 is the first byte, if the caller is requesting read only.
    private const byte PipeUpdateDirection = 0xA2; // A2 is the first byte, if the caller is asking us to update something.
    private const byte PipeFireAndForgetDirection = 0xA3; // A3 is the first byte, if the caller wants something done quick and don't really care about the outcome.
    private const byte PipeSetFocusedCommand = 0xB1; // B1 is the second byte following a A3, to tell our process it needs to be ready to take focus and unthrottle the FPS ASAP.
    private const byte PipeSuccessResponseCode = 0x01; // 01 is the response we send at the end of a A2 request, like a success return code. 

    private static void StartPipeServer()
    {
        var failureCount = 0;
        string lastPlace = "";
        while (_isNamedPipeRunning)
        {
            try
            {
                // _log($"Named pipe {_fpsLimiterPipeName} WaitForConnection()");
                _namedPipeServerStream.WaitForConnection();

                using (var reader = new BinaryReader(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                using (var writer = new BinaryWriter(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    lastPlace = "ReadByte directionByte";
                    var directionByte = reader.ReadByte();
                    switch (directionByte)
                    {
                        case PipeFireAndForgetDirection:
                            if (reader.ReadByte() == PipeSetFocusedCommand)
                            {
                                SetOurWindowInFocus(true);
                                // _log($"PipeSetFocusedCommand processed");
                            }

                            break;
                        case PipeUpdateDirection:
                            lastPlace = "ReadByte PipeFpsPrefixByteFocused";
                            if (reader.ReadByte() == PipeFpsPrefixByteFocused)
                            {
                                // since we read the 0xF1 we know the next 4 bytes are going to be an 32 bit int.
                                lastPlace = "ReadInt32 newTargetFpsFocus";
                                var newTargetFpsFocus = reader.ReadInt32();

                                if (newTargetFpsFocus < 1 || newTargetFpsFocus > 1000)
                                {
                                    // anything that might look like it's just unthrottled, we will turn off the throttle.
                                    _targetFpsInFocus = 0;
                                    _perFrameTargetMsInFocus = 0;
                                }
                                else
                                {
                                    _targetFpsInFocus = newTargetFpsFocus;
                                    _perFrameTargetMsInFocus = 1000.0 / newTargetFpsFocus;
                                }
                            }

                            lastPlace = "ReadByte PipeFpsPrefixByteBackground";
                            if (reader.ReadByte() == PipeFpsPrefixByteBackground)
                            {
                                // since we read the 0xF2 we know the next 4 bytes are going to be an 32 bit int.
                                lastPlace = "ReadInt32 newTargetFpsBackground";
                                var newTargetFpsBackground = reader.ReadInt32();

                                if (newTargetFpsBackground < 1 || newTargetFpsBackground > 1000)
                                {
                                    // anything that might look like it's just unthrottled, we will turn off the throttle.
                                    _targetFpsInBackground = 0;
                                    _perFrameTargetMsInBackground = 0;
                                }
                                else
                                {
                                    _targetFpsInBackground = newTargetFpsBackground;
                                    _perFrameTargetMsInBackground = 1000.0 / newTargetFpsBackground;
                                }
                            }

                            // If both focus and background are 0, then disable the whole throttling.
                            _isFpsThrottleActive = _perFrameTargetMsInBackground + _perFrameTargetMsInFocus > 0;

                            lastPlace = "Write PipeSuccessResponseCode Setting";
                            writer.Write(PipeSuccessResponseCode);
                            
                            _log($"PipeUpdateDirection set to FpsInFocus: {_targetFpsInFocus} FpsInBackground: {_targetFpsInBackground}");

                            break;

                        case PipeQueryDirection:
                            lastPlace = "ReadByte Getting";
                            reader.ReadByte(); // Reserved for future instruction if we ever want to do more than query the set throttles.

                            lastPlace = "Write PipeFpsPrefixByteFocused";
                            writer.Write(PipeFpsPrefixByteFocused);
                            lastPlace = "Write _targetFpsInFocus";
                            writer.Write(_targetFpsInFocus);
                            lastPlace = "Write PipeFpsPrefixByteBackground";
                            writer.Write(PipeFpsPrefixByteBackground);
                            lastPlace = "Write _targetFpsInBackground";
                            writer.Write(_targetFpsInBackground);
                            break;
                    }

                    lastPlace = "Writer Flush";
                    writer.Flush();
                    lastPlace = "Writer Close";
                    writer.Close(); ;
                    lastPlace = "Reader Close";
                    reader.Close();
                }

                lastPlace = "IsConnected";
                if (_namedPipeServerStream.IsConnected)
                {
                    lastPlace = "Disconnect";
                    _namedPipeServerStream.Disconnect();
                }

            }
            catch (Exception ex)
            {
                _log($"Named pipe {_namedPipeServerStream} encountered error after {lastPlace}: {ex}");
                Thread.Sleep(10); // hopefully we never crash in a loop but if we do, save the cpu

                // Rather silent failure than crashing another process

                _namedPipeServerStream.Dispose();
                _namedPipeServerStream = CreateNamedPipeServer();
                
                failureCount++;

                if (failureCount > 100)
                {
                    // Something is very wrong. Let's just give up on everything.
                    _isNamedPipeRunning = false;
                    _isFpsThrottleActive = false;
                    MessageBox(IntPtr.Zero, $"Aborting FPS Limiter. Named pipe error at {lastPlace}: {ex}", "FPS Limiter", 0);
                }
            }
        }
    }
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    
    [DllImport("user32.dll")]
    static extern bool MessageBeep(uint uType);
    const uint MB_OK = 0x00000000; // just a ding
    const uint MB_ICONERROR = 0x00000010; // another noise for testing


    private static byte[] _originalQpcBytes = new byte[14];



    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static long _qpcTicksPer16ms;
    private static delegate* unmanaged[Stdcall]<long*, bool> _originalQPC;


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    private const uint MEM_COMMIT_RESERVE = 0x3000;

}