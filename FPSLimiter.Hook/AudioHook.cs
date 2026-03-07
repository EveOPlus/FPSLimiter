using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FPSLimiter.Hook.DebugLogger;

namespace FPSLimiter.Hook;

internal static unsafe class AudioHook
{
    private static delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint> _originalPostEvent;

    private static delegate* unmanaged[Cdecl]<int, uint, int, int, void> _executeAction; 

    private const uint jump_gates_start_play = 3689163958;
    private const uint jump_gates_exit_play = 1537508544;
    private const uint jump_gates_lightning_play = 1768044352;
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const int PAGE_EXECUTE_READWRITE = 0x40;

    private static readonly uint[] _mutedIds = new uint[1024];
    private static int _mutedCount = 0;
    private static readonly Lock _muteLock = new();

    internal static void InstallAudioHooks()
    {
        var audio2Module = NativeMethods.GetModuleHandle("_audio2.dll");

        IntPtr postEventAddr = NativeMethods.GetProcAddress(audio2Module,
            "?PostEvent@SoundEngine@AK@@YAII_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");

        if (postEventAddr == IntPtr.Zero)
        {
            Error($"[{nameof(InstallAudioHooks)}] Could not resolve PostEvent");
            return;
        }

        ReadOnlySpan<byte> actualBytes = new ReadOnlySpan<byte>((void*)postEventAddr, 16);
        ReadOnlySpan<byte> expectedBytes = [0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x4C];
        if (!actualBytes.SequenceEqual(expectedBytes))
        {
            var prologueHex = FormatHex(actualBytes);
            Error($"[{nameof(InstallAudioHooks)}] Unexpected prologue for PostEvent address {prologueHex}.");
            return;
        }

        const int STOLEN_BYTES = 7; // mov rax, rsp (3) (the bytes we need) + mov [rax+8], rbx (4) (the bytes to make up 5 bytes we need with 2 spares.)

        IntPtr relay = AllocateNear(postEventAddr);

        // Trampoline
        byte* r = (byte*)relay;
        System.Buffer.MemoryCopy((void*)postEventAddr, r, STOLEN_BYTES, STOLEN_BYTES);
        WriteAbsoluteJump(r + STOLEN_BYTES, postEventAddr + STOLEN_BYTES);
        _originalPostEvent = (delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)r;

        // JMP to our hook
        IntPtr hookPtr = (IntPtr)(delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)&HookedPostEvent;
        WriteAbsoluteJump(r + 64, hookPtr);

        // Patch
        if (NativeMethods.VirtualProtect(postEventAddr, STOLEN_BYTES, PAGE_EXECUTE_READWRITE, out uint old))
        {
            int relOffset = (int)((long)(r + 64) - (long)postEventAddr - 5);

            byte* pAk = (byte*)postEventAddr;
            pAk[0] = 0xE9; // JMP relative
            *(int*)(pAk + 1) = relOffset;

            // no-op the extra bytes
            pAk[5] = 0x90;
            pAk[6] = 0x90;

            NativeMethods.VirtualProtect(postEventAddr, STOLEN_BYTES, old, out _);
            Info($"Hooked into PostEvent");
        }

        IntPtr executeActionOnPlayingIDAddr = NativeMethods.GetProcAddress(audio2Module, "?ExecuteActionOnPlayingID@SoundEngine@AK@@YAXW4AkActionOnEventType@12@IHW4AkCurveInterpolation@@@Z");
        _executeAction = (delegate* unmanaged[Cdecl]<int, uint, int, int, void>)executeActionOnPlayingIDAddr;
        if (_executeAction == null)
        {
            Error($"[{nameof(InstallAudioHooks)}] Could not resolve ExecuteActionOnPlayingID");
        }
        else
        {
            Info($"Located ExecuteActionOnPlayingID");
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
                IntPtr allocated = NativeMethods.VirtualAlloc((IntPtr)addr, (UIntPtr)1024, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (allocated != IntPtr.Zero) return allocated;
            }
        }
        return IntPtr.Zero;
    }

    private static unsafe void WriteAbsoluteJump(byte* address, IntPtr target)
    {
        // FF 25 00 00 00 00 = JMP [RIP+0] 
        // This tells the CPU to read the 8 bytes immediately following this instruction
        address[0] = 0xFF;
        address[1] = 0x25;
        *(uint*)(address + 2) = 0x00000000; // Offset is 0

        // Write the 64-bit target address starting at address + 6
        *(IntPtr*)(address + 6) = target;
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
    
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HookedPostEvent(uint eventID, ulong gameObjectID, uint uFlags, IntPtr pfnCallback, IntPtr pCookie, uint cExternals, IntPtr pExternalSources, uint playingID)
    {
        AudioLog.Add(eventID, gameObjectID);
        var resultId = _originalPostEvent(eventID, gameObjectID, uFlags, pfnCallback, pCookie, cExternals, pExternalSources, playingID);
        
        if (IsMuted(eventID) && resultId != 0)
        {
            _executeAction((int)AkActionOnEventType.Stop, resultId, 0, (int)AkCurveInterpolation.Constant);
            Info($"Audio Stop Actioned on PlayingID: {resultId} EventID: {eventID}");
        }

        return resultId;
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