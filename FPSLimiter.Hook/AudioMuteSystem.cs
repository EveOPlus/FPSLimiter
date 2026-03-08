using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FPSLimiter.Hook.DebugLogger;
using static FPSLimiter.Hook.NativeMethods;

namespace FPSLimiter.Hook;

internal static unsafe class AudioMuteSystem
{
    private const int PAGE_EXECUTE_READWRITE = 0x40;
    private const uint STATUS_GUARD_PAGE_VIOLATION = 0x80000001;
    private const uint HARDWARE_BREAKPOINT = 0x80000004;
    private const uint TRAP_FLAG = 0x100;
    
    private static delegate* unmanaged[Cdecl]<int, uint, int, int, void> _executeAction; 

    private static readonly uint[] _mutedIds = new uint[1024];
    private static int _mutedCount = 0;
    private static readonly Lock _muteLock = new();

    private static IntPtr _postEventAddr;
    private static IntPtr _vehHandle;

    internal static void InstallAudioMonitor()
    {
        var audio2Module = NativeMethods.GetModuleHandle("_audio2.dll");

        _postEventAddr = NativeMethods.GetProcAddress(audio2Module,
            "?PostEvent@SoundEngine@AK@@YAII_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");

        if (_postEventAddr == IntPtr.Zero)
        {
            Error($"[{nameof(InstallAudioMonitor)}] Could not resolve PostEvent");
            return;
        }

        _vehHandle = NativeMethods.AddVectoredExceptionHandler(1, &VectoredHandler);
        if (_vehHandle == IntPtr.Zero)
        {
            return;
        }
        
        Arm();

        IntPtr executeActionOnPlayingIDAddr = NativeMethods.GetProcAddress(audio2Module, "?ExecuteActionOnPlayingID@SoundEngine@AK@@YAXW4AkActionOnEventType@12@IHW4AkCurveInterpolation@@@Z");
        _executeAction = (delegate* unmanaged[Cdecl]<int, uint, int, int, void>)executeActionOnPlayingIDAddr;
        if (_executeAction == null)
        {
            Error($"[{nameof(InstallAudioMonitor)}] Could not resolve ExecuteActionOnPlayingID");
        }
        else
        {
            Info("Located ExecuteActionOnPlayingID");
        }
    }

    private static void Arm()
    {
        uint oldProtect; // FIX 3: Always provide a real variable for the output
        NativeMethods.VirtualProtect(_postEventAddr, 1, 0x40 | 0x100, out oldProtect);
    }

