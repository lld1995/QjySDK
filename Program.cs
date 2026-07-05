// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new PhoenixNirvana("34310514ce86466c86a977fcf539f51a");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

