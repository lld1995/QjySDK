// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new BollRsiShortReversion("6c1414bd0758433c9772dd7f2263958b");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

