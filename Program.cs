// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new TrendAwareReversalTurtleTrading("9df3cd468da44245bf7ae31c7ad68bf2");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

