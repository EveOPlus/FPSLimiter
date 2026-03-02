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
    private static delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int> _originalPresent;

    // See signature https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiswapchain1-present1 "plus" this pointer.
    private static delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int> _originalPresent1;

    private static int _targetFpsInFocus = 60;
    private static int _targetFpsInBackground = 5;
    private static double _perFrameTargetMsInFocus = 1000.0 / _targetFpsInFocus;
    private static double _perFrameTargetMsInBackground = 1000.0 / _targetFpsInBackground;

    private static bool _lastCheckedFocusResult = true;
    private static bool _isFpsThrottleActive = true;
    private static bool _isNamedPipeRunning = true;

    private static IntPtr _thisClientsHandle = IntPtr.Zero; // We don't know it at the start.

    private static NamedPipeServerStream _namedPipeServerStream;

    private static readonly uint CurrentPid = (uint)Process.GetCurrentProcess().Id;
    private static readonly Stopwatch LastFrameSw = Stopwatch.StartNew();
    private static readonly Stopwatch LastCheckedFocusSw = Stopwatch.StartNew();
    private static readonly PrecisionSleep PrecisionSleep = new ();
    
    // Name the pipe based on the Process ID so clients don't conflict, we can also use this like a mutex which should work on linux too.
    private static readonly string _fpsLimiterPipeName = "FpsLimiter_" + Process.GetCurrentProcess().Id;
    
    [UnmanagedCallersOnly(EntryPoint = "Initialize", CallConvs = [typeof(CallConvStdcall)])]
    public static void Initialize()
    {
        //TimeBeginPeriod(1); // We need a precision sleep, but trying to replace the Sleep with a WaitableTimer instead.

        try
        {
            _namedPipeServerStream = CreateNamedPipeServer();
        }
        catch
        {
            // Double on using the named pipe like a cross-platform mutex. If the named pipe is already taken then don't hook again.
            return;
        }

        // Setup a named pipe so we can manage the target fps from another process such as Eve-O Preview.
        Task.Run(StartPipeServer);
        
        // Setup dummy DXGI objects to find the VTable address
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

        // Present should be at index 8 for DirectX 11.
        void** presentEntryPtr = &vTablePointer[8];
        _originalPresent = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)*presentEntryPtr;

        // Grant access to the memory address
        if (VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtect))
        {
            // Jump to our instruction instead so we can wait before handing it back to dx.
            *presentEntryPtr = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&HookedPresent;
            // Set the protection back to what it was before we got here.
            VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), oldProtect, out _);
        }

        bool isDx12Game = false;
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName?.ToLower() == "d3d12.dll")
            {
                isDx12Game = true;
                break;
            }
        }

        if (isDx12Game)
        {
            // Present1 should be at index 22 for DirectX 12.
            void** presentEntry22 = &vTablePointer[22];
            _originalPresent1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)*presentEntry22;

            if (VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtectDx12))
            {
                *presentEntry22 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)&HookedPresent1;

                VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), oldProtectDx12, out _);
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInFocus()
    {
        // Save some CPU cycles and don't check too often.
        if (LastCheckedFocusSw.ElapsedMilliseconds < 10)
        {
            return _lastCheckedFocusResult;
        }

        LastCheckedFocusSw.Restart();

        IntPtr foregroundHandle = GetForegroundWindow();
        
        // If we know the handle, just check it directly
        if (_thisClientsHandle != IntPtr.Zero)
        {
            _lastCheckedFocusResult = foregroundHandle == _thisClientsHandle;
            return _lastCheckedFocusResult;
        }

        // We can't be in focus if nothing is.
        if (foregroundHandle == IntPtr.Zero)
        {
            _lastCheckedFocusResult = false;
            return _lastCheckedFocusResult;
        }

        // If we don't know the handle yet, compare the process Id until we find it.
        GetWindowThreadProcessId(foregroundHandle, out uint foregroundPid);

        if (foregroundPid == CurrentPid)
        {
            _thisClientsHandle = foregroundHandle;
            _lastCheckedFocusResult = true;
            return _lastCheckedFocusResult;
        }

        _lastCheckedFocusResult = false;
        return _lastCheckedFocusResult;
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
                while (timeLeftToWait > 12.0)
                {
                    PrecisionSleep.Sleep(10);

                    // If we received focus, then break out of this frame to reduce lag when switching.
                    if (!inFocusAtStartOfFrame && IsInFocus())
                    {
                        timeLeftToWait = 0;
                    }
                    else
                    {
                        // Refresh remaining time left to wait.
                        timeLeftToWait = perFrameTargetMs - LastFrameSw.Elapsed.TotalMilliseconds;
                    }
                }
                
                PrecisionSleep.Sleep(timeLeftToWait);

                //// Wait out the final big chunk to save the CPU a bit longer. We're so close to the end theres no point trying to escape.
                //if (timeLeftToWait > 1.0)
                //{
                //    //PrecisionSleep.Sleep(timeLeftToWait - 1);
                //}

                //// Busy wait for the final high-precision micro-seconds
                //while (LastFrameSw.Elapsed.TotalMilliseconds < perFrameTargetMs)
                //{
                //    Thread.SpinWait(10);
                //}
            }

            // Start timing the next frame so we know how much extra delay we need to add.
            LastFrameSw.Restart();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HookedPresent(IntPtr swapChain, uint syncInterval, uint flags)
    {
        ThrottleTheFrame();
        
        // Let DirectX 11 do its thing to generate the next frame.
        return _originalPresent(swapChain, syncInterval, flags);
        //return _originalPresent(swapChain, _isFpsThrottleActive ? 0 : syncInterval, flags);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HookedPresent1(IntPtr swapChain, uint syncInterval, uint flags, IntPtr present1Parameters)
    {
        ThrottleTheFrame();
        
        // Let DirectX 12 do its thing to generate the next frame.
        return _originalPresent1(swapChain, syncInterval, flags, present1Parameters);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NamedPipeServerStream CreateNamedPipeServer()
    {
        return new NamedPipeServerStream(_fpsLimiterPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }

    // Just making up a custom mini protocol to send a couple parameters.
    // We honestly don't need to prefix or have instructions, but 
    private const byte PipeFpsPrefixByteFocused = 0xF1; // F1 sounds good for FPS. why not?
    private const byte PipeFpsPrefixByteBackground = 0xF2;
    private const byte PipeQueryDirection = 0xA1;
    private const byte PipeUpdateDirection = 0xA2;
    private const byte PipeSuccessResponseCode = 0x01;

    private static void StartPipeServer()
    {
        var failureCount = 0;
        string lastPlace = "";
        while (_isNamedPipeRunning)
        {
            try
            {
                lastPlace = "WaitForConnection";
                _namedPipeServerStream.WaitForConnection();

                using (var reader = new BinaryReader(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                using (var writer = new BinaryWriter(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    lastPlace = "ReadByte directionByte";
                    var directionByte = reader.ReadByte();
                    switch (directionByte)
                    {
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Leaving this here so I can find it back if it's needed again. Hopefully we can move to a waitable timer instead though.
    //
    //[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    //private static extern uint TimeBeginPeriod(uint uPeriod);

}