    [ThreadStatic] private static uint _currentEventId;
    [ThreadStatic] private static ulong _currentGameObjectId;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int VectoredHandler(EXCEPTION_POINTERS* pExp)
    {
        var code = pExp->ExceptionRecord->ExceptionCode;
        var ctx = pExp->ContextRecord;

        if (code == STATUS_GUARD_PAGE_VIOLATION)
        {
            if (ctx->Rip == (ulong)_postEventAddr)
            {
                // Keep track of the eventId for when we hit the response breakpoint.
                _currentEventId = (uint)ctx->Rcx;
                _currentGameObjectId = (uint)ctx->Rdx;

                if (ctx->Rsp != 0)
                {
                    // Set Return Breakpoint
                    ctx->Dr0 = *(ulong*)ctx->Rsp;
                    ctx->Dr7 |= 0x1UL;
                }
            }

            ctx->EFlags |= TRAP_FLAG; // Force Single Step to Re-arm
            return -1;
        }

        if (code == HARDWARE_BREAKPOINT)
        {
            // Check if this specific step was our Hardware Breakpoint (Return)
            if ((ctx->Dr6 & 0x1UL) != 0)
            {
                uint playingId = (uint)ctx->Rax;

                // Cleanup HW registers
                ctx->Dr0 = 0;
                ctx->Dr7 &= ~0x1UL;
                ctx->Dr6 &= ~0x1UL;

                if (IsMuted(_currentEventId) && playingId != 0)
                {
                    _executeAction((int)AkActionOnEventType.Stop, playingId, 0, (int)AkCurveInterpolation.Constant);
                    Info($"Audio Stop Actioned on PlayingID: {playingId} EventID: {_currentEventId}");
                }

                AudioLog.Add(_currentEventId, _currentGameObjectId);
            }

            // Always re-arm the Page Guard on EVERY single step
            Arm();

            return -1;
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsMuted(uint eventId)
    {
        if (_mutedCount == 0)
        {
            return false;
        }
        
        return Array.BinarySearch(_mutedIds, 0, _mutedCount, eventId) >= 0;
    }

    internal static void AddMutedId(uint id)
    {
        lock (_muteLock)
        {
            if (_mutedCount >= _mutedIds.Length || IsMuted(id))
            {
                return;
            } 
            
            _mutedIds[_mutedCount++] = id;
            Array.Sort(_mutedIds, 0, _mutedCount);
        }
    }

    internal static void RemoveMutedId(uint id)
    {
        lock (_muteLock)
        {
            int index = Array.BinarySearch(_mutedIds, 0, _mutedCount, id);
            if (index < 0)
            {
                return;
            }

            // Shift elements left to fill the gap (Native-speed move)
            if (index < _mutedCount - 1)
            {
                Array.Copy(_mutedIds, index + 1, _mutedIds, index, _mutedCount - index - 1);
            }

            _mutedCount--;
            _mutedIds[_mutedCount] = 0; // Clear stale entry
        }
    }

    internal static void ClearMutedIds()
    {
        lock (_muteLock)
        {
            Array.Clear(_mutedIds, 0, _mutedIds.Length);
            _mutedCount = 0;
        }
    }

    internal static List<uint> GetMutedIds()
    {
        lock (_muteLock)
        {
            return _mutedIds.Take(_mutedCount).ToList();
        }
    }
    
    internal enum AkActionOnEventType : int
    {
        Stop = 1,
        Pause = 2,
        Resume = 3,
        Break = 4,
        ReleaseEnvelope = 5,
        Mute = 6,
        Unmute = 7
    }

    internal enum AkCurveInterpolation : int
    {
        Log3 = 0, // Logarithmic (Curving slowly at first, then fast)
        Sine = 1, // Sine wave (Smooth start and end)
        Log1 = 2, // Logarithmic (Faster initial drop than Log3)
        InvSCurve = 3, // Inversed S-Curve
        Linear = 4, // Linear (Default straight-line transition)
        SCurve = 5, // S-Curve (Smooth transition)
        Exp1 = 6, // Exponential (Slow drop, then accelerates)
        SineRecip = 7, // Reciprocal of a sine curve
        Exp3 = 8, // Exponential (Steepest acceleration)
        Constant = 9  // Constant (Instant jump, no interpolation)
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct LoggedAudioEvent
{
    public uint EventID;
    public ulong GameObjectID;
    public long Timestamp;
}

internal static unsafe class AudioLog
{
    private const int LogSize = 128;
    private const int LogMask = LogSize - 1;
    private static readonly LoggedAudioEvent[] _eventHistory = new LoggedAudioEvent[LogSize];
    private static int _globalSequenceCount = 0;

    internal static void Add(uint eventID, ulong gameObjectID)
    {
        try
        {
            int sequence = Interlocked.Increment(ref _globalSequenceCount);
            int index = sequence & LogMask;

            ref var entry = ref _eventHistory[index];
            entry.EventID = eventID;
            entry.GameObjectID = gameObjectID;
            entry.Timestamp = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            Error(ex, $"{nameof(AudioLog)}.{nameof(Add)}");
        }
    }

    public static List<LoggedAudioEvent> GetOrderedEventHistory()
    {
        return _eventHistory
            .Where(e => e.EventID != 0)
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    public static void ClearEventHistory()
    {
        Interlocked.Exchange(ref _globalSequenceCount, 0);
        Array.Clear(_eventHistory, 0, LogSize);
    }
}