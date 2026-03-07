using System.IO.Pipes;
using System.Runtime.CompilerServices;
using static FPSLimiter.Hook.DebugLogger;
using static FPSLimiter.Hook.Global;

namespace FPSLimiter.Hook;

public static unsafe class NamedPipeServer
{
    internal static bool IsNamedPipeRunning = true;
    private static NamedPipeServerStream _namedPipeServerStream = null!;
    private static readonly string FpsLimiterPipeName = "FpsLimiter_" + ThisClientsHandle;
    
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
    
    private const byte PipeSoundUnmuteAll = 0xC1; // A2 C1 = Unmute all sounds. Reply with 0x01
    private const byte PipeSoundUnmuteList = 0xC2; // A2 C2 = Unmute a list of sounds. Receive (int) lengthOfList each{(uint) soundsEventId}. Reply with 0x01
    private const byte PipeSoundMuteList = 0xC3; // A2 C3 = Mute a list of sounds. Receive (int) lengthOfList each{(uint) soundsEventId}. Reply with 0x01
    private const byte PipeSoundGetMuted = 0xC4; // A1 C4 = Get a list of muted sounds. Send (int) lengthOfList each{(uint) soundsEventId}
    private const byte PipeSoundEventHistory = 0xC5; // A1 C5 = Get a list of history. Send response (int) listLength each{(uint) eventId (ulong) gameObjectId (ulong) timestamp}

