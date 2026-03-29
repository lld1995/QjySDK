// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new PolyRsiAdx("1d1563c4a9d2425c9de8f538ae8ee393");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

