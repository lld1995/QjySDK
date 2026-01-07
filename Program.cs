// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK;

GlobalDef.Init();

{
    var sd = new StgDemo("3c647d6d87ae46ed8efa7d20472f5bf8");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