    private const byte PipeSuccessResponseCode = 0x01; // 01 is the response we send at the end of a A2 request, like a success return code. 

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Initialize()
    {
        _namedPipeServerStream = CreateNamedPipeServer();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NamedPipeServerStream CreateNamedPipeServer()
    {
        Info($"Creating named pipe server: {FpsLimiterPipeName}");
        return new NamedPipeServerStream(FpsLimiterPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }
    
    internal static void StartPipeServer()
    {
        var failureCount = 0;
        while (IsNamedPipeRunning)
        {
            var cmdBytes = new byte[2];
            try
            {
                //Log($"Named pipe {FpsLimiterPipeName} WaitForConnection()");
                _namedPipeServerStream.WaitForConnection();

                using (var reader =
                       new BinaryReader(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                using (var writer =
                       new BinaryWriter(_namedPipeServerStream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    //lastPlace = "ReadByte directionByte";
                    cmdBytes[0] = reader.ReadByte();
                    cmdBytes[1] = reader.ReadByte();
                    switch (cmdBytes[0])
                    {
                        case PipeFireAndForgetDirection:
                            switch (cmdBytes[1])
                            {
                                case PipeSetFocusedCommand:
                                    A3B1_SetFocusNow();
                                    break;
                                case PipePredictFocusCommand:
                                    A3B3_PrepareToTakeFocusSoon(reader);
                                    break;
                            }

                            break;
                        case PipeUpdateDirection:
                            switch (cmdBytes[1])
                            {
                                case PipeTakeOwnershipCommand:
                                    A2B4_ClaimProcessOwnership(reader);
                                    break;

                                case PipeFpsPrefixByteFocused:
                                    A2F1_SetFpsTargets(reader);
                                    break;
                                case PipeSoundUnmuteAll:
                                    A2C1_UnmuteAllSounds();
                                    break;
                                case PipeSoundUnmuteList:
                                    A2C2_UnmuteSounds(reader);
                                    break;
                                case PipeSoundMuteList:
                                    A2C3_MuteSounds(reader);
                                    break;
                            }

                            ReplySuccess(writer);
                            
                            break;

                        case PipeQueryDirection:
                            switch (cmdBytes[1])
                            {
                                case PipeQueryDirection: // The default will be getting the FPS settings. 
                                    A1A1_QueryFpsSettings(writer);
                                    break;
                                case PipePingRequestCode:
                                    A1B2_Ping(writer);
                                    break;
                                case PipeSoundGetMuted:
                                    A1C4_SendAllMutedSounds(writer);
                                    break;
                                case PipeSoundEventHistory:
                                    A1C5_SendSoundEventHistory(writer);
                                    break;
                            }

                            break;
                    }

                    writer.Flush();
                    writer.Close();
                    reader.Close();
                }
            }
            catch (Exception ex)
            {
                Error(ex, $"Named pipe {_namedPipeServerStream} processing command {cmdBytes[0]:X} {cmdBytes[1]:X}");
                Thread.Sleep(10); // hopefully we never crash in a loop but if we do, save the cpu

                // Rather silent failure than crashing another process

                _namedPipeServerStream.Dispose();
                _namedPipeServerStream = CreateNamedPipeServer();

                failureCount++;

                if (failureCount > 100)
                {
                    // Something is very wrong. Let's just give up on everything.
                    IsNamedPipeRunning = false;
                    DxHook.IsFpsThrottleActive = false;
                    NativeMethods.MessageBox(IntPtr.Zero, $"Aborting FPS Limiter. Named pipe error: {ex}",
                        "FPS Limiter", 0);
                }
            }
            finally
            {
                if (_namedPipeServerStream.IsConnected)
                {
                    _namedPipeServerStream.Disconnect();
                }
            }
        }
    }

    private static void A2C3_MuteSounds(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        for (int i = 0; i < length; i++)
        {
            var nextId = reader.ReadUInt32();
            AudioHook.AddMutedId(nextId);
        }
    }

    private static void A2C2_UnmuteSounds(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        for (int i = 0; i < length; i++)
        {
            var nextId = reader.ReadUInt32();
            AudioHook.RemoveMutedId(nextId);
        }
    }

    private static void A2C1_UnmuteAllSounds()
    {
        AudioHook.ClearMutedIds();
    }

    private static void A1C5_SendSoundEventHistory(BinaryWriter writer)
    {
        var eventHistory = AudioLog.GetOrderedEventHistory();
        writer.Write(eventHistory.Count);
        foreach (var e in eventHistory)
        {
            writer.Write(e.EventID);
            writer.Write(e.GameObjectID);
            writer.Write(e.Timestamp);
        }
    }

    private static void A1C4_SendAllMutedSounds(BinaryWriter writer)
    {
        var allMutedSounds = AudioHook.GetMutedIds();
        writer.Write(allMutedSounds.Count);

        foreach (var id in allMutedSounds)
        {
            writer.Write(id);
        }
    }

    private static void A1B2_Ping(BinaryWriter writer)
    {
        writer.Write(PipeSuccessResponseCode);
    }

    private static void A1A1_QueryFpsSettings(BinaryWriter writer)
    {
        //lastPlace = "Write PipeFpsPrefixByteFocused";
        writer.Write(PipeFpsPrefixByteFocused);
        //lastPlace = "Write _targetFpsInFocus";
        writer.Write(DxHook.TargetFpsInFocus);
        //lastPlace = "Write PipeFpsPrefixByteBackground";
        writer.Write(PipeFpsPrefixByteBackground);
        //lastPlace = "Write _targetFpsInBackground";
        writer.Write(DxHook.TargetFpsInBackground);
        //lastPlace = "Write PipeFpsPrefixBytePredict";
        writer.Write(PipeFpsPrefixBytePredict);
        //lastPlace = "Write _targetFpsInPredictFocus";
        writer.Write(DxHook.TargetFpsInPredictFocus);
        writer.Write(PipeSuccessResponseCode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReplySuccess(BinaryWriter writer)
    {
        writer.Write(PipeSuccessResponseCode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void A2F1_SetFpsTargets(BinaryReader reader)
    {
        // since we read the 0xF1 already we know the next 4 bytes are going to be an 32 bit int.
        //lastPlace = "ReadInt32 newTargetFpsFocus";
        var newTargetFpsFocus = reader.ReadInt32();

        if (newTargetFpsFocus < 1 || newTargetFpsFocus > 1000)
        {
            // anything that might look like it's just unthrottled, we will turn off the throttle.
            DxHook.TargetFpsInFocus = 0;
            DxHook.PerFrameTargetMsInFocus = 0;
        }
        else
        {
            DxHook.TargetFpsInFocus = newTargetFpsFocus;
            DxHook.PerFrameTargetMsInFocus = 1000.0 / newTargetFpsFocus;
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
                DxHook.TargetFpsInBackground = 0;
                DxHook.PerFrameTargetMsInBackground = 0;
            }
            else
            {
                DxHook.TargetFpsInBackground = newTargetFpsBackground;
                DxHook.PerFrameTargetMsInBackground = 1000.0 / newTargetFpsBackground;
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
                DxHook.TargetFpsInPredictFocus = 0;
                DxHook.PerFrameTargetMsInPredictFocus = 0;
            }
            else
            {
                DxHook.TargetFpsInPredictFocus = newTargetFpsPredict;
                DxHook.PerFrameTargetMsInPredictFocus = 1000.0 / newTargetFpsPredict;
            }
        }

        // If both focus and background are 0, then disable the whole throttling.
        DxHook.IsFpsThrottleActive = DxHook.PerFrameTargetMsInBackground + DxHook.PerFrameTargetMsInFocus > 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void A2B4_ClaimProcessOwnership(BinaryReader reader)
    {
        OwnerProcessId = reader.ReadInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void A3B3_PrepareToTakeFocusSoon(BinaryReader reader)
    {
        DxHook.SetOurWindowInFocus(FocusType.Predicted);
        DxHook.PredictedFocusTimeoutMs = reader.ReadInt32();
        // Log($"PipePredictFocusCommand processed");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void A3B1_SetFocusNow()
    {
        DxHook.SetOurWindowInFocus(FocusType.Foreground);
        // Log($"PipeSetFocusedCommand processed");
    }
}