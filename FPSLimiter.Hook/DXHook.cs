using System.ComponentModel.DataAnnotations;
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
    private static int _targetFpsInPredictFocus = 20;
    private static double _perFrameTargetMsInFocus = 1000.0 / _targetFpsInFocus;
    private static double _perFrameTargetMsInBackground = 1000.0 / _targetFpsInBackground;
    private static double _perFrameTargetMsInPredictFocus = 1000.0 / _targetFpsInPredictFocus;

    private static FocusType _ourFocus = FocusType.Background;
    private static int _predictedFocusTimeoutMs = 5000;
    private static bool _isFpsThrottleActive = true;
    private static bool _isNamedPipeRunning = true;
    private static bool _ignoreNextLostFocus = false;
    private static int _ownerProcessId = -1; // -1 means anyone. just run with no owner.
    
    // We're going to assume that the MainWindowHandle is the one we care about.
    // This may not be true for every game, but it should hold true most the time, and it should do what I need for now...
    private static IntPtr _thisClientsHandle = Process.GetCurrentProcess().MainWindowHandle; 

    private static NamedPipeServerStream _namedPipeServerStream = null!;

    private static readonly uint CurrentPid = (uint)Process.GetCurrentProcess().Id;
    private static readonly Stopwatch LastFrameSw = Stopwatch.StartNew();
    private static readonly Stopwatch LastCheckedFocusSw = Stopwatch.StartNew();
    private static readonly PrecisionSleep PrecisionSleep = new ();
    
    // Name the pipe based on the MainWindowHandle so clients don't conflict and so it's easy to find, we can also use this like a mutex which should work on linux too.
    private static readonly string FpsLimiterPipeName = "FpsLimiter_" + _thisClientsHandle;
    
    private static readonly Action<string> Log = x => DebugLogger.WriteLine(x, _thisClientsHandle);

    [UnmanagedCallersOnly(EntryPoint = "Initialize", CallConvs = [typeof(CallConvStdcall)])]
    public static void Initialize()
    {
        try
        {
            _namedPipeServerStream = CreateNamedPipeServer();
        }
        catch (Exception ex)
        {
            // Double on using the named pipe like a cross-platform mutex. If the named pipe is already taken then don't hook again.
            Log($"Failed to create named pipe with error: {ex}");
            return;
        }

        // Setup a named pipe so we can manage the target fps from another process such as Eve-O Preview.
        Task.Run(StartPipeServer);

        //Log($"Subscribing to EVENT_SYSTEM_FOREGROUND");
        WinEventHook.StartListening(HandleForegroundChangedEvent);

        //Log($"Setup dummy DXGI objects to find the VTable address");
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

        //Log($"Locating Present should at index 8 for DirectX 11");
        void** presentEntryPtr = &vTablePointer[8];
        _originalPresent = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)*presentEntryPtr;

        // Grant access to the memory address
        if (VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtect))
        {
            Log($"Hooking into Present");
            *presentEntryPtr = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&HookedPresent;
            // Set the protection back to what it was before we got here.
            VirtualProtect((IntPtr)presentEntryPtr, (UIntPtr)sizeof(nint), oldProtect, out _);
        }

        bool isDx12Game = false;
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName?.ToLower() == "d3d12.dll")
            {
                //Log($"Located DirectX 12 module is loaded.");
                isDx12Game = true;
                break;
            }
        }

        if (isDx12Game)
        {
            //Log($"Locating Present1 should at index 22 for DirectX 12");
            void** presentEntry22 = &vTablePointer[22];
            _originalPresent1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)*presentEntry22;

            if (VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), PAGE_EXECUTE_READWRITE, out var oldProtectDx12))
            {
                Log($"Hooking into Present1");
                *presentEntry22 = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, int>)&HookedPresent1;

                VirtualProtect((IntPtr)presentEntry22, (UIntPtr)sizeof(nint), oldProtectDx12, out _);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FocusType GetOurCurrentFocus()
    {
        var msBeforeNextCheck = _ourFocus != FocusType.Predicted ? 3000 : _predictedFocusTimeoutMs;
        if (LastCheckedFocusSw.ElapsedMilliseconds < msBeforeNextCheck)
        {
            return _ourFocus;
        }

        // Just as a safe guard lets manually check if we're in focus if it's been a while since anything happened.
        // We depend mostly on events updating our focus now so we most the time we can trust it but checking once every few seconds is a trivial backup.
        // This is mostly to protect us accidentally thinking we have focus e.g. if the named pipe gives us focus, but then we lost it somehow in a race condition.
        IntPtr foregroundHandle = GetForegroundWindow();
        var currentFocus = IsThisOurHandle(foregroundHandle) ? FocusType.Foreground : FocusType.Background;
        SetOurWindowInFocus(currentFocus);

        EnsureOwnerIsStillAlive();

        return _ourFocus;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureOwnerIsStillAlive()
    {
        if (_ownerProcessId == -1)
        {
            return;
        }

        if (IsProcessRunning(_ownerProcessId))
        {
            return;
        }

        _ownerProcessId = 0;
        _isFpsThrottleActive = false;
    }

    private static void HandleForegroundChangedEvent(IntPtr handleTakingFocus)
    {
        var currentFocus = IsThisOurHandle(handleTakingFocus) ? FocusType.Foreground : FocusType.Background;
        SetOurWindowInFocus(currentFocus);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetOurWindowInFocus(FocusType newFocus)
    {
        if (_ignoreNextLostFocus && newFocus == FocusType.Background)
        {
            _ignoreNextLostFocus = false;
            return;
        }

        _ourFocus = newFocus;
        LastCheckedFocusSw.Restart();
        
        if (newFocus == FocusType.Predicted)
        {
            _ignoreNextLostFocus = true;
        }
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
            var focusAtStartOfFrame = GetOurCurrentFocus();

            double perFrameTargetMs;
            switch (focusAtStartOfFrame)
            {
                case FocusType.Background:
                    perFrameTargetMs = _perFrameTargetMsInBackground;
                    break;
                case FocusType.Foreground:
                    perFrameTargetMs = _perFrameTargetMsInFocus;
                    break;
                case FocusType.Predicted:
                    perFrameTargetMs = _perFrameTargetMsInPredictFocus > 0.9
                        ? _perFrameTargetMsInPredictFocus
                        : _perFrameTargetMsInBackground;
                    break;
                default:
                    perFrameTargetMs = 0;
                    break;
            }

            double elapsed = LastFrameSw.Elapsed.TotalMilliseconds;
            if (elapsed < perFrameTargetMs)
            {
                double timeLeftToWait = perFrameTargetMs - elapsed;

                // ToDo: Wait out the bulk in a PrecisionSleep, with a callback to interrupt if we need to take focus.
                // Then we can remove the whole loop and check logic for a sleep that is much more predicable on the CPU.
                
                // First lets wait out some big chunks, but still small enough to break out if we take focus.
                // This should only really be needed if we're going super slow fps and need to snap back to high speed.
                while (timeLeftToWait > 16.0)
                {
                    PrecisionSleep.Sleep(15); // We can do a Sleep(1) here which will pass about 15-16ms but our PrecisionSleep uses a waitable object so should be better on the CPU.

                    // If we received focus, then break out of this frame to reduce lag when switching.
                    if (focusAtStartOfFrame != FocusType.Foreground && GetOurCurrentFocus() == FocusType.Foreground)
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
        ThrottleTheFrame();
        
        // Let DirectX 11 do its thing to generate the next frame.
        //return _originalPresent(swapChain, syncInterval, flags);
        return _originalPresent(swapChain, _isFpsThrottleActive ? 0 : syncInterval, flags);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HookedPresent1(IntPtr swapChain, uint syncInterval, uint flags, IntPtr present1Parameters)
    {
        ThrottleTheFrame();
        
        // Let DirectX 12 do its thing to generate the next frame.
        return _originalPresent1(swapChain, _isFpsThrottleActive ? 0 : syncInterval, flags, present1Parameters);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NamedPipeServerStream CreateNamedPipeServer()
    {
        Log($"Creating named pipe server: {FpsLimiterPipeName}");
        return new NamedPipeServerStream(FpsLimiterPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }

    // Just making up a custom mini messaging protocol just randomly really.
    private const byte PipeFireAndForgetDirection = 0xA3; // A3 is the first byte, if the caller wants something done quick and don't really care about the outcome.
    private const byte PipeSetFocusedCommand = 0xB1; // B1 is the second byte following a A3, to tell our process it needs to be ready to take focus and unthrottle the FPS ASAP.
    private const byte PipePredictFocusCommand = 0xB3; // B3 is the second byte following a A3, to tell our process that focus might be on the way soon.

    private const byte PipeQueryDirection = 0xA1; // A1 is the first byte, if the caller is requesting read only.
    private const byte PipePingRequestCode = 0xB2; // B2 is used for a simple ping, following an A1

    private const byte PipeUpdateDirection = 0xA2; // A2 is the first byte, if the caller is asking us to update something.
    private const byte PipeFpsPrefixByteFocused = 0xF1; // F1 is a prefix before an int (4 bytes) for the target focused FPS rate.
    private const byte PipeFpsPrefixByteBackground = 0xF2; // F2 is a prefix before an int (4 bytes) for the target background FPS rate.
    private const byte PipeFpsPrefixBytePredict = 0xF3; // F3 is a prefix before an int (4 bytes) for the target FPS rate when predicting focus is coming.
    private const byte PipeTakeOwnershipCommand = 0xB4; // 0xB4 is a prefix for the calling process to claim ownership of this.

    private const byte PipeSuccessResponseCode = 0x01; // 01 is the response we send at the end of a A2 request, like a success return code. 

    private static void StartPipeServer()
    {
        var failureCount = 0;
        string lastPlace = "";
        while (_isNamedPipeRunning)
        {
            try
            {
                //Log($"Named pipe {FpsLimiterPipeName} WaitForConnection()");
                _namedPipeServerStream.WaitForConnection();

                using (var reader = new BinaryReader(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                using (var writer = new BinaryWriter(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    //lastPlace = "ReadByte directionByte";
                    var directionByte = reader.ReadByte();
                    switch (directionByte)
                    {
                        case PipeFireAndForgetDirection:
                            var cmdByte = reader.ReadByte();
                            switch (cmdByte)
                            {
                                case PipeSetFocusedCommand:
                                    SetOurWindowInFocus(FocusType.Foreground);
                                    // Log($"PipeSetFocusedCommand processed");
                                    break;
                                case PipePredictFocusCommand:
                                    SetOurWindowInFocus(FocusType.Predicted);
                                    _predictedFocusTimeoutMs = reader.ReadInt32();
                                    // Log($"PipePredictFocusCommand processed");
                                    break;
                            }

                            break;
                        case PipeUpdateDirection:
                            //lastPlace = "ReadByte PipeFpsPrefixByteFocused";
                            var firstUpdateByte = reader.ReadByte();
                            switch (firstUpdateByte) 
                            {
                                case PipeTakeOwnershipCommand:
                                    _ownerProcessId = reader.ReadInt32();
                                    break;
                                
                                case PipeFpsPrefixByteFocused:
                                    if (reader.ReadByte() == PipeFpsPrefixByteFocused)
                                    {
                                        // since we read the 0xF1 we know the next 4 bytes are going to be an 32 bit int.
                                        //lastPlace = "ReadInt32 newTargetFpsFocus";
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

                                    //lastPlace = "ReadByte PipeFpsPrefixByteBackground";
                                    if (reader.ReadByte() == PipeFpsPrefixByteBackground)
                                    {
                                        // since we read the 0xF2 we know the next 4 bytes are going to be an 32 bit int.
                                        //lastPlace = "ReadInt32 newTargetFpsBackground";
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

                                    //lastPlace = "ReadByte PipeFpsPrefixBytePredict";
                                    if (reader.ReadByte() == PipeFpsPrefixBytePredict)
                                    {
                                        // since we read the 0xF1 we know the next 4 bytes are going to be an 32 bit int.
                                        //lastPlace = "ReadInt32 newTargetFpsPredict";
                                        var newTargetFpsPredict = reader.ReadInt32();

                                        if (newTargetFpsPredict < 1 || newTargetFpsPredict > 1000)
                                        {
                                            // anything that might look like it's just unthrottled, we will turn off the throttle.
                                            _targetFpsInPredictFocus = 0;
                                            _perFrameTargetMsInPredictFocus = 0;
                                        }
                                        else
                                        {
                                            _targetFpsInPredictFocus = newTargetFpsPredict;
                                            _perFrameTargetMsInPredictFocus = 1000.0 / newTargetFpsPredict;
                                        }
                                    }

                                    // If both focus and background are 0, then disable the whole throttling.
                                    _isFpsThrottleActive = _perFrameTargetMsInBackground + _perFrameTargetMsInFocus > 0;
                                    
                                    break;
                            }
                            
                            //lastPlace = "Write PipeSuccessResponseCode Setting";
                            writer.Write(PipeSuccessResponseCode);

                            //Log($"PipeUpdateDirection set to FpsInFocus: {_targetFpsInFocus} FpsInBackground: {_targetFpsInBackground}");

                            break;

                        case PipeQueryDirection:
                            //lastPlace = "ReadByte Getting";
                            var queryType = reader.ReadByte(); // Reserved for future instruction if we ever want to do more than query the set throttles.
                            if (queryType == PipePingRequestCode)
                            {
                                writer.Write(PipeSuccessResponseCode);
                                //Log($"PipeQueryDirection received ping request.");
                            }
                            else
                            {
                                //lastPlace = "Write PipeFpsPrefixByteFocused";
                                writer.Write(PipeFpsPrefixByteFocused);
                                //lastPlace = "Write _targetFpsInFocus";
                                writer.Write(_targetFpsInFocus);
                                //lastPlace = "Write PipeFpsPrefixByteBackground";
                                writer.Write(PipeFpsPrefixByteBackground);
                                //lastPlace = "Write _targetFpsInBackground";
                                writer.Write(_targetFpsInBackground);
                                //lastPlace = "Write PipeFpsPrefixBytePredict";
                                writer.Write(PipeFpsPrefixBytePredict);
                                //lastPlace = "Write _targetFpsInPredictFocus";
                                writer.Write(_targetFpsInPredictFocus);
                                writer.Write(PipeSuccessResponseCode);
                            }
                            
                            break;
                    }

                    //lastPlace = "Writer Flush";
                    writer.Flush();
                    //lastPlace = "Writer Close";
                    writer.Close(); ;
                    //lastPlace = "Reader Close";
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
                Log($"Named pipe {_namedPipeServerStream} encountered error: {ex}");
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
    
    [DllImport("user32.dll")]
    static extern bool MessageBeep(uint uType);
    const uint MB_OK = 0x00000000; // just a ding
    const uint MB_ICONERROR = 0x00000010; // another noise for testing

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static bool IsProcessRunning(int pid)
    {
        IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (processHandle != IntPtr.Zero)
        {
            CloseHandle(processHandle);
            return true;
        }
        return false;
    }
}