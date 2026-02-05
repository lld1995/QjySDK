// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ElliottWave("3a24200de12547f18ee0588cee46f4a4");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

