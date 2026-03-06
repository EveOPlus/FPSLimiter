using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Net.Mime.MediaTypeNames;

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
        _log("Started Initialization");
        
        try
        {
            InstallPostEventHook();
        }
        catch (Exception ex)
        {
            _log(ex.ToString());

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

    // Ordinal 54 Signature
    private static delegate* unmanaged[Cdecl]<int, uint, int, int, void> _executeAction;

    // demangled exports
    // 178   B1 0014A2D0 unsigned int AK::SoundEngine::PostEvent(unsigned int,unsigned __int64,unsigned int,void (__cdecl*)(enum AkCallbackType,struct AkCallbackInfo * __ptr64),void * __ptr64,unsigned int,struct AkExternalSourceInfo * __ptr64,unsigned int)
    // 179   B2 0014A5D0 unsigned int AK::SoundEngine::PostEvent(char const * __ptr64,unsigned __int64,unsigned int,void (__cdecl*)(enum AkCallbackType,struct AkCallbackInfo * __ptr64),void * __ptr64,unsigned int,struct AkExternalSourceInfo * __ptr64,unsigned int)
    // 180   B3 0014A700 unsigned int AK::SoundEngine::PostEvent(wchar_t const * __ptr64,unsigned __int64,unsigned int,void (__cdecl*)(enum AkCallbackType,struct AkCallbackInfo * __ptr64),void * __ptr64,unsigned int,struct AkExternalSourceInfo * __ptr64,unsigned int)

    // Export 178 (ID-based)
    private static delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint> _originalPostEventIdBased;

    //// Export 179 (Standard String / char*)
    //private static delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint> _originalPostEventStringBased;

    //// Export 180 (Wide String / wchar_t*)
    //private static delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint> _originalPostEventWStringBased;


    private const uint msg_MenuActivate_play = 1668343618;
    private const uint jump_gates_start_play = 3689163958;
    private const uint jump_gates_exit_play = 1537508544;
    private const uint jump_gates_lightning_play = 1768044352;

    private const uint ship_engine_S_warpdrive_1st_on = 1659125884;
    private const uint ship_engine_S_booster_1st_on = 1749375730;
    
    private const uint worldobject_jumpgate_state_two_play = 2136228966;



    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HookedPostEvent(uint eventID, ulong gameObjectID, uint uFlags, IntPtr pfnCallback, IntPtr pCookie, uint cExternals, IntPtr pExternalSources, uint playingID)
    {
        _log($"eventID {eventID} || gameObjectID {gameObjectID} || uFlags {uFlags} || pfnCallback {pfnCallback} || pCookie {pCookie} || cExternals {cExternals} || pExternalSources {pExternalSources} || playingID {playingID}");
        var resultId = _originalPostEventIdBased(eventID, gameObjectID, uFlags, pfnCallback, pCookie, cExternals, pExternalSources, playingID);
        if ((eventID == jump_gates_start_play || eventID == jump_gates_exit_play || eventID == jump_gates_lightning_play) && resultId != 0)
        {
            _executeAction((int)AkActionOnEventType.Stop, resultId, 0, (int)AkCurveInterpolation.Constant);

            _log($"[Muted] Action applied to PlayingID: {resultId} (Event: {eventID})");
        }


        return resultId;
    }

    // Returns AKRESULT (int). Parameters: GroupID (uint), StateID (uint)
    private static delegate* unmanaged[Cdecl]<uint, uint, int> _originalSetState;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HookedSetState(uint in_stateGroup, uint in_stateID)
    {
            _log($"[State Log] Group: {in_stateGroup} | Requested ID: {in_stateID}");

            
            // Block these two specific groups entirely
        if (in_stateGroup == 1360507279 || in_stateGroup == 2164173825)
        {
            _log($"[State Blocked] Group: {in_stateGroup} | Requested ID: {in_stateID}");

            return 1;
        }

        // Let everything else through (UI, Combat, Music states)
        return _originalSetState(in_stateGroup, in_stateID);
    }
    
    
    

    //[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    //private static uint HookedPostEventSb(IntPtr pszEventName, ulong gameObjectID, uint uFlags, IntPtr pfnCallback, IntPtr pCookie, uint cExternals, IntPtr pExternalSources, uint playingID)
    //{
    //    string eventName = Marshal.PtrToStringAnsi(pszEventName);

    //    _log($"[String Based] pszEventName {eventName} || gameObjectID {gameObjectID} || uFlags {uFlags} || pfnCallback {pfnCallback} || pCookie {pCookie} || cExternals {cExternals} || pExternalSources {pExternalSources} || playingID {playingID}");
    //    var resultId = _originalPostEventStringBased(pszEventName, gameObjectID, uFlags, pfnCallback, pCookie, cExternals, pExternalSources, playingID);

    //    _executeAction((int)AkActionOnEventType.Mute, resultId, 0, (int)AkCurveInterpolation.Constant);

    //    _log($"[Muted] Action applied to PlayingID: {resultId} (Event: {eventName})");

    //    return resultId;
    //}

    //[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    //private static uint HookedPostEventWSb(IntPtr pszEventName, ulong gameObjectID, uint uFlags, IntPtr pfnCallback, IntPtr pCookie, uint cExternals, IntPtr pExternalSources, uint playingID)
    //{
    //    string eventName = Marshal.PtrToStringUni(pszEventName);

    //    _log($"[Wide String Based] pszEventName {eventName} || gameObjectID {gameObjectID} || uFlags {uFlags} || pfnCallback {pfnCallback} || pCookie {pCookie} || cExternals {cExternals} || pExternalSources {pExternalSources} || playingID {playingID}");
    //    var resultId = _originalPostEventWStringBased(pszEventName, gameObjectID, uFlags, pfnCallback, pCookie, cExternals, pExternalSources, playingID);

    //    _executeAction((int)AkActionOnEventType.Mute, resultId, 0, (int)AkCurveInterpolation.Constant);

    //    _log($"[Muted] Action applied to PlayingID: {resultId} (Event: {eventName})");

    //    return resultId;
    //}

    // Wwise Action Types
    public enum AkActionOnEventType : int
    {
        Stop = 1,
        Pause = 2,
        Resume = 3,
        Break = 4,
        ReleaseEnvelope = 5,
        Mute = 6,
        Unmute = 7
    }

    public enum AkCurveInterpolation : int
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












    // Parameters: AkPlayingID (uint), TransitionDuration (int), Curve (int)
    private static delegate* unmanaged[Cdecl]<uint, int, int, int> _originalDynamicPlay;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HookedDynamicPlay(uint playingID, int uTransitionDuration, int eFadeCurve)
    {
        _log($"[Play DynamicSequence] playingID {playingID}");

        // To mute this, we use the same ExecuteActionOnPlayingID (Mute = 6) 
        // because DynamicSequence handles are also PlayingIDs!
        //if (_executeAction != null)
        //{
        //    _executeAction(6, playingID, 0, 9);
        //}

        return _originalDynamicPlay(playingID, uTransitionDuration, eFadeCurve);
    }

    //private static void InstallPostEventHook()
    //{
    //    // 1. Find PostEvent 178
    //    _log("Locating _audio2.dll Play DynamicSequence");
    //    var audio2Module = GetModuleHandle("_audio2.dll");



    //    IntPtr playAddr = GetProcAddress(audio2Module,
    //        "?Play@DynamicSequence@SoundEngine@AK@@YA?AW4AKRESULT@@IHW4AkCurveInterpolation@@@Z");
    //    _log($"GetProcAddress result for Play DynamicSequence {playAddr}");

    //    byte* p = (byte*)playAddr;
    //    string hex = "";
    //    for (int i = 0; i < 16; i++)
    //    {
    //        hex += p[i].ToString("X2") + " ";
    //    }
    //    _log($"Play DynamicSequence Prologue Bytes: {hex}");

    //    const int STOLEN_BYTES = 5; // 48 89 74 24 10 — mov [rsp+10h], rsi (5 bytes)

    //    _log("Allocate Near");
    //    // 2. Allocate Relay within 2GB of AkSoundEngine.dll
    //    IntPtr relay = AllocateNear(playAddr);

    //    _log("Relay start");
    //    // 3. Setup Trampoline at relay start
    //    // [Stolen Bytes (7 bytes)] + [Absolute JMP back to akAddr + 5]
    //    byte* r = (byte*)relay;
    //    System.Buffer.MemoryCopy((void*)playAddr, r, STOLEN_BYTES, STOLEN_BYTES); // Steal exactly 5 bytes
    //    WriteAbsoluteJump(r + STOLEN_BYTES, playAddr + STOLEN_BYTES);
    //    _originalDynamicPlay = (delegate* unmanaged[Cdecl]<uint, int, int, int>)r;


    //    _log("set detour");
    //    // 4. Setup Detour at relay + 64 (JMP to your C# Hook)
    //    IntPtr hookPtr = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int, int>)&HookedDynamicPlay;
    //    WriteAbsoluteJump(r + 64, hookPtr);

    //    _log("write");
    //    // 5. Final 7-byte patch at AkSoundEngine.dll
    //    if (VirtualProtect(playAddr, (UIntPtr)STOLEN_BYTES, PAGE_EXECUTE_READWRITE, out uint old))
    //    {
    //        int relOffset = (int)((long)(r + 64) - (long)playAddr - 5);

    //        byte* pAk = (byte*)playAddr;
    //        pAk[0] = 0xE9; // JMP relative
    //        *(int*)(pAk + 1) = relOffset;

    //        VirtualProtect(playAddr, (UIntPtr)STOLEN_BYTES, old, out _);
    //    }




    //    IntPtr executeActionOnPlayingIDAddr = GetProcAddress(audio2Module, "?ExecuteActionOnPlayingID@SoundEngine@AK@@YAXW4AkActionOnEventType@12@IHW4AkCurveInterpolation@@@Z");
    //    _executeAction = (delegate* unmanaged[Cdecl]<int, uint, int, int, void>)executeActionOnPlayingIDAddr;
    //    if (_executeAction == null)
    //    {
    //        _log("Could not resolve ExecuteActionOnPlayingID");
    //    }
    //}


    private static void InstallPostEventHook()
    {
        // 1. Find PostEvent 178
        _log("Locating _audio2.dll PostEvent");
        var audio2Module = GetModuleHandle("_audio2.dll");



        IntPtr postEventAddr = GetProcAddress(audio2Module,
            "?PostEvent@SoundEngine@AK@@YAII_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");
        _log($"getprocaddress result for id based {postEventAddr}");

        byte* p = (byte*)postEventAddr;
        string hex = "";
        for (int i = 0; i < 16; i++)
        {
            hex += p[i].ToString("X2") + " ";
        }
        _log($"PostEvent Prologue Bytes: {hex}");

        const int STOLEN_BYTES = 7; // mov rax, rsp (3) (the bytes we need) + mov [rax+8], rbx (4) (the bytes to make up 5 bytes we need with 2 spares.)

        _log("Allocate Near");
        // 2. Allocate Relay within 2GB of AkSoundEngine.dll
        IntPtr relay = AllocateNear(postEventAddr);

        _log("Relay start");
        // 3. Setup Trampoline at relay start
        // [Stolen Bytes (7 bytes)] + [Absolute JMP back to akAddr + 5]
        byte* r = (byte*)relay;
        System.Buffer.MemoryCopy((void*)postEventAddr, r, STOLEN_BYTES, STOLEN_BYTES); // Steal exactly 5 bytes
        WriteAbsoluteJump(r + STOLEN_BYTES, postEventAddr + STOLEN_BYTES);
        _originalPostEventIdBased = (delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)r;

        _log("set detour");
        // 4. Setup Detour at relay + 64 (JMP to your C# Hook)
        IntPtr hookPtr = (IntPtr)(delegate* unmanaged[Cdecl]<uint, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)&HookedPostEvent;
        WriteAbsoluteJump(r + 64, hookPtr);

        _log("write");
        // 5. Final 7-byte patch at AkSoundEngine.dll
        if (VirtualProtect(postEventAddr, STOLEN_BYTES, PAGE_EXECUTE_READWRITE, out uint old))
        {
            int relOffset = (int)((long)(r + 64) - (long)postEventAddr - 5);

            byte* pAk = (byte*)postEventAddr;
            pAk[0] = 0xE9; // JMP relative
            *(int*)(pAk + 1) = relOffset;

            // IMPORTANT: Fill the 2 "leftover" bytes with NOPs (0x90)
            // Since we stole 7 bytes but only used 5 for the JMP.
            pAk[5] = 0x90;
            pAk[6] = 0x90;

            VirtualProtect(postEventAddr, STOLEN_BYTES, old, out _);
        }





        IntPtr setStateAddr = GetProcAddress(audio2Module,
            "?SetState@SoundEngine@AK@@YA?AW4AKRESULT@@II@Z");
        _log($"getprocaddress result for SetState {setStateAddr}");
        
        byte* p2 = (byte*)setStateAddr;
        string hex2 = "";
        for (int i = 0; i < 16; i++)
        {
            hex2 += p2[i].ToString("X2") + " ";
        }
        _log($"setState Prologue Bytes: {hex2}");

        const int STOLEN_BYTES2 = 5; // 48 89 5C 24 08 — mov [rsp+8], rbx (Exactly 5 bytes)

        _log("Allocate Near");
        // 2. Allocate Relay within 2GB of AkSoundEngine.dll
        IntPtr relay2 = AllocateNear(setStateAddr);
        
        _log("Relay start");
        // 3. Setup Trampoline at relay start
        // [Stolen Bytes (5 bytes)] + [Absolute JMP back to akAddr + 5]
        byte* r2 = (byte*)relay2;
        System.Buffer.MemoryCopy((void*)setStateAddr, r2, STOLEN_BYTES2, STOLEN_BYTES2); // Steal exactly 5 bytes
        WriteAbsoluteJump(r2 + STOLEN_BYTES2, setStateAddr + STOLEN_BYTES2);
        _originalSetState = (delegate* unmanaged[Cdecl]<uint, uint, int>)r2;
        
        _log("set detour");
        // 4. Setup Detour at relay + 64 (JMP to your C# Hook)
        IntPtr hookPtr2 = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&HookedSetState;
        WriteAbsoluteJump(r2 + 64, hookPtr2);
        
        _log("write");
        // 5. Final 7-byte patch at AkSoundEngine.dll
        if (VirtualProtect(setStateAddr, STOLEN_BYTES2, PAGE_EXECUTE_READWRITE, out uint old2))
        {
            int relOffset = (int)((long)(r2 + 64) - (long)setStateAddr - 5);
        
            byte* pAk2 = (byte*)setStateAddr;
            pAk2[0] = 0xE9; // JMP relative
            *(int*)(pAk2 + 1) = relOffset;
        
            VirtualProtect(setStateAddr, STOLEN_BYTES2, old2, out _);
        }






        //        const int STOLEN_BYTES2 = 5;


        //        IntPtr postEventAddrS = GetProcAddress(audio2Module,
        //    "?PostEvent@SoundEngine@AK@@YAIPEBD_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");
        //        _log($"getprocaddress result for string based {postEventAddrS}");

        //        byte* pS = (byte*)postEventAddrS;
        //        string hex2 = "";
        //        for (int i = 0; i < 16; i++)
        //        {
        //            hex2 += pS[i].ToString("X2") + " ";
        //        }
        //        _log($"PostEvent String Based Prologue Bytes: {hex2}");

        //        _log("Allocate Near");
        //        // 2. Allocate Relay within 2GB of AkSoundEngine.dll
        //        IntPtr relay2 = AllocateNear(postEventAddrS);

        //        _log("Relay start");
        //        // 3. Setup Trampoline at relay start
        //        // [Stolen Bytes (7 bytes)] + [Absolute JMP back to akAddr + 5]
        //        byte* r2 = (byte*)relay2;
        //        System.Buffer.MemoryCopy((void*)postEventAddrS, r2, STOLEN_BYTES2, STOLEN_BYTES2); // Steal exactly 5 bytes
        //        WriteAbsoluteJump(r2 + STOLEN_BYTES2, postEventAddrS + STOLEN_BYTES2);
        //        _originalPostEventStringBased = (delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)r2;

        //        _log("set detour");
        //        // 4. Setup Detour at relay + 64 (JMP to your C# Hook)
        //        IntPtr hookPtr2 = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)&HookedPostEventSb;
        //        WriteAbsoluteJump(r2 + 64, hookPtr2);

        //        _log("write");
        //        // 5. Final 7-byte patch at AkSoundEngine.dll
        //        if (VirtualProtect(postEventAddrS, STOLEN_BYTES2, PAGE_EXECUTE_READWRITE, out uint old2))
        //        {
        //            int relOffset2 = (int)((long)(r2 + 64) - (long)postEventAddrS - 5);

        //            byte* pAk2 = (byte*)postEventAddrS;
        //            pAk2[0] = 0xE9; // JMP relative
        //            *(int*)(pAk2 + 1) = relOffset2;

        //            VirtualProtect(postEventAddrS, STOLEN_BYTES2, old2, out _);
        //        }








        //        IntPtr postEventAddrWS = GetProcAddress(audio2Module,
        //"?PostEvent@SoundEngine@AK@@YAIPEB_W_KIP6AXW4AkCallbackType@@PEAUAkCallbackInfo@@@ZPEAXIPEAUAkExternalSourceInfo@@I@Z");
        //        _log($"getprocaddress result for wide string based {postEventAddrWS}");

        //        byte* pWS = (byte*)postEventAddrWS;
        //        string hex3 = "";
        //        for (int i = 0; i < 16; i++)
        //        {
        //            hex3 += pWS[i].ToString("X2") + " ";
        //        }
        //        _log($"PostEvent Wide String Based Prologue Bytes: {hex3}");

        //        _log("Allocate Near");
        //        // 2. Allocate Relay within 2GB of AkSoundEngine.dll
        //        IntPtr relay3 = AllocateNear(postEventAddrWS);

        //        _log("Relay start");
        //        // 3. Setup Trampoline at relay start
        //        // [Stolen Bytes (7 bytes)] + [Absolute JMP back to akAddr + 5]
        //        byte* r3 = (byte*)relay3;
        //        System.Buffer.MemoryCopy((void*)postEventAddrWS, r3, STOLEN_BYTES2, STOLEN_BYTES2); // Steal exactly 5 bytes
        //        WriteAbsoluteJump(r3 + STOLEN_BYTES2, postEventAddrWS + STOLEN_BYTES2);
        //        _originalPostEventWStringBased = (delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)r3;

        //        _log("set detour");
        //        // 4. Setup Detour at relay + 64 (JMP to your C# Hook)
        //        IntPtr hookPtr3 = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, uint, IntPtr, IntPtr, uint, IntPtr, uint, uint>)&HookedPostEventWSb;
        //        WriteAbsoluteJump(r3 + 64, hookPtr3);

        //        _log("write");
        //        // 5. Final 7-byte patch at AkSoundEngine.dll
        //        if (VirtualProtect(postEventAddrWS, STOLEN_BYTES2, PAGE_EXECUTE_READWRITE, out uint old3))
        //        {
        //            int relOffset3 = (int)((long)(r3 + 64) - (long)postEventAddrWS - 5);

        //            byte* pAk3 = (byte*)postEventAddrWS;
        //            pAk3[0] = 0xE9; // JMP relative
        //            *(int*)(pAk3 + 1) = relOffset3;

        //            VirtualProtect(postEventAddrWS, STOLEN_BYTES2, old3, out _);
        //        }









        IntPtr executeActionOnPlayingIDAddr = GetProcAddress(audio2Module, "?ExecuteActionOnPlayingID@SoundEngine@AK@@YAXW4AkActionOnEventType@12@IHW4AkCurveInterpolation@@@Z");
        _executeAction = (delegate* unmanaged[Cdecl]<int, uint, int, int, void>)executeActionOnPlayingIDAddr;
        if (_executeAction == null)
        {
            _log("Could not resolve ExecuteActionOnPlayingID");
        }
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