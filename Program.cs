// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ChanLunBi("b7d3b9120668497fb8fc1d83262d1e70");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

