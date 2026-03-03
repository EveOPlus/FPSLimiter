// See https://aka.ms/new-console-template for more information

using FpsLimiter;

DllInjector.InjectAndInitialize("exefile", // exefile, 3DMarkSteelNomad, 3DMarkICFWorkload
    @"..\..\..\..\FPSLimiter.Hook\bin\Release\net10.0\win-x64\publish\FPSLimiter.Hook.dll");
