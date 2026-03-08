using SharpDX;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FPSLimiter.Hook.DebugLogger;
using static FPSLimiter.Hook.NativeMethods;

namespace FPSLimiter.Hook;

internal static unsafe class MuteSystem
{
    private static delegate* unmanaged[Cdecl]<int, uint, int, int, void> _executeAction; 
    
    private static readonly uint[] _mutedIds = new uint[1024];
    private static int _mutedCount = 0;
    private static readonly Lock _muteLock = new();

    private static IntPtr _postEventAddr;
    private static IntPtr _vehHandle;

    internal static void InstallAudioMonitor()
    {
        try
        {
            AddMutedId(3689163958);
            AddMutedId(1537508544);
            AddMutedId(1768044352);
            AddMutedId(2377891014);
            AddMutedId(3090840445);
            
            var audio2Module = NativeMethods.GetModuleHandle("_audio2.dll");

            IntPtr postEventAddr = NativeMethods.GetProcAddress(audio2Module,
                "?PostEvent@SoundEngine@AK@@YAII_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");

            if (postEventAddr == IntPtr.Zero)
            {
                Error($"[{nameof(InstallAudioMonitor)}] Could not resolve PostEvent");
                return;
            }

            _postEventAddr = postEventAddr;

            _vehHandle = NativeMethods.AddVectoredExceptionHandler(1, &VectoredHandler);
            if (_vehHandle == IntPtr.Zero) return;
            Arm();

            IntPtr executeActionOnPlayingIDAddr = NativeMethods.GetProcAddress(audio2Module, "?ExecuteActionOnPlayingID@SoundEngine@AK@@YAXW4AkActionOnEventType@12@IHW4AkCurveInterpolation@@@Z");
            _executeAction = (delegate* unmanaged[Cdecl]<int, uint, int, int, void>)executeActionOnPlayingIDAddr;
            if (_executeAction == null)
            {
                Error($"[{nameof(InstallAudioMonitor)}] Could not resolve ExecuteActionOnPlayingID");
            }
            else
            {
                Info($"Located ExecuteActionOnPlayingID");
            }
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private static void Arm()
    {
        uint oldProtect; // FIX 3: Always provide a real variable for the output
        NativeMethods.VirtualProtect(_postEventAddr, 1, 0x40 | 0x100, out oldProtect);
    }

    [ThreadStatic] private static uint _currentEventId;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int VectoredHandler(EXCEPTION_POINTERS* pExp)
    {
        uint code = pExp->ExceptionRecord->ExceptionCode;
        CONTEXT64* ctx = pExp->ContextRecord;

        // --- CASE 1: Guard Page Hit (Entry) ---
        if (code == 0x80000001)
        {
            if (ctx->Rip == (ulong)_postEventAddr)
            {
                _currentEventId = (uint)ctx->Rcx;

                if (ctx->Rsp != 0)
                {
                    // Set Return Breakpoint
                    ctx->Dr0 = *(ulong*)ctx->Rsp;
                    ctx->Dr7 |= 0x1UL;
                }
            }

            ctx->EFlags |= 0x100; // Force Single Step to Re-arm
            return -1;
        }

        // --- CASE 2 & 3: Single Step (Re-arm + Return Capture) ---
        if (code == 0x80000004)
        {
            // Check if this specific step was our Hardware Breakpoint (Return)
            if ((ctx->Dr6 & 0x1UL) != 0)
            {
                uint playingId = (uint)ctx->Rax;
                //Info($"Event: {_currentEventId} -> Result: {playingId}");
                
                if (IsMuted(_currentEventId) && playingId != 0)
                {
                    _executeAction((int)AkActionOnEventType.Stop, playingId, 0, (int)AkCurveInterpolation.Constant);
                    Info($"Audio Stop Actioned on PlayingID: {playingId} EventID: {_currentEventId}");
                }

                // Cleanup HW registers
                ctx->Dr0 = 0;
                ctx->Dr7 &= ~0x1UL;
                ctx->Dr6 &= ~0x1UL;
            }

            // CRITICAL: Always re-arm the Page Guard on EVERY single step
            // This ensures the "tripwire" is put back even if we just hit a return.
            Arm();

            return -1;
        }

        return 0;
    }
    
    private const ulong DR7_ENTRY_MASK = 0x1 | 0x2 | 0x100 | 0x200;
    private static void ApplyBreakpointToAllThreads(IntPtr address)
    {
        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        using var currentProcess = Process.GetCurrentProcess();

        foreach (ProcessThread thread in currentProcess.Threads)
        {
            uint tid = (uint)thread.Id;
            if (tid == currentThreadId) continue;

            IntPtr hThread = NativeMethods.OpenThread(0x1A, false, tid);
            if (hThread == IntPtr.Zero) continue;

            bool suspended = false;
            try
            {
                // Suspend returns the previous suspend count, 0xFFFFFFFF on error
                if (NativeMethods.SuspendThread(hThread) != uint.MaxValue)
                {
                    suspended = true;
                    
                    CONTEXT64 ctx = default;
                    Unsafe.InitBlock(&ctx, 0, (uint)sizeof(CONTEXT64));

                    ctx.ContextFlags = 0x100010; // CONTEXT_AMD64 | CONTEXT_DEBUG_REGISTERS


                    // 0x10001F = CONTEXT_AMD64 | CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS | CONTEXT_DEBUG_REGISTERS
                    ctx.ContextFlags = 0x10001F;

                    if (NativeMethods.GetThreadContext(hThread, &ctx))
                    {
                        ctx.Dr0 = (ulong)address;
                        ctx.Dr7 = DR7_ENTRY_MASK;

                        NativeMethods.SetThreadContext(hThread, &ctx);
                    }
                }
            }
            finally
            {
                if (suspended)
                {
                    NativeMethods.ResumeThread(hThread);
                }
                NativeMethods.CloseHandle(hThread);
            }
        }
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
    
    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;

        return string.Create((bytes.Length * 3) - 1, bytes, (chars, b) =>
        {
            for (int i = 0; i < b.Length; i++)
            {
                int pos = i * 3;
                b[i].TryFormat(chars.Slice(pos, 2), out _, "X2");

                if (i < b.Length - 1)
                {
                    chars[pos + 2] = ' ';
                }
            }
        });
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