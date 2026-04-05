// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new FourierTransform("9a26d8217ecd40f3822eb3fb7e5231e4");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

