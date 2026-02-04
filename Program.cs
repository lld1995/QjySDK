// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new RSI_Fourier("479ccb6ba3bc4e87ac115124d9f76ce7");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

